import { useSyncExternalStore } from 'react';

// Язык интерфейса: русский — сама фраза в коде, украинский и английский — словари рядом со страницами.
// ⚠ Словарь типизирован: фраза без перевода не соберётся (tsc).

export type Lang = 'ru' | 'uk' | 'en';

/** Названия — каждое на своём языке. */
export const LANGS: Array<[Lang, string]> = [['uk', 'Українська'], ['ru', 'Русский'], ['en', 'English']];

export type Dict = Record<string, { uk: string; en: string }>;

const KEY = 'lang';

export const isLang = (value: unknown): value is Lang => value === 'ru' || value === 'uk' || value === 'en';

/** До входа: язык, выбранный в этом браузере, затем язык браузера. */
function initial(): Lang {
  try {
    const saved = localStorage.getItem(KEY);
    if (isLang(saved)) return saved;
  } catch { /* хранилище закрыто — язык браузера */ }
  for (const tag of navigator.languages ?? [navigator.language]) {
    const base = tag.toLowerCase().split('-')[0];
    if (isLang(base)) return base;
  }
  return 'en';
}

let current: Lang = initial();
const listeners = new Set<() => void>();

document.documentElement.lang = current;

export const lang = (): Lang => current;

/** Сменить язык; запоминается в браузере для страницы входа. */
export function setLang(next: unknown) {
  if (!isLang(next)) return;
  try { localStorage.setItem(KEY, next); } catch { /* только до перезагрузки */ }
  if (next === current) return;
  current = next;
  document.documentElement.lang = next;
  listeners.forEach(listener => listener());
}

/** Перерисовать дерево при смене языка. */
export function useLang(): Lang {
  return useSyncExternalStore(
    listener => { listeners.add(listener); return () => { listeners.delete(listener); }; },
    () => current,
  );
}

/** Перевод фразы словаря; `{имя}` — подстановка. */
export function translator<D extends Dict>(dict: D) {
  return (phrase: keyof D & string, values?: Record<string, string | number>): string => {
    const text = current === 'ru' ? phrase : dict[phrase]?.[current] ?? phrase;
    return values ? text.replace(/\{(\w+)\}/g, (all, name: string) => (name in values ? String(values[name]) : all)) : text;
  };
}

/** Тег языка для дат и сравнения строк. */
export const locale = (): string => ({ ru: 'ru-RU', uk: 'uk-UA', en: 'en-GB' })[current];
