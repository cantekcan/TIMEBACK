import { describe, expect, it } from "vitest";
import { clampWeight, evenSplit, rebalance, totalOf } from "./allocation";

const SYMBOLS = ["GOLD", "BIST100", "BTC", "SP500"];

describe("rebalance", () => {
  it("always keeps the total at exactly 100", () => {
    let w = evenSplit(SYMBOLS);
    for (const [sym, val] of [["GOLD", 73], ["BTC", 50], ["SP500", 0], ["BIST100", 12]] as const) {
      w = rebalance(w, sym, val);
      expect(totalOf(w)).toBe(100);
      expect(Object.values(w).every((n) => Number.isInteger(n) && n >= 0 && n <= 100)).toBe(true);
      expect(w[sym]).toBe(val);
    }
  });

  it("distributes proportionally to previous weights", () => {
    const w = rebalance({ A: 20, B: 40, C: 40 }, "A", 40);
    expect(w.A).toBe(40);
    expect(w.B).toBe(w.C); // B and C were equal -> stay equal
    expect(w.B + w.C).toBe(60);
  });

  it("splits evenly when the other weights were all zero", () => {
    const w = rebalance({ A: 100, B: 0, C: 0 }, "A", 40);
    expect(w).toEqual({ A: 40, B: 30, C: 30 });
  });

  it("handles a single asset", () => {
    expect(rebalance({ A: 50 }, "A", 10)).toEqual({ A: 100 });
  });

  it("clamps out-of-range input", () => {
    expect(clampWeight(-5)).toBe(0);
    expect(clampWeight(140)).toBe(100);
    expect(totalOf(rebalance(evenSplit(SYMBOLS), "GOLD", 999))).toBe(100);
  });

  it("evenSplit sums to 100 for any asset count", () => {
    for (let n = 1; n <= 8; n++) {
      expect(totalOf(evenSplit(Array.from({ length: n }, (_, i) => `A${i}`)))).toBe(100);
    }
  });
});
