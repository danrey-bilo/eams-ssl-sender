import { useEffect, useRef, useState, type FormEvent, type ReactNode } from 'react';
import { auth } from '../i18n/auth';
import { common } from '../i18n/common';
import { adopt, api, ApiError, Cancelled, errorText, publicPost } from '../lib/api';
import { passwordRules } from '../lib/format';
import { translator } from '../lib/i18n';
import { qrPath } from '../lib/qr';
import type { PasswordPolicy, SecondFactorChallenge, Tokens, TotpSetup } from '../lib/types';
import { CodeInput, ErrorText, Field, Spinner } from '../ui/bits';
import { Brand } from '../ui/Icon';

const t = translator({ ...common, ...auth });

export function AuthCard({ title, children }: { title: string; children: ReactNode }) {
  return (
    <div className="auth">
      <div className="auth-card">
        <Brand />
        <h1>{title}</h1>
        <div className="auth-body">{children}</div>
      </div>
    </div>
  );
}

/** Отправка формы входа; любые ошибки — в форму. */
function useSubmit(action: () => Promise<void>) {
  const [error, setError] = useState('');
  const [busy, setBusy] = useState(false);
  const submit = async (event: FormEvent) => {
    event.preventDefault();
    setError('');
    setBusy(true);
    try {
      await action();
    } catch (err) {
      if (!(err instanceof Cancelled)) setError(errorText(err));
    } finally {
      setBusy(false);
    }
  };
  return { error, setError, busy, submit };
}

export function Login({ message, onEntered, onSecondFactor }: {
  message?: string;
  onEntered: () => void;
  onSecondFactor: (challenge: SecondFactorChallenge) => void;
}) {
  const [email, setEmail] = useState('');
  const [password, setPassword] = useState('');
  const [forgot, setForgot] = useState(false);
  const { error, busy, submit } = useSubmit(async () => {
    const r = await publicPost<Tokens>('/api/auth/login', { email: email.trim(), password, use_cookie: true });
    if (r.ok) { adopt(r.data); onEntered(); return; }
    if (r.error.code === 'TWO_FACTOR_REQUIRED') { onSecondFactor(r.error.details as unknown as SecondFactorChallenge); return; }
    throw r.error;
  });

  if (forgot) return <Forgot email={email} onBack={() => setForgot(false)} />;

  return (
    <AuthCard title={t('Вход')}>
      <form onSubmit={submit}>
        <Field label={t('Почта')}>
          <input type="email" autoComplete="username" required autoFocus placeholder="admin@example.com"
            value={email} onChange={e => setEmail(e.target.value)} />
        </Field>
        <Field label={t('Пароль')}>
          <input type="password" autoComplete="current-password" required value={password} onChange={e => setPassword(e.target.value)} />
        </Field>
        <ErrorText text={error || message || ''} />
        <button type="submit" className="primary" disabled={busy}>{t('Войти')}</button>
        <button type="button" className="link" onClick={() => setForgot(true)}>{t('Забыли пароль?')}</button>
      </form>
    </AuthCard>
  );
}

function Forgot({ email: initial, onBack }: { email: string; onBack: () => void }) {
  const [email, setEmail] = useState(initial);
  const [sent, setSent] = useState(false);
  const { busy, submit } = useSubmit(async () => {
    await publicPost('/api/password-reset/request', { email: email.trim() });
    setSent(true);
  });

  return (
    <AuthCard title={t('Сброс пароля')}>
      <form onSubmit={submit}>
        <Field label={t('Почта')}>
          <input type="email" required autoFocus value={email} onChange={e => setEmail(e.target.value)} />
        </Field>
        {sent && <p className="muted">{t('Если адрес есть в системе, ссылка придёт на почту.')}</p>}
        <button type="submit" className="primary" disabled={busy}>{t('Отправить ссылку')}</button>
        <button type="button" className="link" onClick={onBack}>{t('Назад')}</button>
      </form>
    </AuthCard>
  );
}

