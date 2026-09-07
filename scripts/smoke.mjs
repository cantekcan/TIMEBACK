const B = process.env.API ?? "http://localhost:5080/api/v1";
const j = async (r) => { const t = await r.text(); try { return JSON.parse(t); } catch { return t; } };

const start = await j(await fetch(`${B}/games`, { method: "POST" }));
const gid = start.gameId, token = start.gameToken;
console.log("START:", gid, "token:", token?.slice(0, 12) + "…");
if (!gid || !token) process.exit(1);
const H = { "content-type": "application/json", "x-game-token": token };

const alloc = [
  { symbol: "GOLD", weight: 40 }, { symbol: "BTC", weight: 20 },
  { symbol: "BIST100", weight: 20 }, { symbol: "SP500", weight: 20 },
];

for (let n = 1; n <= 3; n++) {
  const round = await j(await fetch(`${B}/games/${gid}/round`, { headers: H }));
  const res = await j(await fetch(`${B}/games/${gid}/rounds/${n}/submit`, {
    method: "POST", headers: H, body: JSON.stringify(alloc),
  }));
  console.log(`R${n}: entry=${round.effectiveMarketDate} val=${round.assets ? "" : ""}${res.number ? "" : ""} score=${res.score} final=${Math.round(res.finalValue)} nominal=${(res.nominalReturnFraction*100).toFixed(0)}% real=${(res.realReturnFraction*100).toFixed(0)}% best=${res.bestPossibleSymbol} missed=${Math.round(res.missedGain)}`);
}

const result = await j(await fetch(`${B}/games/${gid}/result`, { headers: H }));
console.log("FINAL SCORE:", result.finalScore, "/", result.maxScore, " winners meaningful?");
console.log("AI:", result.aiCommentary);

const save = await j(await fetch(`${B}/leaderboard`, {
  method: "POST", headers: H, body: JSON.stringify({ gameId: gid, nickname: "TimeLord" }),
}));
console.log("SAVE:", JSON.stringify(save));

// negative: no token
const noTok = await fetch(`${B}/games/${gid}/round`);
console.log("NO-TOKEN ->", noTok.status);
// negative: bad total
const bad = await j(await fetch(`${B}/games/${gid}/rounds/1/submit`, {
  method: "POST", headers: H, body: JSON.stringify([{ symbol: "GOLD", weight: 50 }]),
}));
console.log("BAD TOTAL ->", bad.status, bad.detail);
