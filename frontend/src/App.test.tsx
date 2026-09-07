/** @vitest-environment jsdom */
import { afterEach, describe, expect, it, vi } from "vitest";
import { cleanup, render, screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { App } from "./App";
import { api } from "./api";

afterEach(() => {
  cleanup();
  vi.restoreAllMocks();
});

describe("App navigation", () => {
  it("goes from Landing to the Leaderboard and back to Landing via the back button", async () => {
    // No real network call: the leaderboard fetch is mocked, never hits the actual API.
    vi.spyOn(api, "leaderboard").mockResolvedValue([]);
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
