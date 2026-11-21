import { client } from '../client.ts';
import { useStore } from '../store.ts';
import { type Key, translate } from './strings.ts';

/** Translation function bound to the current language (re-renders when the language changes). */
export function useT(): (key: Key, params?: Record<string, string>) => string {
  const { lang } = useStore(client.store);
  return (key, params) => translate(lang, key, params);
}
