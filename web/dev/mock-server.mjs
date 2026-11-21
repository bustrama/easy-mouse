// Dev mock of the Easy Mouse server (docs/protocol.md) so the web app can be exercised without the
// C# server. Not for production: it keeps devices in a local JSON file and trusts the LAN.
//
//   node dev/mock-server.mjs [--port 8787] [--auto-approve] [--host 0.0.0.0] [--name "Office PC"]
//                            [--fresh] [--print-secret]
//
// Reuses the client's protocol modules (Node 24 strips the TypeScript types natively).
import { createServer } from 'node:http';
import { existsSync, readFileSync, writeFileSync } from 'node:fs';
import { extname, join, normalize, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { networkInterfaces } from 'node:os';
import { createInterface } from 'node:readline';
import { WebSocketServer } from 'ws';
import { b64decode, b64encode, bytesToHex, hexToBytes } from '../src/protocol/b64.ts';
import {
  authMac,
  DIR_C2S,
  DIR_S2C,
  FrameReceiver,
  FrameSender,
  macEqual,
  pairHelloMac,
  pairingCode,
  randomBytes,
  readyMac,
  sessionKeys,
  wrapSecret,
} from '../src/protocol/crypto.ts';
import { decodeOp, encJson, encPing, encState, MEDIA } from '../src/protocol/ops.ts';

const args = process.argv.slice(2);
const flag = (n) => args.includes(`--${n}`);
const opt = (n, d) => {
  const i = args.indexOf(`--${n}`);
  return i >= 0 && args[i + 1] ? args[i + 1] : d;
};

const PORT = Number(opt('port', '8787'));
const HOST = opt('host', '0.0.0.0');
const PC_NAME = opt('name', 'Office PC');
const AUTO = flag('auto-approve');
const DIST = resolve(fileURLToPath(new URL('../dist', import.meta.url)));
const DEVICES_FILE = fileURLToPath(new URL('./.mock-devices.json', import.meta.url));
const PAIR_WINDOW_MS = 2 * 60 * 1000;

const log = (...a) => console.log(new Date().toISOString().slice(11, 23), ...a);

// ---- devices (persisted so a reload or restart reconnects without re-pairing) ----
/** @type {Map<string, {secret: string, name: string}>} id(b64) -> device */
const devices = new Map();
if (!flag('fresh') && existsSync(DEVICES_FILE)) {
  try {
    for (const [id, d] of Object.entries(JSON.parse(readFileSync(DEVICES_FILE, 'utf8')))) devices.set(id, d);
  } catch {
    /* ignore a corrupt file */
  }
}
const saveDevices = () => writeFileSync(DEVICES_FILE, JSON.stringify(Object.fromEntries(devices), null, 2));

// ---- one-time pairing secret ----
let k = randomBytes(16);
let kExpires = Date.now() + PAIR_WINDOW_MS;
let failures = 0;
let pairingPending = false;
function newSecret(reason) {
  k = randomBytes(16);
  kExpires = Date.now() + PAIR_WINDOW_MS;
  failures = 0;
  log(`pairing secret regenerated (${reason}): ${pairUrl()}`);
}
function lanIp() {
  for (const list of Object.values(networkInterfaces()))
    for (const i of list ?? []) if (i.family === 'IPv4' && !i.internal) return i.address;
  return 'localhost';
}
const pairUrl = (host = `localhost:${PORT}`) => `http://${host}/#k=${b64encode(k)}`;

// ---- interactive approvals ----
const rl = createInterface({ input: process.stdin });
/** @type {((answer: boolean) => void) | null} */
let pendingAnswer = null;
rl.on('line', (line) => {
  const l = line.trim().toLowerCase();
  if (pendingAnswer && (l === 'y' || l === 'n')) {
    const f = pendingAnswer;
    pendingAnswer = null;
    f(l === 'y');
  } else if (l === 'revoke') {
    devices.clear();
    saveDevices();
    for (const c of wss.clients) c.close(4401, 'revoked');
    log('all devices revoked');
  } else if (l === 'new' || l === 'qr') {
    newSecret('requested');
  } else if (l) {
    log('commands: y / n (answer a prompt), revoke (revoke all devices), new (new pairing URL)');
  }
});
const ask = (question) =>
  AUTO
    ? Promise.resolve(true)
    : new Promise((res) => {
        log(`${question} [y/n]`);
        pendingAnswer = res;
      });

// ---- fake PC state ----
const pcState = {
  media: { status: 'playing', title: 'Dune: Part Two', artist: 'Denis Villeneuve', positionMs: 4328000, durationMs: 9960000 },
  volume: { level: 62, muted: false },
  host: { locked: false, elevated: false },
};
const stateFrame = () => encState({ t: 'state', ...pcState });
const remotesFrame = () => encJson({ t: 'remotes', remotes: Object.values(REMOTES) });
const REMOTES = {
  stremio: {
    schema: 1,
    id: 'stremio',
    name: 'Stremio',
    layout: {
      cols: 2,
      items: [
        { type: 'button', icon: 'play', label: 'Play / Pause', action: { do: 'smtc', cmd: 'toggle' } },
        { type: 'button', icon: 'fullscreen', label: 'Fullscreen', action: [{ do: 'focus' }, { do: 'key', keys: 'F' }] },
        { type: 'button', icon: 'subtitles', label: 'Subtitles', action: { do: 'key', keys: 'S' } },
        { type: 'button', icon: 'next', label: 'Next episode', action: { do: 'key', keys: 'N' } },
        { type: 'button', icon: 'skip', label: 'Skip intro', action: { do: 'key', keys: 'Right' } },
        { type: 'button', icon: 'back', label: 'Back', action: { do: 'key', keys: 'Escape' } },
        { type: 'label', bind: 'smtc.title' },
        { type: 'slider', label: 'Volume', bind: 'volume' },
      ],
    },
  },
  movie: {
    schema: 1,
    id: 'movie',
    name: 'Movie night',
    layout: {
      cols: 3,
      items: [
        { type: 'button', icon: 'rewind', action: { do: 'key', keys: 'Left' } },
        { type: 'button', icon: 'pause', action: { do: 'smtc', cmd: 'toggle' } },
        { type: 'button', icon: 'forward', action: { do: 'key', keys: 'Right' } },
        { type: 'button', label: '<img src=x onerror=alert(1)>', action: { do: 'key', keys: 'F' } },
        { type: 'label', text: 'Labels render as plain text' },
      ],
    },
  },
};

const sockets = new Set(); // authenticated controller sockets' contexts
let controller = null;

// The position advances silently; the phone extrapolates it from the last snapshot, as it does with the core.
setInterval(() => {
  if (pcState.media?.status === 'playing') pcState.media.positionMs = Math.min(pcState.media.durationMs, pcState.media.positionMs + 1000);
}, 1000);

// ---- HTTP: static web app + /api/info ----
const TYPES = { '.html': 'text/html; charset=utf-8', '.js': 'text/javascript; charset=utf-8', '.css': 'text/css; charset=utf-8', '.woff2': 'font/woff2', '.json': 'application/json', '.svg': 'image/svg+xml' };
const SECURITY_HEADERS = {
  // same policy as the C# server (docs/protocol.md)
  'Content-Security-Policy': "default-src 'self'; connect-src 'self' ws:; img-src 'self' data:; object-src 'none'; base-uri 'none'; form-action 'none'; frame-ancestors 'none'",
  'Cross-Origin-Resource-Policy': 'same-origin',
  'X-Frame-Options': 'DENY',
  'X-Content-Type-Options': 'nosniff',
  'Referrer-Policy': 'no-referrer',
  'Cache-Control': 'no-store',
};

const server = createServer((req, res) => {
  const url = new URL(req.url ?? '/', 'http://x');
  if (url.pathname === '/api/info') {
    res.writeHead(200, { ...SECURITY_HEADERS, 'Content-Type': 'application/json' });
    return void res.end(JSON.stringify({ name: PC_NAME, version: '0.1.0-mock', proto: 0 }));
  }
  let rel = normalize(decodeURIComponent(url.pathname)).replace(/^([/\\])+/, '');
  if (rel === '' || rel.endsWith('/')) rel += 'index.html';
  const file = join(DIST, rel);
  if (!file.startsWith(DIST) || !existsSync(file)) {
    res.writeHead(404, { ...SECURITY_HEADERS, 'Content-Type': 'text/plain' });
    return void res.end('Not found. Build the web app first: npm run build (or use `npm run dev`, which proxies to this server).');
  }
  res.writeHead(200, { ...SECURITY_HEADERS, 'Content-Type': TYPES[extname(file)] ?? 'application/octet-stream' });
  res.end(readFileSync(file));
});

// ---- WebSocket ----
const wss = new WebSocketServer({ noServer: true, maxPayload: 8192 });
server.on('upgrade', (req, socket, head) => {
  const url = new URL(req.url ?? '/', 'http://x');
  const origin = req.headers.origin;
  // The vite dev proxy rewrites Host, so compare Origin against Host loosely: same host name.
  if (url.pathname !== '/ws' || (origin && new URL(origin).hostname !== (req.headers.host ?? '').split(':')[0] && !flag('allow-any-origin'))) {
    socket.write('HTTP/1.1 403 Forbidden\r\n\r\n');
    return socket.destroy();
  }
  wss.handleUpgrade(req, socket, head, (ws) => onConnection(ws, req));
});

function onConnection(ws, req) {
  const tag = `[${req.socket.remoteAddress}]`;
  let phase = 'new';
  let pairNc = null;
  let pairNs = null;
  let nc = null;
  let ns = null;
  let device = null;
  let rx = null;
  let tx = null;
  let isController = false;
  let tokens = 200;
  let refilled = Date.now();

  const sendJson = (o) => ws.send(JSON.stringify(o));
  const ctx = {
    send: (plain) => {
      if (isController && tx && ws.readyState === 1) ws.send(tx.seal(plain));
    },
  };
  const fail = (code, why) => {
    log(tag, `close ${code} (${why})`);
    ws.close(code, why);
  };

  ws.on('close', () => {
    if (controller === ctx) controller = null;
    sockets.delete(ctx);
    log(tag, 'closed');
  });

  ws.on('message', async (data, isBinary) => {
    try {
      if (!isBinary) {
        const m = JSON.parse(data.toString('utf8'));
        switch (m.t) {
          case 'pair.hello': {
            if (phase !== 'new') return fail(4400, 'unexpected pair.hello');
            if (Date.now() > kExpires || pairingPending) return fail(4408, 'pairing window closed, expired or busy');
            const name = String(m.name ?? '');
            const nonce = b64decode(String(m.nc));
            if (nonce.length !== 16 || !name || new TextEncoder().encode(name).length > 64) return fail(4400, 'bad hello');
            if (!macEqual(b64decode(String(m.mac)), pairHelloMac(k, nonce, name))) {
              if (++failures >= 5) newSecret('5 failed attempts');
              return fail(4401, 'bad mac');
            }
            const kUsed = k;
            newSecret('spent by a valid pair.hello'); // single use, whatever the user decides
            pairingPending = true;
            pairNc = nonce;
            pairNs = randomBytes(16);
            phase = 'pending';
            const code = pairingCode(kUsed, pairNc, pairNs);
            sendJson({ t: 'pair.pending', ns: b64encode(pairNs) });
            log(tag, `pairing request from "${name}", code ${code}`);
            const ok = await ask(`Allow "${name}" (code ${code})?`);
            pairingPending = false;
            if (ws.readyState !== 1) return;
            if (!ok) {
              sendJson({ t: 'pair.denied' });
              return ws.close(1000, 'denied');
            }
            const id = randomBytes(8);
            const secret = randomBytes(32);
            devices.set(b64encode(id), { secret: bytesToHex(secret), name });
            saveDevices();
            sendJson({ t: 'pair.ok', id: b64encode(id), wrap: b64encode(wrapSecret(kUsed, pairNc, pairNs, secret)) });
            log(tag, `paired "${name}"`);
            return ws.close(1000, 'paired'); // the phone now connects on a new socket
          }
          case 'hello': {
            if (phase !== 'new') return fail(4400, 'unexpected hello');
            device = devices.get(String(m.id)) ?? null; // unknown ids also get a challenge
            nc = b64decode(String(m.nc));
            if (nc.length !== 16) return fail(4400, 'bad nc');
            ns = randomBytes(16);
            phase = 'challenged';
            return sendJson({ t: 'challenge', ns: b64encode(ns) });
          }
          case 'auth': {
            if (phase !== 'challenged') return fail(4400, 'unexpected auth');
            if (!device) return fail(4401, 'unknown device');
            const S = hexToBytes(device.secret);
            if (!macEqual(b64decode(String(m.mac)), authMac(S, nc, ns))) return fail(4401, 'bad auth mac');
            const keys = sessionKeys(S, nc, ns);
            rx = new FrameReceiver(keys.c2s, DIR_C2S);
            tx = new FrameSender(keys.s2c, DIR_S2C);
            phase = 'ready';
            sendJson({ t: 'ready', mac: b64encode(readyMac(S, nc, ns)) });
            log(tag, `authenticated "${device.name}"`);
            if (controller && controller !== ctx) {
              phase = 'busy';
              return sendJson({ t: 'busy' });
            }
            becomeController();
            return;
          }
          case 'takeover': {
            if (phase !== 'busy') return;
            const ok = !controller || (await ask(`"${device.name}" wants to take control. Allow?`));
            if (ws.readyState !== 1) return;
            if (!ok) return sendJson({ t: 'takeover.denied' });
            controller?.close?.();
            sendJson({ t: 'active' });
            becomeController();
            return;
          }
          default:
            return fail(4400, 'unknown message');
        }
      }
      // binary: encrypted frames
      const now = Date.now();
      tokens = Math.min(200, tokens + ((now - refilled) / 1000) * 200);
      refilled = now;
      if (--tokens < 0) return fail(4429, 'rate limited');
      if (!rx || phase !== 'ready' || !isController) return; // not the controller: ignore
      const plain = rx.open(new Uint8Array(data));
      if (!plain) return log(tag, 'dropped frame (bad tag, replay or reorder)');
      handleOp(decodeOp(plain), tag, ctx);
    } catch (e) {
      log(tag, 'error', e instanceof Error ? e.message : e);
      fail(4400, 'malformed');
    }
  });

  function becomeController() {
    phase = 'ready';
    isController = true;
    ctx.close = () => ws.close(4409, 'replaced');
    controller = ctx;
    sockets.add(ctx);
    ctx.send(stateFrame());
    ctx.send(remotesFrame()); // provisional: remotes are pushed, never requested
  }
}

function handleOp(op, tag, ctx) {
  if (!op) return log(tag, 'unparseable op');
  switch (op.op) {
    case 'move':
    case 'wheel':
      return log(tag, op.op, op.dx, op.dy);
    case 'button':
      return log(tag, 'button', ['left', 'right', 'middle'][op.button] ?? op.button, op.down ? 'down' : 'up');
    case 'text':
      return log(tag, 'text', JSON.stringify(op.text));
    case 'key':
      return log(tag, 'key', `0x${op.vk.toString(16)}`, ['', 'down', 'up', 'press'][op.flags] ?? op.flags);
    case 'media': {
      log(tag, 'media', Object.keys(MEDIA).find((n) => MEDIA[n] === op.cmd) ?? op.cmd);
      if (op.cmd === MEDIA.playpause && pcState.media) pcState.media.status = pcState.media.status === 'playing' ? 'paused' : 'playing';
      if (op.cmd === MEDIA.volup) pcState.volume.level = Math.min(100, pcState.volume.level + 2);
      if (op.cmd === MEDIA.voldown) pcState.volume.level = Math.max(0, pcState.volume.level - 2);
      if (op.cmd === MEDIA.mute) pcState.volume.muted = !pcState.volume.muted;
      return ctx.send(stateFrame());
    }
    case 'ping':
      log(tag, 'ping -> ping');
      return ctx.send(encPing());
    case 'json': {
      const m = op.value;
      log(tag, 'json', JSON.stringify(m));
      if (m?.t !== 'invoke') throw new Error('only invoke is accepted'); // the core closes with 4400
      // the phone names an item; the PC looks the action up itself
      const remote = REMOTES[m.remote];
      const item = remote?.layout.items.find((it, i) => (it.id ?? String(i)) === m.item);
      if (!item) return ctx.send(encJson({ t: 'result', rid: m.rid, ok: false, err: 'unsupported' }));
      if (item.icon === 'play' || item.icon === 'pause') pcState.media.status = pcState.media.status === 'playing' ? 'paused' : 'playing';
      if (item.type === 'slider' && typeof m.value === 'number') pcState.volume.level = Math.round(m.value);
      ctx.send(encJson({ t: 'result', rid: m.rid, ok: true }));
      return ctx.send(stateFrame());
    }
    default:
      return log(tag, 'unhandled', op.op);
  }
}

server.listen(PORT, HOST, () => {
  log(`mock server for "${PC_NAME}" on http://${HOST}:${PORT} (${AUTO ? 'auto-approve' : 'type y/n to approve'})`);
  log(`known devices: ${devices.size}. Static files from ${DIST}${existsSync(DIST) ? '' : ' (missing: run npm run build)'}`);
  log(`pair on this PC:  ${pairUrl()}`);
  log(`pair from phone:  ${pairUrl(`${lanIp()}:${PORT}`)}`);
  log('stdin commands: y / n, revoke, new');
});
