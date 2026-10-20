import type { PasswordPolicy } from './types';

const DATE_TIME = new Intl.DateTimeFormat('ru-RU', { dateStyle: 'short', timeStyle: 'short' });
const DATE = new Intl.DateTimeFormat('ru-RU', { dateStyle: 'short' });

export const fmtDateTime = (iso: string | null | undefined) => (iso ? DATE_TIME.format(new Date(iso)) : '—');

export const fmtDate = (ymd: string | null | undefined) => (ymd ? DATE.format(new Date(ymd + 'T00:00:00')) : '—');

export function passwordRules(p: PasswordPolicy): string {
  const parts = ['от ' + p.min_length + ' символов'];
  if (p.require_uppercase) parts.push('заглавная');
  if (p.require_lowercase) parts.push('строчная');
  if (p.require_digit) parts.push('цифра');
  if (p.require_symbol) parts.push('спецсимвол');
  return parts.join(' · ');
}

/** Браузер и система словами: «Chrome · Windows». */
export function agentText(agent: string | null): string {
  if (!agent) return 'Неизвестное устройство';
  const browser = /Edg\//.test(agent) ? 'Edge' : /Firefox\//.test(agent) ? 'Firefox' : /Chrome\//.test(agent) ? 'Chrome'
    : /Safari\//.test(agent) ? 'Safari' : /curl/i.test(agent) ? 'curl' : 'Браузер';
  const os = /Windows/.test(agent) ? 'Windows' : /Mac OS/.test(agent) ? 'macOS' : /Android/.test(agent) ? 'Android'
    : /iPhone|iPad/.test(agent) ? 'iOS' : /Linux/.test(agent) ? 'Linux' : '';
  return os ? browser + ' · ' + os : browser;
}
