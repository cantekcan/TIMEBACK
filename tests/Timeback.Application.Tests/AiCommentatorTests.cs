using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Timeback.Application.Abstractions;
using Timeback.Infrastructure.Ai;

namespace Timeback.Application.Tests;

public class AiCommentatorTests
{
    private static readonly GameSummaryForAi Summary = new(
        FinalScore: 1900, MaxScore: 3000,
        RoundScores: [800, 200, 900],
        StrongestDecision: "Round 3: BTC %60, 900 puan",
        WeakestDecision: "Round 2: SP500 %100, 200 puan",
        AverageAllocationByAsset: new Dictionary<string, int> { ["GOLD"] = 40, ["BTC"] = 35, ["BIST100"] = 25 },
        TotalMissedGain: 187_450m);

    private const string PrimaryModel = "gemini-3.5-flash-lite";
    private const string FallbackModel = "gemini-3.1-flash-lite";

    private static GeminiAiCommentator NewCommentator(HttpMessageHandler handler, string apiKey = "test-key") =>
        new(new HttpClient(handler) { BaseAddress = new Uri("https://generativelanguage.googleapis.com") },
            Options.Create(new GeminiOptions { ApiKey = apiKey, PrimaryModel = PrimaryModel, FallbackModel = FallbackModel }),
            new FallbackAiCommentator(),
            NullLogger<GeminiAiCommentator>.Instance);

    /// <summary>Builds a Gemini-shaped 200 OK response body for <paramref name="text"/>.</summary>
    private static HttpResponseMessage GeminiOk(string text, string? modelVersion = PrimaryModel) => new(HttpStatusCode.OK)
    {
        Content = JsonContent.Create(new
        {
            candidates = new[] { new { content = new { parts = new[] { new { text } }, role = "model" }, finishReason = "STOP", index = 0 } },
            modelVersion,
        }),
    };

    [Fact]
    public async Task Fallback_always_returns_a_short_non_empty_turkish_comment()
    {
        var fallback = new FallbackAiCommentator();
        var comment = await fallback.CommentAsync(Summary, default);
        comment.Text.Should().NotBeNullOrWhiteSpace();
        comment.Text.Length.Should().BeLessThan(200);
        comment.Model.Should().BeNull();
    }

    [Fact]
    public async Task Fallback_never_says_cash_when_no_round_was_ever_invested()
    {
        // Every round timed out with nothing locked in: AverageAllocationByAsset is empty. The
        // fallback must not fill its "favourite asset" slot with "nakit" - that reads exactly like
        // the AI claiming the player chose to hold cash, which never happened.
        var neverInvested = Summary with { AverageAllocationByAsset = new Dictionary<string, int>() };
        var fallback = new FallbackAiCommentator();

        var comment = await fallback.CommentAsync(neverInvested, default);

        comment.Text.Should().NotContain("nakit", "the fallback must never claim the player chose to hold cash");
        comment.Text.Should().NotContain("CASH");
    }

    [Fact]
    public async Task Gemini_with_no_api_key_uses_the_fallback()
    {
        var gemini = NewCommentator(new ThrowingHandler(), apiKey: "");

        var comment = await gemini.CommentAsync(Summary, default);

        comment.Text.Should().NotBeNullOrWhiteSpace();
        comment.Model.Should().BeNull();
    }

    [Fact]
    public async Task Gemini_swallows_provider_failure_and_never_throws()
    {
        var gemini = NewCommentator(new ThrowingHandler());

        var act = async () => await gemini.CommentAsync(Summary, default);

        var comment = await act.Should().NotThrowAsync();
        comment.Subject.Text.Should().NotBeNullOrWhiteSpace(); // fell all the way back
        comment.Subject.Model.Should().BeNull();
    }

    [Fact]
    public async Task Gemini_parses_a_successful_response_into_the_comment_text_and_captures_the_real_model()
    {
        var handler = new StubHandler((_, _) => GeminiOk("Gerçekten tarihe damga vurmuşsun, tebrikler."));
        var gemini = NewCommentator(handler);

        var comment = await gemini.CommentAsync(Summary, default);

        comment.Text.Should().Be("Gerçekten tarihe damga vurmuşsun, tebrikler.");
        comment.Model.Should().Be(PrimaryModel);
        handler.Requests.Should().HaveCount(1); // primary succeeded - fallback model never queried
    }

