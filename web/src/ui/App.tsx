import { useEffect, useState } from 'preact/hooks';
import { takeAutoOffer } from '../a2hs.ts';
import { client, type Tab } from '../client.ts';
import { useT } from '../i18n/useT.ts';
import { useStore } from '../store.ts';
import { AddToHome } from './AddToHome.tsx';
import { Icon } from './icons.tsx';
import { Keyboard } from './Keyboard.tsx';
import { Media } from './Media.tsx';
import { Busy, InApp, Offline, PairScreen, Scan } from './Screens.tsx';
import { Remotes } from './Remotes.tsx';
import { Settings } from './Settings.tsx';
import { Touchpad } from './Touchpad.tsx';

import { REMOTES_ENABLED } from '../features.ts';

const TABS: Tab[] = REMOTES_ENABLED ? ['mouse', 'keyboard', 'media', 'remotes'] : ['mouse', 'keyboard', 'media'];

export function App() {
  const s = useStore(client.store);
  if (s.screen === 'scan') return <Scan />;
  if (s.screen === 'inapp') return <InApp />;
  if (s.screen === 'pairing') return <PairScreen />;
  if (s.screen === 'offline') return <Offline />;
  if (s.screen === 'busy') return <Busy />;
  return <Shell />;
}

function Shell() {
  const s = useStore(client.store);
  const t = useT();
  const pc = s.pcName ?? t('pc.default');
  const [settings, setSettings] = useState(false);
  // 'auto': offered by itself once after the first pairing; 'manual': opened from Settings
  const [a2hs, setA2hs] = useState<'auto' | 'manual' | null>(null);
  useEffect(() => {
    // A short pause so the sheet does not pop up during the connect-and-paint of the first screen.
    const id = setTimeout(() => {
      const justPaired = client.store.get().justPaired;
      if (justPaired) client.store.set({ justPaired: false });
      if (takeAutoOffer(justPaired)) setA2hs('auto');
    }, 900);
    return () => clearTimeout(id);
  }, []);
  return (
    <div class="shell">
      <header class="top">
        <div class="pcname">
          <span class="dot" />
          <span class="pcname-text">{pc}</span>
        </div>
        {s.tab === 'mouse' ? (
          <button class="sqbtn" aria-label={t('settings.title')} onClick={() => setSettings(true)}>
            <Icon name="settings" size={22} stroke={1.8} />
          </button>
        ) : (
          <div class="tabtitle">{t(`tab.${s.tab}`)}</div>
        )}
      </header>
      {s.pc.locked && (
        <div class="notice" role="status">
          {t('remotes.locked')}
        </div>
      )}
      <main class="main">
        {s.tab === 'mouse' && <Touchpad />}
        {s.tab === 'keyboard' && <Keyboard />}
        {s.tab === 'media' && <Media />}
        {REMOTES_ENABLED && s.tab === 'remotes' && <Remotes />}
      </main>
      <nav class="tabbar" aria-label="Main">
        {TABS.map((tab) => (
          <button key={tab} class={`tab${s.tab === tab ? ' on' : ''}`} aria-current={s.tab === tab ? 'page' : undefined} onClick={() => client.setTab(tab)}>
            <Icon name={tab} />
            {t(`tab.${tab}`)}
          </button>
        ))}
      </nav>
      {settings && (
        <Settings
          onClose={() => setSettings(false)}
          onAddToHome={() => {
            setSettings(false);
            setA2hs('manual');
          }}
        />
      )}
      {a2hs && <AddToHome auto={a2hs === 'auto'} onClose={() => setA2hs(null)} />}
    </div>
  );
}
