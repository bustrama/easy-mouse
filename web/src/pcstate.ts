// Parsing of the server's `state` message (docs/protocol.md, provisional). Defensive: unknown or
// ill-typed fields are dropped, strings are length-limited, and everything is rendered as text.

export interface MediaInfo {
  app: string;
  title: string;
  artist: string;
  playing: boolean;
  pos: number;
  dur: number;
}

export interface PcState {
  pc?: string;
  volume?: number;
  muted: boolean;
  media: MediaInfo | null;
  bind: Record<string, string | number>;
  locked: boolean;
}

export const EMPTY_STATE: PcState = { muted: false, media: null, bind: {}, locked: false };

const isObj = (v: unknown): v is Record<string, unknown> => typeof v === 'object' && v !== null && !Array.isArray(v);
const num = (v: unknown, lo: number, hi: number): number | undefined =>
  typeof v === 'number' && Number.isFinite(v) ? Math.max(lo, Math.min(hi, v)) : undefined;
const text = (v: unknown, max: number): string => (typeof v === 'string' ? v.slice(0, max) : '');

export function parseState(v: unknown): PcState {
  if (!isObj(v)) return EMPTY_STATE;
  let media: MediaInfo | null = null;
  const m = v['media'];
  if (isObj(m)) {
    media = {
      app: text(m['app'], 40),
      title: text(m['title'], 200),
      artist: text(m['artist'], 200),
      playing: m['playing'] === true,
      pos: num(m['pos'], 0, 1e7) ?? 0,
      dur: num(m['dur'], 0, 1e7) ?? 0,
    };
  }
  const bind: Record<string, string | number> = {};
  if (isObj(v['bind'])) {
    for (const [k, val] of Object.entries(v['bind']).slice(0, 64)) {
      if (typeof val === 'number' && Number.isFinite(val)) bind[k.slice(0, 64)] = val;
      else if (typeof val === 'string') bind[k.slice(0, 64)] = val.slice(0, 200);
    }
  }
  return {
    pc: typeof v['pc'] === 'string' ? v['pc'].slice(0, 64) : undefined,
    volume: num(v['volume'], 0, 100),
    muted: v['muted'] === true,
    media,
    bind,
    locked: v['locked'] === true,
  };
}

export function formatTime(totalSeconds: number): string {
  const s = Math.max(0, Math.floor(totalSeconds));
  const h = Math.floor(s / 3600);
  const m = Math.floor((s % 3600) / 60);
  const sec = String(s % 60).padStart(2, '0');
  return h > 0 ? `${h}:${String(m).padStart(2, '0')}:${sec}` : `${m}:${sec}`;
}
