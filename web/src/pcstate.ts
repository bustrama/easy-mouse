// Parsing of the server's `state` frame (docs/protocol.md, "0x11 state"). Defensive: unknown or
// ill-typed fields are dropped, strings are length-limited, and everything is rendered as text.

export type MediaStatus = 'none' | 'playing' | 'paused' | 'stopped';

export interface MediaInfo {
  status: MediaStatus;
  title: string;
  artist: string;
  positionMs: number;
  durationMs: number;
}

export interface PcState {
  media: MediaInfo | null;
  /** id of the current album art (see the art frame); null when there is none */
  artId: string | null;
  /** null when the volume is unknown */
  volume: { level: number; muted: boolean } | null;
  locked: boolean;
  elevated: boolean;
}

export const EMPTY_STATE: PcState = { media: null, artId: null, volume: null, locked: false, elevated: false };

const isObj = (v: unknown): v is Record<string, unknown> => typeof v === 'object' && v !== null && !Array.isArray(v);
const int = (v: unknown, lo: number, hi: number): number => (typeof v === 'number' && Number.isFinite(v) ? Math.max(lo, Math.min(hi, Math.trunc(v))) : 0);
const text = (v: unknown, max: number): string => (typeof v === 'string' ? v.slice(0, max) : '');
const STATUSES: readonly string[] = ['none', 'playing', 'paused', 'stopped'];

export function parseState(v: unknown): PcState {
  if (!isObj(v) || v['t'] !== 'state') return EMPTY_STATE;
  let media: MediaInfo | null = null;
  const m = v['media'];
  if (isObj(m)) {
    const status = typeof m['status'] === 'string' && STATUSES.includes(m['status']) ? (m['status'] as MediaStatus) : 'none';
    media = {
      status,
      title: text(m['title'], 200),
      artist: text(m['artist'], 200),
      positionMs: int(m['positionMs'], 0, 1e10),
      durationMs: int(m['durationMs'], 0, 1e10),
    };
  }
  let volume: PcState['volume'] = null;
  const vol = v['volume'];
  if (isObj(vol)) volume = { level: int(vol['level'], 0, 100), muted: vol['muted'] === true };
  const host = isObj(v['host']) ? v['host'] : {};
  const a = v['artId'];
  const artId = typeof a === 'string' && /^[a-z0-9]{1,32}$/.test(a) ? a : null;
  return { media, artId, volume, locked: host['locked'] === true, elevated: host['elevated'] === true };
}

/** Values a remote's `bind` can show: now-playing text and the master volume. */
export function bindValue(s: PcState, bind: string): string | number | undefined {
  switch (bind) {
    case 'smtc.title':
      return s.media?.title || undefined;
    case 'smtc.artist':
      return s.media?.artist || undefined;
    case 'volume':
    case 'volume.master':
      return s.volume?.level;
    default:
      return undefined;
  }
}

export function formatTime(totalSeconds: number): string {
  const s = Math.max(0, Math.floor(totalSeconds));
  const h = Math.floor(s / 3600);
  const m = Math.floor((s % 3600) / 60);
  const sec = String(s % 60).padStart(2, '0');
  return h > 0 ? `${h}:${String(m).padStart(2, '0')}:${sec}` : `${m}:${sec}`;
}
