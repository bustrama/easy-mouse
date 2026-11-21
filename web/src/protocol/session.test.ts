import { describe, expect, it } from 'vitest';
import { b64decode, b64encode } from './b64.ts';
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
} from './crypto.ts';
import { decodeOp, encMove, encState } from './ops.ts';
import { type Credentials, Session, type WsLike } from './session.ts';

/** A scripted server end implementing docs/protocol.md; frames pass through the real crypto. */
class FakeSocket implements WsLike {
  binaryType = 'blob';
  onopen: WsLike['onopen'] = null;
  onmessage: WsLike['onmessage'] = null;
  onclose: WsLike['onclose'] = null;
  onerror: WsLike['onerror'] = null;
  sent: (string | Uint8Array)[] = [];
  closedWith: number | null = null;
  onSend: (d: string | Uint8Array) => void = () => {};
  send(d: string | Uint8Array): void {
    this.sent.push(d);
    this.onSend(d);
  }
  close(code?: number): void {
    this.closedWith = code ?? 1000;
  }
  deliver(d: string | Uint8Array): void {
    this.onmessage?.({ data: typeof d === 'string' ? d : d.buffer.slice(d.byteOffset, d.byteOffset + d.byteLength) });
  }
}

const J = (x: unknown) => JSON.stringify(x);

function connectServer(sock: FakeSocket, secret: Uint8Array, opts: { badReadyMac?: boolean } = {}) {
  const ns = randomBytes(16);
  const srv: { rx?: FrameReceiver; tx?: FrameSender; received: unknown[] } = { received: [] };
  let nc: Uint8Array;
  sock.onSend = (d) => {
    if (typeof d === 'string') {
      const m = JSON.parse(d);
      if (m.t === 'hello') {
        nc = b64decode(m.nc);
        sock.deliver(J({ t: 'challenge', ns: b64encode(ns) }));
      } else if (m.t === 'auth') {
        expect(macEqual(b64decode(m.mac), authMac(secret, nc, ns))).toBe(true);
        const mac = opts.badReadyMac ? randomBytes(32) : readyMac(secret, nc, ns);
        const k = sessionKeys(secret, nc, ns);
        srv.rx = new FrameReceiver(k.c2s, DIR_C2S);
        srv.tx = new FrameSender(k.s2c, DIR_S2C);
        sock.deliver(J({ t: 'ready', mac: b64encode(mac) }));
      }
    } else if (srv.rx) {
      const p = srv.rx.open(d);
      if (p) srv.received.push(decodeOp(p));
    }
  };
  return srv;
}

describe('connect handshake', () => {
  const creds: Credentials = { id: 'AAECAwQFBgc', secret: randomBytes(32) };

  it('authenticates, derives keys, and exchanges encrypted frames both ways', () => {
    const sock = new FakeSocket();
    const srv = connectServer(sock, creds.secret);
    const states: unknown[] = [];
    const phases: string[] = [];
    const s = new Session(sock, { creds, handlers: { onPhase: (p) => phases.push(p), onState: (x) => states.push(x) } });
    sock.onopen?.({});
    expect(phases).toEqual(['auth', 'online']);
    expect(s.sendPlain(encMove(3, 4))).toBe(true);
    expect(srv.received).toEqual([{ op: 'move', dx: 3, dy: 4 }]);
    sock.deliver(srv.tx!.seal(encState({ volume: 40 })));
    expect(states).toEqual([{ volume: 40 }]);
  });

  it('drops replayed server frames', () => {
    const sock = new FakeSocket();
    const srv = connectServer(sock, creds.secret);
    const states: unknown[] = [];
    new Session(sock, { creds, handlers: { onState: (x) => states.push(x) } });
    sock.onopen?.({});
    const f = srv.tx!.seal(encState({ n: 1 }));
    sock.deliver(f);
    sock.deliver(f);
    expect(states).toHaveLength(1);
  });

  it('aborts if the server cannot prove it knows the secret', () => {
    const sock = new FakeSocket();
    connectServer(sock, creds.secret, { badReadyMac: true });
    let closed: number | null = null;
    const s = new Session(sock, { creds, handlers: { onClose: (i) => (closed = i.code) } });
    sock.onopen?.({});
    expect(closed).toBe(4003);
    expect(s.sendPlain(encMove(1, 1))).toBe(false);
  });

  it('reports busy and sends takeover', () => {
    const sock = new FakeSocket();
    connectServer(sock, creds.secret);
    let denied = 0;
    const s = new Session(sock, { creds, handlers: { onTakeoverDenied: () => denied++ } });
    sock.onopen?.({});
    sock.deliver(J({ t: 'busy' }));
    expect(s.phase).toBe('busy');
    expect(s.sendPlain(encMove(1, 1))).toBe(false);
    s.takeover();
    expect(JSON.parse(sock.sent.at(-1) as string)).toEqual({ t: 'takeover' });
    sock.deliver(J({ t: 'takeover.denied' }));
    expect(denied).toBe(1);
    expect(s.phase).toBe('busy');
    sock.deliver(J({ t: 'active' }));
    expect(s.phase).toBe('online');
  });

  it('closes on a malformed handshake message', () => {
    const sock = new FakeSocket();
    let code: number | null = null;
    new Session(sock, { creds, handlers: { onClose: (i) => (code = i.code) } });
    sock.onopen?.({});
    sock.deliver('{not json');
    expect(code).toBe(4001);
    expect(sock.closedWith).toBe(4001);
  });

  it('reports the server close code and whether it was ever online', () => {
    const sock = new FakeSocket();
    connectServer(sock, creds.secret);
    const info: unknown[] = [];
    new Session(sock, { creds, handlers: { onClose: (i) => info.push(i) } });
    sock.onopen?.({});
    sock.onclose?.({ code: 4401 });
    expect(info).toEqual([{ code: 4401, wasOnline: true, paired: false }]);
  });
});

