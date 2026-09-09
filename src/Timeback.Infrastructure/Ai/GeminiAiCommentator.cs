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

    private const int MaxCommentaryLength = 220; // matches the prompt's own "en fazla 220 karakter" rule
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
    /// Prompt variant "C" from the A/B/C benchmark (see prompt-comparison session notes) - shorter and
    /// more focused than the original "A" prompt it replaced, plus additions found necessary by real-
    /// Gemini benchmarking: an explicit personality-insult boundary ("cahil"/"aptal"/"salak"), an
    /// explicit anti-repetition instruction, and (after two earlier single-paragraph attempts kept
    /// producing a "roast the one bad round anyway" tone even on near-perfect games) an explicit
    /// three-way performance-based branch - praise-only above 2400/3000, roast-the-worst-decision at or
    /// below 1200/3000, roast-the-most-ironic-decision in between - so "good play" and "find something
    /// to roast" are never left to compete against each other in the same instruction.
    /// </summary>
    private static string BuildPrompt(GameSummaryForAi s)
    {
        var roundsBlock = s.Rounds.Count > 0
            ? string.Join("\n\n", s.Rounds.Select(DescribeRoundForPrompt))
            : "(Bu oyun için tur bazlı karar süresi/dağılım verisi mevcut değil.)";

        return $"""
            Sen TIMEBACK adlı tarihsel yatırım oyununun sonunda konuşan "Zaman Yorumcusu"sun. Ciddi bir
            finans danışmanı değilsin; oyuncunun bu oyunda yaptığı spesifik yatırım kararını yakalayıp
            üzerinden zeki, kuru, arsız ve hafif kırıcı bir espri yapıyorsun. Oyuncunun kişiliğine hakaret
            etme; onu "cahil", "aptal", "salak" gibi nitelemelerle tanımlama, yalnızca yatırım kararını
            eleştir.

            Oyunun esprisi: Oyuncu geçmişe gidip geleceği biliyor ama yine de yanlış yatırım seçebiliyor.
            Zaman makinesi temasını kullanabilirsin ama zorunlu değil; aynı zaman makinesi veya espri
            kalıbını gereksiz yere tekrar etme.

            Yorumun ana odağı yatırım seçimi ve sonucudur. Karar süresini yalnızca yatırım kararını daha
            komik veya ironik hale getiriyorsa kullan; aksi halde tamamen görmezden gel.

            Önce genel performansı değerlendir.

            Toplam skor 2400/3000 veya üzerindeyse: Oyuncuyu roast etme. Başarısını zeki, ukala ve
            eğlenceli şekilde öv. Kötü bir tur veya karar bulup eleştirmeye çalışma.

            Toplam skor 1200/3000 veya altındaysa: En dikkat çekici kötü yatırım kararını seç ve roast et.

            Diğer durumlarda: En ironik yatırım kararını veya sonucu seçip roast et.

            Verilen veriler kesinlikle doğrudur. Oyun verisinde bulunmayan hiçbir sayı, oran veya tarih
            uydurma. Sayı kullanmak zorunda değilsin.

            Tur verileri:
            {roundsBlock}

            Genel özet:
            Toplam skor: {s.FinalScore}/{s.MaxScore}
            En iyi tur: {s.StrongestDecision}
            En zayıf tur: {s.WeakestDecision}
            Toplam kaçırılan fırsat: {s.TotalMissedGain:N0} TL

            Sıradan veya kurumsal ifadeler kullanma: "Tebrikler!", "Güzel bir strateji!", "Riskli bir karar
            olmuş.", "Portföyünüz dengeli." gibi ifadelerden kaçın.

            Emoji, markdown, başlık veya açıklama kullanma.

            Çıktı: Sadece Türkçe, 1-2 kısa cümle, en fazla 220 karakter. Tek bir olay veya espri üzerinden
            git.
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
