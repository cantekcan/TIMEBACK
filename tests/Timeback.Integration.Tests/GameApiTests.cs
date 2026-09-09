using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Testcontainers.PostgreSql;

namespace Timeback.Integration.Tests;

[Collection(nameof(PostgresCollection))]
public sealed class GameApiTests(PostgresFixture fx)
{
    private HttpClient NewClient() => fx.Factory.CreateClient();

    private static readonly object Allocation = new[]
    {
        new { symbol = "GOLD", weight = 40 }, new { symbol = "BTC", weight = 20 },
        new { symbol = "BIST100", weight = 20 }, new { symbol = "SP500", weight = 20 },
    };

    private static void Auth(HttpClient c, string token)
    {
        c.DefaultRequestHeaders.Remove("X-Game-Token");
        c.DefaultRequestHeaders.Add("X-Game-Token", token);
    }

    [Fact]
    public async Task Health_endpoint_is_up()
        => (await NewClient().GetAsync("/health")).StatusCode.Should().Be(HttpStatusCode.OK);

    [Fact]
    public async Task Cors_allows_the_configured_origin_and_rejects_others()
    {
        // The test host's Cors:Origins is the appsettings.json default (http://localhost:5173) - this
        // exercises the real CORS middleware end to end, the same mechanism Cors__Origins__0 drives in
        // production, rather than just unit-testing config parsing.
        var allowed = NewClient();
        allowed.DefaultRequestHeaders.Add("Origin", "http://localhost:5173");
        var allowedResponse = await allowed.GetAsync("/health");
        allowedResponse.Headers.TryGetValues("Access-Control-Allow-Origin", out var allowedValues).Should().BeTrue();
        allowedValues!.Should().ContainSingle().Which.Should().Be("http://localhost:5173");

        var disallowed = NewClient();
        disallowed.DefaultRequestHeaders.Add("Origin", "https://evil.example.com");
        var disallowedResponse = await disallowed.GetAsync("/health");
        disallowedResponse.Headers.Contains("Access-Control-Allow-Origin").Should().BeFalse();
    }

    [Fact]
    public async Task Play_a_full_game_and_land_on_the_leaderboard()
    {
        var client = NewClient();
        var start = await (await client.PostAsync("/api/v1/games", null)).Content.ReadFromJsonAsync<StartDto>();
        start!.GameId.Should().NotBeEmpty();
        start.GameToken.Should().NotBeNullOrWhiteSpace();
        Auth(client, start.GameToken);

        for (var n = 1; n <= 3; n++)
        {
            await client.GetAsync($"/api/v1/games/{start.GameId}/round");
            (await client.PostAsJsonAsync($"/api/v1/games/{start.GameId}/rounds/{n}/submit", Allocation))
                .EnsureSuccessStatusCode();
        }

        var result = await (await client.GetAsync($"/api/v1/games/{start.GameId}/result"))
            .Content.ReadFromJsonAsync<ResultDto>();
        result!.Status.Should().Be("Completed");
        result.FinalScore.Should().BeInRange(0, 3000);
        result.AiCommentary.Should().NotBeNullOrWhiteSpace();
        // No real Gemini key is configured for this test host, so the fallback commentator must
        // have produced the comment - and it never reports a model of its own.
        result.AiModel.Should().BeNull();

        (await client.PostAsJsonAsync("/api/v1/leaderboard",
            new { gameId = start.GameId, nickname = "IntegTester" })).EnsureSuccessStatusCode();

        var board = await (await client.GetAsync("/api/v1/leaderboard?count=10"))
            .Content.ReadFromJsonAsync<List<RowDto>>();
        board!.Should().Contain(r => r.Nickname == "IntegTester" && r.Score == result.FinalScore);
    }

    [Fact]
    public async Task Leaderboard_save_without_a_gameId_is_rejected_with_400()
    {
        // Guid is a value type - a client that omits "gameId" entirely must not silently bind to
        // Guid.Empty and fall through to a confusing 404; JsonRequired makes this an explicit 400.
        var client = NewClient();
        var response = await client.PostAsJsonAsync("/api/v1/leaderboard", new { nickname = "NoGameId" });
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Requests_without_the_session_token_are_404()
    {
        var client = NewClient();
        var start = await (await client.PostAsync("/api/v1/games", null)).Content.ReadFromJsonAsync<StartDto>();
        // no X-Game-Token header
        (await client.GetAsync($"/api/v1/games/{start!.GameId}/round")).StatusCode
            .Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Bad_allocation_total_is_rejected_with_422()
    {
        var client = NewClient();
        var start = await (await client.PostAsync("/api/v1/games", null)).Content.ReadFromJsonAsync<StartDto>();
        Auth(client, start!.GameToken);
        await client.GetAsync($"/api/v1/games/{start.GameId}/round");
        (await client.PostAsJsonAsync($"/api/v1/games/{start.GameId}/rounds/1/submit",
            new[] { new { symbol = "GOLD", weight = 30 } })).StatusCode
            .Should().Be(HttpStatusCode.UnprocessableEntity);
    }

    [Fact]
    public async Task Concurrent_submissions_for_the_same_round_do_not_double_score_or_corrupt_the_result()
    {
        var client = NewClient();
        var start = await (await client.PostAsync("/api/v1/games", null)).Content.ReadFromJsonAsync<StartDto>();
        Auth(client, start!.GameToken);
        await client.GetAsync($"/api/v1/games/{start.GameId}/round");

        async Task<(HttpStatusCode Status, RoundDto? Body)> SubmitOnce()
        {
            var c = fx.Factory.CreateClient();
            c.DefaultRequestHeaders.Add("X-Game-Token", start.GameToken);
            var r = await c.PostAsJsonAsync($"/api/v1/games/{start.GameId}/rounds/1/submit", Allocation);
            var body = r.IsSuccessStatusCode ? await r.Content.ReadFromJsonAsync<RoundDto>() : null;
            return (r.StatusCode, body);
        }

        var results = await Task.WhenAll(SubmitOnce(), SubmitOnce(), SubmitOnce());

        // Whichever request(s) actually write, the round is scored exactly once: every successful
        // response reports that same score (a request that lands after the round is already locked
        // returns that result instead of erroring - it never re-scores), and a request that instead
        // loses a true DB-level write race gets a clean 409, never a corrupted or duplicated result.
        var succeeded = results.Where(r => r.Status == HttpStatusCode.OK).ToList();
        succeeded.Should().NotBeEmpty();
        succeeded.Select(r => r.Body!.Score).Distinct().Should().ContainSingle();

        results.Select(r => r.Status).Should().OnlyContain(s => s == HttpStatusCode.OK || s == HttpStatusCode.Conflict);
    }

    private sealed record StartDto(Guid GameId, string GameToken);
    private sealed record ResultDto(string Status, int? FinalScore, string? AiCommentary, string? AiModel);
    private sealed record RoundDto(int Score);
    private sealed record RowDto(string Nickname, int Score);
}
