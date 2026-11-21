import { detectLang, type Lang, type LangPref } from './i18n/strings.ts';
import { takePairingSecret } from './protocol/fragment.ts';
import {
  encButton,
  encJson,
  encKey,
  encMedia,
  KEYFLAG,
  MEDIA,
  encMove,
  encText,
  encWheel,
} from './protocol/ops.ts';
import { type CloseInfo, Session, type Credentials, type WsLike } from './protocol/session.ts';
import { EMPTY_STATE, type PcState, parseState } from './pcstate.ts';
import { type Remote, sanitizeRemotes } from './remotes.ts';
import { clearCredentials, loadCredentials, saveCredentials, storageGet, storageSet } from './storage.ts';
import { createStore } from './store.ts';

export type Screen = 'scan' | 'pairing' | 'offline' | 'busy' | 'online';
export type ScanReason = 'none' | 'revoked' | 'denied' | 'expired' | 'failed';
export type Tab = 'mouse' | 'keyboard' | 'media' | 'remotes';

export interface AppState {
  screen: Screen;
  scanReason: ScanReason;
  /** true until the first connect attempt has either succeeded or failed */
  connecting: boolean;
  replaced: boolean;
  takeoverAsked: boolean;
  takeoverDenied: boolean;
  /** short-lived message, e.g. an unsupported remote action */
  toast: 'unsupported' | 'failed' | null;
  pcName: string | null;
  code: string;
  pc: PcState;
  remotes: Remote[];
  tab: Tab;
  langPref: LangPref;
  lang: Lang;
  sensitivity: number;
}

const SENS_KEY = 'em.sens';
const LANG_KEY = 'em.lang';
const PC_KEY = 'em.pc';
export const DEFAULT_SENSITIVITY = 1.4;

function loadSensitivity(): number {
  const v = Number(storageGet(SENS_KEY));
  return Number.isFinite(v) && v >= 0.4 && v <= 3.5 ? v : DEFAULT_SENSITIVITY;
}

function loadLangPref(): LangPref {
  const v = storageGet(LANG_KEY);
  return v === 'en' || v === 'he' ? v : 'auto';
}

export const resolveLang = (pref: LangPref): Lang => (pref === 'auto' ? detectLang(navigator.languages ?? [navigator.language]) : pref);

/** A readable device name for the PC's approval dialog, e.g. "Pixel 8 (Chrome)". */
export function deviceName(ua: string = navigator.userAgent): string {
  let device: string;
  if (/iPhone/.test(ua)) device = 'iPhone';
  else if (/iPad/.test(ua)) device = 'iPad';
  else {
    const m = /Android [\d.]+; ([^;)]+)/.exec(ua);
    device = m?.[1]?.trim() || 'Android phone';
  }
  const browser = /CriOS|Chrome\//.test(ua) ? 'Chrome' : /FxiOS|Firefox\//.test(ua) ? 'Firefox' : /EdgiOS|Edg\//.test(ua) ? 'Edge' : /Safari\//.test(ua) ? 'Safari' : 'browser';
  let name = `${device} (${browser})`;
  while (new TextEncoder().encode(name).length > 64) name = name.slice(0, -1);
  return name;
}

const wsUrl = (): string => `${location.protocol === 'https:' ? 'wss:' : 'ws:'}//${location.host}/ws`;

type Release = () => void;

class Client {
  readonly store = createStore<AppState>({
    screen: 'offline',
    scanReason: 'none',
    connecting: true,
    replaced: false,
    takeoverAsked: false,
    takeoverDenied: false,
    toast: null,
    pcName: storageGet(PC_KEY),
    code: '',
    pc: EMPTY_STATE,
    remotes: [],
    tab: 'mouse',
    langPref: loadLangPref(),
    lang: resolveLang(loadLangPref()),
    sensitivity: loadSensitivity(),
  });

  private session: Session | null = null;
  private creds: Credentials | null = null;
  private pairActive = false;
  private attempts = 0;
  private retryTimer: ReturnType<typeof setTimeout> | null = null;
  private offlineTimer: ReturnType<typeof setTimeout> | null = null;
  private hiddenAt = 0;
  private readonly releases = new Set<Release>();

  /** Call once at startup, before the first render. Reads and removes the QR fragment. */
  start(): void {
    this.applyLang();
    const k = takePairingSecret(location, history);
    this.creds = loadCredentials();
    document.addEventListener('visibilitychange', () => this.onVisibility());
    window.addEventListener('online', () => this.retryNow());
    window.addEventListener('pagehide', () => this.releaseInputs());
    void this.fetchInfo();
    if (k) {
      this.startPair(k);
    } else if (this.creds) {
      this.connect();
    } else {
      this.store.set({ screen: 'scan', scanReason: 'none', connecting: false });
    }
  }

  // ---- settings ----