    [Fact]
    public async Task Gemini_falls_back_to_the_queried_model_name_when_response_omits_modelVersion()
    {
        var handler = new StubHandler((_, _) => GeminiOk("Kısa ve acımasız bir yorum.", modelVersion: null));
        var gemini = NewCommentator(handler);

        var comment = await gemini.CommentAsync(Summary, default);

        comment.Model.Should().Be(PrimaryModel);
    }

    [Theory]
    [InlineData("User Safety: safe")]
    [InlineData("safe")]
    [InlineData("unsafe")]
    [InlineData("This content has been flagged by our moderation system.")]
    [InlineData("{\"score\": 3200, \"verdict\": \"ok\"}")]
    [InlineData("# Oyun Özeti\n**Skor:** 3200")]
    [InlineData("```python\ndef roast(score):\n    return score\n```")]
    [InlineData("I am an AI language model and I cannot generate insulting content.")]
    public async Task Gemini_rejects_non_comment_responses_and_uses_the_fallback(string invalidReply)
    {
        var handler = new StubHandler((_, _) => GeminiOk(invalidReply));
        var gemini = NewCommentator(handler);
        var expectedFallback = await new FallbackAiCommentator().CommentAsync(Summary, default);

        var comment = await gemini.CommentAsync(Summary, default);

        comment.Text.Should().Be(expectedFallback.Text);
        comment.Model.Should().BeNull("the fallback text must never be attributed to a real model");
    }

    [Fact]
    public async Task Gemini_rejects_an_excessively_long_response_and_uses_the_fallback()
    {
        var wayTooLong = string.Concat(Enumerable.Repeat("Bu oyuncu gerçekten çok ilginç kararlar aldı ve ", 20));
        var handler = new StubHandler((_, _) => GeminiOk(wayTooLong));
        var gemini = NewCommentator(handler);
        var expectedFallback = await new FallbackAiCommentator().CommentAsync(Summary, default);

        var comment = await gemini.CommentAsync(Summary, default);

        comment.Text.Should().Be(expectedFallback.Text);
        comment.Model.Should().BeNull();
    }

    [Fact]
    public async Task Gemini_response_longer_than_the_220_char_limit_is_trimmed_to_the_last_complete_sentence()
    {
        var firstSentence = "Bu turda tüm parasını Bitcoin'e yatırıp sıfır çekmesi, zaman makinesini sadece pahalı bir turizm aracına çevirmiş olmasından başka bir şey değil.";
        // Comfortably under MaxPlausibleRawLength (600), so this is still accepted as a real comment
        // (not rejected as garbage) - but well over MaxCommentaryLength (220), so the second sentence
        // must be trimmed away entirely rather than the whole reply falling back.
        var secondSentence = " Üstüne üstlük ikinci turda da aynı hatayı tekrarlayıp parasının geri kalanını da benzer şekilde eritmeyi başarmış, bu da onu iki kere aynı taşa çarpan biri yapıyor.";
        var tooLongButValid = firstSentence + secondSentence;
        tooLongButValid.Length.Should().BeGreaterThan(220).And.BeLessThan(600); // sanity-check the fixture

        var handler = new StubHandler((_, _) => GeminiOk(tooLongButValid));
        var gemini = NewCommentator(handler);

        var comment = await gemini.CommentAsync(Summary, default);

        comment.Model.Should().Be(PrimaryModel, "a real, valid comment that's just long must still be accepted, not fall back");
        comment.Text.Length.Should().BeLessThanOrEqualTo(220);
        comment.Text.Should().Be(firstSentence, "trimming must cut at the last complete sentence within the 220-char limit, not mid-sentence");
    }

    [Fact]
    public async Task Gemini_with_empty_message_content_uses_the_fallback()
    {
        var handler = new StubHandler((_, _) => GeminiOk(""));
        var gemini = NewCommentator(handler);
        var expectedFallback = await new FallbackAiCommentator().CommentAsync(Summary, default);

        var comment = await gemini.CommentAsync(Summary, default);

        comment.Text.Should().Be(expectedFallback.Text);
        comment.Model.Should().BeNull();
    }

