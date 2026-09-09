/** @vitest-environment jsdom */
import { afterEach, describe, expect, it, vi } from "vitest";
import { cleanup, render, screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { App } from "./App";
import { api, ApiError, type RoundResultView, type RoundView, type StartGameResponse } from "./api";

afterEach(() => {
  cleanup();
  vi.restoreAllMocks();
  vi.unstubAllGlobals();
});

describe("App navigation", () => {
  it("goes from Landing to the Leaderboard and back to Landing via the back button", async () => {
    // No real network call: the leaderboard fetch is mocked, never hits the actual API.
    vi.spyOn(api, "leaderboard").mockResolvedValue([]);
    // App also fires a background warm-up ping on mount now (see below) - stub it so this test
    // never makes a real network call either.
    vi.stubGlobal("fetch", vi.fn().mockResolvedValue(new Response(null, { status: 200 })));
    const user = userEvent.setup();

    render(<App />);
    expect(screen.getByRole("button", { name: "OYUNA BAŞLA" })).toBeTruthy();

    await user.click(screen.getByRole("button", { name: /Leaderboard/ }));

    expect(await screen.findByText("🏆 Top 10")).toBeTruthy();
    expect(api.leaderboard).toHaveBeenCalledWith(10);

    // This is the exact behavior the earlier dead ternary claimed to distinguish on ("from") but
    // never actually did - both branches always went to landing. Clicking the real back button
    // and asserting Landing re-renders is what covers that line honestly.
    await user.click(screen.getByRole("button", { name: "ANA SAYFA" }));

    expect(screen.getByRole("button", { name: "OYUNA BAŞLA" })).toBeTruthy();
  });
});

describe("Backend warm-up", () => {
  it("pings /health once on mount without blocking or affecting Landing", async () => {
    const fetchMock = vi.fn().mockResolvedValue(new Response(null, { status: 200 }));
    vi.stubGlobal("fetch", fetchMock);

    render(<App />);

    // Landing renders immediately - the warm-up ping never gates the UI.
    expect(screen.getByRole("button", { name: "OYUNA BAŞLA" })).toBeTruthy();
    expect(fetchMock).toHaveBeenCalledTimes(1);
    expect(fetchMock).toHaveBeenCalledWith("http://localhost:5080/health");
  });

  it("never surfaces an error to the player when the warm-up ping fails", async () => {
    vi.stubGlobal("fetch", vi.fn().mockRejectedValue(new Error("network down")));

    render(<App />);

    // Give the rejected promise's microtask a turn, then confirm Landing is still fine and no
    // error banner appeared - a failed warm-up is invisible to the player, never retried.
    await new Promise((r) => setTimeout(r, 0));
    expect(screen.getByRole("button", { name: "OYUNA BAŞLA" })).toBeTruthy();
    expect(screen.queryByText(/hata|başarısız/i)).toBeNull();
  });
});

describe("Round timer and locking", () => {
  // A fresh `startedAtUtc` (taken at the moment the round actually begins in each test) - the retry
  // window is now computed off this, mirroring how the server always has it set (StartGameHandler
  // and GetCurrentRoundHandler both begin the round before returning it), so a stale, file-load-time
  // timestamp would make the deadline-based retry bail out immediately in later tests.
  const makeRound = (): RoundView => ({
    number: 1, totalRounds: 3, requestedDate: "2020-01-01",
    startingCapital: 100_000, startedAtUtc: new Date().toISOString(),
    // A short real window (not the production 15s) so the timeout test doesn't need to wait that
    // long - selectionWindowSeconds is just a prop RoundScreen reads, never hardcoded on the client.
    selectionWindowSeconds: 1, holdingPeriodYears: 2,
    assets: [
      { symbol: "GOLD", displayName: "Altın", assetClass: "Commodity" },
      { symbol: "BTC", displayName: "Bitcoin", assetClass: "Crypto" },
    ],
  });

  const autoLockedResult: RoundResultView = {
    number: 1, autoLocked: true, startingCapital: 100_000, finalValue: 100_000,
    nominalReturnFraction: 0, realReturnFraction: -0.02, inflationFraction: 0.02,
    bestPossibleValue: 120_000, bestPossibleSymbol: "BTC", worstPossibleValue: 90_000,
    missedGain: 20_000, score: 0, investmentScore: 0, timeBonus: 0, assets: [],
  };

  const startGame = (): Promise<StartGameResponse> =>
    Promise.resolve({ gameId: "g1", gameToken: "tok", currentRound: makeRound() });

  it("never sends the player's unlocked slider selection when the timer runs out", async () => {
    vi.spyOn(api, "startGame").mockImplementation(startGame);
    const submitSpy = vi.spyOn(api, "submit").mockResolvedValue(autoLockedResult);
    vi.stubGlobal("fetch", vi.fn().mockResolvedValue(new Response(null, { status: 200 })));

    render(<App />);
    await userEvent.setup().click(screen.getByRole("button", { name: "OYUNA BAŞLA" }));
    await screen.findByText("YATIRIMI KİLİTLE");

    // Never click "Kilitle" - just let the 1-second window run out on its own.
    await screen.findByText("Süre doldu, yatırım kaydedilmedi.", {}, { timeout: 3000 });

    // The auto-timeout must submit an empty allocation - never the sliders' (untouched, still
    // evenly-split) live values - so nothing the player merely selected but never locked in is
    // ever recorded as an investment.
    expect(submitSpy).toHaveBeenCalledWith("g1", 1, []);
  }, 8000);

  it("sends the player's actual selection when they press Kilitle before time runs out", async () => {
    vi.spyOn(api, "startGame").mockImplementation(startGame);
    const submitSpy = vi.spyOn(api, "submit").mockResolvedValue({
      ...autoLockedResult, autoLocked: false, score: 500, investmentScore: 350, timeBonus: 150,
      assets: [
        { symbol: "GOLD", growthFactor: 1.1, returnFraction: 0.1 },
        { symbol: "BTC", growthFactor: 1.2, returnFraction: 0.2 },
      ],
    });
    vi.stubGlobal("fetch", vi.fn().mockResolvedValue(new Response(null, { status: 200 })));

    render(<App />);
    await userEvent.setup().click(screen.getByRole("button", { name: "OYUNA BAŞLA" }));
    await screen.findByText("YATIRIMI KİLİTLE");
    await userEvent.setup().click(screen.getByRole("button", { name: /KİLİTLE/ }));

    await screen.findByText("Paran ne oldu?");

    // A real, on-time click sends the player's actual (evenly-split default) selection - summing
    // to 100, never an empty array.
    expect(submitSpy).toHaveBeenCalledTimes(1);
    const [, , allocations] = submitSpy.mock.calls[0];
    expect(allocations.reduce((sum, a) => sum + a.weight, 0)).toBe(100);
    expect(allocations.length).toBeGreaterThan(0);
  });

  it("retries a timed-out empty submission a bounded number of times without re-creating the countdown effect", async () => {
    // The server's own deadline sits slightly after the client's, so the first (and sometimes
    // second) empty submission the client fires right at 0s can legitimately race a round that's
    // still open server-side (422) before the same request finally succeeds once the server's own
    // deadline has also passed - see RoundScreen's submitTimeout.
    vi.spyOn(api, "startGame").mockImplementation(startGame);
    const submitSpy = vi.spyOn(api, "submit")
      .mockRejectedValueOnce(new ApiError("Allocation must contain at least one asset.", 422))
      .mockRejectedValueOnce(new ApiError("Allocation must contain at least one asset.", 422))
      .mockResolvedValueOnce(autoLockedResult);
    vi.stubGlobal("fetch", vi.fn().mockResolvedValue(new Response(null, { status: 200 })));

    render(<App />);
    await userEvent.setup().click(screen.getByRole("button", { name: "OYUNA BAŞLA" }));
    await screen.findByText("YATIRIMI KİLİTLE");

    await screen.findByText("Süre doldu, yatırım kaydedilmedi.", {}, { timeout: 5000 });

    // Exactly the two rejected attempts plus the one that finally succeeds - bounded, never an
    // unbounded retry storm - and every attempt carried an empty allocation, never the (untouched,
    // still evenly-split) live slider values. The retry loop lives entirely inside submitTimeout's
    // own `for` loop (not in the countdown effect re-mounting on every App re-render), so this
    // count is exactly what submitTimeout's own bound would produce - proof the old
    // effect-recreation-driven retry storm is gone.
    expect(submitSpy).toHaveBeenCalledTimes(3);
    submitSpy.mock.calls.forEach(([, , allocations]) => expect(allocations).toEqual([]));

    // The two expected mid-retry 422s are not surfaced as application errors - only a final,
    // real failure would ever show the ⚠ toast.
    expect(screen.queryByText(/⚠/)).toBeNull();
  }, 8000);

  it("never retries a manual Kilitle submission - a failure surfaces immediately as a real error", async () => {
    vi.spyOn(api, "startGame").mockImplementation(startGame);
    const submitSpy = vi.spyOn(api, "submit").mockRejectedValue(new ApiError("Sunucu hatası.", 500));
    vi.stubGlobal("fetch", vi.fn().mockResolvedValue(new Response(null, { status: 200 })));

    render(<App />);
    await userEvent.setup().click(screen.getByRole("button", { name: "OYUNA BAŞLA" }));
    await screen.findByText("YATIRIMI KİLİTLE");
    await userEvent.setup().click(screen.getByRole("button", { name: /KİLİTLE/ }));

    await screen.findByText(/Sunucu hatası\./);

    // A manual submission is never retried, unlike the timeout path above.
    expect(submitSpy).toHaveBeenCalledTimes(1);
  });

  it("keeps the lock button disabled for the whole timeout-retry window, so no concurrent duplicate request is possible", async () => {
    vi.spyOn(api, "startGame").mockImplementation(startGame);
    let resolveSubmit: ((v: RoundResultView) => void) | undefined;
    const submitSpy = vi.spyOn(api, "submit").mockImplementation(
      () => new Promise<RoundResultView>((resolve) => { resolveSubmit = resolve; }),
    );
    vi.stubGlobal("fetch", vi.fn().mockResolvedValue(new Response(null, { status: 200 })));

    render(<App />);
    await userEvent.setup().click(screen.getByRole("button", { name: "OYUNA BAŞLA" }));
    await screen.findByText("YATIRIMI KİLİTLE");

    // Deadline passes, the timeout submission starts and never resolves yet.
    await screen.findByText("KİLİTLENİYOR…", {}, { timeout: 3000 });
    expect((screen.getByRole("button", { name: "KİLİTLENİYOR…" }) as HTMLButtonElement).disabled).toBe(true);
    // `locking.current` blocks any second concurrent attempt for as long as the first is in flight.
    expect(submitSpy).toHaveBeenCalledTimes(1);

    resolveSubmit?.(autoLockedResult);
    await screen.findByText("Süre doldu, yatırım kaydedilmedi.");
  }, 8000);

  it("gives up once the server's own deadline has passed and surfaces a real (player-friendly) error instead of retrying forever", async () => {
    vi.spyOn(api, "startGame").mockImplementation(startGame);
    const submitSpy = vi.spyOn(api, "submit")
      .mockRejectedValue(new ApiError("Allocation must contain at least one asset.", 422));
    vi.stubGlobal("fetch", vi.fn().mockResolvedValue(new Response(null, { status: 200 })));

    render(<App />);
    await userEvent.setup().click(screen.getByRole("button", { name: "OYUNA BAŞLA" }));
    await screen.findByText("YATIRIMI KİLİTLE");

    // The bound is now the server's real deadline (startedAtUtc + window + NetworkGrace + safety
    // margin), not a blind attempt count - for this test's 1s window that's a few seconds of
    // retrying before giving up.
    await screen.findByText(/Süre doldu, tur kilitlenemedi/, {}, { timeout: 8000 });

    // Retried more than once (it's a real retry loop) but never unbounded - well under the hard
    // attempt backstop, and the raw domain validation message ("Allocation must contain at least
    // one asset.") is never shown to the player.
    expect(submitSpy.mock.calls.length).toBeGreaterThan(1);
    expect(submitSpy.mock.calls.length).toBeLessThanOrEqual(25);
    expect(screen.queryByText(/Allocation must contain/)).toBeNull();
  }, 12000);

  it("keeps the countdown effect stable across an unrelated App re-render (a manual submit error), never spawning duplicate timeout retries", async () => {
    // The one thing that can legitimately trigger an App-level re-render mid-round (other than the
    // round itself resolving) is a failed manual submit, via onError -> setError. onLocked being
    // stable means this must never tear down and re-mount the countdown effect.
    vi.spyOn(api, "startGame").mockImplementation(startGame);
    const submitSpy = vi.spyOn(api, "submit")
      .mockRejectedValueOnce(new ApiError("Sunucu hatası.", 500))
      .mockResolvedValueOnce(autoLockedResult);
    vi.stubGlobal("fetch", vi.fn().mockResolvedValue(new Response(null, { status: 200 })));

    render(<App />);
    await userEvent.setup().click(screen.getByRole("button", { name: "OYUNA BAŞLA" }));
    await screen.findByText("YATIRIMI KİLİTLE");
    await userEvent.setup().click(screen.getByRole("button", { name: /KİLİTLE/ }));
    await screen.findByText(/Sunucu hatası\./);

    // Let the toast and the round's own 1s window both elapse - the round must still resolve via
    // exactly one more (timeout) submit call, not a storm caused by the App re-render above tearing
    // down and re-mounting the countdown effect.
    await screen.findByText("Süre doldu, yatırım kaydedilmedi.", {}, { timeout: 6000 });

    expect(submitSpy).toHaveBeenCalledTimes(2); // the one failed manual click + the one timeout submit
    const [, , timeoutAllocation] = submitSpy.mock.calls[1];
    expect(timeoutAllocation).toEqual([]);
  }, 10000);
});
