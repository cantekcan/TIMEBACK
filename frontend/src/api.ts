const API_ORIGIN = import.meta.env.VITE_API_BASE_URL ?? "http://localhost:5080";
const BASE = API_ORIGIN + "/api/v1";

export interface AssetView { symbol: string; displayName: string; assetClass: string; }
export interface RoundView {
  number: number; totalRounds: number; requestedDate: string;
  startingCapital: number; startedAtUtc: string | null;
  selectionWindowSeconds: number; holdingPeriodYears: number; assets: AssetView[];
}
export interface StartGameResponse { gameId: string; gameToken: string; currentRound: RoundView; }
export interface AssetResultView {
  symbol: string; growthFactor: number; returnFraction: number;
}
export interface RoundResultView {
  number: number; autoLocked: boolean; startingCapital: number; finalValue: number;
  nominalReturnFraction: number; realReturnFraction: number; inflationFraction: number;
  bestPossibleValue: number; bestPossibleSymbol: string; worstPossibleValue: number;
  missedGain: number; score: number;
  assets: AssetResultView[];
}
export interface GameResultView {
  gameId: string; status: string; finalScore: number | null; maxScore: number;
  rounds: RoundResultView[]; aiCommentary: string | null; aiModel: string | null; onLeaderboard: boolean;
}
export interface LeaderboardRow { rank: number; nickname: string; score: number; createdAtUtc: string; }

/** The game session token is a bearer capability for one game; kept only in memory. */
let sessionToken: string | null = null;
export const setSessionToken = (t: string | null) => { sessionToken = t; };

async function req<T>(path: string, init?: RequestInit): Promise<T> {
  const headers: Record<string, string> = { "content-type": "application/json", ...(init?.headers as Record<string, string>) };
  if (sessionToken) headers["X-Game-Token"] = sessionToken;

  let res: Response;
  try {
    res = await fetch(BASE + path, { ...init, headers });
  } catch {
    throw new ApiError("Sunucuya ulaşılamadı. Bağlantını kontrol et.", 0);
  }
  if (!res.ok) {
    const body = await res.json().catch(() => ({} as Record<string, string>));
    throw new ApiError(body.detail ?? body.title ?? `İstek başarısız (${res.status})`, res.status);
  }
  return res.status === 204 ? (undefined as T) : res.json();
}

export class ApiError extends Error {
  constructor(message: string, public readonly status: number) { super(message); }
}

/** Fire-and-forget ping to wake up a sleeping Render free-tier instance as early as possible - no
 *  auth header, no retry, and its outcome is never checked. Not a guarantee against cold starts,
 *  just a head start: the request lands the moment the app mounts, before the player has clicked
 *  anything, instead of only starting when they hit "Oyuna Başla". */
export const warmUpBackend = () => { void fetch(API_ORIGIN + "/health").catch(() => {}); };

export const api = {
  startGame: () => req<StartGameResponse>("/games", { method: "POST" }),
  currentRound: (id: string) => req<RoundView>(`/games/${id}/round`),
  submit: (id: string, round: number, allocations: { symbol: string; weight: number }[]) =>
    req<RoundResultView>(`/games/${id}/rounds/${round}/submit`, { method: "POST", body: JSON.stringify(allocations) }),
  result: (id: string) => req<GameResultView>(`/games/${id}/result`),
  leaderboard: (count = 10) => req<LeaderboardRow[]>(`/leaderboard?count=${count}`),
  saveScore: (gameId: string, nickname: string) =>
    req<{ score: number }>("/leaderboard", { method: "POST", body: JSON.stringify({ gameId, nickname }) }),
};

const tl = new Intl.NumberFormat("tr-TR", { style: "currency", currency: "TRY", maximumFractionDigits: 0 });
export const fmtTRY = (n: number) => tl.format(Math.round(n));
export const fmtPct = (frac: number) =>
  `${frac >= 0 ? "+" : "−"}${Math.abs(frac * 100).toLocaleString("tr-TR", { maximumFractionDigits: frac < 0.1 ? 1 : 0 })}%`;
