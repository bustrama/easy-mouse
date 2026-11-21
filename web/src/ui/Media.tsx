import { useEffect, useRef, useState } from 'preact/hooks';
import { client } from '../client.ts';
import { useT } from '../i18n/useT.ts';
import { formatTime } from '../pcstate.ts';
import { MEDIA } from '../protocol/ops.ts';
import { useStore } from '../store.ts';
import { Icon } from './icons.tsx';
import { Slider } from './Slider.tsx';

export function Media() {
  const t = useT();
  const { pc } = useStore(client.store);
  const m = pc.media;
  const [now, setNow] = useState(Date.now());
  const stamp = useRef(Date.now());

  // `pos` is a snapshot; extrapolate while playing.
  useEffect(() => {
    stamp.current = Date.now();
    setNow(stamp.current);
  }, [m]);
  useEffect(() => {
    if (!m?.playing) return;
    const id = setInterval(() => setNow(Date.now()), 1000);
    return () => clearInterval(id);
  }, [m?.playing]);

  const pos = m ? Math.min(m.dur || Infinity, m.pos + (m.playing ? (now - stamp.current) / 1000 : 0)) : 0;
  const pct = m && m.dur > 0 ? Math.max(0, Math.min(100, (pos / m.dur) * 100)) : 0;
  const volume = pc.volume ?? 0;

  return (
    <div class="media">
      <div class="art">
        <Icon name="media" size={64} stroke={1.4} />
      </div>
      <div class="meta">
        <div class="title" dir="auto">
          {m ? m.title || t('media.nothing') : t('media.nothing')}
        </div>
        <div class="sub" dir="auto">
          {m ? [m.artist, m.app ? t('media.playingIn', { app: m.app }) : ''].filter(Boolean).join(' · ') : t('media.nothingBody')}
        </div>
      </div>
      <div class="progress" aria-hidden="true">
        <div class="bar">
          <div class="barfill" style={{ width: `${pct}%` }} />
        </div>
        <div class="times" dir="ltr">
          <span>{formatTime(pos)}</span>
          <span>{m && m.dur > 0 ? formatTime(m.dur) : '--:--'}</span>
        </div>
      </div>
      <div class="transport" dir="ltr">
        <button class="rbtn" aria-label={t('media.prev')} onClick={() => client.media(MEDIA.prev)}>
          <Icon name="prev" fill />
        </button>
        <button class="rbtn txt" aria-label={t('media.back10')} onClick={() => client.json({ t: 'seek', by: -10 })}>
          -10
        </button>
        <button class="rbtn main" aria-label={t('media.playpause')} onClick={() => client.media(MEDIA.playpause)}>
          <Icon name={m?.playing ? 'pause' : 'play'} size={32} fill />
        </button>
        <button class="rbtn txt" aria-label={t('media.fwd10')} onClick={() => client.json({ t: 'seek', by: 10 })}>
          +10
        </button>
        <button class="rbtn" aria-label={t('media.next')} onClick={() => client.media(MEDIA.next)}>
          <Icon name="next" fill />
        </button>
      </div>
      <div class="volrow">
        <button class={`sqbtn${pc.muted ? ' on' : ''}`} aria-label={t('media.mute')} aria-pressed={pc.muted} onClick={() => client.media(MEDIA.mute)}>
          <Icon name={pc.muted ? 'mute' : 'volume'} size={22} stroke={1.8} />
        </button>
        <Slider value={volume} label={t('media.volume')} onChange={(v) => client.json({ t: 'volume', set: v })} />
        <span class="volnum" dir="ltr">
          {volume}
        </span>
      </div>
    </div>
  );
}
