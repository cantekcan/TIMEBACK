# TIMEBACK

> *"Geçmişe dönseydin paranı nereye yatırırdın?"*

A server-authoritative historical investment game. You're dropped on a random date in the past with
**100.000 TL** and **10 seconds** to split it across Gold, BIST 100, Bitcoin and S&P 500.
Three rounds later you learn what your money would be worth — nominally **and** after inflation — how
close you got to the best possible play, and you get a short, savage AI roast of your worst call.

Portfolio piece demonstrating a pragmatic Clean Architecture, DDD where it earns its keep, a real
market-data ingestion pipeline, deterministic domain math, optimistic concurrency, and a
cheat-resistant game server.

---

## Contents

- [Gameplay](#gameplay) · [Architecture](#architecture) · [Tech stack & why](#tech-stack--why)
- [Domain model](#domain-model) · [Scoring](#scoring-methodology) · [Inflation](#inflation-methodology)
- [Round selection](#round-selection) · [Market data](#historical-market-data)
- [Security](#security) · [Persistence & concurrency](#persistence--concurrency) · [AI](#ai-commentator)
- [API](#api) · [Local development](#local-development) · [Docker](#docker) · [Testing](#testing)
- [CI pipeline](#ci-pipeline) · [Limitations](#known-limitations)

## Gameplay

1. `POST /games` → creates a game, returns round 1 **and a one-time `gameToken`**. Every later call
   for this game must send `X-Game-Token: <token>`.
2. The server starts round 1's clock: `StartedAtUtc` + 10s window + 2s network grace = `EndsAtUtc`.
   The 10s is what the player sees counting down; the 2s is invisible slack so a submit that's
   in flight when the visible countdown hits zero still lands in time.
3. The client shows a countdown ring (pure UX) and drag-to-allocate sliders. A largest-remainder
   rebalance keeps the split at exactly 100% with no floating-point drift.
4. `POST …/rounds/{n}/submit` sends the allocation. The server re-validates **everything** (weights,
   total, duplicates, supported assets, deadline, token) and scores the round from historical prices.
5. Miss the deadline → the next request that touches the game auto-locks the round (all-cash, score 0).
6. After the final round: game completes, final score = Σ round scores, `GET …/result` generates the
   AI comment once and returns the full breakdown.
7. `POST /leaderboard` with `X-Game-Token` + a nickname copies the **server-computed** score to the board.

## Architecture

Clean Architecture monolith, dependencies point inward, `Domain` references nothing.

```text
┌─────────────────────────────────────────────────────────────────────┐
│  Timeback.Api      controllers · RFC 7807 · CORS · Serilog           │
│                    per-IP + per-mutation rate limiting · OpenAPI     │
│                    `dotnet run -- ingest` CLI entrypoint             │
└───────────────┬─────────────────────────────────────────────────────┘
                │ direct calls: controller → handler class (no mediator)
┌───────────────▼─────────────────────────────────────────────────────┐
│  Timeback.Application                                                │
│    one FOLDER per use case, Command/Query + Handler in separate      │
│    files, auth check inline, FluentValidation called inline:         │
│    Games/StartGame, GetCurrentRound, SubmitAllocation, GetGameResult │
│    Leaderboard/SaveLeaderboard, GetLeaderboard                       │
│    shared: GamePlayService (deadline enforcement, used by 3 use cases)│
│    abstractions: IGameRepository · IMarketDataStore · IInflationStore│
│      IMarketDataProvider · IAiCommentator · IGameTokenFactory · IClock│
└───────────────┬─────────────────────────────────────────────────────┘
                │ implements
┌───────────────▼──────────────────────────┐   ┌─────────────────────┐
│  Timeback.Infrastructure                 │   │  Timeback.Domain     │
│   EF Core 10 + PostgreSQL (+ migration)  │──▶│  Game (aggregate)    │
│   xmin optimistic concurrency            │   │  Round · RoundResult │
│   IMemoryCache market/inflation stores   │   │  Asset · DailyPrice  │
│   ── ingestion ──────────────────────    │   │  InflationIndex      │
│   IMarketDataProvider:                   │   │  Money · Percentage  │
│     EmbeddedCsvMarketDataProvider (dflt) │   │  AllocationSet       │
│     YahooFinanceMarketDataProvider (live)│   │  PortfolioCalculator │
│   FredInflationProvider / CSV            │   │  RoundScoring (pure)  │
│   MarketDataIngestionService (idempotent)│   └─────────────────────┘
│   OpenRouter (Qwen3, free) + fallback AI │
│   GameTokenFactory (CSPRNG + SHA-256)    │
└──────────────────────────────────────────┘
```

### Market-data pipeline

```text
Yahoo Finance / FRED  ──(scripts/refresh-market-data.mjs)──▶  SeedData/*.csv  (committed, real)
                                                                    │
                          EmbeddedCsvMarketDataProvider  ◀───────────┘   (default, offline)
   OR YahooFinanceMarketDataProvider (dotnet run -- ingest --live via MarketData:Provider=yahoo)
                                                                    │
                                                                    ▼
                     MarketDataIngestionService   normalise → TRY, forward-fill gaps
                                                  idempotent upsert (unique (symbol,date))
                                                                    │
                                                                    ▼
                              PostgreSQL  daily_prices / inflation_indices   ← source of truth
                                                                    │
                          IMemoryCache  ◀─── EfMarketDataStore ─────┘
                                                                    │
                                                                    ▼
                                              Game engine (reads DB only, never a live API)
```

## Tech stack & why

| Choice | Why | Rejected |
|---|---|---|
| .NET 10 / ASP.NET Core | installed SDK; requested | — |
| Clean Architecture, 4 projects + feature folders | keeps scoring core isolated & testable | module assemblies — ceremony at this size |
| Controller calls the use-case handler class directly | one file per use case is enough at 5 use cases; no request/response indirection to trace | MediatR — pipeline/mediator overhead nothing here needs |
| FluentValidation, called inline at the top of `Handle` | declarative request validation, still readable without a pipeline | DataAnnotations |
| EF Core 10 + PostgreSQL | relational data, `decimal` money, migrations; `xmin` concurrency token | — |
| `IMemoryCache` behind the store | historical data is immutable → **zero invalidation logic**; whole dataset fits in memory | Redis — real overengineering for one node |
| Ingestion via CLI + startup seeder | one idempotent path; committed CSVs make it deterministic & offline | Hangfire — one job, not yet scheduled |
| Yahoo Finance + FRED (both keyless) | free, reputable, cover all six series | Stooq (now JS-challenged), paid LBMA/Quandl |
| OpenRouter Qwen3 (free) + local fallback | free; game never blocks on it | paid model |
| React + TS + Vite + Vitest | SPA game, no SSR; fast tests | Next.js |
| xUnit + FluentAssertions + NSubstitute + Testcontainers | matches reference repo; **real Postgres** in integration | EF in-memory provider (hides SQL/mapping bugs) |
| Serilog + built-in OpenAPI + Scalar | structured logs; Swashbuckle not yet ASP.NET Core 10 compatible | — |

## Domain model

| Concept | Kind | Notes |
|---|---|---|
| `Game` | Aggregate root | Owns 5 `Round`s. Sole authority over timing, scoring, completion, session token. |
| `Round` | Entity (in `Game`) | `RequestedDate` / `EffectiveMarketDate` / `ValuationDate` kept distinct. Holds deadline + locked `RoundResult`. |
| `RoundResult` | Owned, **JSON column** | Immutable scored snapshot. |
| `Money` | Value object | `decimal`, non-negative, currency-tagged, banker's rounding. Never `double`. |
| `Percentage` | Value object | Integer 0–100 so slider and server agree exactly. |
| `AllocationSet` | Value object | The single server gate: distinct assets, each 0–100, total exactly 100. |
| `Asset` / `DailyPrice` / `InflationIndex` | Reference / entities | Immutable historical facts, `Source` column for traceability. |
| `LeaderboardEntry` / `Nickname` | Aggregate / VO | One entry per game (unique index); score copied from the game, nickname regex `[A-Za-z0-9_\-.]{3,16}`. |
| `PortfolioCalculator`, `RoundScoring` | Pure static domain services | Deterministic, side-effect free — the core of the test suite. |

## Scoring methodology

Four **separate** concepts (inflation never touches the skill score):

```text
growthFactorᵢ  = priceNowᵢ / priceThenᵢ                    (priceNow = price at the round's ValuationDate)
finalValue     = Σ  capital · weightᵢ · growthFactorᵢ
── nominal ──   nominalReturn = finalValue / capital − 1
── inflation ── inflation     = cpiValuation / cpiEntry − 1
── real ──      realReturn    = (1 + nominalReturn) · cpiEntry / cpiValuation − 1     (Fisher deflation)
── skill score ──
  opportunity set = every single-asset outcome for this round
  worst = capital · min(growthFactor)     best = capital · max(growthFactor)
  skill = clamp01( (finalValue − worst) / (best − worst) )
  roundScore = round( RoundScoring.MaxRoundScore · skill )        MaxRoundScore = 1000
finalScore = Σ roundScore                                          0 … RoundScoring.MaxGameScore (3000)
missedGain = max(0, best − finalValue)
```

`skill` measures **allocation quality against what was achievable that round**, not luck — a sharp
pick in a flat market scores well; a lazy split in a rally scores badly. A forfeited round scores 0.
All knobs are named constants (`RoundScoring.MaxRoundScore`, `TotalRounds`). Tests assert the score
is invariant under inflation and that real return is the correctly-deflated nominal return.

## Inflation methodology

- **Source:** OECD "CPI all items, Turkey", series `TURCPIALLMINMEI` via FRED (monthly, index 2015=100).
  Committed to `SeedData/TUR_CPI.csv`.
- Real return is derived **only** from the ratio of the CPI level at the entry month and the
  valuation month — the game never invents an inflation rate. The base period cancels in the ratio.
- The result screen shows nominal return, the period's inflation, and the real return side by side so
  the player sees the gap.

## Round selection

`RoundDatePlanner` (deterministic given a seed):

- Each round holds for a **short, varied horizon (1–4 years)** — `ValuationDate = EntryDate + horizon`,
  not "check back today". Long "to today" windows let BTC + the lira slide make one asset win almost
  every round; short windows let gold, equities, USD and BTC each genuinely win different periods.
- Candidate windows whose best/worst asset ratio is absurd (> 12×) or whose winner more than 8×'d are
  filtered out — those are "guess the winner or score zero", not strategy. **The returns are still
  100% real; the game just doesn't show you the degenerate windows.**
- The `RoundScoring.TotalRounds` (3) chosen windows maximise the number of distinct winning assets,
  are ≥ 8 months apart, and are sampled from the seed so replays differ.
- Tested: the planned number of rounds, ≥ 3 distinct winning assets on synthetic multi-regime data,
  deterministic per seed.

A balanced, even split across the four assets scores well below the max — clear headroom for skill,
not trivially maxed.

## Historical market data

**All data is real, downloaded, and committed** — nothing is fabricated. It is **monthly** and, for
the game, converted to TRY and gap-filled; it is *not* investment-grade tick data.

| Series | Source | Provider symbol | Range | Freq | Native unit | Transformation to TRY |
|---|---|---|---|---|---|---|
| Gold ("Altın") | Yahoo Finance | `GC=F` (COMEX) | 2015→ | monthly | USD / troy oz | `close / 31.1035 × USDTRY` |
| BIST 100 | Yahoo Finance | `XU100.IS` | 2015→ | monthly | TRY | none |
| Bitcoin | Yahoo Finance | `BTC-USD` | 2015→ | monthly | USD | `close × USDTRY` |
| S&P 500 | Yahoo Finance | `^GSPC` | 2015→ | monthly | USD (index) | `close × USDTRY` |
| USD/TRY *(FX backbone only, not a playable asset)* | Yahoo Finance | `TRY=X` | 2015→ | monthly | TRY per USD | none (holding USD) |
| CPI (TÜFE) | FRED / OECD | `TURCPIALLMINMEI` | 2014→ | monthly | index 2015=100 | none |

**Methodology & limitations**
- **Committed snapshot:** `SeedData/*.csv` are month-end closes fetched by `scripts/refresh-market-data.mjs`.
  `EmbeddedCsvMarketDataProvider` is the default so seeding is deterministic and works offline / in CI.
- **Forward-fill:** a missing month carries the last known value forward (Yahoo occasionally nulls a
  month, esp. `GC=F`). Leading months before an asset's first observation are dropped — never back-filled.
- **FX normalisation:** USD-quoted assets are converted at the same month's USD/TRY close, so a TRY
  investor's return correctly includes the currency move. USD/TRY itself is not offered as something
  the player can allocate into — it is only the conversion rate `MarketDataIngestionService` uses for
  every USD-quoted asset (see its own fetch of `TRY=X`, independent of `AssetCatalog`).
- **"Altın" is a futures-derived price, not a retail gram-gold quote:** `GC=F` is a COMEX gold futures
  contract, divided by troy-ounce grams and converted to TRY — a reasonable per-gram proxy given
  gold's tight spot/futures arbitrage, but not literally a jeweller's gram price. The UI therefore
  says "Altın", not "Gram Altın", to avoid implying more precision than the data actually has.
- **BIST scale:** Yahoo's `XU100.IS` series is internally consistent (post-2020 convention throughout);
  it is not spliced to the pre-2020 quoting.
- **Refresh:** `dotnet run --project src/Timeback.Api -- ingest` (embedded) or set
  `MarketData:Provider=yahoo` and run `-- ingest --live` to pull fresh data straight into Postgres.
- **Provider ToS:** Yahoo Finance and FRED are used read-only for personal/portfolio purposes; no
  redistribution of raw feeds beyond the small committed snapshot needed to run the demo.

## Security

- **Server-authoritative:** deadline, dates, prices, allocation validity, portfolio value, returns,
  inflation, score, leaderboard score. The client sends allocations + a nickname; nothing else.
- **Game session token:** `POST /games` returns a 256-bit CSPRNG `gameToken`, stored only as its
  SHA-256 hash. Every `…/games/{id}/…` call and the leaderboard write require `X-Game-Token`; a
  missing/wrong token and a missing game both return an identical **404** (no id probing). Play stays
  fully anonymous — no login.
- **Deadline:** `EndsAtUtc` set server-side; late submit → `422`; expired rounds auto-lock.
- **Rate limiting:** 120 req/min per IP for gameplay, **15 req/min per IP** for `POST /games` and
  `POST /leaderboard` (the abuse-cheap endpoints). `/health` is exempt.
- **Validation:** FluentValidation on request shape + domain invariants (`AllocationSet`, `Money`, `Percentage`, `Nickname`).
- **Errors:** RFC 7807 ProblemDetails via `IExceptionHandler`; 500s log server-side and return a generic message.
- **Secrets:** `OPENROUTER_API_KEY` + connection string from env/config only; `.env.example` documents them.
- **Leaderboard abuse:** one row per game (unique index), score read from the completed game, nickname regex-constrained, mutation rate-limited.

## Persistence & concurrency

- EF Core 10, PostgreSQL, one migration (`InitialSchema`).
- `Game` loads with `Include(g => g.Rounds).ThenInclude(r => r.Allocations)` + `AsSplitQuery()` —
  **strongly typed**, no string navigation. Read queries use `AsNoTracking()`.
- `RoundResult` (+ its asset lines) is a single JSON column via `OwnsOne(...).ToJson()`.
- **Optimistic concurrency:** `Game` maps PostgreSQL's `xmin` system column as a concurrency token.
  Two submissions racing the same game → the loser gets `DbUpdateConcurrencyException`, translated to
  a `409`. Integration test fires 3 parallel submits at one round and asserts exactly 1 succeeds.
- Indexes: `daily_prices (symbol,date)` unique (ingestion idempotency) + `(date)`;
  `leaderboard_entries (gameId)` unique + `(score, createdAtUtc)`; `rounds (gameId, number)` unique;
  `games (gameTokenHash)` unique.

## AI commentator

- `IAiCommentator`. `OpenRouterAiCommentator` calls OpenRouter's free `minimax/minimax-m3:free` model
  directly (OpenAI-compatible Chat Completions API) — **not** the `openrouter/free` auto-router, which
  was tried and dropped: it sometimes landed on non-conversational free models (a content-safety
  classifier, a code assistant) that returned nonsense instead of a comment. `FallbackAiCommentator` is
  a deterministic local generator, used whenever MiniMax is unavailable or its own answer doesn't look
  like a real comment. Never falls back to a paid model.
- The backend hands the AI a `GameSummaryForAi` — final score, round scores, strongest/weakest round
  (with the actual pick), average allocation per asset, total missed gain. **Nothing else.** The
  prompt forbids calculation, invented facts, and financial advice, and casts the AI as a "Zaman
  Yorumcusu" (Time Commentator) that picks the single most notable event of the playthrough (priority:
  any zero-score round, else the lowest-scoring round, else a huge missed opportunity, else a sharp
  best/worst contrast, else — grudgingly — praise) and roasts only that, in 1-2 sentences, ≤ 220
  characters, using at most one number.
- **The model shown to the player is the response's own `model` field** (falling back to the
  configured `minimax/minimax-m3:free` if that field is ever missing), formatted into a short display
  name (`aiModelDisplayName` on the frontend, e.g. "MiniMax M3"). A small, low-contrast disclosure line
  under the AI comment always names the real model, or falls back to a generic "🤖 Yapay zekâ
  tarafından oluşturuldu" when the local fallback commentator (not MiniMax) produced the line.
- **Response sanity check:** before a MiniMax reply is trusted, it is checked against the shapes a
  broken/off-topic answer takes — a moderation verdict ("safe"/"unsafe", "content safety", …), JSON,
  Markdown, code, a model self-description, empty, or excessively long — and rejected to the fallback
  if it matches (`LooksLikeInvalidComment`). This is what stopped the `openrouter/free` "User Safety:
  safe" failure mode from ever reaching a player again.
- Every failure path (no key, timeout, 401/403/429/5xx, empty response, bad JSON, or a response that
  fails the sanity check) returns the fallback. `CommentAsync` never throws. A long real response is
  trimmed to the last complete sentence within 500 characters rather than cut mid-word. Tests cover:
  fallback shape, no-key path, provider-outage path, successful parsing **and model capture**, model
  fallback when the field is missing, **8 invalid-response shapes → fallback**, an excessively long
  reply → fallback, empty-content fallback, 429 fallback, and that the real request sent
  `minimax/minimax-m3:free` (never `openrouter/free`) plus the player's actual data (never fabricated
  numbers).

## API

| Method | Route | Notes |
|---|---|---|
| `POST` | `/api/v1/games` | returns `gameId` + `gameToken`; rate-limited `mutation` |
| `GET` | `/api/v1/games/{id}/round` | needs `X-Game-Token` |
| `POST` | `/api/v1/games/{id}/rounds/{n}/submit` | needs `X-Game-Token`; body = `[{symbol,weight}]` |
| `GET` | `/api/v1/games/{id}/result` | needs `X-Game-Token`; completed games only |
| `GET` | `/api/v1/leaderboard?count=10` | public |
| `POST` | `/api/v1/leaderboard` | needs `X-Game-Token`; body = `{gameId,nickname}`; rate-limited `mutation` |
| `GET` | `/health` · `/scalar/v1` | health check · API reference UI |

## Local development

**Prerequisites:** .NET 10 SDK, Docker, Node 22.

```bash
docker run -d --name tb-pg -e POSTGRES_USER=timeback -e POSTGRES_PASSWORD=timeback \
  -e POSTGRES_DB=timeback -p 5432:5432 postgres:17-alpine

dotnet run --project src/Timeback.Api        # http://localhost:5080  (migrates + seeds real data on boot)
cd frontend && npm install && npm run dev     # http://localhost:5173

node scripts/smoke.mjs                        # drives a full game over HTTP
```

Ingestion / migrations:

```bash
dotnet run --project src/Timeback.Api -- ingest                 # re-run (idempotent)
MarketData__Provider=yahoo dotnet run --project src/Timeback.Api -- ingest --live   # pull fresh
node scripts/refresh-market-data.mjs                            # regenerate SeedData/*.csv
dotnet ef migrations add <Name> -p src/Timeback.Infrastructure -s src/Timeback.Api -o Persistence/Migrations
```

## Docker

```bash
cp .env.example .env          # optionally set OPENROUTER_API_KEY
docker compose up --build
# api → http://localhost:8080/scalar/v1   ·   frontend → http://localhost:5173   ·   postgres :5432
```

## Environment variables

| Variable | Default | Used by |
|---|---|---|
| `ConnectionStrings__Postgres` | `Host=localhost;Port=5432;Database=timeback;Username=timeback;Password=timeback` | API |
| `Cors__Origins__0` | `http://localhost:5173` | API |
| `Seed__OnStartup` | `true` | API — migrate + seed on boot |
| `MarketData__Provider` | `embedded` | API — `embedded` \| `yahoo` |
| `OpenRouter__ApiKey` / `OPENROUTER_API_KEY` | *(empty → fallback AI)* | API |
| `VITE_API_BASE_URL` | `http://localhost:5080` (compose: `http://localhost:8080`) | frontend build |

## Testing

```bash
dotnet test Timeback.slnx --filter "FullyQualifiedName!~Integration"   # 49 fast tests
dotnet test tests/Timeback.Integration.Tests                            # 10, real Postgres via Testcontainers
cd frontend && npm run test                                             # 10 (rebalance invariants + AI model naming)
```

| Suite | Count | Covers |
|---|---|---|
| Domain | 23 | portfolio valuation, nominal/real return, best/worst & missed-gain, score bounds, **score ⟂ inflation**, allocation rules, game creation, server deadline, full completion & sum, auto-lock = 0, **double submit**, **out-of-order rounds**, **completed game rejects submits**, session-token match |
| Application | 26 | full playthrough + leaderboard eligibility, **wrong token → 404**, **score cannot be forged**, result-before-finish → 409, late submit → auto-lock 0, duplicate submit, round-planner variety + determinism, AI fallback/parsing/model-capture, **8 invalid-response shapes → fallback**, excessively-long-reply → fallback |
| Integration (real PG) | 10 | health, full game over HTTP + leaderboard, no-token → 404, bad allocation → 422, **concurrent submits: exactly 1 wins**, seeding loads real prices/CPI for every asset, **ingestion idempotency**, coverage never fakes a future date, stale forward-fill cleanup |
| Frontend | 10 | rebalance always totals 100 / integers / proportional / even-split / clamping, AI model slug → display name + disclosure text |

## CI pipeline

GitHub Actions (`.github/workflows/ci.yml`) runs on every push/PR to `main` (and on demand via
`workflow_dispatch`): backend restore/build, Domain + Application + Integration tests with Cobertura
coverage, frontend tests with lcov coverage, frontend build, a Docker build-only check for both
images, and a [SonarQube Cloud](https://sonarcloud.io/project/overview?id=cantekcan_TIMEBACK)
analysis (project `cantekcan_TIMEBACK`) gated on its Quality Gate - a failed gate fails the pipeline.
Backend and frontend coverage reports are uploaded as workflow artifacts on every run.

## Known limitations

1. **Monthly, forward-filled data.** Good enough for month-scale decisions; intra-month timing and
   dividends/roll are not modelled. `GC=F` (gold futures) is a proxy for spot.
2. **Yahoo/FRED have no SLA.** The committed CSV snapshot is the safety net; a live refresh can fail.
3. **AI is best-effort.** Fallback keeps the game whole; the fallback lines are a small fixed set.
4. **Anonymous sessions.** The `gameToken` is a bearer capability with no expiry or revocation — fine
   for a casual demo, not for anything with stakes.
5. **No profanity filter** on nicknames beyond the character-class regex.
6. **`System.Security.Cryptography.Xml` transitive advisory** surfaced by `dotnet` (via EF Core 10 /
   OpenAPI). No code path uses it; pin once a patched transitive flows through.
7. **Round-selection replay variety** is decent but not large — a handful of seeds produce similar
   window sets because the "decisive but not absurd" pool is finite on 10 years of data.
