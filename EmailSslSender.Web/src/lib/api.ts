// Обращения к API: токен доступа в памяти, обновление пары кукой.
//
// ⚠ Токен доступа живёт только в памяти страницы; токен обновления — в куке HttpOnly
// `__Host-sender-refresh`, до которой сценарий не дотягивается. Перезагрузка страницы
// восстанавливает сессию обменом куки на новую пару.

import type { Tokens } from './types';

export class ApiError extends Error {
  readonly status: number;
  readonly code: string | undefined;
  readonly details: Record<string, unknown> | undefined;

  constructor(status: number, code: string | undefined, message: string | undefined, details?: Record<string, unknown>) {
    super(message || 'Сервер недоступен.');
    this.status = status;
    this.code = code;
    this.details = details;
  }
}

/** Сессия кончилась и показан вход — сообщать об ошибке нечего. */
export class Cancelled extends Error {}

interface Envelope {
  code?: string;
  message?: string;
  details?: Record<string, unknown>;
}

let token: string | null = null;
let timer: ReturnType<typeof setTimeout> | undefined;
let refreshing: Promise<boolean> | null = null;

let onSignedOut: (message?: string) => void = () => {};

/**
 * Кто показывает вход, когда сессия кончилась.
 *
 * ⚠ Второй фактор спрашивается только при входе: действия после входа кода не требуют.
 */
export function connect(handlers: { signedOut: (message?: string) => void }) {
  onSignedOut = handlers.signedOut;
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

/** Обменять куку обновления на новую пару. Один обмен на всех: кука одноразовая. */
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

/** Запрос без сессии: вход, второй шаг, ссылки из писем. Ответ — тело или конверт ошибки. */
export async function publicPost<T>(path: string, body: unknown): Promise<{ ok: true; data: T } | { ok: false; error: ApiError }> {
  try {
    const r = await fetch(path, {
      method: 'POST', headers: { 'Content-Type': 'application/json' }, cache: 'no-store', body: JSON.stringify(body),
    });
    const data = await r.json().catch(() => null) as (T & Envelope) | null;
    if (r.ok) return { ok: true, data: data as T };
    return { ok: false, error: new ApiError(r.status, data?.code, data?.message, data?.details) };
  } catch {
    return { ok: false, error: new ApiError(0, 'NETWORK', 'Сервер недоступен.') };
  }
}

const SESSION_GONE = ['TOKEN_EXPIRED', 'UNAUTHENTICATED', 'SESSION_REVOKED', 'SESSION_EXPIRED'];

async function request<T>(method: string, path: string, body?: unknown, retry = true): Promise<T> {
  const headers: Record<string, string> = {};
  if (body !== undefined) headers['Content-Type'] = 'application/json';
  if (token) headers.Authorization = 'Bearer ' + token;

  let r: Response;
  try {
    r = await fetch(path, {
      method, headers, cache: 'no-store', body: body === undefined ? undefined : JSON.stringify(body),
    });
  } catch {
    throw new ApiError(0, 'NETWORK', 'Сервер недоступен.');
  }

  if (r.status === 204 || r.status === 202) return null as T;
  const data = await r.json().catch(() => null) as (T & Envelope) | null;
  if (r.ok) return data as T;

  const code = data?.code;

  if (r.status === 401 && retry && token && (code === 'TOKEN_EXPIRED' || code === 'UNAUTHENTICATED')) {
    if (await refresh()) return request<T>(method, path, body, false);
  }

  if (r.status === 401 && token && code && SESSION_GONE.includes(code)) {
    forget();
    onSignedOut('Сессия закончилась. Войдите снова.');
    throw new Cancelled();
  }

  throw new ApiError(r.status, code, data?.message, data?.details);
}

export const api = {
  get: <T>(path: string) => request<T>('GET', path),
  post: <T = null>(path: string, body?: unknown) => request<T>('POST', path, body ?? {}),
  put: <T = null>(path: string, body?: unknown) => request<T>('PUT', path, body ?? {}),
  patch: <T = null>(path: string, body?: unknown) => request<T>('PATCH', path, body ?? {}),
  del: <T = null>(path: string) => request<T>('DELETE', path),
};

export const errorText = (err: unknown): string => (err instanceof ApiError ? err.message : 'Что-то пошло не так.');
