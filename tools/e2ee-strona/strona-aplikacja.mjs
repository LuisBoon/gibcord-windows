// 6.4 zgodność szyfrowanych rozmów: strona (dc-next w headless Chromium) ↔ aplikacja Windows (GibCord.Core, test WebInteropTests).
// Faza 1: strona wchodzi pierwsza (lider, losuje klucz), aplikacja dołącza. Faza 2: aplikacja pierwsza, strona dołącza.
// Sprawdza: ten sam klucz i numer, LiveKit z szyfrowaniem po obu stronach, obie strony się słyszą (odszyfrowany dźwięk), ten sam kod bezpieczeństwa.
// Uruchomienie (kopia testowa serwera z LiveKit, EMAIL_REQUIRED=0, PUBLIC_URL = adres strony; proxy ze stroną dc-next):
//   cd <pusty katalog roboczy> && GC_URL=http://127.0.0.1:9096 GC_WEB=http://127.0.0.1:9097 INTEROP_DIR=$PWD node /opt/dc/windows/tools/e2ee-strona/strona-aplikacja.mjs &
//   GC_TEST_URL=http://127.0.0.1:9096 GC_TEST_INTEROP=$PWD dotnet test tests/GibCord.Core.Tests --filter WebInteropTests
// Wymiana przez pliki w INTEROP_DIR: ready.json (konta), web-in-N.json, web-code-N.json (strona), app-in-N.json / app-done-N.json (aplikacja).
import fs from 'node:fs';
import { browser, wait } from './cdp.mjs';
const DIR = (process.env.INTEROP_DIR || process.cwd()).replace(/\/?$/, '/');
const B = process.env.GC_URL || 'http://127.0.0.1:9096', WEB = process.env.GC_WEB || 'http://127.0.0.1:9097';
const H = t => ({ 'content-type': 'application/json', 'x-dc-client': 'app', ...(t ? { authorization: 'Bearer ' + t } : {}) });
async function api(t, m, p, b) { const r = await fetch(B + p, { method: m, headers: H(t), body: b ? JSON.stringify(b) : undefined }); const j = await r.json().catch(() => ({})); if (!r.ok) throw new Error(`${m} ${p} ${r.status} ${JSON.stringify(j)}`); return j; }
const PASS = 'Haslo-testowe-1234';
for (const f of fs.readdirSync(DIR)) if (/^(ready|web-|app-).*\.json$/.test(f)) fs.rmSync(DIR + f);
const tag = Date.now().toString(36).slice(-5);
const aTok = (await api(null, 'POST', '/api/register', { username: 'ia' + tag, password: PASS })).token;
const wTok = (await api(null, 'POST', '/api/register', { username: 'iw' + tag, password: PASS })).token;
const A = (await api(aTok, 'GET', '/api/me')).me, Wu = (await api(wTok, 'GET', '/api/me')).me;
const srv = await api(aTok, 'POST', '/api/servers', { name: 'Interop ' + tag });
const inv = await api(aTok, 'POST', `/api/servers/${srv.id}/invites`, {});
await api(wTok, 'POST', `/api/invites/${inv.code}/join`, {});
const dm = await api(aTok, 'POST', '/api/dms', { userId: Wu.id });
fs.writeFileSync(DIR + 'ready.json', JSON.stringify({ user: 'ia' + tag, pass: PASS, dm: dm.id, web: Wu.id, app: A.id }));
console.log('konta gotowe', A.id, Wu.id, 'dm', dm.id);