    [Fact]
    public async Task Gemini_with_429_too_many_requests_on_both_models_uses_the_fallback()
    {
        var handler = new StubHandler((_, _) => new HttpResponseMessage((HttpStatusCode)429));
        var gemini = NewCommentator(handler);
        var expectedFallback = await new FallbackAiCommentator().CommentAsync(Summary, default);

        var comment = await gemini.CommentAsync(Summary, default);

        comment.Text.Should().Be(expectedFallback.Text);
        comment.Model.Should().BeNull();
        handler.Requests.Should().HaveCount(2); // primary, then fallback model - both tried
    }

    [Fact]
    public async Task Gemini_sends_the_configured_model_and_the_real_game_data_never_fabricated()
    {
        var handler = new StubHandler((_, _) => GeminiOk("Yorum."));
        var gemini = NewCommentator(handler);

        await gemini.CommentAsync(Summary, default);

        handler.Requests.Should().ContainSingle();
        var (request, body) = handler.Requests[0];
        request.RequestUri!.PathAndQuery.Should().Be($"/v1beta/models/{PrimaryModel}:generateContent");
        // The key travels as a header, never in the URL query string, so it can never end up in a
        // logged/traced request URI.
        request.Headers.TryGetValues("x-goog-api-key", out var keys).Should().BeTrue();
        keys!.Should().ContainSingle().Which.Should().Be("test-key");

        body.Should().Contain("1900/3000"); // the real score, not a made-up one
        body.Should().Contain("Round 3: BTC %60, 900 puan");
    }

    // -- Primary -> fallback model chaining ---------------------------------------------------------
    // The one genuinely new piece of behavior in this provider: a failed primary model must transparently
    // hand off to the fallback model before ever reaching the deterministic commentator, and the real
    // model that actually answered must always be the one reported - never a fixed/guessed name.

    [Fact]
    public async Task Primary_model_failure_falls_through_to_the_fallback_model_and_reports_its_real_name()
    {
        var handler = new StubHandler((req, _) => req.RequestUri!.PathAndQuery.Contains(PrimaryModel)
            ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
            : GeminiOk("Yedek modelden gelen yorum.", modelVersion: FallbackModel));
        var gemini = NewCommentator(handler);

        var comment = await gemini.CommentAsync(Summary, default);

        comment.Text.Should().Be("Yedek modelden gelen yorum.");
        comment.Model.Should().Be(FallbackModel, "the player-facing model name must reflect which model actually answered");
        handler.Requests.Should().HaveCount(2);
        handler.Requests[0].Request.RequestUri!.PathAndQuery.Should().Contain(PrimaryModel);
        handler.Requests[1].Request.RequestUri!.PathAndQuery.Should().Contain(FallbackModel);
    }

    [Fact]
    public async Task Primary_model_success_never_queries_the_fallback_model()
    {
        var handler = new StubHandler((req, _) => req.RequestUri!.PathAndQuery.Contains(PrimaryModel)
            ? GeminiOk("Birincil modelden yorum.", modelVersion: PrimaryModel)
            : throw new InvalidOperationException("Fallback model should never be called when primary succeeds."));
        var gemini = NewCommentator(handler);

        var comment = await gemini.CommentAsync(Summary, default);

        comment.Model.Should().Be(PrimaryModel);
        handler.Requests.Should().ContainSingle();
    }

    [Fact]
    public async Task Both_models_failing_uses_the_deterministic_fallback_never_a_paid_model()
    {
        var handler = new StubHandler((_, _) => new HttpResponseMessage(HttpStatusCode.InternalServerError));
        var gemini = NewCommentator(handler);
        var expectedFallback = await new FallbackAiCommentator().CommentAsync(Summary, default);

        var comment = await gemini.CommentAsync(Summary, default);

        comment.Text.Should().Be(expectedFallback.Text);
        comment.Model.Should().BeNull("a deterministic-fallback comment must never be attributed to any AI model");
        handler.Requests.Should().HaveCount(2);
    }

    [Fact]
    public async Task Primary_returning_an_invalid_comment_falls_through_to_the_fallback_model()
    {
        // Not just transport failures - a non-comment shape (moderation verdict, JSON, ...) from the
        // primary model must also hand off to the fallback model, not go straight to the deterministic one.
        var handler = new StubHandler((req, _) => req.RequestUri!.PathAndQuery.Contains(PrimaryModel)
            ? GeminiOk("safe")
            : GeminiOk("Gerçek bir yorum bu sefer.", modelVersion: FallbackModel));
        var gemini = NewCommentator(handler);

        var comment = await gemini.CommentAsync(Summary, default);

        comment.Text.Should().Be("Gerçek bir yorum bu sefer.");
        comment.Model.Should().Be(FallbackModel);
    }