describe('pairing', () => {
  it('pairs, shows the matching code, hands over credentials, then closes so the caller can connect', () => {
    const k = randomBytes(16);
    const S = randomBytes(32);
    const sock = new FakeSocket();
    const ns = randomBytes(16);
    let nc: Uint8Array;
    sock.onSend = (d) => {
      const m = JSON.parse(d as string);
      expect(m.t).toBe('pair.hello'); // nothing else is ever sent on the pairing socket
      nc = b64decode(m.nc);
      expect(macEqual(b64decode(m.mac), pairHelloMac(k, nc, m.name))).toBe(true);
      sock.deliver(J({ t: 'pair.pending', ns: b64encode(ns) }));
      // the PC user clicks Allow
      sock.deliver(J({ t: 'pair.ok', id: 'AAECAwQFBgc', wrap: b64encode(wrapSecret(k, nc, ns, S)) }));
    };
    let code = '';
    let paired: Credentials | undefined;
    let close: unknown;
    const phases: string[] = [];
    new Session(sock, {
      pair: { k, name: "Dana's iPhone" },
      handlers: { onCode: (c) => (code = c), onPaired: (c) => (paired = c), onPhase: (p) => phases.push(p), onClose: (i) => (close = i) },
    });
    sock.onopen?.({});
    expect(code).toBe(pairingCode(k, nc!, ns));
    expect(code).toMatch(/^[0-9]{4}$/);
    expect(paired?.id).toBe('AAECAwQFBgc');
    expect(paired?.secret).toEqual(S);
    expect(phases).toEqual(['pairing', 'pending', 'closed']);
    expect(close).toEqual({ code: 1000, wasOnline: false, paired: true });
    expect(sock.closedWith).toBe(1000);
  });

  it('a denied pairing closes the session', () => {
    const sock = new FakeSocket();
    sock.onSend = () => sock.deliver(J({ t: 'pair.denied' }));
    let code: number | null = null;
    new Session(sock, { pair: { k: randomBytes(16), name: 'x' }, handlers: { onClose: (i) => (code = i.code) } });
    sock.onopen?.({});
    expect(code).toBe(4002);
  });

  it('rejects a wrap that does not decrypt under the shared secret', () => {
    const k = randomBytes(16);
    const sock = new FakeSocket();
    sock.onSend = (d) => {
      const m = JSON.parse(d as string);
      if (m.t !== 'pair.hello') return;
      sock.deliver(J({ t: 'pair.pending', ns: b64encode(randomBytes(16)) }));
      sock.deliver(J({ t: 'pair.ok', id: 'AAECAwQFBgc', wrap: b64encode(randomBytes(48)) }));
    };
    let paired = false;
    let code: number | null = null;
    new Session(sock, {
      pair: { k, name: 'x' },
      handlers: { onPaired: () => (paired = true), onClose: (i) => (code = i.code) },
    });
    sock.onopen?.({});
    expect(paired).toBe(false);
    expect(code).toBe(4001);
  });
});
