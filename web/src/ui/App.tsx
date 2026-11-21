import { useState } from 'preact/hooks';
import { client, type Tab } from '../client.ts';
import { useT } from '../i18n/useT.ts';
import { useStore } from '../store.ts';
import { Icon } from './icons.tsx';
import { Keyboard } from './Keyboard.tsx';
import { Media } from './Media.tsx';
import { Busy, Offline, PairScreen, Scan } from './Screens.tsx';
import { Remotes } from './Remotes.tsx';
import { Settings } from './Settings.tsx';
import { Touchpad } from './Touchpad.tsx';

const TABS: Tab[] = ['mouse', 'keyboard', 'media', 'remotes'];

export function App() {
  const s = useStore(client.store);
  if (s.screen === 'scan') return <Scan />;
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
      <main class="main">
        {s.tab === 'mouse' && <Touchpad />}
        {s.tab === 'keyboard' && <Keyboard />}
        {s.tab === 'media' && <Media />}
        {s.tab === 'remotes' && <Remotes />}
      </main>
      <nav class="tabbar" aria-label="Main">
        {TABS.map((tab) => (
          <button key={tab} class={`tab${s.tab === tab ? ' on' : ''}`} aria-current={s.tab === tab ? 'page' : undefined} onClick={() => client.setTab(tab)}>
            <Icon name={tab} />
            {t(`tab.${tab}`)}
          </button>
        ))}
      </nav>
      {settings && <Settings onClose={() => setSettings(false)} />}
    </div>
  );
}