const b = await browser('iw', 9334);
const until = async (f, ms, what) => { const end = Date.now() + ms; while (Date.now() < end) { try { if (await f()) return true; } catch {} await wait(250); } console.log('  ✗ nie doczekano się: ' + what); return false; };
const waitFile = (f, ms) => until(() => fs.existsSync(DIR + f), ms, f);
let ok = 0, bad = 0; const check = (c, m) => { if (c) { ok++; console.log('  ✓ ' + m); } else { bad++; console.log('  ✗ ' + m); } };
try {
  await b.go(WEB + '/');
  const st = await b.ev(`fetch('/api/login',{method:'POST',headers:{'content-type':'application/json','X-DC':'1'},credentials:'include',body:JSON.stringify({username:'iw${tag}',password:'${PASS}'})}).then(r=>r.status)`);
  console.log('logowanie strony', st);
  await b.go(WEB + '/');
  await until(() => b.ev(`!!(typeof S !== 'undefined' && S.me && S.me.id === ${Wu.id} && typeof voiceJoin === 'function' && typeof e2OnMsg === 'function')`), 15000, 'strona wczytana');
  // dziennik ramek szyfrowania po stronie strony (do diagnozy, bez zmian w kodzie strony)
  await b.ev(`(() => { if (window.__e2log) return; window.__e2log = true; const o = e2OnMsg; e2OnMsg = function (d) { console.log('E2MSG ' + JSON.stringify({ kind: d.kind, from: d.from, dev: d.dev, fromDev: d.fromDev })); return o.apply(this, arguments); }; const s = e2Send; e2Send = function (c, x) { console.log('E2SEND ' + JSON.stringify({ to: x.to, kind: x.kind, dev: x.dev })); return s.apply(this, arguments); }; })()`);
  const webState = () => b.ev(`(() => { const c = E2.call; const lk = M && M.lk; return { key: c && c.key, idx: c && c.idx, src: c && c.src, enc: !!(lk && lk.isE2EEEnabled), conn: M && M.room, spk: M ? M.isSpeaking(${A.id}) : false, lvl: M ? M.level(${A.id}) : 0, devs: c ? [...c.devs.entries()] : [] }; })()`);
  for (const phase of [1, 2]) {
    console.log(`— faza ${phase}: ${phase === 1 ? 'strona pierwsza (lider), aplikacja dołącza' : 'aplikacja pierwsza (lider), strona dołącza'}`);
    if (phase === 2) await waitFile('app-in-2.json', 60000);
    await b.ev(`voiceJoin(${dm.id})`);
    await until(async () => (await webState()).key, 25000, 'klucz na stronie');
    let ws = await webState();
    console.log('  strona: klucz', ws.key?.slice(0, 6) + '…', 'nr', ws.idx, 'od', ws.src, 'szyfrowanie LiveKit', ws.enc);
    fs.writeFileSync(DIR + `web-in-${phase}.json`, JSON.stringify({ key: ws.key, idx: ws.idx }));
    // aplikacja mówi sinusem — czy strona ją słyszy (odszyfrowany dźwięk → poziom i „mówi”)
    let maxLvl = 0, spoke = false;
    await until(async () => { const s = await webState(); maxLvl = Math.max(maxLvl, s.lvl || 0); spoke ||= s.spk; return spoke && maxLvl > 0.05; }, 30000, 'strona słyszy aplikację');
    ws = await webState();
    check(spoke && maxLvl > 0.05, `strona słyszy aplikację (poziom ${maxLvl.toFixed(3)}, mówi: ${spoke})`);
    check(ws.enc, 'strona: LiveKit z szyfrowaniem');
    const code = await b.ev(`(async () => { try { return await e2CodeFor(E2.call); } catch (e) { return 'błąd ' + e.message; } })()`);
    fs.writeFileSync(DIR + `web-code-${phase}.json`, JSON.stringify({ code, key: ws.key, idx: ws.idx }));
    console.log('  kod bezpieczeństwa na stronie:', code);
    await waitFile(`app-done-${phase}.json`, 60000);
    const app = JSON.parse(fs.readFileSync(DIR + `app-done-${phase}.json`));
    check(app.key === ws.key && app.idx === ws.idx, 'ten sam klucz rozmowy i numer w aplikacji i na stronie');
    check(app.heard === true, `aplikacja słyszy stronę (szczyt ${app.peak})`);
    check(typeof code === 'string' && code === app.code, `ten sam kod bezpieczeństwa (${app.code})`);
    await b.ev(`voiceLeave()`);
    await wait(1500);
  }
} catch (e) { bad++; console.log('BŁĄD', e.message); }
finally {
  console.log((b.logs || []).filter(l => /E2MSG|E2SEND|E2EE|error|EXC/.test(l)).slice(-40).join('\n'));
  b.close();
}
console.log(`\n${ok} OK, ${bad} źle`);
fs.writeFileSync(DIR + 'web-end.json', JSON.stringify({ ok, bad }));
process.exit(bad ? 1 : 0);
