// 25.11.2026 transmisja ze strony w H.264: strona nadaje ekran (płótno zamiast prawdziwego ekranu), druga strona i GibCord 6 oglądają.
// Sprawdza: kodek H.264 u nadającego, obraz dochodzi do przeglądarki i do aplikacji (GibCord.Core, test ScreenInteropTests),
// w fazie 2 rozmowa prywatna szyfrowana od końca do końca, raporty czujnika cięcia trafiają na serwer (tabela stream_stats).
// Wszystko na własnej kopii: serwer GibCorda (zbudowany, katalog SERVER_DIR), osobny LiveKit, strona z WEB_DIR (dc-next).
// Uruchomienie (pusty katalog roboczy):
//   cd <katalog> && nice -n 19 node /opt/dc/windows/tools/transmisja/strona-h264.mjs &
//   GC_TEST_URL=http://127.0.0.1:8899 GC_TEST_SCREEN=$PWD dotnet test tests/GibCord.Core.Tests --filter ScreenInteropTests
import fs from 'node:fs'; import http from 'node:http'; import net from 'node:net'; import path from 'node:path';
import { spawn, execFileSync } from 'node:child_process';
import WebSocket from '/opt/dc/serwer6/tools/node_modules/ws/index.js';

const DIR = process.cwd().replace(/\/?$/, '/');
const SERVER_DIR = process.env.SERVER_DIR || '/root/gibcord6/do-wdrozenia', WEB_DIR = process.env.WEB_DIR || '/var/www/dc-next';
const LK = { port: 7890, tcp: 7891, udp: 7892, key: 'testkey', secret: 'test-sekret-tylko-do-testow-0123456789abcdef' };
const API = 8899, WEB = 8898, B = `http://127.0.0.1:${API}`, W = `http://127.0.0.1:${WEB}`;
const kids = [];
const wait = ms => new Promise(r => setTimeout(r, ms));
let ok = 0, bad = 0; const check = (c, m) => { if (c) { ok++; console.log('  ✓ ' + m); } else { bad++; console.log('  ✗ ' + m); } };
const until = async (f, ms, what) => { const end = Date.now() + ms; while (Date.now() < end) { try { if (await f()) return true; } catch {} await wait(300); } console.log('  … nie doczekano się: ' + what); return false; };
for (const f of fs.readdirSync(DIR)) if (/^(ready|app-|web-).*\.json$/.test(f)) fs.rmSync(DIR + f);

// ── LiveKit i kopia serwera ──
fs.mkdirSync(DIR + 'dane', { recursive: true });
fs.writeFileSync(DIR + 'livekit.yaml', `port: ${LK.port}\nbind_addresses: ['127.0.0.1']\nrtc:\n  tcp_port: ${LK.tcp}\n  udp_port: ${LK.udp}\n  use_external_ip: false\n  node_ip: 127.0.0.1\nkeys:\n  ${LK.key}: ${LK.secret}\nroom:\n  auto_create: true\n  empty_timeout: 30\nlogging:\n  level: warn\n`);
const run = (cmd, args, env, name) => { const p = spawn('nice', ['-n', '19', cmd, ...args], { env: { ...process.env, ...env }, stdio: ['ignore', 'pipe', 'pipe'] }); const log = fs.createWriteStream(DIR + name + '.log'); p.stdout.pipe(log); p.stderr.pipe(log); kids.push(p); return p; };
run('/opt/livekit/livekit-server', ['--config', DIR + 'livekit.yaml'], {}, 'livekit');
run('/opt/dotnet/dotnet', [SERVER_DIR + '/gibcord-serwer.dll'], {
  DOTNET_ROOT: '/opt/dotnet', HOST: '127.0.0.1', PORT: String(API), DATA_DIR: DIR + 'dane', MAIL_DIR: DIR + 'poczta', REGISTRATION: 'open',
  EMAIL_GATE: '0', EMAIL_REQUIRED: '0', PWNED_CHECK: '0', PUSH: '0', DC_TEST_HOOKS: '1', COOKIE_SECURE: '0', PUBLIC_URL: W, ALLOWED_ORIGINS: W,
  LK_API_KEY: LK.key, LK_API_SECRET: LK.secret, LK_URL: `ws://127.0.0.1:${LK.port}`, LK_HTTP: `http://127.0.0.1:${LK.port}`, LK_ROOM_PREFIX: 'test-',
}, 'serwer');

