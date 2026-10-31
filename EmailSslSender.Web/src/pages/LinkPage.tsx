import { useEffect, useState, type FormEvent } from 'react';
import { auth } from '../i18n/auth';
import { common } from '../i18n/common';
import { errorText, publicPost } from '../lib/api';
import { passwordRules } from '../lib/format';
import { translator } from '../lib/i18n';
import type { PasswordPolicy } from '../lib/types';
import { ErrorText, Spinner } from '../ui/bits';
import { AuthCard } from './Auth';

const t = translator({ ...common, ...auth });

type Kind = 'activate' | 'reset';

// ⚠ Токен — во фрагменте адреса (не уходит на сервер); стирается до первого рендера.
const token = new URLSearchParams(location.hash.slice(1)).get('token') ?? '';
if (location.pathname === '/activate' || location.pathname === '/password-reset') {
  history.replaceState(null, '', location.pathname);
}

/** Страницы ссылок из писем: приглашение (/activate) и сброс пароля (/password-reset). */
export function LinkPage({ kind }: { kind: Kind }) {
  const [state, setState] = useState<'loading' | 'invalid' | 'ready' | 'done'>(token ? 'loading' : 'invalid');
  const [who, setWho] = useState('');
  const [policy, setPolicy] = useState<PasswordPolicy | null>(null);
  const [password, setPassword] = useState('');
  const [repeat, setRepeat] = useState('');
  const [error, setError] = useState('');
  const [busy, setBusy] = useState(false);

  useEffect(() => {
    if (!token) return;
    (async () => {
      if (kind === 'activate') {
        // ⚠ Токен — телом POST: адрес оседает в журналах прокси.
        const r = await publicPost<{ email: string; policy: PasswordPolicy }>('/api/activation/info', { token });
        if (!r.ok) return setState('invalid');
        setWho(r.data.email);
        setPolicy(r.data.policy);
      } else {
        const r = await publicPost<PasswordPolicy>('/api/password-reset/info', { token });
        if (!r.ok) return setState('invalid');
        setPolicy(r.data);
      }
      setState('ready');
    })().catch(() => setState('invalid'));
  }, [kind]);

  const submit = async (event: FormEvent) => {
    event.preventDefault();
    setError('');
    if (password !== repeat) return setError(t('Пароли не совпадают.'));
    setBusy(true);
    const r = await publicPost(kind === 'activate' ? '/api/activation' : '/api/password-reset', { token, password });
    setBusy(false);
    if (r.ok) setState('done'); else setError(errorText(r.error));
  };

  const title = kind === 'activate' ? t('Добро пожаловать') : t('Новый пароль');

  if (state === 'loading') return <AuthCard title={title}><Spinner /></AuthCard>;

  if (state === 'invalid') {
    return (
      <AuthCard title={title}>
        <ErrorText text={kind === 'activate'
          ? t('Ссылка недействительна или уже использована. Попросите новую у администратора.')
          : t('Ссылка недействительна или уже использована. Запросите новую.')} />
      </AuthCard>
    );
  }

  if (state === 'done') {
    return (
      <AuthCard title={title}>
        <p className="ok-text">
          {kind === 'activate'
            ? t('Пароль задан. При первом входе подключите приложение-аутентификатор.')
            : t('Пароль изменён, прежние сессии закрыты.')}
        </p>
        <a className="button primary" href="/">{t('Войти')}</a>
      </AuthCard>
    );
  }

  return (
    <AuthCard title={title}>
      <form autoComplete="off" onSubmit={submit}>
        <p className="muted">{who || t('Придумайте новый пароль.')}</p>
        <input type="password" placeholder={t('Новый пароль')} autoComplete="new-password" required autoFocus
          value={password} onChange={e => setPassword(e.target.value)} />
        <input type="password" placeholder={t('Ещё раз')} autoComplete="new-password" required
          value={repeat} onChange={e => setRepeat(e.target.value)} />
        {policy && <p className="muted small">{passwordRules(policy)}</p>}
        <ErrorText text={error} />
        <button type="submit" className="primary" disabled={busy}>{kind === 'activate' ? t('Задать пароль') : t('Сменить пароль')}</button>
      </form>
    </AuthCard>
  );
}
