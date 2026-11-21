import { useState } from 'preact/hooks';
import { client } from '../client.ts';
import { useT } from '../i18n/useT.ts';
import { bindValue, type PcState } from '../pcstate.ts';
import type { Remote, RemoteItem } from '../remotes.ts';
import { storageGet, storageSet } from '../storage.ts';
import { useStore } from '../store.ts';
import { Icon } from './icons.tsx';
import { Slider } from './Slider.tsx';

const SEL_KEY = 'em.remote';

export function Remotes() {
  const t = useT();
  const s = useStore(client.store);
  const [sel, setSel] = useState<string | null>(storageGet(SEL_KEY));

  if (s.remotes.length === 0) return <div class="empty">{t('remotes.none')}</div>;
  const active = s.remotes.find((r) => r.id === sel) ?? s.remotes[0]!;

  return (
    <div class="remotes">
      <div class="pills" role="tablist">
        {s.remotes.map((r) => (
          <button
            key={r.id}
            role="tab"
            aria-selected={r.id === active.id}
            class={`pill${r.id === active.id ? ' on' : ''}`}
            onClick={() => {
              setSel(r.id);
              storageSet(SEL_KEY, r.id);
            }}
          >
            {r.name}
          </button>
        ))}
      </div>
      {s.toast && <div class="notice" role="status">{t(s.toast === 'unsupported' ? 'remotes.unsupported' : 'remotes.failed')}</div>}
      {s.pc.media?.title && (
        <div class="card">
          <div class="cardhead">{t('remotes.nowPlaying')}</div>
          <div class="cardtitle" dir="auto">
            {s.pc.media.title}
          </div>
        </div>
      )}
      <Layout remote={active} pc={s.pc} />
    </div>
  );
}

function Layout({ remote, pc }: { remote: Remote; pc: PcState }) {
  return (
    <div class="rgrid" style={{ gridTemplateColumns: `repeat(${remote.cols}, minmax(0, 1fr))` }}>
      {remote.items.map((it) => (
        <Item key={it.id} item={it} remote={remote} pc={pc} />
      ))}
    </div>
  );
}

function Item({ item, remote, pc }: { item: RemoteItem; remote: Remote; pc: PcState }) {
  if (item.type === 'button') {
    const primary = item.icon === 'play' || item.icon === 'pause';
    return (
      <button class={`rbutton${primary ? ' primary' : ''}`} onClick={() => client.invoke(remote.id, item.id)}>
        {item.icon && <Icon name={item.icon} size={26} stroke={1.8} />}
        {item.label && <span class="rlabel">{item.label}</span>}
      </button>
    );
  }
  if (item.type === 'slider') {
    const v = item.bind ? bindValue(pc, item.bind) : undefined;
    return (
      <div class="rslider">
        <span class="rslabel">{item.label ?? ''}</span>
        <Slider value={typeof v === 'number' ? v : 0} label={item.label ?? item.id} onChange={(value) => client.invoke(remote.id, item.id, value)} />
      </div>
    );
  }
  const bound = item.bind ? bindValue(pc, item.bind) : undefined;
  return (
    <div class="rtext" dir="auto">
      {bound !== undefined ? String(bound) : (item.text ?? '')}
    </div>
  );
}
