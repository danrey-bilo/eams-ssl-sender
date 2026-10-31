import { t } from '../i18n/common';
import { locale } from './i18n';
import type { PasswordPolicy } from './types';

const formats = new Map<string, [dateTime: Intl.DateTimeFormat, date: Intl.DateTimeFormat]>();

/** Форматы дат языка интерфейса — по одному на язык. */
function formatsOf(tag: string) {
  let pair = formats.get(tag);
  if (!pair) {
    // Год — четырьмя цифрами на всех языках (короткий стиль uk-UA даёт «27.10.26»).
    const date = { day: '2-digit', month: '2-digit', year: 'numeric' } as const;
    pair = [new Intl.DateTimeFormat(tag, { ...date, hour: '2-digit', minute: '2-digit' }), new Intl.DateTimeFormat(tag, date)];
    formats.set(tag, pair);
  }
  return pair;
}

export const fmtDateTime = (iso: string | null | undefined) => (iso ? formatsOf(locale())[0].format(new Date(iso)) : '—');

export const fmtDate = (ymd: string | null | undefined) => (ymd ? formatsOf(locale())[1].format(new Date(ymd + 'T00:00:00')) : '—');

export function passwordRules(p: PasswordPolicy): string {
  const parts = [t('от {n} символов', { n: p.min_length })];
  if (p.require_uppercase) parts.push(t('заглавная'));
  if (p.require_lowercase) parts.push(t('строчная'));
  if (p.require_digit) parts.push(t('цифра'));
  if (p.require_symbol) parts.push(t('спецсимвол'));
  return parts.join(' · ');
}

/** Браузер и система словами: «Chrome · Windows». */
export function agentText(agent: string | null): string {
  if (!agent) return t('Неизвестное устройство');
  const browser = /Edg\//.test(agent) ? 'Edge' : /Firefox\//.test(agent) ? 'Firefox' : /Chrome\//.test(agent) ? 'Chrome'
    : /Safari\//.test(agent) ? 'Safari' : /curl/i.test(agent) ? 'curl' : t('Браузер');
  const os = /Windows/.test(agent) ? 'Windows' : /Mac OS/.test(agent) ? 'macOS' : /Android/.test(agent) ? 'Android'
    : /iPhone|iPad/.test(agent) ? 'iOS' : /Linux/.test(agent) ? 'Linux' : '';
  return os ? browser + ' · ' + os : browser;
}
