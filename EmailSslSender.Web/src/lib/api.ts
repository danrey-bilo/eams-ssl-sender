// Обращения к API: токен доступа в памяти, обновление пары кукой, повтор после кода.
//
// ⚠ Токен доступа живёт только в памяти страницы; токен обновления — в куке HttpOnly
// `__Host-sender-refresh`, до которой сценарий не дотягивается. Перезагрузка страницы
// восстанавливает сессию обменом куки на новую пару.
//
// ⚠ Опасные действия (выгрузка сертификата и ключа, безопасность, люди, подписки, почта)
// сервер пускает только со свежим кодом второго фактора: отказ `SECOND_FACTOR_REQUIRED`
// открывает окно кода, и то же действие повторяется само. Код действует несколько минут
// (по умолчанию 10), и следующие опасные действия за это время его не спрашивают.

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

/** Действие отменил человек (закрыл окно кода) или сессия кончилась — показывать нечего. */
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

/** Кто показывает вход, когда сессия кончилась, и кто спрашивает код перед опасным действием. */
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

/** Отправить запрос с токеном; удача — ответ как есть, отказ — ApiError или Cancelled. */
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
    throw new ApiError(0, 'NETWORK', 'Сервер недоступен.');
  }

  if (r.ok) return r;

  const data = await r.json().catch(() => null) as Envelope | null;
  const code = data?.code;

  if (r.status === 401 && retry && token && (code === 'TOKEN_EXPIRED' || code === 'UNAUTHENTICATED')) {
    if (await refresh()) return send(method, path, body, false, stepUp);
  }

  if (r.status === 401 && token && code && SESSION_GONE.includes(code)) {
    forget();
    onSignedOut('Сессия закончилась. Войдите снова.');
    throw new Cancelled();
  }

  // ⚠ Опасное действие без свежего кода: спросить код и повторить то же самое. Окно одно на
  // всех — два запроса, упёршихся одновременно, не откроют два окна.
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

/**
 * Отдать браузеру файл на сохранение.
 *
 * ⚠ Тип всегда application/octet-stream: Chrome на телефоне дописывает к имени расширение «своего»
 * типа — .crt превращался в .crt.cer, ключ — в .key.pem. У «просто байтов» своего расширения нет.
 */
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

/** Имя файла из Content-Disposition: сперва filename* (UTF-8), затем filename. */
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
  /**
   * Скачать файл ответом на POST: браузер сохраняет его под именем из Content-Disposition.
   *
   * ⚠ Через fetch и blob, а не ссылкой: токен доступа живёт в памяти и в адрес не попадает.
   */
  download: async (path: string, body: unknown, fallbackName: string) => {
    const r = await send('POST', path, body);
    saveFile(await r.blob(), fileName(r.headers.get('Content-Disposition')) ?? fallbackName);
  },
};

/** Текст ошибки; `details.reason` (ответ чужого сервера — SMTP, Key Manager) точнее общего сообщения. */
export const errorText = (err: unknown): string => {
  if (!(err instanceof ApiError)) return 'Что-то пошло не так.';
  const reason = err.details?.reason;
  return typeof reason === 'string' && reason ? reason : err.message;
};
