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

/** Сохранить настройки: пустая строка — null у обнуляемых и умолчание у прочих. */
export async function saveSettings(settings: Settings, values: Record<string, string | number | boolean>) {
  const body: { values: Record<string, unknown>; reset: string[] } = { values: {}, reset: [] };
  for (const [key, value] of Object.entries(values)) {
    if (value === '') {
      if (settings[key]?.nullable) body.values[key] = null; else body.reset.push(key);
    } else {
      body.values[key] = value;
    }
  }
  await api.patch('/api/settings/system', body);
}