    // -- Per-round decision-time + full-allocation data actually reaches the prompt --------------
    // The AI decides what's funny; these tests only prove the deterministic, testable half: that the
    // exact facts it needs to notice a pattern (equal split, all-in, decision time, big miss) are
    // faithfully delivered in the request body, and that missing data never breaks the call.

    private static GameSummaryForAi SummaryFor(RoundSummaryForAi round, decimal missedGain = 0m) => new(
        FinalScore: round.Score, MaxScore: 3000,
        RoundScores: [round.Score],
        StrongestDecision: $"Round {round.Number}: test, {round.Score} puan",
        WeakestDecision: $"Round {round.Number}: test, {round.Score} puan",
        AverageAllocationByAsset: round.Allocation.Where(kv => kv.Value > 0).ToDictionary(kv => kv.Key, kv => kv.Value),
        TotalMissedGain: missedGain,
        Rounds: [round]);

    /// <summary>Sends <paramref name="summary"/> through the real prompt builder and returns the
    /// actual (unescaped) prompt text sent as the request's content part - not the raw JSON, whose
    /// default escaping would turn "ü"/"ç" into "\uXXXX" sequences and break plain string assertions.</summary>
    private static async Task<string> CapturedPromptFor(GameSummaryForAi summary)
    {
        var handler = new StubHandler((_, _) => GeminiOk("Yorum."));
        await NewCommentator(handler).CommentAsync(summary, default);
        var (_, body) = handler.Requests[0];
        using var doc = System.Text.Json.JsonDocument.Parse(body);
        return doc.RootElement.GetProperty("contents")[0].GetProperty("parts")[0].GetProperty("text").GetString()!;
    }

    [Fact]
    public async Task Scenario_1_zero_score_long_decision_equal_split_reaches_the_prompt()
    {
        var round = new RoundSummaryForAi(1, Score: 0, AutoLocked: false, DecisionTimeSeconds: 9.8m,
            Allocation: new Dictionary<string, int> { ["GOLD"] = 25, ["BIST100"] = 25, ["BTC"] = 25, ["SP500"] = 25 });

        var body = await CapturedPromptFor(SummaryFor(round));

        body.Should().Contain("9.8 saniyede karar verildi");
        body.Should().Contain("GOLD %25").And.Contain("BIST100 %25").And.Contain("BTC %25").And.Contain("SP500 %25");
        body.Should().Contain("Skor: 0");
    }

    [Fact]
    public async Task Scenario_2_zero_score_fast_decision_btc_heavy_reaches_the_prompt()
    {
        var round = new RoundSummaryForAi(2, Score: 0, AutoLocked: false, DecisionTimeSeconds: 2.0m,
            Allocation: new Dictionary<string, int> { ["BTC"] = 100, ["GOLD"] = 0, ["BIST100"] = 0, ["SP500"] = 0 });

        var body = await CapturedPromptFor(SummaryFor(round));

        body.Should().Contain("2.0 saniyede karar verildi");
        body.Should().Contain("BTC %100");
        body.Should().Contain("Skor: 0");
    }

    [Fact]
    public async Task Scenario_3_good_score_fast_decision_reaches_the_prompt()
    {
        var round = new RoundSummaryForAi(1, Score: 950, AutoLocked: false, DecisionTimeSeconds: 2.1m,
            Allocation: new Dictionary<string, int> { ["BTC"] = 100, ["GOLD"] = 0, ["BIST100"] = 0, ["SP500"] = 0 });

        var body = await CapturedPromptFor(SummaryFor(round));

        body.Should().Contain("2.1 saniyede karar verildi");
        body.Should().Contain("BTC %100");
        body.Should().Contain("Skor: 950");
    }

    [Fact]
    public async Task Scenario_4_good_score_slow_balanced_decision_reaches_the_prompt()
    {
        var round = new RoundSummaryForAi(1, Score: 800, AutoLocked: false, DecisionTimeSeconds: 9.5m,
            Allocation: new Dictionary<string, int> { ["GOLD"] = 25, ["BIST100"] = 25, ["BTC"] = 25, ["SP500"] = 25 });

        var body = await CapturedPromptFor(SummaryFor(round));

        body.Should().Contain("9.5 saniyede karar verildi");
        body.Should().Contain("GOLD %25").And.Contain("BIST100 %25").And.Contain("BTC %25").And.Contain("SP500 %25");
        body.Should().Contain("Skor: 800");
    }

