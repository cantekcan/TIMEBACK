using System.Globalization;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Timeback.Application.Abstractions;

namespace Timeback.Infrastructure.Ai;

public sealed class OpenRouterOptions
{
    public const string SectionName = "OpenRouter";
    public string ApiKey { get; set; } = "";
    public string Model { get; set; } = "minimax/minimax-m3:free";
    public bool Enabled => !string.IsNullOrWhiteSpace(ApiKey);
}

/// <summary>
/// Calls OpenRouter's free `minimax/minimax-m3:free` model directly (OpenAI-compatible Chat
/// Completions API) for a one-line Turkish quip - never the `openrouter/free` auto-router, which was
/// found to sometimes land on non-conversational free models (content-safety classifiers, code
/// assistants) that return nonsense instead of a comment. The AI only *phrases* the already-computed
/// result - the prompt hands it finished numbers and forbids calculation. Any failure (no key,
/// 401/403/429/5xx, timeout, empty response, bad JSON, or a response that doesn't look like a real
/// comment - see <see cref="LooksLikeInvalidComment"/>) falls back to
/// <see cref="FallbackAiCommentator"/>; this method never throws and never falls back to a paid model.
/// </summary>
public sealed class OpenRouterAiCommentator(
    HttpClient http,
    IOptions<OpenRouterOptions> options,
    FallbackAiCommentator fallback,
    ILogger<OpenRouterAiCommentator> logger) : IAiCommentator
{
    private readonly OpenRouterOptions _options = options.Value;

    public async Task<AiComment> CommentAsync(GameSummaryForAi summary, CancellationToken ct)
    {
        if (!_options.Enabled)
            return await fallback.CommentAsync(summary, ct);

        try
        {
            var prompt = BuildPrompt(summary);
            var payload = new
            {
                model = _options.Model,
                messages = new[] { new { role = "user", content = prompt } },
                temperature = 0.9,
                max_tokens = 300,
                reasoning = new { enabled = false } // this is a short quip, not a reasoning task
            };

            using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/chat/completions")
            {
                Content = JsonContent.Create(payload)
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.ApiKey);

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(20));

            var response = await http.SendAsync(request, cts.Token);
            response.EnsureSuccessStatusCode();

            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cts.Token));
            var text = doc.RootElement
                .GetProperty("choices")[0].GetProperty("message").GetProperty("content")
                .GetString();

            if (string.IsNullOrWhiteSpace(text) || LooksLikeInvalidComment(text))
            {
                logger.LogWarning("OpenRouter returned a non-comment response; using fallback");
                return await fallback.CommentAsync(summary, ct);
            }

            if (ContainsHallucinatedNumber(text, BuildAllowedNumbers(summary)))
            {
                logger.LogWarning("OpenRouter response used a number not present in the game data; using fallback");
                return await fallback.CommentAsync(summary, ct);
            }

            // The configured model is sent as-is, but log/report the model the response body actually
            // names, in case OpenRouter ever substitutes or fails over to a different free model.
            var model = doc.RootElement.TryGetProperty("model", out var m) ? m.GetString() : _options.Model;

            return new AiComment(TrimToCompleteSentence(text.Trim().Replace("\n", " "), MaxCommentaryLength), model);
        }
        catch (Exception ex)
        {
            // Safe to log: HttpRequestException.Message only ever contains the status line (e.g.
            // "429 Too Many Requests"), never headers - the Authorization header is never touched here.
            logger.LogWarning(ex, "OpenRouter commentary failed; using fallback");
            return await fallback.CommentAsync(summary, ct);
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

    private static string BuildPrompt(GameSummaryForAi s)
    {
        var zeroScoreRounds = s.RoundScores.Count(sc => sc == 0);
        var roundsBlock = s.Rounds.Count > 0
            ? string.Join("\n\n", s.Rounds.Select(DescribeRoundForPrompt))
            : "(Bu oyun için tur bazlı karar süresi/dağılım verisi mevcut değil.)";

        return $"""
            Sen TIMEBACK adlı bir tarihsel yatırım oyununun sonunda konuşan "Zaman Yorumcusu"sun. Ciddi
            bir finans danışmanı değilsin, sonuç özeti de çıkarmıyorsun; oyuncunun bu oyunda yaptığı
            SPESİFİK bir yatırım kararını yakalayıp onun üzerinden biraz arsız, ukala, hafif kırıcı ama
            zeki ve kuru mizahlı bir laf sokan bir karaktersin. Oyuncu yorumu okuyunca "lan bu bana laf
            soktu" desin, ama gerçek kişiliğine/zekasına/hayatına değil, sadece TIMEBACK içindeki
            yatırım kararına saldırdığını hissetsin.

            TIMEBACK'in temel esprisi şu: oyuncu geçmişe gidip geleceği biliyor, ama yine de yanlış /
            kararsız / aşırı riskli bir yatırım yapabiliyor. Bu fikri kullanabilirsin (zaman makinesi,
            geçmişin ikinci şansı, geleceği bilmek vb.) ama HER yorumda kullanmak zorunda değilsin -
            bazen doğrudan karardan, bazen karar süresinden, bazen sade bir retorik sorudan başla.
            Kullanacaksan bile şu kalıpları birebir tekrarlama, sadece ilham al: "zamanda geriye
            döndün", "geçmiş sana ikinci bir şans verdi", "geleceği biliyordun", "zaman makinesi işini
            yaptı ama", "tarih sana fırsatı gösterdi", "takvim geri gitti".

            Aşağıdaki veriler backend tarafından hesaplandı ve kesinlikle doğru; kendin hesap yapma,
            yeni sayı/oran/tarih/istatistik uydurma, yatırım tavsiyesi verme.

            Never invent a number. If a number is not explicitly present in the game data below, do
            not use it. Prefer writing the roast without numbers.
            Türkçesi: Asla sayı uydurma. Aşağıdaki oyun verisinde açıkça yer almayan bir sayıyı
            kullanma (örn. "%31", "31 puan", "2.7 milyon" gibi kafandan rakamlar YASAK). Sayı kullanmak
            ZORUNDA DEĞİLSİN - espri sayı gerektirmiyorsa hiç kullanma. Örneğin 0 puanlık bir turda
            "0 puan" yazmak yerine "geçmişe dönüp geleceği biliyordun, yine yanlış ata bindin" gibi
            sayısız bir cümle çoğu zaman daha doğal durur.

            Tur bazlı gerçek veriler:
            {roundsBlock}

            Genel özet (sadece arka plan bilgisi - bunu sıralayıp anlatma):
            Toplam skor: {s.FinalScore}/{s.MaxScore}
            Sıfır puan alan tur sayısı: {zeroScoreRounds}
            En iyi tur: {s.StrongestDecision}
            En zayıf tur: {s.WeakestDecision}
            Toplam kaçırılan fırsat: {s.TotalMissedGain:N0} TL

            GÖREVİN: Yorumu yazmadan önce kendine şunu sor: "Bu oyuncunun bu oyunda yaptığı en komik /
            en ironik / en dikkat çekici yatırım kararı ne?" Yorumun SADECE bu tek karara dayansın -
            genel bir özet veya sonuç raporu değil.

            Hangi kararı seçeceğine şu öncelik sırasına göre karar ver:
            1. 0 puan alınan bir tur varsa MUTLAKA önce onu değerlendir - mümkünse ilk cümlede kullan.
               "0 puan" rakamını yazmak zorunda değilsin, "yine yanlış ata bindin" gibi sayısız bir
               cümle de aynı işi görür.
            2. Yoksa çok kötü sonuçlanan spesifik bir dağılımı değerlendir.
            3. Aşırı temkinli / gereksiz dengeli bir dağılım varsa (ör. tüm varlıklara eşit % - "Gold
               %25, BIST100 %25, BTC %25, SP500 %25" gibi) bunu "kararsızlık / garanti arama" üzerinden
               yakala - ama her dengeli portföyü otomatik kötüleme, sonuç iyiyse bunu da ukala şekilde
               öv.
            4. Çok yüksek riskli, tek varlığa yığılmış bir dağılım varsa (ör. bir varlık %90+) bunu
               yakala - ama sonuç çok iyi çıktıysa oyuncuyu tamamen ezme, "yanlış görünüp doğru çıkan
               karar" tuhaflığını kullan.
            5. Büyük bir fırsatın kaçırıldığı bir dağılım varsa bunu kullan.
            6. Karar süresi ile sonuç arasında ilginç bir tezat varsa (çok uzun düşünüp kötü karar / çok
               hızlı karar verip iyi ya da kötü sonuç) bunu kullan - ama süreyi sadece gerçekten espriyi
               güçlendiriyorsa kullan, her yorumda zorunlu değil.
            7. Turlar arasında ilginç bir değişim/tezat varsa (ör. bir turda temkinli, başka turda aşırı
               riskli) bunu kullan.
            8. Oyuncu gerçekten iyi bir karar verdiyse bunu ASLA düz/yapay bir "tebrikler" ile övme -
               ukala, hafif şaşkın bir tonla öv, ör: "Üç turda da doğru yatırımı buldun. Geleceği
               bilmenin hakkını vermişsin. Biraz ayıp ama." veya "Üçte üç. Geçmişe dönüp geleceği
               bilmenin hilesini nihayet düzgün kullanmışsın." Yine de gerçek kişiye "aptalsın",
               "salaksın", "tembelsin" gibi doğrudan saldırma - hedef yine kararı/performansı.
            9. Bunların hiçbiri belirgin değilse genel performans üzerinden hafif bir roast yap.

            YORUMU OLUŞTURMA MANTIĞI (bu senin iç akışın, çıktıya yazma):
            1. Oyundaki en komik/ironik olayı bul (yukarıdaki öncelik sırasına göre).
            2. O olayla ilgili spesifik dağılım/skor/karar süresi detayını seç.
            3. İstersen zaman yolculuğu fikriyle bağla, istersen doğrudan karara saldır.
            4. Son cümlede iğneleyici, hafif kırıcı bir punchline yap.

            AÇILIŞINI ÇEŞİTLENDİR - her yorumda farklı bir başlangıç tipi dene: doğrudan karara gir,
            karar süresine gir, sonuçtan/skordan gir, allocation'dan gir, zaman yolculuğu kelime oyunu
            yap, retorik soru sor, kısa bir punchline ile başla, ukala övgüyle başla.

            Şu kalıpları KESİNLİKLE kullanma - ne birebir ne de hafif değiştirilmiş haliyle:
            - "Zaman makinesi sana X verdi, sen Y yaptın."
            - "Geçmiş sana X verdi, sen Y yaptın."
            - "Üç tur boyunca X yaptın."
            - "Üç tur, üç tur boyunca..."
            Aynı kelimeyi art arda tekrar ederek başlayan cümlelerden KAÇIN (ör. "Üç tur, üç tur
            boyunca...", "Zaman makinesi, zaman makinesi..." gibi kekeleyen tekrarlar) - kulağa yapay
            geliyor, doğal Türkçe konuşma gibi durmuyor. Aynı diagnostic/oyun içinde önceki bir yorumda
            kullandığın cümle iskeletini de tekrar etme.

            ÇIKTI KURALLARI (kesinlikle uy):
            - Türkçe, SADECE 1 veya 2 kısa cümle, en fazla 220 karakter. 220'yi aşacağını hissedersen
              cümleyi KISALT - 220'yi geçmek kesinlikle kabul edilemez.
            - En fazla 1 ana olay anlat, en fazla 1 ana espri yap - başka olaya atlama.
            - Mümkünse hiç sayı kullanma; kullanacaksan en fazla 1 sayı, sadece oyunun gerçek verisinden
              ve yalnızca punchline'ı güçlendiriyorsa.
            - Sonuçları/istatistikleri arka arkaya listeleme, kuru bir finans raporu gibi konuşma,
              sonuç özeti çıkarma.
            - Emoji, markdown, JSON, başlık kullanma. Sabit bir açılış kalıbı kullanma.
            - "Kibar AI" gibi konuşma - bunları KULLANMA: "Tebrikler!", "Güzel bir strateji!", "İlginç
              bir tercih!", "Riskli bir karar olmuş.", "Portföyünüz dengeli.", "Performansınız...",
              "Bu turda...", "Genel olarak başarılısınız.", "Bir sonraki denemede...", "Toplam X TL
              kazandın/kaçırdın", "X puan aldın ve", "Stratejin...", "Başarılı bir şekilde...", sabit
              "Ortalama bir kâhin:" girişi. Bunlar finans danışmanı botu gibi hissettiriyor - bunun
              yerine doğal, arsız cümleler kur (ör. "Cesaret güzel şey de...", "Bu kadar fırsatı
              görüp...", "Portföy değil, kura çekmişsin.", "Zaman makinesi çalışıyor, sorun başka
              yerde.").
            - Roast'ın hedefi oyuncunun KENDİSİ değil, YAPTIĞI KARAR olsun. "aptalsın", "salaksın",
              "gerizekalısın", "beyinsizsin", "beceriksizsin", "eziksin", "malsın", "zekan yok",
              "üşengeçsin", "karakterin böyle", "hep böylesin", "yine yaptın", "hiçbir şeyi
              beceremiyorsun", "sen zaten böylesin" gibi kişiye/karaktere doğrudan hakaret eden
              ifadeler KESİNLİKLE yasak. Hafif kırıcı olabilirsin ama gerçek kişiye küfür/ağır hakaret
              etme. Bunun yerine "bu karar", "bu seçim", "bu portföy", "bu tur", "10 saniyen",
              "geçmişteki fırsat", "zaman makinesi" gibi oyun içi kavramlara odaklan.
            - Bu oyuncunun TIMEBACK'i oynadığı tek ve ilk kayıt - sistemde önceki oyunlarına dair hiçbir
              veri yok. "yine", "her zamanki gibi", "her zaman", "hep", "sürekli", "alışkanlık",
              "karakterin", "sen böylesin", "gene" gibi geçmiş oyun/persona ima eden ifadeler kullanma.
              Sadece BU oyunun turları arasında karşılaştırma yapabilirsin ("ilk turda...", "son
              turda...", "üçünde de...") - ama "Üç tur boyunca X yaptın" veya "Üç tur, üç tur
              boyunca..." kalıbını kullanma, yukarıda ayrıca yasaklandı.
            - Ton: biraz arsız, biraz ukala, hafif kırıcı, iğneleyici, zeki, kuru mizah - oyuncunun
              kararına acımayan ama eğlenceli. Kurumsal/robotik dil, finans uzmanı tavrı, motivasyon
              konuşması, yapay zeka gibi konuşma YASAK.
            - Sadece yorum metnini döndür, başka açıklama ekleme.

            GÖNDERMEDEN ÖNCE KENDİNE SOR: Gerçekten spesifik bir karara mı laf soktum? Punchline var mı?
            Yeterince arsız mı, yoksa fazla kibar/kurumsal mı kaçtı? Yasaklı kalıplardan birini mi
            kullandım, ya da aynı kelimeyi art arda tekrarlayarak mı başladım? Geçmiş oyun iması var mı?
            Uydurma sayı var mı? 220 karakter altında mı? Bu cümle kulağa gerçekten bir insanın
            arkadaşına laf sokması gibi mi geliyor, yoksa yapay/kalıp gibi mi duruyor? Cevaplardan biri
            olumsuzsa yeniden yaz.

            ÖRNEK TONLAR (yalnızca üslup/yapı referansı - aynı cümleleri asla kopyalama, kendi cümleni
            kur, farklı açılış tiplerini gözlemle):
            "0.8 saniyede bütün parayı BTC'ye gömdün. Zaman makinesi hızlıymış; karar biraz daha
            hızlıymış."
            "Geçmişe kadar gittin ve yatırım stratejin 'hepsinden biraz' oldu. Zaman makinesi var,
            büfe değil."
            "10 saniye düşündün. Açıkçası daha iyi bir final bekliyordum."
            "Geçmişe dönüp geleceği biliyordun, yine yanlış ata bindin."
            "BTC'ye bütün parayı bastın ve tuttu. Bu kadar yanlış görünümlü bir kararın bu kadar doğru
            çıkması biraz sinir bozucu."
            "Bu portföyün en riskli kısmı getirisi değil, neden böyle olduğu."
            "Üç turda da doğru yatırımı buldun. Geleceği bilmenin hakkını vermişsin. Biraz ayıp ama."
            "Üçte üç. Geçmişe dönüp geleceği bilmenin hilesini nihayet düzgün kullanmışsın."
            """;
    }

    /// <summary>Renders one round's decision time (or why it's absent), full allocation and score as a
    /// small block the AI can reason over - never a pre-written joke, just the raw facts.</summary>
    private static string DescribeRoundForPrompt(RoundSummaryForAi r)
    {
        var allocText = string.Join(", ", r.Allocation.OrderByDescending(kv => kv.Value).Select(kv => $"{kv.Key} %{kv.Value}"));
        var timeText = r.AutoLocked
            ? "süre doldu, hiç karar verilmedi (otomatik kilitlendi)"
            : r.DecisionTimeSeconds is { } t
                ? $"{t.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture)} saniyede karar verildi"
                : "karar süresi bilinmiyor";

        return $"""
            Round {r.Number}:
            - Karar süresi: {timeText}
            - Dağılım: {allocText}
            - Skor: {r.Score}
            """;
    }
}