// ── strona: pliki z WEB_DIR, /api /ws /uploads → kopia serwera ──
const TYPES = { '.html': 'text/html; charset=utf-8', '.js': 'text/javascript', '.css': 'text/css', '.svg': 'image/svg+xml', '.png': 'image/png', '.json': 'application/json', '.wasm': 'application/wasm', '.woff2': 'font/woff2' };
const proxied = u => /^\/(api|ws|uploads|dekor)(\/|$|\?)/.test(u);
const web = http.createServer((req, res) => {
  if (proxied(req.url)) {
    const p = http.request({ host: '127.0.0.1', port: API, path: req.url, method: req.method, headers: req.headers }, r => { res.writeHead(r.statusCode, r.headers); r.pipe(res); });
    p.on('error', () => { res.writeHead(502); res.end(); }); req.pipe(p); return;
  }
  let f = path.join(WEB_DIR, decodeURIComponent(req.url.split('?')[0]));
  if (!f.startsWith(WEB_DIR) || !fs.existsSync(f) || fs.statSync(f).isDirectory()) f = path.join(WEB_DIR, 'index.html');
  res.writeHead(200, { 'content-type': TYPES[path.extname(f)] || 'application/octet-stream' }); fs.createReadStream(f).pipe(res);
});
web.on('upgrade', (req, sock, head) => {
  const up = net.connect(API, '127.0.0.1', () => {
    up.write(`${req.method} ${req.url} HTTP/1.1\r\n` + Object.entries(req.headers).map(([k, v]) => `${k}: ${v}`).join('\r\n') + '\r\n\r\n'); up.write(head); sock.pipe(up); up.pipe(sock);
  });
  up.on('error', () => sock.destroy()); sock.on('error', () => up.destroy());
});
web.listen(WEB, '127.0.0.1');

// ── przeglądarka (headless chromium, osobny profil, niski priorytet i limit pamięci) ──
async function browser(name, port) {
  const dir = `${DIR}prof-${name}`; fs.rmSync(dir, { recursive: true, force: true });
  const p = spawn('systemd-run', ['--scope', '-q', '-p', 'MemoryMax=650M', '-p', 'MemorySwapMax=0', '-p', 'CPUWeight=20', 'nice', '-n', '19', 'chromium', '--js-flags=--max-old-space-size=256', '--headless=new', '--no-sandbox', '--disable-gpu', `--remote-debugging-port=${port}`, `--user-data-dir=${dir}`, '--window-size=1100,700', '--use-fake-ui-for-media-stream', '--use-fake-device-for-media-stream', '--autoplay-policy=no-user-gesture-required', 'about:blank'], { stdio: 'ignore' });
  kids.push(p);
  let tabs; for (let i = 0; i < 60; i++) { try { tabs = await (await fetch(`http://127.0.0.1:${port}/json`)).json(); if (tabs.find(t => t.type === 'page')) break; } catch {} await wait(250); }
  const ws = new WebSocket(tabs.find(t => t.type === 'page').webSocketDebuggerUrl, { perMessageDeflate: false, maxPayload: 64 * 1024 * 1024 });
  await new Promise(r => ws.on('open', r));
  let id = 0; const pend = new Map(), logs = [];
  ws.on('message', d => { const m = JSON.parse(d); if (m.id && pend.has(m.id)) { pend.get(m.id)(m); pend.delete(m.id); } else if (m.method === 'Runtime.consoleAPICalled') logs.push(m.params.type + ': ' + m.params.args.map(a => a.value ?? a.description).join(' ')); else if (m.method === 'Runtime.exceptionThrown') logs.push('EXC: ' + (m.params.exceptionDetails.exception?.description || m.params.exceptionDetails.text)); });
  const send = (method, params = {}) => new Promise(r => { const i = ++id; pend.set(i, r); ws.send(JSON.stringify({ id: i, method, params })); });
  await send('Runtime.enable'); await send('Page.enable');
  return {
    logs,
    go: async url => { await send('Page.navigate', { url }); await wait(2500); },
    ev: async expr => { const r = await send('Runtime.evaluate', { expression: expr, awaitPromise: true, returnByValue: true }); if (r.result?.exceptionDetails) throw new Error(name + ': ' + (r.result.exceptionDetails.exception?.description || r.result.exceptionDetails.text)); return r.result?.result?.value; },
  };
}

