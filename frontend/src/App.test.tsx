/** @vitest-environment jsdom */
import { afterEach, describe, expect, it, vi } from "vitest";
import { cleanup, render, screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { App } from "./App";
import { api, type RoundResultView, type RoundView, type StartGameResponse } from "./api";

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
  const round: RoundView = {
    number: 1, totalRounds: 3, requestedDate: "2020-01-01",
    startingCapital: 100_000, startedAtUtc: null,
    // A short real window (not the production 15s) so the timeout test doesn't need to wait that
    // long - selectionWindowSeconds is just a prop RoundScreen reads, never hardcoded on the client.
    selectionWindowSeconds: 1, holdingPeriodYears: 2,
    assets: [
      { symbol: "GOLD", displayName: "Altın", assetClass: "Commodity" },
      { symbol: "BTC", displayName: "Bitcoin", assetClass: "Crypto" },
    ],
  };

  const autoLockedResult: RoundResultView = {
    number: 1, autoLocked: true, startingCapital: 100_000, finalValue: 100_000,
    nominalReturnFraction: 0, realReturnFraction: -0.02, inflationFraction: 0.02,
    bestPossibleValue: 120_000, bestPossibleSymbol: "BTC", worstPossibleValue: 90_000,
    missedGain: 20_000, score: 0, assets: [],
  };

  const startGame = (): Promise<StartGameResponse> =>
    Promise.resolve({ gameId: "g1", gameToken: "tok", currentRound: round });

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
      ...autoLockedResult, autoLocked: false, score: 500,
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
});
