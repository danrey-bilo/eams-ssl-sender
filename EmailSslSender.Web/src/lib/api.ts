// ⚠ Токен доступа — только в памяти; обновления — кука HttpOnly `__Host-sender-refresh`.
// Отказ `SECOND_FACTOR_REQUIRED` открывает окно кода и повторяет действие.

import { t } from '../i18n/common';
import { serverText } from '../i18n/errors';
import type { Tokens } from './types';

export class ApiError extends Error {
  readonly status: number;
  readonly code: string | undefined;
  readonly details: Record<string, unknown> | undefined;

  constructor(status: number, code: string | undefined, message: string | undefined, details?: Record<string, unknown>) {
    super(message || t('Сервер недоступен.'));
    this.status = status;
    this.code = code;
    this.details = details;
  }
}

/** Окно кода закрыто или сессия кончилась — показывать нечего. */
export class Cancelled extends Error {}

interface Envelope {
  code?: string;
  message?: string;
  details?: Record<string, unknown>;
}

let token: string | null = null;
let timer: ReturnType<typeof setTimeout> | undefined;
let refreshing: Promise<boolean> | null = null;
let stepping: Promise<boolean> | null = null;

let onSignedOut: (message?: string) => void = () => {};
let confirmIdentity: (minutes?: number) => Promise<boolean> = async () => false;

/** Вход по концу сессии и окно кода перед опасным действием. */
export function connect(handlers: {
  signedOut: (message?: string) => void;
  confirmIdentity: (minutes?: number) => Promise<boolean>;
}) {
  onSignedOut = handlers.signedOut;
  confirmIdentity = handlers.confirmIdentity;
}

export function adopt(tokens: Tokens) {
  token = tokens.access_token;
  clearTimeout(timer);
  const wait = new Date(tokens.access_expires_at).getTime() - Date.now() - 60_000;
  timer = setTimeout(() => { void refresh(); }, Math.max(wait, 10_000));
}

export function forget() {
  token = null;
  clearTimeout(timer);
}

/** Один обмен на всех: кука одноразовая. */
export function refresh(): Promise<boolean> {
  refreshing ??= (async () => {
    try {
      const r = await fetch('/api/auth/refresh', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ use_cookie: true }),
        cache: 'no-store',
      });
      if (!r.ok) return false;
      adopt(await r.json() as Tokens);
      return true;
    } catch {
      return false;
    } finally {
      setTimeout(() => { refreshing = null; }, 0);
    }
  })();
  return refreshing;
}

/** Запрос без сессии: вход, второй шаг, ссылки из писем. */
export async function publicPost<T>(path: string, body: unknown): Promise<{ ok: true; data: T } | { ok: false; error: ApiError }> {
  try {
    const r = await fetch(path, {
      method: 'POST', headers: { 'Content-Type': 'application/json' }, cache: 'no-store', body: JSON.stringify(body),
    });
    const data = await r.json().catch(() => null) as (T & Envelope) | null;
    if (r.ok) return { ok: true, data: data as T };
    return { ok: false, error: new ApiError(r.status, data?.code, data?.message, data?.details) };
  } catch {
    return { ok: false, error: new ApiError(0, 'NETWORK', t('Сервер недоступен.')) };
  }
}

const SESSION_GONE = ['TOKEN_EXPIRED', 'UNAUTHENTICATED', 'SESSION_REVOKED', 'SESSION_EXPIRED'];

/** Запрос с токеном; отказ — ApiError или Cancelled. */
async function send(method: string, path: string, body?: unknown, retry = true, stepUp = true): Promise<Response> {
  const headers: Record<string, string> = {};
  if (body !== undefined) headers['Content-Type'] = 'application/json';
  if (token) headers.Authorization = 'Bearer ' + token;

  let r: Response;
  try {
    r = await fetch(path, {
      method, headers, cache: 'no-store', body: body === undefined ? undefined : JSON.stringify(body),
    });
  } catch {
    throw new ApiError(0, 'NETWORK', t('Сервер недоступен.'));
  }

  if (r.ok) return r;

  const data = await r.json().catch(() => null) as Envelope | null;
  const code = data?.code;

  if (r.status === 401 && retry && token && (code === 'TOKEN_EXPIRED' || code === 'UNAUTHENTICATED')) {
    if (await refresh()) return send(method, path, body, false, stepUp);
  }

  if (r.status === 401 && token && code && SESSION_GONE.includes(code)) {
    forget();
    onSignedOut(t('Сессия закончилась. Войдите снова.'));
    throw new Cancelled();
  }

  // Окно кода одно на все одновременные запросы.
  if (r.status === 403 && code === 'SECOND_FACTOR_REQUIRED' && stepUp) {
    const minutes = typeof data?.details?.window_minutes === 'number' ? data.details.window_minutes : undefined;
    stepping ??= confirmIdentity(minutes).finally(() => { stepping = null; });
    if (await stepping) return send(method, path, body, retry, false);
    throw new Cancelled();
  }

  throw new ApiError(r.status, code, data?.message, data?.details);
}

async function request<T>(method: string, path: string, body?: unknown, stepUp = true): Promise<T> {
  const r = await send(method, path, body, true, stepUp);
  if (r.status === 204 || r.status === 202) return null as T;
  return await r.json().catch(() => null) as T;
}

/** ⚠ Всегда octet-stream: иначе Chrome на телефоне дописывает расширение (.crt.cer). */
export function saveFile(data: Blob, name: string) {
  const url = URL.createObjectURL(new Blob([data], { type: 'application/octet-stream' }));
  const link = document.createElement('a');
  link.href = url;
  link.download = name;
  document.body.append(link);
  link.click();
  link.remove();
  setTimeout(() => URL.revokeObjectURL(url), 10_000);
}

/** Имя из Content-Disposition: filename*, затем filename. */
function fileName(disposition: string | null): string | null {
  if (!disposition) return null;
  const star = /filename\*\s*=\s*UTF-8''([^;]+)/i.exec(disposition)?.[1];
  if (star) {
    try { return decodeURIComponent(star.trim()); } catch { /* ниже — простое имя */ }
  }
  return /filename\s*=\s*"?([^";]+)"?/i.exec(disposition)?.[1]?.trim() ?? null;
}

export const api = {
  get: <T>(path: string) => request<T>('GET', path),
  post: <T = null>(path: string, body?: unknown) => request<T>('POST', path, body ?? {}),
  put: <T = null>(path: string, body?: unknown) => request<T>('PUT', path, body ?? {}),
  patch: <T = null>(path: string, body?: unknown) => request<T>('PATCH', path, body ?? {}),
  del: <T = null>(path: string) => request<T>('DELETE', path),
  /** Подтверждение кодом — без повторного окна, если код не подошёл. */
  stepUp: (code: string) => request<null>('POST', '/api/me/step-up', { code }, false),
  /** ⚠ Через fetch и blob, не ссылкой: токен не должен попасть в адрес. */
  download: async (path: string, body: unknown, fallbackName: string) => {
    const r = await send('POST', path, body);
    saveFile(await r.blob(), fileName(r.headers.get('Content-Disposition')) ?? fallbackName);
  },
};

/** Текст ошибки; `details.reason` (ответ SMTP, Key Manager) точнее общего. */
export const errorText = (err: unknown): string => {
  if (!(err instanceof ApiError)) return t('Что-то пошло не так.');
  const reason = err.details?.reason;
  return typeof reason === 'string' && reason ? reason : serverText(err.code, err.message, err.details?.field);
};
