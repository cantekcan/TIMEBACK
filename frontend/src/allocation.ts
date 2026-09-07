export type Weights = Record<string, number>;

export const clampWeight = (n: number) => Math.max(0, Math.min(100, Math.round(n)));

/**
 * Deterministic rebalance: when the user sets one slider to `value`, the remaining `100 - value` is
 * distributed across the other assets in proportion to their previous weights, using the
 * largest-remainder method so the result is always integers summing to exactly 100.
 * No floating-point drift is ever shown to the user.
 */
export function rebalance(weights: Weights, changed: string, value: number): Weights {
  const v = clampWeight(value);
  const others = Object.keys(weights).filter((k) => k !== changed);
  if (others.length === 0) return { [changed]: 100 };

  const remaining = 100 - v;
  const prevSum = others.reduce((s, k) => s + weights[k], 0);

  const raw = others.map((k) =>
    prevSum > 0 ? (remaining * weights[k]) / prevSum : remaining / others.length,
  );
  const floors = raw.map(Math.floor);
  let deficit = remaining - floors.reduce((a, b) => a + b, 0);

  // hand the leftover units to the largest fractional parts (stable by index)
  const order = raw
    .map((r, i) => ({ i, frac: r - Math.floor(r) }))
    .sort((a, b) => b.frac - a.frac || a.i - b.i);

  const result: Weights = { [changed]: v };
  others.forEach((k, i) => (result[k] = floors[i]));
  for (const { i } of order) {
    if (deficit <= 0) break;
    result[others[i]] += 1;
    deficit -= 1;
  }
  return result;
}

export const totalOf = (w: Weights) => Object.values(w).reduce((a, b) => a + b, 0);

export function evenSplit(symbols: string[]): Weights {
  const base = Math.floor(100 / symbols.length);
  const w: Weights = {};
  symbols.forEach((s, i) => (w[s] = i === 0 ? 100 - base * (symbols.length - 1) : base));
  return w;
}