const H = t => ({ 'content-type': 'application/json', 'x-dc-client': 'app', ...(t ? { authorization: 'Bearer ' + t } : {}) });
async function api(t, m, p, b) { const r = await fetch(B + p, { method: m, headers: H(t), body: b ? JSON.stringify(b) : undefined }); const j = await r.json().catch(() => ({})); if (!r.ok) throw new Error(`${m} ${p} ${r.status} ${JSON.stringify(j)}`); return j; }
const PASS = 'Haslo-testowe-1234';
let code = 1;
try {
  check(await until(async () => (await fetch(B + '/api/info')).ok, 60000, 'kopia serwera'), 'kopia serwera działa');
  check(await until(async () => (await fetch(`http://127.0.0.1:${LK.port}/`)).ok, 30000, 'LiveKit'), 'LiveKit działa');
  const tag = Date.now().toString(36).slice(-5);
  const reg = async n => (await api(null, 'POST', '/api/register', { username: n + tag, password: PASS })).token;
  const sTok = await reg('ts'), vTok = await reg('tw'), aTok = await reg('ta');
  const S = (await api(sTok, 'GET', '/api/me')).me;
  const srv = await api(sTok, 'POST', '/api/servers', { name: 'Transmisja ' + tag });
  const vc = await api(sTok, 'POST', `/api/servers/${srv.id}/channels`, { type: 'voice', name: 'Granie' });
  const inv = await api(sTok, 'POST', `/api/servers/${srv.id}/invites`, {});
  for (const t of [vTok, aTok]) await api(t, 'POST', `/api/invites/${inv.code}/join`, {});
  const A = (await api(aTok, 'GET', '/api/me')).me;
  const dm = await api(sTok, 'POST', '/api/dms', { userId: A.id });
  fs.writeFileSync(DIR + 'ready.json', JSON.stringify({ user: 'ta' + tag, pass: PASS, channel: vc.id, dm: dm.id, sender: S.id }));
  console.log('konta gotowe, kanał', vc.id, 'nadaje', S.id);

  const login = async (b, n) => {
    await b.go(W + '/');
    await b.ev(`fetch('/api/login',{method:'POST',headers:{'content-type':'application/json','X-DC':'1'},credentials:'include',body:JSON.stringify({username:'${n}${tag}',password:'${PASS}'})}).then(r=>r.status)`);
    await b.go(W + '/');
    return until(() => b.ev(`!!(typeof S !== 'undefined' && S.me && typeof voiceJoin === 'function' && typeof M === 'object' && M)`), 20000, 'strona ' + n);
  };
  const bs = await browser('ts', 9341), bv = await browser('tw', 9342);
  check(await login(bs, 'ts') && await login(bv, 'tw'), 'obie strony zalogowane');
  // nadający: płótno 640×360, 15 kl/s, ruchomy obraz (koder ma co liczyć)
  await bs.ev(`(() => { const c = document.createElement('canvas'); c.width = 640; c.height = 360; const g = c.getContext('2d'); let i = 0;
    setInterval(() => { i++; g.fillStyle = 'hsl(' + (i * 7 % 360) + ',70%,45%)'; g.fillRect(0, 0, 640, 360); g.fillStyle = '#fff'; g.fillRect((i * 9) % 600, 150, 40, 40); g.font = '28px sans-serif'; g.fillText('GibCord ' + i, 20, 40); }, 66);
    window.__scr = c.captureStream(15); return true; })()`);
  await bs.ev(`voiceJoin(${vc.id})`);
  check(await until(() => bs.ev(`!!(M && M.lk && M._lkState === 'connected')`), 25000, 'nadający w rozmowie'), 'nadający połączony z serwerem głosu');
  await bv.ev(`voiceJoin(${vc.id})`);
  check(await until(() => bv.ev(`!!(M && M.lk && M._lkState === 'connected')`), 25000, 'oglądający w rozmowie'), 'oglądający połączony z serwerem głosu');
  await bs.ev(`(async () => { M._testScreenStream = window.__scr; return await M.startScreen({ res: '720p', fps: 30 }); })()`);
  check(await until(() => bs.ev(`!!(M._pub && M._pub.scrV)`), 15000, 'ekran nadawany'), 'strona nadaje ekran');
  const sendInfo = () => bs.ev(`(async () => { const s = M._pub.scrV.track.sender; const r = await s.getStats(); let o = null, c = null; r.forEach(x => { if (x.type === 'outbound-rtp' && x.kind === 'video') o = x; }); if (o && o.codecId) c = r.get(o.codecId); return { codec: c && c.mimeType, impl: o && o.encoderImplementation, frames: o && o.framesEncoded, h: o && o.frameHeight, e2ee: !!M.lk.isE2EEEnabled }; })()`);
  await until(async () => ((await sendInfo()).frames || 0) > 30, 20000, 'klatki zakodowane');
  const si = await sendInfo();
  console.log('  nadawanie:', JSON.stringify(si));
  check(/h264/i.test(si.codec || ''), `kodek ekranu H.264 (${si.codec})`);
  await bv.ev(`M.watch(${S.id})`);
  const recvInfo = () => bv.ev(`(async () => { const x = M.peers.get(${S.id}); const t = x && x.scrTrack; if (!t || !t.receiver) return null; const r = await t.receiver.getStats(); let i = null, c = null; r.forEach(y => { if (y.type === 'inbound-rtp' && y.kind === 'video') i = y; }); if (i && i.codecId) c = r.get(i.codecId); return { codec: c && c.mimeType, frames: i && i.framesDecoded, w: i && i.frameWidth, h: i && i.frameHeight }; })()`);
  check(await until(async () => ((await recvInfo())?.frames || 0) > 30, 30000, 'obraz w przeglądarce'), 'przeglądarka odbiera i dekoduje obraz');
  console.log('  odbiór w przeglądarce:', JSON.stringify(await recvInfo()));
  // GibCord 6 (test ScreenInteropTests) ogląda ten sam ekran
  const appDone = async n => { const okk = await until(() => fs.existsSync(DIR + `app-done-${n}.json`), 150000, 'aplikacja ' + n); return okk ? JSON.parse(fs.readFileSync(DIR + `app-done-${n}.json`, 'utf8')) : {}; };
  let app = await appDone(1);
  check(app.frames > 10 && app.w > 0, `kanał serwera: GibCord 6 odbiera obraz ze strony (${app.frames} klatek, ${app.w}×${app.h}${app.error ? ', ' + app.error : ''})`);
  // czujnik cięcia: raporty co 30 s od nadającego i oglądającego
  const rows = () => { try { return execFileSync('sqlite3', ['-readonly', DIR + 'dane/dc.sqlite', 'SELECT role || \'|\' || app || \'|\' || data FROM stream_stats ORDER BY id'], { encoding: 'utf8' }).trim().split('\n').filter(Boolean); } catch { return []; } };
  await until(() => rows().some(r => r.startsWith('send|')) && rows().some(r => r.startsWith('recv|')), 70000, 'raporty czujnika');
  const rs = rows(); for (const r of rs) console.log('  raport:', r.slice(0, 260));
  check(rs.some(r => r.startsWith('send|strona|') && /"codec":"h264"/.test(r)), 'raport nadającego na serwerze (kodek h264)');
  check(rs.some(r => r.startsWith('recv|strona|')), 'raport oglądającego na serwerze');
  // faza 2: rozmowa prywatna (szyfrowana od końca do końca) — strona nadaje, GibCord 6 ogląda
  await bv.ev(`voiceLeave()`); await bs.ev(`voiceLeave()`); await wait(2000);
  await bs.ev(`voiceJoin(${dm.id})`);
  check(await until(() => bs.ev(`!!(M && M.lk && M._lkState === 'connected' && M.lk.isE2EEEnabled)`), 30000, 'szyfrowana rozmowa'), 'rozmowa prywatna: strona w szyfrowanej rozmowie');
  await bs.ev(`(async () => { M._testScreenStream = window.__scr; return await M.startScreen({ res: '720p', fps: 30 }); })()`);
  await until(async () => ((await sendInfo()).frames || 0) > 30, 20000, 'klatki zakodowane (faza 2)');
  const si2 = await sendInfo(); console.log('  nadawanie (faza 2):', JSON.stringify(si2));
  check(/h264/i.test(si2.codec || '') && si2.e2ee, `rozmowa prywatna: H.264 z szyfrowaniem (${si2.codec}, szyfrowanie ${si2.e2ee})`);
  fs.writeFileSync(DIR + 'web-in-2.json', '{}');
  app = await appDone(2);
  check(app.frames > 10 && app.w > 0 && app.enc, `rozmowa prywatna: GibCord 6 odbiera odszyfrowany obraz (${app.frames} klatek, ${app.w}×${app.h}, szyfrowanie ${app.enc}${app.error ? ', ' + app.error : ''})`);
  const errs = [...bs.logs, ...bv.logs].filter(l => /^(EXC|error):/.test(l) && !/favicon|push|sw\.js|ServiceWorker|notification/i.test(l));
  if (errs.length) console.log('  dziennik stron:\n   ' + errs.slice(-15).join('\n   '));
  code = bad ? 1 : 0;
} catch (e) { bad++; console.log('BŁĄD', e.message); }
finally {
  fs.writeFileSync(DIR + 'web-end.json', JSON.stringify({ ok, bad }));
  console.log(`\n${ok} OK, ${bad} źle`);
  for (const k of kids) { try { k.kill('SIGKILL'); } catch {} }
  try { execFileSync('pkill', ['-f', '--', `user-data-dir=${DIR}prof-`]); } catch {}
  web.close();
  setTimeout(() => process.exit(code), 500);
}
