/** @vitest-environment jsdom */
import { afterEach, describe, expect, it, vi } from "vitest";
import { cleanup, render, screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { App } from "./App";
import { api } from "./api";

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
