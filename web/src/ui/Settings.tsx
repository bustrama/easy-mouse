import { client } from '../client.ts';
import { useT } from '../i18n/useT.ts';
import { useStore } from '../store.ts';
import { LangSwitch } from './Screens.tsx';

export function Settings({ onClose }: { onClose: () => void }) {
  const t = useT();
  const { sensitivity } = useStore(client.store);
  return (
    <div class="sheetback" onClick={onClose}>
      <div class="sheet" role="dialog" aria-modal="true" aria-label={t('settings.title')} onClick={(e) => e.stopPropagation()}>
        <div class="sheethead">
          <h2>{t('settings.title')}</h2>
          <button class="btn ghost small" onClick={onClose}>
            {t('settings.close')}
          </button>
        </div>
        <label class="sect" for="sens">
          {t('settings.sensitivity')} <span dir="ltr">({sensitivity.toFixed(1)}x)</span>
        </label>
        <input
          id="sens"
          class="range"
          type="range"
          min="0.5"
          max="3"
          step="0.1"
          value={sensitivity}
          onInput={(e) => client.setSensitivity(Number((e.currentTarget as HTMLInputElement).value))}
        />
        <div class="sect">{t('settings.language')}</div>
        <LangSwitch />
      </div>
    </div>
  );
}
