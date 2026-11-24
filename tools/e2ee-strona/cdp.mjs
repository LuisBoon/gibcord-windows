// Minimalny klient CDP: uruchamia chromium (osobny profil), otwiera stronę, eval, zrzut ekranu.
import { spawn } from 'node:child_process'; import fs from 'node:fs';
import WebSocket from '/opt/dc/serwer6/tools/node_modules/ws/index.js';
export async function browser(name, port, w = 1280, h = 800) {
  const dir = `${process.cwd()}/prof-${name}`; fs.rmSync(dir, { recursive: true, force: true });
  // chromium w osobnej grupie z limitem pamięci (maszyna ma mało RAM i działa na niej produkcja)
  const p = spawn('systemd-run', ['--scope', '-q', '-p', `MemoryMax=${process.env.CHROME_MEM || '650M'}`, '-p', 'MemorySwapMax=0', 'chromium', '--js-flags=--max-old-space-size=256', '--headless=new', '--no-sandbox', '--disable-gpu', `--remote-debugging-port=${port}`, `--user-data-dir=${dir}`, `--window-size=${w},${h}`, '--use-fake-ui-for-media-stream', '--use-fake-device-for-media-stream', '--autoplay-policy=no-user-gesture-required', '--hide-scrollbars', 'about:blank'], { stdio: 'ignore' });
  let tabs;
  for (let i = 0; i < 50; i++) { try { tabs = await (await fetch(`http://127.0.0.1:${port}/json`)).json(); if (tabs.find(t => t.type === 'page')) break; } catch {} await new Promise(r => setTimeout(r, 200)); }
  const t = tabs.find(t => t.type === 'page');
  const ws = new WebSocket(t.webSocketDebuggerUrl, { perMessageDeflate: false, maxPayload: 256 * 1024 * 1024 });
  await new Promise(r => ws.on('open', r));
  let id = 0; const pend = new Map(); const logs = [];
  ws.on('message', d => { const m = JSON.parse(d); if (m.id && pend.has(m.id)) { pend.get(m.id)(m); pend.delete(m.id); } else if (m.method === 'Runtime.consoleAPICalled') logs.push(m.params.type + ': ' + m.params.args.map(a => a.value ?? a.description).join(' ')); else if (m.method === 'Runtime.exceptionThrown') logs.push('EXC: ' + (m.params.exceptionDetails.exception?.description || m.params.exceptionDetails.text)); });
  const send = (method, params = {}) => new Promise(r => { const i = ++id; pend.set(i, r); ws.send(JSON.stringify({ id: i, method, params })); });
  await send('Runtime.enable'); await send('Page.enable');
  await send('Emulation.setDeviceMetricsOverride', { width: w, height: h, deviceScaleFactor: process.env.DPR ? +process.env.DPR : 1, mobile: !!process.env.MOBILE }); if (process.env.MOBILE) await send('Emulation.setTouchEmulationEnabled', { enabled: true, maxTouchPoints: 5 });
  const b = {
    logs, send,
    go: async url => { await send('Page.navigate', { url }); await new Promise(r => setTimeout(r, 2500)); },
    ev: async (expr) => { const r = await send('Runtime.evaluate', { expression: expr, awaitPromise: true, returnByValue: true }); if (r.result?.exceptionDetails) throw new Error(name + ': ' + (r.result.exceptionDetails.exception?.description || r.result.exceptionDetails.text)); return r.result?.result?.value; },
    shot: async file => { const r = await send('Page.captureScreenshot', { format: 'png' }); fs.writeFileSync(file, Buffer.from(r.result.data, 'base64')); },
    click: async (x, y) => { for (const type of ['mousePressed', 'mouseReleased']) await send('Input.dispatchMouseEvent', { type, x, y, button: 'left', clickCount: 1 }); },
    close: () => { try { ws.close(); } catch {} p.kill('SIGKILL'); },
  };
  return b;
}
export const wait = ms => new Promise(r => setTimeout(r, ms));