  setLangPref(pref: LangPref): void {
    storageSet(LANG_KEY, pref);
    this.store.set({ langPref: pref, lang: resolveLang(pref) });
    this.applyLang();
  }

  private applyLang(): void {
    const { lang } = this.store.get();
    document.documentElement.lang = lang;
    document.documentElement.dir = lang === 'he' ? 'rtl' : 'ltr';
  }

  setSensitivity(v: number): void {
    storageSet(SENS_KEY, String(v));
    this.store.set({ sensitivity: v });
  }

  setTab(tab: Tab): void {
    this.releaseInputs();
    this.store.set({ tab });
  }

  // ---- pairing and connecting ----

  private startPair(k: Uint8Array): void {
    this.pairActive = true;
    this.store.set({ screen: 'pairing', code: '', connecting: false });
    this.open({ pair: { k, name: deviceName() } });
  }

  cancelPair(): void {
    this.pairActive = false;
    this.dropSession();
    this.store.set({ screen: 'scan', scanReason: 'none' });
  }

  /** Forget this device and ask the user to scan a new code. */
  forgetDevice(): void {
    this.clearTimers();
    this.dropSession();
    clearCredentials();
    this.creds = null;
    this.store.set({ screen: 'scan', scanReason: 'none', connecting: false });
  }

  connect(): void {
    if (!this.creds) return;
    this.clearTimers();
    this.open({ creds: this.creds });
  }

  retryNow(): void {
    const { screen } = this.store.get();
    if (this.pairActive || screen === 'scan' || screen === 'online' || screen === 'busy' || !this.creds) return;
    this.attempts = 0;
    this.store.set({ replaced: false });
    this.connect();
  }

  private open(opts: { pair?: { k: Uint8Array; name: string }; creds?: Credentials }): void {
    this.dropSession();
    const ws = new WebSocket(wsUrl()) as unknown as WsLike;
    let self: Session | null = null;
    const current = () => self === this.session;
    self = new Session(ws, {
      ...opts,
      handlers: {
        onCode: (code) => current() && this.store.set({ code }),
        onPaired: (creds) => {
          if (!current()) return;
          this.pairActive = false;
          this.creds = creds;
          saveCredentials(creds);
        },
        onPhase: (phase) => current() && this.onPhase(phase),
        onState: (v) => {
          if (!current()) return;
          this.assumedVolume = null; // a fresh snapshot replaces our estimate
          this.store.set({ pc: parseState(v) });
        },
        onTakeoverDenied: () => current() && this.store.set({ takeoverAsked: false, takeoverDenied: true }),
        onJson: (v) => current() && this.onJson(v),
        onClose: (info) => current() && this.onClose(info),
      },
    });
    this.session = self;
  }

  /** Detach first so the old session's close event is ignored as stale. */
  private dropSession(): void {
    const old = this.session;
    this.session = null;
    old?.close();
  }

  private onPhase(phase: string): void {
    if (phase === 'online') {
      this.attempts = 0;
      this.clearOfflineTimer();
      this.store.set({ screen: 'online', connecting: false, replaced: false, takeoverAsked: false, takeoverDenied: false });
    } else if (phase === 'busy') {
      this.releaseInputs();
      this.store.set({ screen: 'busy', connecting: false });
    }
  }

  private onJson(v: unknown): void {
    if (typeof v !== 'object' || v === null) return;
    const m = v as Record<string, unknown>;
    if (m['t'] === 'remotes') {
      this.store.set({ remotes: sanitizeRemotes(m) });
    } else if (m['t'] === 'result' && m['ok'] === false) {
      this.showToast(m['err'] === 'unsupported' ? 'unsupported' : 'failed');
    }
  }

  private onClose(info: CloseInfo): void {
    this.session = null;
    this.releaseInputs();
    if (info.paired) {
      // pairing finished: the server closed that socket, connect with the new credentials
      this.connect();
      return;
    }
    if (this.pairActive) {
      this.pairActive = false;
      const reason: ScanReason = info.code === 4002 ? 'denied' : info.code === 4408 ? 'expired' : 'failed';
      this.store.set({ screen: 'scan', scanReason: reason, connecting: false });
      return;
    }
    if (info.code === 4401 && (info.authSent || info.wasOnline)) {
      // the server refused our credentials (revoked, expired, unknown). A 4401 before we sent `auth` is
      // only its handshake timeout (for example the page was suspended), so that case just retries.
      clearCredentials();
      this.creds = null;
      this.store.set({ screen: 'scan', scanReason: 'revoked', connecting: false });
      return;
    }
    if (info.code === 4409) {
      // another device took over: do not fight for control automatically
      this.showOffline({ replaced: true });
      return;
    }
    this.showOffline({});
    const delay = this.attempts === 0 && info.wasOnline ? 0 : Math.min(5000, 500 * 2 ** Math.min(this.attempts, 4));
    this.attempts++;
    this.retryTimer = setTimeout(() => this.connect(), delay);
  }