    [Fact]
    public async Task Scenario_5_large_missed_gain_with_specific_allocation_reaches_the_prompt()
    {
        var round = new RoundSummaryForAi(3, Score: 40, AutoLocked: false, DecisionTimeSeconds: 5.4m,
            Allocation: new Dictionary<string, int> { ["GOLD"] = 10, ["BIST100"] = 90, ["BTC"] = 0, ["SP500"] = 0 });

        const decimal missedGain = 264_606m;
        var body = await CapturedPromptFor(SummaryFor(round, missedGain));

        // BuildPrompt formats this with {:N0} under the current culture (by design - it's a
        // player-facing Turkish string, not a machine-readable one), so the separator differs
        // between environments (e.g. "264.606" on a tr-TR runner, "264,606" on the en-US CI
        // runner). Formatting the same value the same way here - instead of hardcoding one
        // culture's rendering - is what actually verifies "264606 reached the prompt correctly",
        // regardless of which culture the test happens to run under.
        body.Should().Contain(missedGain.ToString("N0") + " TL");
        body.Should().Contain("BIST100 %90");
        body.Should().Contain("Skor: 40");
    }

    [Fact]
    public async Task Scenario_6_ai_failure_still_falls_back_successfully()
    {
        var round = new RoundSummaryForAi(1, Score: 0, AutoLocked: false, DecisionTimeSeconds: 3m,
            Allocation: new Dictionary<string, int> { ["BTC"] = 100 });
        var gemini = NewCommentator(new ThrowingHandler());

        var comment = await gemini.CommentAsync(SummaryFor(round), default);

        comment.Text.Should().NotBeNullOrWhiteSpace();
        comment.Model.Should().BeNull();
    }

    [Fact]
    public async Task Scenario_7_missing_decision_time_never_breaks_the_call()
    {
        // A round with no decision-time data at all (e.g. an older game predating this field).
        var noTimeRound = new RoundSummaryForAi(1, Score: 500, AutoLocked: false, DecisionTimeSeconds: null,
            Allocation: new Dictionary<string, int> { ["GOLD"] = 100 });
        var bodyWithNullTime = await CapturedPromptFor(SummaryFor(noTimeRound));
        bodyWithNullTime.Should().Contain("karar süresi bilinmiyor");
        bodyWithNullTime.Should().Contain("Skor: 500");

        // An auto-locked round (deadline passed, nothing ever locked in) gets its own honest
        // description, never a fabricated decision time and never a synthetic "cash" allocation -
        // there is no allocation at all, and the prompt must say so in plain Turkish.
        var autoLockedRound = new RoundSummaryForAi(2, Score: 0, AutoLocked: true, DecisionTimeSeconds: null,
            Allocation: new Dictionary<string, int>());
        var bodyAutoLocked = await CapturedPromptFor(SummaryFor(autoLockedRound));
        bodyAutoLocked.Should().Contain("süre doldu, karar kilitlenmedi");
        bodyAutoLocked.Should().Contain("Dağılım: Yatırım yapılmadı");
        bodyAutoLocked.Should().NotContain("CASH");

        // No per-round data at all (Rounds omitted entirely) must still produce a working call.
        var noRoundsSummary = new GameSummaryForAi(
            FinalScore: 500, MaxScore: 3000, RoundScores: [500],
            StrongestDecision: "Round 1: GOLD %100, 500 puan", WeakestDecision: "Round 1: GOLD %100, 500 puan",
            AverageAllocationByAsset: new Dictionary<string, int> { ["GOLD"] = 100 }, TotalMissedGain: 0m);
        var act = async () => await NewCommentator(new StubHandler((_, _) => GeminiOk("Yorum.")))
            .CommentAsync(noRoundsSummary, default);
        var noRoundsComment = await act.Should().NotThrowAsync();
        noRoundsComment.Subject.Text.Should().Be("Yorum.");
    }

    // -- Numeric hallucination guard ---------------------------------------------------------------
    // Real incident (with the previous provider): the model once wrote "-%31" in a game where no
    // round, score, allocation or decision time was anywhere near 31. These prove real numbers pass
    // untouched and invented ones fall through to the fallback model.

