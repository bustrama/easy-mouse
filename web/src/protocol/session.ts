import { b64decode, b64encode } from './b64.ts';
import {
  authMac,
  DIR_C2S,
  DIR_S2C,
  FrameReceiver,
  FrameSender,
  macEqual,
  NONCE_LEN,
  pairHelloMac,
  pairingCode,
  randomBytes,
  readyMac,
  sessionKeys,
  unwrapSecret,
} from './crypto.ts';
import { decodeOp, encPing } from './ops.ts';

export interface Credentials {
  /** b64 device id */
  id: string;
  secret: Uint8Array;
}

export type Phase = 'connecting' | 'pairing' | 'pending' | 'auth' | 'online' | 'busy' | 'closed';

export interface CloseInfo {
  /** WebSocket close code; 4000 = local timeout, 4001 = protocol violation, 4002 = denied, 4003 = server failed authentication */
  code: number;
  /** true when the connection had reached `online` */
  wasOnline: boolean;
}

export interface Handlers {
  onPhase?(phase: Phase): void;
  onCode?(code: string): void;
  onPaired?(creds: Credentials): void;
  onState?(state: unknown): void;
  onJson?(msg: unknown): void;
  onClose?(info: CloseInfo): void;
}

/** The subset of WebSocket this class needs, so tests can inject a fake. */
export interface WsLike {
  binaryType: string;
  send(data: string | Uint8Array): void;
  close(code?: number, reason?: string): void;
  onopen: ((ev: unknown) => void) | null;
  onmessage: ((ev: { data: unknown }) => void) | null;
  onclose: ((ev: { code: number }) => void) | null;
  onerror: ((ev: unknown) => void) | null;
}

export interface SessionOptions {
  /** Pair with this one-time secret (from the QR fragment) instead of connecting. */
  pair?: { k: Uint8Array; name: string };
  /** Existing device credentials. Also used right after pairing succeeds. */
  creds?: Credentials;
  handlers: Handlers;
  pingMs?: number;
  silenceMs?: number;
}

export const CLOSE_TIMEOUT = 4000;
export const CLOSE_PROTOCOL = 4001;
export const CLOSE_DENIED = 4002;
export const CLOSE_BAD_SERVER = 4003;

/** One socket's lifetime: pairing and/or connect handshake, then encrypted frames. */
export class Session {
  phase: Phase = 'connecting';
  private readonly ws: WsLike;
  private readonly opts: SessionOptions;
  private readonly h: Handlers;
  private k: Uint8Array | null;
  private creds: Credentials | null;
  private nc: Uint8Array = new Uint8Array(0);
  private ns: Uint8Array = new Uint8Array(0);
  private sender: FrameSender | null = null;
  private receiver: FrameReceiver | null = null;
  private lastRx = 0;
  private lastTx = 0;
  private timer: ReturnType<typeof setInterval> | null = null;
  private wasOnline = false;
  private closed = false;

  constructor(ws: WsLike, opts: SessionOptions) {
    this.ws = ws;
    this.opts = opts;
    this.h = opts.handlers;
    this.k = opts.pair?.k ?? null;
    this.creds = opts.creds ?? null;
    ws.binaryType = 'arraybuffer';
    ws.onopen = () => this.start();
    ws.onmessage = (ev) => this.onMessage(ev.data);
    ws.onclose = (ev) => this.finish(ev.code);
    ws.onerror = () => {
      /* a close event always follows */
    };
  }

  private setPhase(p: Phase): void {
    this.phase = p;
    this.h.onPhase?.(p);
  }

  private sendJson(obj: unknown): void {
    this.ws.send(JSON.stringify(obj));
  }

  private start(): void {
    this.lastRx = Date.now();
    if (this.opts.pair && this.k) {
      this.nc = randomBytes(NONCE_LEN);
      const { name } = this.opts.pair;
      this.setPhase('pairing');
      this.sendJson({ t: 'pair.hello', name, nc: b64encode(this.nc), mac: b64encode(pairHelloMac(this.k, this.nc, name)) });
    } else if (this.creds) {
      this.sendHello();
    } else {
      this.abort(CLOSE_PROTOCOL);
    }
  }

  private sendHello(): void {
    if (!this.creds) return this.abort(CLOSE_PROTOCOL);
    this.nc = randomBytes(NONCE_LEN);
    this.setPhase('auth');
    this.sendJson({ t: 'hello', id: this.creds.id, nc: b64encode(this.nc) });
  }