  private showOffline(extra: { replaced?: boolean }): void {
    const wasOnline = this.store.get().screen === 'online' || this.store.get().screen === 'busy';
    const apply = () => this.store.set({ screen: 'offline', connecting: false, replaced: extra.replaced ?? false });
    if (extra.replaced || !wasOnline) return apply();
    // a quick reconnect should not flash the offline screen
    this.clearOfflineTimer();
    this.offlineTimer = setTimeout(apply, 700);
  }

  private clearOfflineTimer(): void {
    if (this.offlineTimer) clearTimeout(this.offlineTimer);
    this.offlineTimer = null;
  }

  private clearTimers(): void {
    if (this.retryTimer) clearTimeout(this.retryTimer);
    this.retryTimer = null;
  }

  takeover(): void {
    this.store.set({ takeoverAsked: true, takeoverDenied: false });
    this.session?.takeover();
  }

  private onVisibility(): void {
    if (document.visibilityState === 'hidden') {
      this.hiddenAt = Date.now();
      this.releaseInputs();
      return;
    }
    if (this.pairActive || !this.creds) return;
    const hiddenFor = Date.now() - this.hiddenAt;
    const phase = this.session?.phase;
    // iOS and Android drop sockets in the background without telling the page: reconnect fresh.
    if (!this.session || phase === 'closed' || hiddenFor > 3000) {
      this.attempts = 0;
      this.clearTimers();
      this.connect();
    }
  }

  private async fetchInfo(): Promise<void> {
    try {
      const ctl = new AbortController();
      const t = setTimeout(() => ctl.abort(), 3000);
      const r = await fetch('/api/info', { cache: 'no-store', signal: ctl.signal });
      clearTimeout(t);
      const j = (await r.json()) as { name?: unknown };
      if (typeof j.name === 'string' && j.name) {
        const name = j.name.slice(0, 64);
        storageSet(PC_KEY, name);
        this.store.set({ pcName: name });
      }
    } catch {
      /* the name is cosmetic */
    }
  }

  // ---- input API used by the screens ----

  /** Register a callback that must release held keys/buttons (page hidden, tab change, socket lost). */
  onRelease(fn: Release): () => void {
    this.releases.add(fn);
    return () => this.releases.delete(fn);
  }

  private releaseInputs(): void {
    for (const fn of [...this.releases]) fn();
  }

  private sendPlain(p: Uint8Array): boolean {
    return this.session?.sendPlain(p) ?? false;
  }

  move = (dx: number, dy: number) => this.sendPlain(encMove(dx, dy));
  wheel = (dx: number, dy: number) => this.sendPlain(encWheel(dx, dy));
  button = (b: number, down: boolean) => this.sendPlain(encButton(b, down));
  key = (vk: number, flags: number) => this.sendPlain(encKey(vk, flags));
  media = (cmd: number) => this.sendPlain(encMedia(cmd));
  json = (obj: unknown) => this.sendPlain(encJson(obj));

  private rid = 0;
  /** Press a remote's button or move its slider: the phone names the item, the PC owns the action. */
  invoke(remote: string, item: string, value?: number): boolean {
    return this.json({ t: 'invoke', rid: this.rid++, remote, item, ...(value === undefined ? {} : { value }) });
  }

  private toastTimer: ReturnType<typeof setTimeout> | null = null;
  private showToast(toast: 'unsupported' | 'failed'): void {
    if (this.toastTimer) clearTimeout(this.toastTimer);
    this.store.set({ toast });
    this.toastTimer = setTimeout(() => this.store.set({ toast: null }), 2500);
  }

  private assumedVolume: number | null = null;
  /**
   * The protocol has volume steps (media op 4/5), not an absolute level. Each step is 2 %, so move
   * toward the target by the number of steps needed; remember the estimate until the next snapshot.
   */
  setVolume(target: number): void {
    const cur = this.assumedVolume ?? this.store.get().pc.volume?.level;
    if (cur === undefined) return;
    const steps = Math.max(-50, Math.min(50, Math.round((target - cur) / 2)));
    if (steps === 0) return;
    for (let i = 0; i < Math.abs(steps); i++) this.media(steps > 0 ? MEDIA.volup : MEDIA.voldown);
    this.assumedVolume = Math.max(0, Math.min(100, cur + steps * 2));
  }

  /** Arrow keys are the near-universal seek keys of players (the focused player decides how far). */
  seek(direction: -1 | 1): void {
    this.key(direction < 0 ? 0x25 : 0x27, KEYFLAG.press);
  }
  text(text: string): boolean {
    const frames = encText(text);
    return frames.length > 0 && frames.every((f) => this.sendPlain(f));
  }
}

export const client = new Client();
