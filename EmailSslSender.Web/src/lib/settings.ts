import { api } from './api';
import type { Setting } from './types';

export type Settings = Record<string, Setting>;

export async function loadSettings(): Promise<Settings> {
  const list = await api.get<Setting[]>('/api/settings/system');
  return Object.fromEntries(list.map(s => [s.key, s]));
}

/** Значение настройки строкой для поля ввода. */
export const text = (settings: Settings, key: string): string => {
  const value = settings[key]?.value;
  return value === null || value === undefined ? '' : String(value);
};

/**
 * Пустая строка — null у обнуляемых, умолчание у прочих.
 * ⚠ Только изменённые ключи: за любой названный ключ защиты сервер спросит код.
 */
export async function saveSettings(settings: Settings, values: Record<string, string | number | boolean>) {
  const body: { values: Record<string, unknown>; reset: string[] } = { values: {}, reset: [] };
  for (const [key, value] of Object.entries(values)) {
    const meta = settings[key];
    const current = meta?.value ?? null;
    if (value === '') {
      if (meta?.nullable) {
        if (current !== null && current !== '') body.values[key] = null;
      } else if (current !== (meta?.default ?? null)) {
        body.reset.push(key);
      }
    } else if (value !== current) {
      body.values[key] = value;
    }
  }
  if (Object.keys(body.values).length === 0 && body.reset.length === 0) return;
  await api.patch('/api/settings/system', body);
}
