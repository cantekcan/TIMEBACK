import { useCallback, useEffect, useMemo, useRef, useState } from "react";
import {
  api, ApiError, fmtPct, fmtTRY, setSessionToken, warmUpBackend,
  type GameResultView, type LeaderboardRow, type RoundResultView, type RoundView,
} from "./api";
import { clampWeight, evenSplit, rebalance, totalOf, type Weights } from "./allocation";
import { aiDisclosureText } from "./aiModel";

type Screen =
  | { k: "landing" }
  | { k: "loading"; label?: string }
  | { k: "round"; gameId: string; round: RoundView }
  | { k: "roundResult"; gameId: string; round: RoundView; result: RoundResultView }
  | { k: "final"; gameId: string; result: GameResultView }
  | { k: "leaderboard"; from: "landing" | "final"; myNick?: string };

export function App() {
  const [screen, setScreen] = useState<Screen>({ k: "landing" });
  const [error, setError] = useState<string | null>(null);

  // Render's free tier sleeps when idle - ping it the moment the app opens so it's already waking
  // up by the time the player taps "Oyuna Başla", instead of only starting cold on that first call.
  useEffect(() => { warmUpBackend(); }, []);

  useEffect(() => {
    if (!error) return;
    const t = setTimeout(() => setError(null), 5000);
    return () => clearTimeout(t);
  }, [error]);

  const run = (fn: () => Promise<void>) => {
    setError(null);
    fn().catch((e) => setError(e instanceof ApiError ? e.message : "Beklenmeyen bir hata oluştu."));
  };

  const startGame = () => run(async () => {
    setScreen({ k: "loading" });
    try {
      const g = await api.startGame();
      setSessionToken(g.gameToken);
      setScreen({ k: "round", gameId: g.gameId, round: g.currentRound });
    } catch (e) {
      setScreen({ k: "landing" });
      throw e;
    }
  });

  const afterRound = (s: Extract<Screen, { k: "roundResult" }>) => run(async () => {
    if (s.round.number >= s.round.totalRounds) {
      setScreen({ k: "loading", label: "Zaman Yorumcusu senin için yorum hazırlıyor…" });
      setScreen({ k: "final", gameId: s.gameId, result: await api.result(s.gameId) });
    } else {
      const round = await api.currentRound(s.gameId);
      setScreen({ k: "round", gameId: s.gameId, round });
    }
  });

  return (
    <div className="shell">
      <div className="brand">
        <span className="dot" />
        <h1>TIMEBACK</h1>
        <small>zaman yatırımcısı</small>
      </div>

      {screen.k === "landing" && (
        <Landing onStart={startGame} onLeaderboard={() => setScreen({ k: "leaderboard", from: "landing" })} />
      )}
      {screen.k === "loading" && <Loading label={screen.label} />}
      {screen.k === "round" && (
        <RoundScreen key={screen.round.number} gameId={screen.gameId} round={screen.round}
          onError={setError}
          onLocked={(result) => setScreen({ k: "roundResult", gameId: screen.gameId, round: screen.round, result })} />
      )}
      {screen.k === "roundResult" && (
        <>
          <ProgressDots current={screen.round.number} total={screen.round.totalRounds} />
          <RoundResultScreen round={screen.round} result={screen.result} onNext={() => afterRound(screen)} />
        </>
      )}
      {screen.k === "final" && (
        <FinalScreen result={screen.result}
          onSaved={(nick) => setScreen({ k: "leaderboard", from: "final", myNick: nick })}
          onError={setError}
          onLeaderboard={() => setScreen({ k: "leaderboard", from: "final" })}
          onReplay={() => { setSessionToken(null); setScreen({ k: "landing" }); }} />
      )}
      {screen.k === "leaderboard" && (
        <LeaderboardScreen myNick={screen.myNick}
          onBack={() => setScreen({ k: "landing" })} />
      )}

      {error && <div className="toast">⚠ {error}</div>}
    </div>
  );
}

/* -------------------------------------------------------------------------- */

function Loading({ label }: { label?: string }) {
  return <div className="center"><div className="spinner" /><p className="muted">{label ?? "Piyasa verileri hazırlanıyor…"}</p></div>;
}

