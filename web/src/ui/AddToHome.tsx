import type { ComponentChildren } from 'preact';
import { useEffect, useRef } from 'preact/hooks';
import { platform, setNever, type A2hsPlatform } from '../a2hs.ts';
import { useT } from '../i18n/useT.ts';
import type { Key } from '../i18n/strings.ts';
import './addToHome.css';

type Glyph = 'share' | 'menu' | 'plus';

const svgProps = {
  width: 22,
  height: 22,
  viewBox: '0 0 24 24',
  fill: 'none',
  stroke: 'currentColor',
  'stroke-width': 1.9,
  'stroke-linecap': 'round' as const,
  'stroke-linejoin': 'round' as const,
  'aria-hidden': true,
};

/** The browser glyphs the user has to find: the iOS share box, the Android three-dot menu, a plus box. */
function GlyphIcon({ glyph }: { glyph: Glyph }) {
  if (glyph === 'share')
    return (
      <svg {...svgProps}>
        <path d="M12 15V3.5M8.2 7.2 12 3.5l3.8 3.7" />
        <path d="M8 10.5H6.5A1.5 1.5 0 0 0 5 12v7a1.5 1.5 0 0 0 1.5 1.5h11A1.5 1.5 0 0 0 19 19v-7a1.5 1.5 0 0 0-1.5-1.5H16" />
      </svg>
    );
  if (glyph === 'menu')
    return (
      <svg {...svgProps} fill="currentColor" stroke="none">
        <rect x="10.4" y="3.6" width="3.2" height="3.2" rx="1" />
        <rect x="10.4" y="10.4" width="3.2" height="3.2" rx="1" />
        <rect x="10.4" y="17.2" width="3.2" height="3.2" rx="1" />
      </svg>
    );
  return (
    <svg {...svgProps}>
      <rect x="4" y="4" width="16" height="16" rx="4" />
      <path d="M12 8.5v7M8.5 12h7" />
    </svg>
  );
}

const STEPS: Record<A2hsPlatform, { text: Key; glyph?: Glyph }[]> = {
  'ios-safari': [
    { text: 'a2hs.ios.1', glyph: 'share' },
    { text: 'a2hs.ios.2', glyph: 'plus' },
    { text: 'a2hs.ios.3' },
  ],
  'android-chrome': [{ text: 'a2hs.android.1', glyph: 'menu' }, { text: 'a2hs.android.2', glyph: 'plus' }, { text: 'a2hs.android.3' }],
  other: [{ text: 'a2hs.other.1', glyph: 'menu' }, { text: 'a2hs.other.2', glyph: 'plus' }, { text: 'a2hs.other.3' }],
};

export type A2hsResult = 'done' | 'later' | 'never' | 'close';

interface Props {
  /** true: offered by itself (does not block the touchpad and has "Not now"/"Don't show again"); false: opened from Settings */
  auto: boolean;
  onClose: (result: A2hsResult) => void;
}

const FOCUSABLE = 'button, [href], input, [tabindex]:not([tabindex="-1"])';

export function AddToHome({ auto, onClose }: Props) {
  const t = useT();
  const plat = platform();
  const sheet = useRef<HTMLDivElement>(null);

  // Focus moves into the sheet on open and returns to where it was on close.
  useEffect(() => {
    const prev = document.activeElement as HTMLElement | null;
    sheet.current?.focus();
    return () => prev?.focus?.();
  }, []);

  const dismiss = (r: A2hsResult) => onClose(r);
  const onKey = (e: KeyboardEvent) => {
    if (e.key === 'Escape') {
      e.stopPropagation();
      dismiss(auto ? 'later' : 'close');
    } else if (e.key === 'Tab' && !auto && sheet.current) {
      // modal: keep Tab inside the sheet
      const items = [...sheet.current.querySelectorAll<HTMLElement>(FOCUSABLE)];
      const first = items[0];
      const last = items[items.length - 1];
      if (!first || !last) return;
      if (e.shiftKey && (document.activeElement === first || document.activeElement === sheet.current)) {
        e.preventDefault();
        last.focus();
      } else if (!e.shiftKey && document.activeElement === last) {
        e.preventDefault();
        first.focus();
      }
    }
  };

  const steps = STEPS[plat];
  const body: ComponentChildren = (
    <>
      <h2 id="a2hs-title">{t('a2hs.title')}</h2>
      <p class="a2hs-lead" id="a2hs-lead">
        {t('a2hs.lead')}
      </p>
      <ol class="a2hs-steps" aria-label={t('a2hs.steps')}>
        {steps.map((s, i) => (
          <li key={s.text}>
            <span class="a2hs-num" aria-hidden="true">
              {i + 1}
            </span>
            <span class="a2hs-text">{t(s.text)}</span>
            {s.glyph && (
              <span class="a2hs-glyph">
                <GlyphIcon glyph={s.glyph} />
              </span>
            )}
          </li>
        ))}
      </ol>
      {plat === 'ios-safari' && (
        <p class="a2hs-note warn" role="note">
          {t('a2hs.ios.note')}
        </p>
      )}
      <p class="a2hs-note" role="note">
        {t('a2hs.ipnote')}
      </p>
      {auto ? (
        <div class="a2hs-actions">
          <button class="btn primary a2hs-btn" onClick={() => dismiss('done')}>
            {t('a2hs.done')}
          </button>
          <div class="a2hs-row">
            <button class="btn ghost a2hs-btn" onClick={() => dismiss('later')}>
              {t('a2hs.later')}
            </button>
            <button
              class="btn ghost a2hs-btn"
              onClick={() => {
                setNever();
                dismiss('never');
              }}
            >
              {t('a2hs.never')}
            </button>
          </div>
        </div>
      ) : (
        <div class="a2hs-actions">
          <button class="btn primary a2hs-btn" onClick={() => dismiss('close')}>
            {t('a2hs.close')}
          </button>
        </div>
      )}
    </>
  );

  return (
    <div class={`sheetback${auto ? ' a2hs-float' : ''}`} onClick={auto ? undefined : () => dismiss('close')}>
      <div
        ref={sheet}
        class="sheet a2hs"
        role="dialog"
        aria-modal={auto ? 'false' : 'true'}
        aria-labelledby="a2hs-title"
        aria-describedby="a2hs-lead"
        tabIndex={-1}
        onKeyDown={onKey}
        onClick={(e) => e.stopPropagation()}
      >
        {body}
      </div>
    </div>
  );
}
