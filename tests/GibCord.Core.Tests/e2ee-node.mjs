// Test zgodności szyfrowania pudełek z WebCrypto (tak samo jak strona i tools/test-e2ee.mjs serwera).
// Wejście (stdin, JSON): {myPriv: pkcs8 base64, theirPub: raw base64, info, box?: {iv, ct}, text?}
// Wyjście: otwarte pudełko → {text}; zamknięte → {box: {iv, ct}}. Do tego kod bezpieczeństwa: {lines:[…]} → {code}.
import { webcrypto as wc } from 'node:crypto';
const b64 = u8 => Buffer.from(u8).toString('base64');
const enc = new TextEncoder();
let raw = '';
for await (const ch of process.stdin) raw += ch;
const q = JSON.parse(raw);
if (q.lines) {
  const h = new Uint8Array(await wc.subtle.digest('SHA-256', enc.encode([...q.lines].sort((x, y) => +x.split(':')[0] - +y.split(':')[0]).join('\n'))));
  const g = [];
  for (let i = 0; i < 6; i += 2) { const n = ((h[i] << 8) | h[i + 1]) % 10000; const s = String(n).padStart(4, '0'); g.push(s.slice(0, 2) + ' ' + s.slice(2)); }
  console.log(JSON.stringify({ code: g.join(' · ') }));
  process.exit(0);
}
const priv = await wc.subtle.importKey('pkcs8', Buffer.from(q.myPriv, 'base64'), { name: 'ECDH', namedCurve: 'P-256' }, false, ['deriveBits']);
const pub = await wc.subtle.importKey('raw', Buffer.from(q.theirPub, 'base64'), { name: 'ECDH', namedCurve: 'P-256' }, false, []);
const bits = await wc.subtle.deriveBits({ name: 'ECDH', public: pub }, priv, 256);
const hk = await wc.subtle.importKey('raw', bits, 'HKDF', false, ['deriveKey']);
const key = await wc.subtle.deriveKey({ name: 'HKDF', hash: 'SHA-256', salt: enc.encode('GibCord-E2EE-1'), info: enc.encode(q.info) }, hk, { name: 'AES-GCM', length: 256 }, false, ['encrypt', 'decrypt']);
if (q.box) {
  const pt = await wc.subtle.decrypt({ name: 'AES-GCM', iv: Buffer.from(q.box.iv, 'base64') }, key, Buffer.from(q.box.ct, 'base64'));
  console.log(JSON.stringify({ text: new TextDecoder().decode(pt) }));
} else {
  const iv = wc.getRandomValues(new Uint8Array(12));
  const ct = new Uint8Array(await wc.subtle.encrypt({ name: 'AES-GCM', iv }, key, enc.encode(q.text)));
  console.log(JSON.stringify({ box: { iv: b64(iv), ct: b64(ct) } }));
}