function ProgressDots({ current, total }: { current: number; total: number }) {
  return (
    <div className="dots" style={{ margin: "0 2px 12px" }}>
      {Array.from({ length: total }, (_, i) => i + 1).map((n) => (
        <i key={n} className={n < current ? "done" : n === current ? "now" : ""} />
      ))}
    </div>
  );
}

function Landing({ onStart, onLeaderboard }: { onStart: () => void; onLeaderboard: () => void }) {
  return (
    <div className="card">
      <div className="hero">
        <div className="kicker">Historical Investing Game</div>
        <div className="big-q">"Geçmişe dönseydin paranı nereye yatırırdın?"</div>
        <p className="muted">Rastgele bir tarih. 100.000 TL. 15 saniye. 3 tur.</p>
      </div>
      <div className="steps">
        <div className="step"><div className="n">1</div><small>Geçmişten bir tarih ve 100.000 TL alırsın</small></div>
        <div className="step"><div className="n">2</div><small>15 saniyede altın, borsa ve kripto arasında dağıt</small></div>
        <div className="step"><div className="n">3</div><small>Zaman ilerler — nominal ve reel getirini gör</small></div>
      </div>
      <div className="time-info">
        <p className="explain">
          Geçmişten bir tarihe döneceksin. O tarihte yatırımını seçip belirlenen süre boyunca
          tutacaksın. Süre sonunda paranı ne kadar büyüttüğüne göre puan kazanacaksın.
        </p>
        <p className="explain">Her tur farklı bir tarih ve yatırım süresi kullanabilir. Yatırım süresine ve verilen tarihe dikkat et.</p>
        <p className="explain">Yatırım süresi her turda 1–4 yıl arasında değişebilir.</p>
      </div>
      <button className="lg" onClick={onStart}>OYUNA BAŞLA</button>
      <div className="btns">
        <button className="ghost" onClick={onLeaderboard}>🏆 Leaderboard</button>
      </div>
    </div>
  );
}

/* -------------------------------------------------------------------------- */

function useCountUp(target: number, ms = 900) {
  const [v, setV] = useState(0);
  useEffect(() => {
    let raf = 0;
    const start = performance.now();
    const from = 0;
    const tick = (now: number) => {
      const t = Math.min(1, (now - start) / ms);
      const eased = 1 - Math.pow(1 - t, 3);
      setV(from + (target - from) * eased);
      if (t < 1) raf = requestAnimationFrame(tick);
    };
    raf = requestAnimationFrame(tick);
    return () => cancelAnimationFrame(raf);
  }, [target, ms]);
  return v;
}

function CountdownRing({ remainingMs, totalMs }: { remainingMs: number; totalMs: number }) {
  const secs = Math.max(0, Math.ceil(remainingMs / 1000));
  const frac = Math.max(0, Math.min(1, remainingMs / totalMs));
  const R = 34, C = 2 * Math.PI * R;
  const low = secs <= 2;
  return (
    <div className={"ring" + (low ? " low" : "")}>
      <svg width="76" height="76" viewBox="0 0 76 76">
        <circle cx="38" cy="38" r={R} fill="none" strokeWidth="6" className="" style={{ stroke: "var(--line)" }} />
        <circle cx="38" cy="38" r={R} fill="none" strokeWidth="6" strokeLinecap="round"
          style={{
            stroke: low ? "var(--bad)" : "var(--accent)",
            strokeDasharray: C, strokeDashoffset: C * (1 - frac),
            transition: "stroke-dashoffset .25s linear, stroke .3s",
          }} />
      </svg>
      <div className="n">{String(secs).padStart(2, "0")}</div>
    </div>
  );
}