export function SecondFactor({ challenge, onEntered, onBack }: {
  challenge: SecondFactorChallenge;
  onEntered: () => void;
  onBack: (message?: string) => void;
}) {
  const viaEmail = challenge.method === 'email';
  const [code, setCode] = useState('');
  const [sent, setSent] = useState(viaEmail);
  const { error, setError, busy, submit } = useSubmit(async () => {
    const r = await publicPost<Tokens>('/api/auth/two-factor', { challenge: challenge.challenge, code, use_cookie: true });
    if (r.ok) { adopt(r.data); onEntered(); return; }
    if (r.error.code === 'ACTIVATION_TOKEN_INVALID' || r.error.code === 'SESSION_EXPIRED') {
      onBack(t('Время на ввод кода вышло. Войдите снова.'));
      return;
    }
    throw r.error;
  });

  const sendEmail = async () => {
    const r = await publicPost('/api/auth/two-factor/email', { challenge: challenge.challenge });
    if (r.ok) setSent(true); else setError(errorText(r.error));
  };

  return (
    <AuthCard title={t('Подтверждение входа')}>
      <form onSubmit={submit}>
        <p className="muted">{viaEmail ? t('Код из письма') : t('Код из приложения-аутентификатора')}</p>
        <CodeInput value={code} onChange={setCode} />
        {sent && <p className="muted small">{t('Код отправлен на почту.')}</p>}
        {!viaEmail && <p className="muted small">{t('Нет доступа к приложению — попросите другого администратора сбросить второй фактор.')}</p>}
        <ErrorText text={error} />
        <button type="submit" className="primary" disabled={busy}>{t('Подтвердить')}</button>
        <div className="inline">
          {(challenge.available ?? []).includes('email') && (
            <button type="button" className="link" onClick={sendEmail}>{viaEmail ? t('Отправить ещё раз') : t('Код на почту')}</button>
          )}
          <button type="button" className="link" onClick={() => onBack()}>{t('Назад')}</button>
        </div>
      </form>
    </AuthCard>
  );
}

export function Enrollment({ onDone, onLogout }: { onDone: () => void; onLogout: () => void }) {
  const [setup, setSetup] = useState<TotpSetup | null>(null);
  const [code, setCode] = useState('');
  const { error, setError, busy, submit } = useSubmit(async () => {
    await api.post('/api/me/two-factor/totp/confirm', { code });
    onDone();
  });

  // ⚠ setup — один раз: каждый вызов выдаёт новый секрет.
  const started = useRef(false);

  useEffect(() => {
    if (started.current) return;
    started.current = true;
    api.post<TotpSetup>('/api/me/two-factor/totp/setup').then(setSetup, err => {
      if (!(err instanceof Cancelled)) setError(errorText(err));
    });
  }, [setError]);

  const qr = setup ? qrPath(setup.uri) : null;

  return (
    <AuthCard title={t('Аутентификатор')}>
      {!setup && !error && <Spinner />}
      {setup && (
        <form onSubmit={submit}>
          <p className="muted">{t('Отсканируйте код в Google Authenticator, Aegis или 1Password и введите шесть цифр.')}</p>
          <div className="qr">
            {qr && (
              <svg viewBox={`0 0 ${qr.size} ${qr.size}`} shapeRendering="crispEdges" role="img" aria-label={t('QR-код для приложения-аутентификатора')}>
                <rect width={qr.size} height={qr.size} fill="#fff" />
                <path d={qr.path} fill="#000" />
              </svg>
            )}
            <p className="secret mono small">{setup.secret.replace(/(.{4})/g, '$1 ').trim()}</p>
          </div>
          <CodeInput value={code} onChange={setCode} />
          <ErrorText text={error} />
          <button type="submit" className="primary" disabled={busy}>{t('Подключить')}</button>
          <button type="button" className="link" onClick={onLogout}>{t('Выйти')}</button>
        </form>
      )}
      {!setup && error && <ErrorText text={error} />}
    </AuthCard>
  );
}

export function PasswordChange({ onDone, onLogout }: { onDone: () => void; onLogout: () => void }) {
  const [policy, setPolicy] = useState<PasswordPolicy | null>(null);
  const [current, setCurrent] = useState('');
  const [next, setNext] = useState('');
  const [repeat, setRepeat] = useState('');
  const { error, busy, submit } = useSubmit(async () => {
    // ⚠ Без кода: errorText подменил бы переведённый текст общим отказом VALIDATION_ERROR.
    if (next !== repeat) throw new ApiError(400, undefined, t('Пароли не совпадают.'));
    await api.post('/api/me/password', { current_password: current, new_password: next });
    onDone();
  });

  useEffect(() => { api.get<PasswordPolicy>('/api/auth/password-policy').then(setPolicy, () => null); }, []);

  return (
    <AuthCard title={t('Новый пароль')}>
      <form onSubmit={submit}>
        <p className="muted">{t('Администратор попросил задать новый пароль.')}</p>
        <Field label={t('Текущий пароль')}>
          <input type="password" autoComplete="current-password" required autoFocus value={current} onChange={e => setCurrent(e.target.value)} />
        </Field>
        <Field label={t('Новый пароль')}>
          <input type="password" autoComplete="new-password" required value={next} onChange={e => setNext(e.target.value)} />
        </Field>
        <Field label={t('Ещё раз')}>
          <input type="password" autoComplete="new-password" required value={repeat} onChange={e => setRepeat(e.target.value)} />
        </Field>
        {policy && <p className="muted small">{passwordRules(policy)}</p>}
        <ErrorText text={error} />
        <button type="submit" className="primary" disabled={busy}>{t('Сменить пароль')}</button>
        <button type="button" className="link" onClick={onLogout}>{t('Выйти')}</button>
      </form>
    </AuthCard>
  );
}