    private static readonly RoundSummaryForAi NumericRound = new(1, Score: 0, AutoLocked: false, DecisionTimeSeconds: 0.84m,
        Allocation: new Dictionary<string, int> { ["GOLD"] = 25, ["BIST100"] = 25, ["BTC"] = 25, ["SP500"] = 25 });
    private static readonly GameSummaryForAi NumericSummary = SummaryFor(NumericRound, missedGain: 50_000m);

    private static async Task<AiComment> CommentWithReply(string reply, GameSummaryForAi? summary = null) =>
        await NewCommentator(new StubHandler((_, _) => GeminiOk(reply))).CommentAsync(summary ?? NumericSummary, default);

    [Fact]
    public async Task Real_decision_time_stated_exactly_is_accepted()
    {
        var comment = await CommentWithReply("0.8 saniyede karar verdin, cesur.", SummaryFor(NumericRound with { DecisionTimeSeconds = 0.8m }, 50_000m));
        comment.Text.Should().Be("0.8 saniyede karar verdin, cesur.");
        comment.Model.Should().Be(PrimaryModel);
    }

    [Fact]
    public async Task Real_decision_time_rounded_to_one_decimal_is_accepted()
    {
        // Real value is 0.84 - the model rounding it to "0.8" must not be treated as an invented number.
        var comment = await CommentWithReply("0.8 saniyede karar verdin, cesur.");
        comment.Text.Should().Be("0.8 saniyede karar verdin, cesur.");
        comment.Model.Should().Be(PrimaryModel);
    }

    [Fact]
    public async Task Real_allocation_percentage_is_accepted()
    {
        var comment = await CommentWithReply("Her varlığa %25 verip taraf tutmamayı seçtin.");
        comment.Text.Should().Be("Her varlığa %25 verip taraf tutmamayı seçtin.");
    }

    [Fact]
    public async Task Real_zero_score_is_accepted()
    {
        var comment = await CommentWithReply("0 puan aldın, zaman makinesi boşuna çalışmış.");
        comment.Text.Should().Be("0 puan aldın, zaman makinesi boşuna çalışmış.");
    }

    [Fact]
    public async Task Invented_percentage_not_in_game_data_falls_back()
    {
        var expectedFallback = await new FallbackAiCommentator().CommentAsync(NumericSummary, default);

        // Both models "say" the same invented number here, so this exercises the guard rejecting the
        // primary AND the fallback model landing on the deterministic commentator.
        var comment = await CommentWithReply("Zaman makinesi seni doğrudan -%31'e ışınladı.");

        comment.Text.Should().Be(expectedFallback.Text);
        comment.Model.Should().BeNull("a number the game never sent must never reach the player");
    }

    [Fact]
    public async Task Invented_score_number_not_in_game_data_falls_back()
    {
        var expectedFallback = await new FallbackAiCommentator().CommentAsync(NumericSummary, default);

        var comment = await CommentWithReply("31 puan öncesine ışınlanmışsın.");

        comment.Text.Should().Be(expectedFallback.Text);
        comment.Model.Should().BeNull();
    }

    [Fact]
    public async Task Numberless_roast_is_accepted()
    {
        var comment = await CommentWithReply("Geleceği biliyordun ve yine de dört varlığa da eşit güvendin. Kararsızlık zaman yolculuğu bile aşamamış bir dert.");
        comment.Text.Should().Contain("Kararsızlık");
        comment.Model.Should().Be(PrimaryModel);
    }

    [Fact]
    public async Task Normal_gemini_response_with_only_real_numbers_passes_through_unchanged()
    {
        var comment = await CommentWithReply("İlk turda 0 puan aldın ama %25'lik payını hiç sorgulamadın.");
        comment.Text.Should().Be("İlk turda 0 puan aldın ama %25'lik payını hiç sorgulamadın.");
        comment.Model.Should().Be(PrimaryModel);
    }

    private sealed class ThrowingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => throw new HttpRequestException("simulated outage");
    }

    /// <summary>Returns a response computed from the outgoing request (and its body), and records
    /// every request made - primary and (if tried) fallback-model calls both land here in order.</summary>
    private sealed class StubHandler(Func<HttpRequestMessage, string, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<(HttpRequestMessage Request, string Body)> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct);
            Requests.Add((request, body));
            return respond(request, body);
        }
    }
}