function RoundScreen({ gameId, round, onLocked, onError }: {
  gameId: string; round: RoundView;
  onLocked: (r: RoundResultView) => void; onError: (m: string) => void;
}) {
  const capital = round.startingCapital;
  const symbols = useMemo(() => round.assets.map((a) => a.symbol), [round]);
  const [weights, setWeights] = useState<Weights>(() => evenSplit(symbols));
  const total = totalOf(weights);

  // The visible countdown is exactly the decision window - the server keeps a couple of extra
  // seconds of slack on top of this for network latency, but that slack is never shown to the
  // player as "more time", otherwise a genuinely-on-time click would have no time left for its
  // own request to actually reach the server.
  //
  // The deadline is anchored to when this screen actually mounts in the browser, not to the
  // server's round.startedAtUtc - the player never sees the network/render time between the
  // server starting the round and the screen appearing eaten out of their 15 seconds. The
  // server still enforces its own authoritative deadline off startedAtUtc independently, so a
  // late submit is rejected there regardless of what the client shows.
  const totalMs = round.selectionWindowSeconds * 1000;
  const [deadline] = useState(() => Date.now() + totalMs);
  const [now, setNow] = useState(() => Date.now());
  const remaining = deadline - now;
  const locking = useRef(false);
  const [submitting, setSubmitting] = useState(false);

  // A slider position is only ever a real investment once the player presses "Kilitle" - this is
  // the one function that actually calls the API, and it always sends exactly the allocation it's
  // given, never reaching back into live slider state itself.
  const submit = useCallback(async (allocations: { symbol: string; weight: number }[]) => {
    if (locking.current) return;
    locking.current = true;
    setSubmitting(true);
    try {
      onLocked(await api.submit(gameId, round.number, allocations));
    } catch (e) {
      // Never strand the player here: re-enable the button so a tap retries. The backend treats a
      // retry as idempotent - it returns the round's already-locked result instead of erroring.
      onError(e instanceof ApiError ? e.message : "Yatırım kilitlenemedi.");
      locking.current = false;
      setSubmitting(false);
    }
  }, [gameId, round.number, onLocked, onError]);

  const submitSelection = useCallback(() => {
    const allocations = symbols.map((s) => ({ symbol: s, weight: weights[s] ?? 0 }));
    const sum = allocations.reduce((a, x) => a + x.weight, 0);
    if (sum !== 100 && allocations.length) allocations[0].weight += 100 - sum;
    return submit(allocations);
  }, [symbols, weights, submit]);

  useEffect(() => {
    const t = setInterval(() => {
      const n = Date.now();
      setNow(n);
      // Time's up with no "Kilitle" click: whatever was still on the sliders was never confirmed,
      // so it was never an investment - send an empty allocation rather than the live slider
      // values. The server resolves this as "no investment, score 0" instead of quietly locking in
      // a selection the player never actually committed to. (If a manual click is already in
      // flight, `locking` makes this a no-op - it never overrides a real, on-time submission.)
      if (deadline - n <= 0) { clearInterval(t); void submit([]); }
    }, 100);
    return () => clearInterval(t);
  }, [deadline, submit]);

  const onSlide = (symbol: string, value: number) =>
    setWeights((w) => rebalance(w, symbol, clampWeight(value)));

  const secs = Math.max(0, Math.ceil(remaining / 1000));
  const dateStr = new Date(round.requestedDate).toLocaleDateString("tr-TR");

  return (
    <>
      <div className="row" style={{ marginBottom: 10 }}>
        <ProgressDots current={round.number} total={round.totalRounds} />
        <span className="muted" style={{ fontSize: 12 }}>Tur {round.number} / {round.totalRounds}</span>
      </div>
      <div className="card compact">
        <div className="rhead">
          <div>
            <div className="date-badge num">{dateStr}'e döndün.</div>
            <div className="holding-period">Bu yatırımı {round.holdingPeriodYears} yıl boyunca tutacaksın.</div>
            <p className="explain" style={{ margin: "2px 0 0" }}>Süre sonunda yatırımının ne kadar büyüdüğünü göreceksin.</p>
            <div className="sub">{round.selectionWindowSeconds} saniyen var. Paran nereye gitsin?</div>
            <div className="capital-pill">💰 {fmtTRY(capital)}</div>
          </div>
          <CountdownRing remainingMs={remaining} totalMs={totalMs} />
        </div>

        <div className="alloc">
          {round.assets.map((a) => {
            const w = weights[a.symbol] ?? 0;
            return (
              <div className="aline" key={a.symbol}>
                <div className="top">
                  <span className="name">{a.displayName} <span className="chip">{classLabel(a.assetClass)}</span></span>
                  <span className="vals">
                    <div className="pct num">{w}%</div>
                    <div className="tl num">{fmtTRY((capital * w) / 100)}</div>
                  </span>
                </div>
                <input type="range" min={0} max={100} value={w} aria-label={a.displayName}
                  style={{ ["--fill" as string]: `${w}%` }}
                  onChange={(e) => onSlide(a.symbol, Number(e.target.value))} />
              </div>
            );
          })}
        </div>

        <div className={"total-bar" + (total === 100 ? "" : " bad")}>
          <div className="lbl">
            <span>Toplam dağılım</span>
            <b className={total === 100 ? "ok" : "bad"}>{total}%</b>
          </div>
          <div className="meter"><i style={{ width: `${Math.min(100, total)}%` }} /></div>
        </div>

        <button className="lg" style={{ marginTop: 10 }} onClick={() => void submitSelection()} disabled={submitting}>
          {submitting ? "KİLİTLENİYOR…" : secs <= 0 ? "SÜRE DOLDU - KİLİTLE" : "YATIRIMI KİLİTLE"}
        </button>
      </div>
    </>
  );
}

