import { client } from '../client.ts';
import type { LangPref } from '../i18n/strings.ts';
import { useT } from '../i18n/useT.ts';
import { useStore } from '../store.ts';

export function LangSwitch() {
  const s = useStore(client.store);
  const t = useT();
  const opts: { v: LangPref; label: string }[] = [
    { v: 'auto', label: t('settings.auto') },
    { v: 'en', label: 'English' },
    { v: 'he', label: 'עברית' },
  ];
  return (
    <div class="seg" role="group" aria-label={t('settings.language')}>
      {opts.map((o) => (
        <button key={o.v} class={s.langPref === o.v ? 'on' : ''} aria-pressed={s.langPref === o.v} onClick={() => client.setLangPref(o.v)}>
          {o.label}
        </button>
      ))}
    </div>
  );
}

export function PairScreen() {
  const s = useStore(client.store);
  const t = useT();
  const pc = s.pcName ?? t('pc.default');
  return (
    <div class="full center">
      <div class="hero">
        <div class="ring" />
        <div class="hero-icon">
          <svg width="46" height="46" viewBox="0 0 24 24" fill="none" stroke="#fff" stroke-width="1.8" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true">
            <rect x="6" y="2.5" width="12" height="19" rx="6" />
            <path d="M12 2.5v7" />
          </svg>
        </div>
      </div>
      <h1>{t('pair.waiting.title')}</h1>
      <p class="lead">{t('pair.waiting.body')}</p>
      <div class="codebox">
        <div class="codelabel">{t('pair.code')}</div>
        <div class="code" dir="ltr" aria-live="polite">
          {s.code || '····'}
        </div>
      </div>
      <div class="small">{t('pair.connectingTo', { pc })}</div>
      <div class="grow" />
      <button class="btn ghost" onClick={() => client.cancelPair()}>
        {t('pair.cancel')}
      </button>
      <div class="fine">{t('pair.privacy')}</div>
    </div>
  );
}

export function Scan() {
  const s = useStore(client.store);
  const t = useT();
  const msg =
    s.scanReason === 'revoked' ? t('scan.revoked') : s.scanReason === 'denied' ? t('scan.denied') : s.scanReason === 'expired' ? t('scan.expired') : s.scanReason === 'failed' ? t('scan.failed') : t('scan.body');
  return (
    <div class="full center">
      <div class="badge">
        <svg width="44" height="44" viewBox="0 0 24 24" fill="none" stroke="#5B8CFF" stroke-width="1.8" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true">
          <path d="M4 8V5a1 1 0 0 1 1-1h3M16 4h3a1 1 0 0 1 1 1v3M20 16v3a1 1 0 0 1-1 1h-3M8 20H5a1 1 0 0 1-1-1v-3M8 8h3v3H8zM13 8h3M13 13h3v3M8 13h3v3" />
        </svg>
      </div>
      <h1>{t('scan.title')}</h1>
      <p class="lead">{msg}</p>
      <div class="grow" />
      <LangSwitch />
    </div>
  );
}

export function Offline() {
  const s = useStore(client.store);
  const t = useT();
  const pc = s.pcName ?? t('pc.default');
  return (
    <div class="full center">
      <div class="badge">
        <svg class="spin" width="44" height="44" viewBox="0 0 24 24" fill="none" stroke="#5B8CFF" stroke-width="2" stroke-linecap="round" aria-hidden="true">
          <path d="M21 12a9 9 0 1 1-3-6.7" />
          <path d="M21 4v5h-5" />
        </svg>
      </div>
      <h1>{s.connecting ? t('offline.connecting', { pc }) : t('offline.title', { pc })}</h1>
      <p class="lead">{s.replaced ? t('offline.replaced') : t('offline.body')}</p>
      <div class="card tips">
        <div class="tipshead">{t('offline.help')}</div>
        <ul>
          <li>{t('offline.tip1')}</li>
          <li>{t('offline.tip2')}</li>
          <li>{t('offline.tip3')}</li>
        </ul>
      </div>
      <div class="grow" />
      <button class="btn primary" onClick={() => client.retryNow()}>
        {t('offline.retry')}
      </button>
      <button class="btn ghost" onClick={() => client.forgetDevice()}>
        {t('offline.scan')}
      </button>
      <LangSwitch />
    </div>
  );
}

export function Busy() {
  const s = useStore(client.store);
  const t = useT();
  const pc = s.pcName ?? t('pc.default');
  return (
    <div class="full center">
      <div class="badge">
        <svg width="44" height="44" viewBox="0 0 24 24" fill="none" stroke="#5B8CFF" stroke-width="1.8" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true">
          <rect x="6" y="2.5" width="12" height="19" rx="6" />
          <path d="M12 2.5v7" />
        </svg>
      </div>
      <h1>{t('busy.title', { pc })}</h1>
      <p class="lead">{s.takeoverAsked ? t('busy.waiting') : t('busy.body')}</p>
      <div class="grow" />
      <button class="btn primary" disabled={s.takeoverAsked} onClick={() => client.takeover()}>
        {t('busy.take')}
      </button>
    </div>
  );
}
