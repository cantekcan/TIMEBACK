using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Timeback.Application.Abstractions;

namespace Timeback.Infrastructure.Ai;

public sealed class GeminiOptions
{
    public const string SectionName = "Gemini";
    public string ApiKey { get; set; } = "";
    public string PrimaryModel { get; set; } = "gemini-3.5-flash-lite";
    public string FallbackModel { get; set; } = "gemini-3.1-flash-lite";
    public bool Enabled => !string.IsNullOrWhiteSpace(ApiKey);
}

/// <summary>
/// Calls the Google Gemini API directly (no router, no third-party proxy) for a one-line Turkish
/// quip, trying <see cref="GeminiOptions.PrimaryModel"/> first and <see cref="GeminiOptions.FallbackModel"/>
/// only if that fails - both are real, named models, never a paid tier and never a different vendor.
/// The AI only *phrases* the already-computed result - the prompt hands it finished numbers and forbids
/// calculation. Any failure on BOTH models (no key, 4xx/5xx, timeout, empty response, bad JSON, or a
/// response that doesn't look like a real comment - see <see cref="LooksLikeInvalidComment"/>, or one that
/// invents a number - see <see cref="ContainsHallucinatedNumber"/>) falls back to
/// <see cref="FallbackAiCommentator"/>; this method never throws and never reaches for a paid model.
/// </summary>
public sealed class GeminiAiCommentator(
    HttpClient http,
    IOptions<GeminiOptions> options,
    FallbackAiCommentator fallback,
    ILogger<GeminiAiCommentator> logger) : IAiCommentator
{
    private readonly GeminiOptions _options = options.Value;

    public async Task<AiComment> CommentAsync(GameSummaryForAi summary, CancellationToken ct)
    {
        if (!_options.Enabled)
            return await fallback.CommentAsync(summary, ct);

        var prompt = BuildPrompt(summary);
        var allowedNumbers = BuildAllowedNumbers(summary);

        var primary = await TryModelAsync(_options.PrimaryModel, prompt, allowedNumbers, ct);
        if (primary is not null) return primary;

        var secondary = await TryModelAsync(_options.FallbackModel, prompt, allowedNumbers, ct);
        if (secondary is not null) return secondary;

        return await fallback.CommentAsync(summary, ct);
    }

    /// <summary>One attempt against one Gemini model. Returns null (never throws) for any failure, so
    /// the caller can move on to the next model in the chain.</summary>
    private async Task<AiComment?> TryModelAsync(string model, string prompt, HashSet<decimal> allowedNumbers, CancellationToken ct)
    {
        try
        {
            var payload = new
            {
                contents = new[] { new { parts = new[] { new { text = prompt } } } },
                generationConfig = new { temperature = 0.9, maxOutputTokens = 150 },
            };

            using var request = new HttpRequestMessage(HttpMethod.Post, $"/v1beta/models/{model}:generateContent")
            {
                Content = JsonContent.Create(payload)
            };
            // Header, not the "?key=" query string - so the key never ends up in a logged/traced URL.
            request.Headers.Add("x-goog-api-key", _options.ApiKey);

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(20));

            var response = await http.SendAsync(request, cts.Token);
            response.EnsureSuccessStatusCode();

            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cts.Token));
            var text = doc.RootElement
                .GetProperty("candidates")[0].GetProperty("content").GetProperty("parts")[0].GetProperty("text")
                .GetString();

            if (string.IsNullOrWhiteSpace(text) || LooksLikeInvalidComment(text))
            {
                logger.LogWarning("Gemini model {Model} returned a non-comment response; trying next", model);
                return null;
            }

            if (ContainsHallucinatedNumber(text, allowedNumbers))
            {
                logger.LogWarning("Gemini model {Model} used a number not present in the game data; trying next", model);
                return null;
            }

            // The model actually queried is sent as-is, but log/report modelVersion from the response
            // body when present, in case Gemini ever serves a slightly different pinned version. A
            // present-but-JSON-null field (TryGetProperty alone would return true for that) must fall
            // back to the queried model name too, not surface a null "model".
            var reportedModel = doc.RootElement.TryGetProperty("modelVersion", out var m) && m.ValueKind == JsonValueKind.String
                ? m.GetString()
                : model;

            return new AiComment(TrimToCompleteSentence(text.Trim().Replace("\n", " "), MaxCommentaryLength), reportedModel);
        }
        catch (Exception ex)
        {
            // Safe to log: HttpRequestException.Message only ever contains the status line, never
            // headers or the request URI - the API key (sent as a header, not a query string) is
            // never touched here.
            logger.LogWarning(ex, "Gemini model {Model} failed", model);
            return null;
        }
    }

    private const int MaxCommentaryLength = 500;
    private const int MaxPlausibleRawLength = 600; // a real 1-2 sentence roast never gets close to this

    /// <summary>Bare verdicts a moderation/classifier model would return instead of a comment.</summary>
    private static readonly string[] BareVerdicts = ["safe", "unsafe", "safe.", "unsafe."];

    /// <summary>Phrases/shapes that mean the response isn't the requested roast at all - a moderation
    /// verdict, a code answer, a self-description, JSON, or Markdown - so it must not reach the player.</summary>
    private static readonly string[] BannedSubstrings =
    [
        "user safety", "content safety", "safety classification", "content classification",
        "moderation", "i am an ai", "i'm an ai", "as an ai", "as a language model",
        "dil modeliyim", "yapay zeka modeliyim", "bir yapay zekayım",
        "```", "<html", "def ", "import ", "select ", "function(", "console.log",
    ];

    /// <summary>True when <paramref name="text"/> doesn't look like the short Turkish roast the prompt
    /// asked for - a moderation verdict, JSON, Markdown, code, or a system/model self-description -
    /// and should be treated the same as an empty response (fall back).</summary>
    private static bool LooksLikeInvalidComment(string text)
    {
        var trimmed = text.Trim();
        if (trimmed.Length == 0 || trimmed.Length > MaxPlausibleRawLength) return true;
        if (trimmed[0] is '{' or '[' or '#') return true; // JSON body or a Markdown heading

        var lower = trimmed.ToLowerInvariant();
        return BareVerdicts.Contains(lower) || BannedSubstrings.Any(lower.Contains);
    }

    /// <summary>Matches a numeric token the model might write: "31", "-31", "0.8", "0,8", "%25", "25%",
    /// "264.606" (Turkish thousands) or "264,606" (English thousands) - sign and percent sign optional
    /// on either side, digits with any mix of '.'/',' separators in between.</summary>
    private static readonly Regex NumberToken = new(@"[-−]?%?\s?\d[\d.,]*%?", RegexOptions.Compiled, TimeSpan.FromSeconds(1));

    /// <summary>
    /// The only numbers the model is allowed to say: every value actually sent to it (scores, decision
    /// times, allocation percentages, missed gain, round numbers) plus a small rounding tolerance per
    /// value (so a real 0.84s decision time makes "0.8" or "1" acceptable, not just "0.84" verbatim).
    /// </summary>
    private static HashSet<decimal> BuildAllowedNumbers(GameSummaryForAi s)
    {
        var allowed = new HashSet<decimal> { 0m, s.Rounds.Count };

        void Allow(decimal value)
        {
            allowed.Add(value);
            allowed.Add(Math.Round(value, 1, MidpointRounding.AwayFromZero));
            allowed.Add(Math.Round(value, 0, MidpointRounding.AwayFromZero));
            allowed.Add(Math.Floor(value * 10) / 10);
            allowed.Add(Math.Ceiling(value * 10) / 10);
        }

        Allow(s.FinalScore);
        Allow(s.MaxScore);
        Allow(s.TotalMissedGain);
        Allow(s.RoundScores.Count(sc => sc == 0)); // "sıfır puan alan tur sayısı" figure given verbatim
        foreach (var sc in s.RoundScores) Allow(sc);
        foreach (var r in s.Rounds)
        {
            Allow(r.Number);
            Allow(r.Score);
            if (r.DecisionTimeSeconds is { } dt) Allow(dt);
            foreach (var weight in r.Allocation.Values) Allow(weight);
        }
        foreach (var weight in s.AverageAllocationByAsset.Values) Allow(weight);

        // StrongestDecision/WeakestDecision are free-text but still real game data (they embed a
        // round's pick, score and real-return% that aren't otherwise structured) - any number written
        // there was genuinely handed to the model, so it's fair game too.
        foreach (var number in ExtractNumbers(s.StrongestDecision).Concat(ExtractNumbers(s.WeakestDecision)))
            Allow(number);

        return allowed;
    }

    private static IEnumerable<decimal> ExtractNumbers(string text) =>
        NumberToken.Matches(text).SelectMany(m => ParseCandidates(m.Value));

    /// <summary>A single written token can be read more than one way depending on decimal/thousands
    /// convention ("0.8" vs "264.606" vs "9,7") - every plausible reading is tried, and the token is
    /// accepted if ANY of them lands on a real number; only a token with no valid reading at all counts
    /// as invented.</summary>
    private static IEnumerable<decimal> ParseCandidates(string raw)
    {
        var digits = raw.Trim().Trim('%', ' ').TrimStart('-', '−');
        if (digits.Length == 0) yield break;

        if (decimal.TryParse(digits, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var literal))
            yield return literal;

        var turkish = digits.Replace(".", "").Replace(",", ".");
        if (decimal.TryParse(turkish, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var turkishStyle))
            yield return turkishStyle;

        var english = digits.Replace(",", "");
        if (decimal.TryParse(english, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var englishStyle))
            yield return englishStyle;
    }

    /// <summary>True if the model wrote any number - magnitude only, sign doesn't matter, "-%31" is as
    /// invented as "31" if 31 was never sent - that isn't in <paramref name="allowed"/> under any
    /// reasonable reading. This is what caught real output like "-%31" when no 31 existed anywhere in
    /// that game's data.</summary>
    private static bool ContainsHallucinatedNumber(string text, HashSet<decimal> allowed) =>
        NumberToken.Matches(text).Any(m => !ParseCandidates(m.Value).Any(candidate =>
            allowed.Contains(candidate) ||
            allowed.Contains(Math.Round(candidate, 1, MidpointRounding.AwayFromZero)) ||
            allowed.Contains(Math.Round(candidate, 0, MidpointRounding.AwayFromZero))));

    /// <summary>Caps at <paramref name="maxLength"/> chars, cutting at the last complete sentence
    /// instead of mid-sentence/mid-word - unless even the first sentence alone is longer than that,
    /// in which case a hard cut at the limit is the only safe option.</summary>
    private static string TrimToCompleteSentence(string text, int maxLength)
    {
        if (text.Length <= maxLength)
            return text;

        var cut = text[..maxLength];
        var lastSentenceEnd = cut.LastIndexOfAny(['.', '!', '?']);
        return lastSentenceEnd > 0 ? cut[..(lastSentenceEnd + 1)] : cut;
    }

    /// <summary>
    /// Shorter, de-duplicated version of the original prompt - same persona, same priority rules, same
    /// banned patterns, same output constraints, but each said once instead of repeated across an
    /// English+Turkish pair, a separate "inner monologue" section and a self-check checklist that never
    /// reached the player. Verified against production's optimized-prompt benchmark (real Gemini/
    /// OpenRouter calls) before landing here - shorter did not mean worse.
    /// </summary>
    private static string BuildPrompt(GameSummaryForAi s)
    {
        var zeroScoreRounds = s.RoundScores.Count(sc => sc == 0);
        var roundsBlock = s.Rounds.Count > 0
            ? string.Join("\n\n", s.Rounds.Select(DescribeRoundForPrompt))
            : "(Bu oyun için tur bazlı karar süresi/dağılım verisi mevcut değil.)";

        return $"""
            Sen TIMEBACK adlı tarihsel yatırım oyununun sonunda konuşan "Zaman Yorumcusu"sun. Ciddi bir
            finans danışmanı değilsin; oyuncunun bu oyunda yaptığı SPESİFİK bir yatırım kararını yakalayıp
            üzerinden arsız, ukala, hafif kırıcı ama zeki ve kuru mizahlı bir laf sokuyorsun. Hedefin
            oyuncunun kararı/performansı - kendisi/kişiliği değil, ona asla küfür/ağır hakaret etme.

            Oyunun esprisi: oyuncu geçmişe gidip geleceği biliyor, yine de yanlış/kararsız/aşırı riskli bir
            yatırım yapabiliyor. Zaman makinesi fikrini kullanabilirsin ama zorunlu değil; kullanacaksan
            "zaman makinesi sana X verdi, sen Y yaptın" gibi kalıpları birebir tekrarlama.

            Aşağıdaki veriler backend tarafından hesaplandı ve kesinlikle doğrudur - kendin hesap yapma,
            oyun verisinde açıkça yer almayan hiçbir sayı/oran/tarih uydurma. Sayı kullanmak zorunda
            değilsin; espri sayı gerektirmiyorsa hiç kullanma.

            Tur bazlı gerçek veriler:
            {roundsBlock}

            Genel özet (arka plan bilgisi - bunu sıralayıp anlatma):
            Toplam skor: {s.FinalScore}/{s.MaxScore}
            Sıfır puan alan tur sayısı: {zeroScoreRounds}
            En iyi tur: {s.StrongestDecision}
            En zayıf tur: {s.WeakestDecision}
            Toplam kaçırılan fırsat: {s.TotalMissedGain:N0} TL

            Roast edeceğin kararı şu öncelikle seç: (1) 0 puanlı bir tur varsa onu, (2) yoksa en kötü
            sonuçlanan dağılımı, (3) aşırı dengeli/kararsız bir dağılımı (ör. her varlığa eşit %),
            (4) tek varlığa aşırı yığılmış (%90+) bir dağılımı, (5) büyük fırsat kaçıran bir dağılımı,
            (6) karar süresi ile sonuç arasındaki tezatı, (7) turlar arası tezatı - hiçbiri belirgin
            değilse (8) genel performansı hafifçe roast et. Oyuncu gerçekten iyi oynadıysa düz "tebrikler"
            deme, ukala ve hafif şaşkın bir tonla öv.

            Yasaklı: "Zaman makinesi sana X verdi, sen Y yaptın", "Üç tur boyunca X yaptın" gibi kalıplar;
            "Tebrikler!", "Güzel bir strateji!", "Riskli bir karar olmuş.", "Portföyünüz dengeli.", "X puan
            aldın ve", "Ortalama bir kâhin:" gibi kurumsal/kibar-asistan ifadeleri; "aptalsın", "salaksın",
            "beceriksizsin", "hep böylesin", "yine yaptın" gibi kişiye doğrudan hakaret; "yine", "her
            zamanki gibi", "hep", "karakterin" gibi geçmiş oyun/persona imaları (bu oyuncunun TIMEBACK'i
            oynadığı tek kayıt, önceki oyun verisi yok). Emoji, markdown, JSON, başlık kullanma.

            Çıktı: SADECE Türkçe, 1-2 kısa cümle, en fazla 220 karakter, tek bir olay/espri - başka
            açıklama ekleme, sadece yorum metnini döndür.

            Örnek tonlar (kopyalama, sadece üslup referansı):
            "0.8 saniyede bütün parayı BTC'ye gömdün. Zaman makinesi hızlıymış; karar biraz daha hızlıymış."
            "15 saniye düşündün. Açıkçası daha iyi bir final bekliyordum."
            "Bu portföyün en riskli kısmı getirisi değil, neden böyle olduğu."
            "Üçte üç. Geçmişe dönüp geleceği bilmenin hilesini nihayet düzgün kullanmışsın."
            """;
    }

    /// <summary>Renders one round's decision time (or why it's absent), full allocation and score as a
    /// small block the AI can reason over - never a pre-written joke, just the raw facts.</summary>
    private static string DescribeRoundForPrompt(RoundSummaryForAi r)
    {
        var timeText = r.AutoLocked
            ? "süre doldu, karar kilitlenmedi"
            : r.DecisionTimeSeconds is { } t
                ? $"{t.ToString("0.0", CultureInfo.InvariantCulture)} saniyede karar verildi"
                : "karar süresi bilinmiyor";
        // An auto-locked round has no allocation at all - the player never locked one in, so there's
        // nothing to list. Saying so explicitly (never an empty line) keeps the AI from having to
        // guess, and keeps any internal-only symbol out of what it reads.
        var allocText = r.Allocation.Count == 0
            ? "Yatırım yapılmadı"
            : string.Join(", ", r.Allocation.OrderByDescending(kv => kv.Value).Select(kv => $"{kv.Key} %{kv.Value}"));

        return $"""
            Round {r.Number}:
            - Karar süresi: {timeText}
            - Dağılım: {allocText}
            - Skor: {r.Score}
            """;
    }
}