/* -------------------------------------------------------------------------- */

/** How far the player's outcome sits between this round's worst and best possible outcome (0-100),
 *  mirroring the backend's own skill-ratio formula so the score explanation always matches the score. */
function skillPercent(final: number, worst: number, best: number): number {
  if (best <= worst) return 100;
  return Math.max(0, Math.min(100, ((final - worst) / (best - worst)) * 100));
}

const pct1 = (n: number) => n.toLocaleString("tr-TR", { maximumFractionDigits: 1 });

function RoundResultScreen({ round, result, onNext }: { round: RoundView; result: RoundResultView; onNext: () => void }) {
  const finalValue = useCountUp(result.finalValue);
  const gain = result.finalValue - result.startingCapital;
  const maxBar = Math.max(result.finalValue, result.bestPossibleValue, 1);
  const bestName = round.assets.find((a) => a.symbol === result.bestPossibleSymbol)?.displayName ?? result.bestPossibleSymbol;
  const skillPct = skillPercent(result.finalValue, result.worstPossibleValue, result.bestPossibleValue);
  const gotTheBest = result.missedGain < 1;

  return (
    <div className="card compact">
      <h3>Paran ne oldu?</h3>
      <p className="explain" style={{ marginBottom: 8 }}>{round.holdingPeriodYears} yıllık yatırımının sonunda:</p>
      <div className="result-hero">
        {result.autoLocked && <p className="explain" style={{ color: "var(--bad)" }}>Süre doldu, yatırım kaydedilmedi.</p>}
        <div className="hero-row">
          <span className="from num">{fmtTRY(result.startingCapital)}</span>
          <span className="arrow">→</span>
          <span className="value num">{fmtTRY(finalValue)}</span>
        </div>
        <div className={"gain num " + (gain >= 0 ? "pos" : "neg")}>
          {gain >= 0 ? "+" : "−"}{fmtTRY(Math.abs(gain))} {gain >= 0 ? "kazandın" : "kaybettin"}
        </div>
      </div>

      <h3>Bu turdaki performansın</h3>
      <div className="perf-list">
        <div className="perf-row">
          <div className="perf-top"><span>Nominal getiri</span><b className={result.nominalReturnFraction >= 0 ? "pos" : "neg"}>{fmtPct(result.nominalReturnFraction)}</b></div>
          <p className="explain">Yatırımın {fmtTRY(result.startingCapital)} tutarını {fmtTRY(result.finalValue)}'ye çıkardı - enflasyondan önceki ham getiri budur.</p>
        </div>
        <div className="perf-row">
          <div className="perf-top"><span>Enflasyon</span><b>{fmtPct(result.inflationFraction)}</b></div>
          <p className="explain">Aynı dönemde fiyatlar ortalama {fmtPct(result.inflationFraction)} değişti - paranın alım gücü bu kadar aşınır.</p>
        </div>
        <div className="perf-row">
          <div className="perf-top"><span>Reel getiri</span><b className={result.realReturnFraction >= 0 ? "pos" : "neg"}>{fmtPct(result.realReturnFraction)}</b></div>
          <p className="explain">Enflasyonun etkisi hesaba katıldığında paranın gerçek alım gücü {fmtPct(result.realReturnFraction)} değişti. Nominal ile karıştırma: {fmtTRY(result.finalValue)}'nin bir bölümü değil, "bugünün parasıyla ne kadar zengin oldun" sorusunun cevabı budur.</p>
        </div>
      </div>

      <h3>Daha iyisini yapabilir miydin?</h3>
      <div className="compare">
        <p className="explain">Bu turda en yüksek getiriyi <b>{bestName}</b> verdi. 100.000 TL'nin tamamı {bestName}'de olsaydı:</p>
        <div className="bar you"><i style={{ width: `${(result.finalValue / maxBar) * 100}%` }} /><span>senin portföyün · {fmtTRY(result.finalValue)}</span></div>
        <div className="bar best"><i style={{ width: `${(result.bestPossibleValue / maxBar) * 100}%` }} /><span>{bestName} · {fmtTRY(result.bestPossibleValue)}</span></div>
        <p className="explain" style={{ marginTop: 8 }}>
          {gotTheBest
            ? "Bu turda zaten en iyi seçimi yaptın."
            : <>{bestName}'i seçseydin <b>{fmtTRY(result.missedGain)}</b> daha fazla paran olacaktı.</>}
        </p>
      </div>

      {result.assets.length > 0 && (
        <>
          <h3>Varlık bazında</h3>
          <div className="assets-mini">
            {[...result.assets].sort((a, b) => b.growthFactor - a.growthFactor).map((a) => {
              const pos = a.returnFraction >= 0;
              const mag = Math.min(100, Math.abs(a.returnFraction) * 40 + 6);
              return (
                <div className="am" key={a.symbol}>
                  <span>{a.symbol}</span>
                  <span className="track"><i style={{ width: `${mag}%`, background: pos ? "var(--good)" : "var(--bad)" }} /></span>
                  <span className="r" style={{ color: pos ? "var(--good)" : "var(--bad)" }}>{fmtPct(a.returnFraction)}</span>
                </div>
              );
            })}
          </div>
        </>
      )}

      <h3>Tur skoru</h3>
      <div className="score-box">
        <div className="score-value num">{result.score}<small> / 1000</small></div>
        <p className="explain">
          Bu turda en kötü olası sonuç {fmtTRY(result.worstPossibleValue)}, en iyi olası sonuç {fmtTRY(result.bestPossibleValue)} idi.
          Sen {fmtTRY(result.finalValue)} ile bu aralığın <b>%{pct1(skillPct)}</b>'ine ulaştın - skorun da bunun 1000 üzerinden karşılığı.
        </p>
      </div>

      <button className="lg" style={{ marginTop: 12 }} onClick={onNext}>DEVAM ET</button>
    </div>
  );
}

/* -------------------------------------------------------------------------- */

function FinalScreen({ result, onSaved, onLeaderboard, onReplay, onError }: {
  result: GameResultView; onSaved: (nick: string) => void;
  onLeaderboard: () => void; onReplay: () => void; onError: (m: string) => void;
}) {
  const [nick, setNick] = useState("");
  const [saving, setSaving] = useState(false);
  const totalScore = useCountUp(result.finalScore ?? 0);
  const best = result.rounds.reduce((a, b) => (b.score > a.score ? b : a), result.rounds[0]);
  const worst = result.rounds.reduce((a, b) => (b.score < a.score ? b : a), result.rounds[0]);

  const save = async () => {
    setSaving(true);
    try { await api.saveScore(result.gameId, nick.trim()); onSaved(nick.trim()); }
    catch (e) { onError(e instanceof ApiError ? e.message : "Kaydedilemedi."); setSaving(false); }
  };

  const nickOk = /^[A-Za-z0-9_\-.]{3,16}$/.test(nick.trim());

  return (
    <div className="card">
      <div className="total-score">
        <div className="muted">TOPLAM SKOR</div>
        <div className="v num">{Math.round(totalScore)}<small> / {result.maxScore}</small></div>
      </div>

      <div className="scorebars">
        {result.rounds.map((r) => (
          <div className="sb" key={r.number}>
            <span className="muted">Round {r.number}</span>
            <span className="track"><i style={{ width: `${(r.score / 1000) * 100}%` }} /></span>
            <span className="s num">{r.score}</span>
          </div>
        ))}
      </div>

      <div className="kv">
        <span className="k">En iyi turun</span><span className="v">Round {best.number} · {best.score} puan</span>
        <span className="k">En zayıf turun</span><span className="v">Round {worst.number} · {worst.score} puan</span>
      </div>

      <div className="ai-card">
        <div className="who">🤖 zaman yorumcusu</div>
        <div className="txt">{result.aiCommentary ?? "…"}</div>
        <div className="disclosure">{aiDisclosureText(result.aiModel)}</div>
      </div>

      {!result.onLeaderboard ? (
        <>
          <div className="nick">
            <input type="text" placeholder="nickname (3-16)" value={nick} maxLength={16}
              onChange={(e) => setNick(e.target.value)} />
            <button onClick={save} disabled={!nickOk || saving}>{saving ? "…" : "KAYDET"}</button>
          </div>
          {nick.length > 0 && !nickOk && <p className="muted" style={{ fontSize: 12, marginTop: 6 }}>Harf, rakam, _ - . · 3–16 karakter</p>}
        </>
      ) : <p className="delta pos" style={{ display: "inline-block", marginTop: 14 }}>Leaderboard'a kaydedildi ✓</p>}

      <div className="btns">
        <button className="ghost" onClick={onLeaderboard}>🏆 Leaderboard</button>
        <button onClick={onReplay}>TEKRAR OYNA</button>
      </div>

      <p className="data-note">
        Bu simülasyon gerçek tarihsel piyasa verileri kullanır: Bitcoin, BIST 100, altın ve S&amp;P 500
        için tarihsel fiyatlar; enflasyon için tarihsel TÜFE verileri.
      </p>
    </div>
  );
}

/* -------------------------------------------------------------------------- */

function LeaderboardScreen({ onBack, myNick }: { onBack: () => void; myNick?: string }) {
  const [rows, setRows] = useState<LeaderboardRow[] | null>(null);
  const [err, setErr] = useState<string | null>(null);
  useEffect(() => {
    api.leaderboard(10).then(setRows).catch((e) => setErr(e instanceof ApiError ? e.message : "Yüklenemedi"));
  }, []);

  const podium = (rows ?? []).slice(0, 3);
  const rest = (rows ?? []).slice(3);

  return (
    <div className="card">
      <h2>🏆 Top 10</h2>
      {err && <p className="muted">{err}</p>}
      {rows === null && <div className="center"><div className="spinner" /></div>}
      {rows && rows.length === 0 && <p className="muted">Henüz kimse yok. İlk sen ol!</p>}

      {podium.length > 0 && (
        <div className="podium">
          {[1, 0, 2].map((idx) => {
            const r = podium[idx];
            if (!r) return <div key={idx} />;
            return (
              <div key={idx} className={"pod p" + r.rank}>
                <div className="medal">{["🥇", "🥈", "🥉"][r.rank - 1]}</div>
                <div className="nm">{r.nickname}</div>
                <div className="sc num">{r.score}</div>
              </div>
            );
          })}
        </div>
      )}

      <div className="lb-list">
        {rest.map((r) => (
          <div key={r.rank} className={"lb-row" + (r.nickname === myNick ? " me" : "")}>
            <span className="rk num">{r.rank}</span>
            <span>{r.nickname}</span>
            <span className="sc num">{r.score}</span>
          </div>
        ))}
      </div>

      <button className="ghost lg" style={{ marginTop: 16 }} onClick={onBack}>ANA SAYFA</button>
    </div>
  );
}

function classLabel(c: string) {
  return ({ Commodity: "emtia", EquityIndex: "borsa", Crypto: "kripto", Currency: "döviz" } as Record<string, string>)[c] ?? c;
}
