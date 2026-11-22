import { useEffect, useRef, useState } from 'preact/hooks';
import { coverFor } from '../art.ts';
import { client } from '../client.ts';
import { useT } from '../i18n/useT.ts';
import { formatTime } from '../pcstate.ts';
import { MEDIA } from '../protocol/ops.ts';
import { useStore } from '../store.ts';
import { Icon } from './icons.tsx';
import { Slider } from './Slider.tsx';

export function Media() {
  const t = useT();
  const { pc, art } = useStore(client.store);
  const cover = coverFor(art, pc.artId); // the image for the current track only
  const m = pc.media;
  const [now, setNow] = useState(Date.now());
  const stamp = useRef(Date.now());
  const playing = m?.status === 'playing';

  // `pos` is a snapshot; extrapolate while playing.
  useEffect(() => {
    stamp.current = Date.now();
    setNow(stamp.current);
  }, [m]);
  useEffect(() => {
    if (!playing) return;
    const id = setInterval(() => setNow(Date.now()), 1000);
    return () => clearInterval(id);
  }, [playing]);

  const dur = m ? m.durationMs / 1000 : 0;
  const pos = m ? Math.min(dur || Infinity, m.positionMs / 1000 + (playing ? (now - stamp.current) / 1000 : 0)) : 0;
  const pct = dur > 0 ? Math.max(0, Math.min(100, (pos / dur) * 100)) : 0;
  const volume = pc.volume?.level ?? 0;
  const muted = pc.volume?.muted === true;

  return (
    <div class="media">
      <div class={`art${cover ? ' has' : ''}`}>
        {cover ? <img src={cover} alt="" draggable={false} /> : <Icon name="media" size={64} stroke={1.4} />}
      </div>
      <div class="meta">
        <div class="title" dir="auto">
          {m ? m.title || t('media.nothing') : t('media.nothing')}
        </div>
        <div class="sub" dir="auto">
          {m && (m.title || m.artist) ? m.artist : t('media.nothingBody')}
        </div>
      </div>
      <div class="progress" aria-hidden="true">
        <div class="bar">
          <div class="barfill" style={{ width: `${pct}%` }} />
        </div>
        <div class="times" dir="ltr">
          <span>{formatTime(pos)}</span>
          <span>{dur > 0 ? formatTime(dur) : '--:--'}</span>
        </div>
      </div>
      <div class="transport" dir="ltr">
        <button class="rbtn" aria-label={t('media.prev')} onClick={() => client.media(MEDIA.prev)}>
          <Icon name="prev" fill />
        </button>
        <button class="rbtn" aria-label={t('media.back')} onClick={() => client.seek(-1)}>
          <Icon name="rewind" fill />
        </button>
        <button class="rbtn main" aria-label={t('media.playpause')} onClick={() => client.media(MEDIA.playpause)}>
          <Icon name={playing ? 'pause' : 'play'} size={32} fill />
        </button>
        <button class="rbtn" aria-label={t('media.forward')} onClick={() => client.seek(1)}>
          <Icon name="forward" fill />
        </button>
        <button class="rbtn" aria-label={t('media.next')} onClick={() => client.media(MEDIA.next)}>
          <Icon name="next" fill />
        </button>
      </div>
      <div class="volrow">
        <button class={`sqbtn${muted ? ' on' : ''}`} aria-label={t('media.mute')} aria-pressed={muted} onClick={() => client.media(MEDIA.mute)}>
          <Icon name={muted ? 'mute' : 'volume'} size={22} stroke={1.8} />
        </button>
        <Slider value={volume} label={t('media.volume')} onChange={(v) => client.setVolume(v)} />
        <span class="volnum" dir="ltr">
          {volume}
        </span>
      </div>
    </div>
  );
}
