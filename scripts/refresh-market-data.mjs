// Regenerates src/Timeback.Infrastructure/SeedData/*.csv from Yahoo Finance + FRED.
// Real data only. Run: node scripts/refresh-market-data.mjs
import { writeFileSync } from "node:fs";
import { fileURLToPath } from "node:url";
import { dirname, join } from "node:path";

const OUT = join(dirname(fileURLToPath(import.meta.url)), "..", "src", "Timeback.Infrastructure", "SeedData");
const FROM = Date.UTC(2015, 0, 1) / 1000;
const TO = Math.floor(Date.now() / 1000);

const YAHOO = { "GC=F": "GC=F", "^GSPC": "%5EGSPC", "BTC-USD": "BTC-USD", "TRY=X": "TRY=X", "XU100.IS": "XU100.IS" };

async function yahoo(name, q) {
  const r = await fetch(
    `https://query1.finance.yahoo.com/v8/finance/chart/${q}?period1=${FROM}&period2=${TO}&interval=1mo`,
    { headers: { "User-Agent": "Mozilla/5.0 (Timeback refresh)" } },
  );
  if (!r.ok) throw new Error(`${name}: HTTP ${r.status}`);
  const res = (await r.json()).chart.result[0];
  const ts = res.timestamp ?? [];
  const close = res.indicators.quote[0].close ?? [];
  const seen = new Set();
  const rows = [];
  ts.forEach((t, i) => {
    if (close[i] == null) return;
    const d = new Date(t * 1000);
    const key = `${d.getUTCFullYear()}-${String(d.getUTCMonth() + 1).padStart(2, "0")}-01`;
    if (seen.has(key)) return;
    seen.add(key);
    rows.push(`${key},${close[i].toFixed(6)}`);
  });
  writeFileSync(join(OUT, `${name.replace("^", "_")}.csv`), "date,close\n" + rows.join("\n") + "\n");
  console.log(`${name}: ${rows.length} months`);
}

async function fredCpi() {
  const r = await fetch(
    "https://fred.stlouisfed.org/graph/fredgraph.csv?id=TURCPIALLMINMEI&cosd=2014-06-01&coed=" +
      new Date().toISOString().slice(0, 10),
  );
  if (!r.ok) throw new Error(`CPI: HTTP ${r.status}`);
  const rows = (await r.text())
    .trim().split("\n").slice(1)
    .map((l) => l.split(","))
    .filter((p) => p[1] && p[1] !== ".")
    .map((p) => `${p[0].slice(0, 10)},${(+p[1]).toFixed(6)}`);
  writeFileSync(join(OUT, "TUR_CPI.csv"), "date,index\n" + rows.join("\n") + "\n");
  console.log(`CPI: ${rows.length} months`);
}

for (const [name, q] of Object.entries(YAHOO)) await yahoo(name, q);
await fredCpi();
console.log("Done. Review the diff, then re-seed / run `dotnet run --project src/Timeback.Api -- ingest`.");