  private onMessage(data: unknown): void {
    this.lastRx = Date.now();
    try {
      if (typeof data === 'string') this.onText(data);
      else if (data instanceof ArrayBuffer) this.onBinary(new Uint8Array(data));
    } catch {
      this.abort(CLOSE_PROTOCOL);
    }
  }

  private onText(text: string): void {
    const m = JSON.parse(text) as Record<string, unknown>;
    switch (m['t']) {
      case 'pair.pending': {
        if (this.phase !== 'pairing' || !this.k) throw new Error('unexpected pair.pending');
        this.ns = b64decode(String(m['ns']));
        if (this.ns.length !== NONCE_LEN) throw new Error('bad ns');
        this.setPhase('pending');
        this.h.onCode?.(pairingCode(this.k, this.nc, this.ns));
        return;
      }
      case 'pair.ok': {
        if (this.phase !== 'pending' || !this.k) throw new Error('unexpected pair.ok');
        const secret = unwrapSecret(this.k, this.nc, this.ns, b64decode(String(m['wrap'])));
        this.creds = { id: String(m['id']), secret };
        this.k = null; // spent
        this.h.onPaired?.(this.creds);
        this.sendHello();
        return;
      }
      case 'pair.denied':
        this.abort(CLOSE_DENIED);
        return;
      case 'challenge': {
        if (this.phase !== 'auth' || !this.creds) throw new Error('unexpected challenge');
        this.ns = b64decode(String(m['ns']));
        if (this.ns.length !== NONCE_LEN) throw new Error('bad ns');
        this.sendJson({ t: 'auth', mac: b64encode(authMac(this.creds.secret, this.nc, this.ns)) });
        return;
      }
      case 'ready': {
        if (this.phase !== 'auth' || !this.creds) throw new Error('unexpected ready');
        const expected = readyMac(this.creds.secret, this.nc, this.ns);
        if (!macEqual(expected, b64decode(String(m['mac'])))) return this.abort(CLOSE_BAD_SERVER);
        const keys = sessionKeys(this.creds.secret, this.nc, this.ns);
        this.sender = new FrameSender(keys.c2s, DIR_C2S);
        this.receiver = new FrameReceiver(keys.s2c, DIR_S2C);
        this.wasOnline = true;
        this.lastTx = Date.now();
        this.setPhase('online');
        this.startTimer();
        return;
      }
      case 'busy':
        if (this.phase === 'online' || this.phase === 'auth') this.setPhase('busy');
        return;
      case 'granted':
        if (this.phase === 'busy') this.setPhase('online');
        return;
      default:
        return; // ignore unknown control frames
    }
  }

  private onBinary(frame: Uint8Array): void {
    if (this.phase !== 'online' || !this.receiver) return;
    const plain = this.receiver.open(frame);
    if (!plain) return; // forged, replayed or reordered: dropped
    const op = decodeOp(plain);
    if (!op) return;
    if (op.op === 'state') this.h.onState?.(op.value);
    else if (op.op === 'json') this.h.onJson?.(op.value);
    else if (op.op === 'ping') this.sendPlain(encPing());
  }

  private startTimer(): void {
    const pingMs = this.opts.pingMs ?? 15000;
    const silenceMs = this.opts.silenceMs ?? 25000;
    this.timer = setInterval(
      () => {
        const now = Date.now();
        if (now - this.lastRx > silenceMs) return this.abort(CLOSE_TIMEOUT);
        if (now - this.lastTx > pingMs) this.sendPlain(encPing());
      },
      Math.min(pingMs, 5000),
    );
  }

  /** Sends one encrypted frame. Returns false when the session is not the active controller. */
  sendPlain(plaintext: Uint8Array): boolean {
    if (this.phase !== 'online' || !this.sender) return false;
    this.ws.send(this.sender.seal(plaintext));
    this.lastTx = Date.now();
    return true;
  }

  /** Ask to take control from another device (the PC user must approve). */
  takeover(): void {
    if (this.phase === 'busy') this.sendJson({ t: 'takeover' });
  }

  close(): void {
    this.abort(1000);
  }

  private abort(code: number): void {
    try {
      this.ws.close(code >= 4000 && code < 5000 ? code : 1000);
    } catch {
      /* already closed */
    }
    this.finish(code);
  }

  private finish(code: number): void {
    if (this.closed) return;
    this.closed = true;
    if (this.timer) clearInterval(this.timer);
    this.sender = null;
    this.receiver = null;
    this.k = null;
    this.setPhase('closed');
    this.h.onClose?.({ code, wasOnline: this.wasOnline });
  }
}
