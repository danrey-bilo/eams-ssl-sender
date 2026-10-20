import { useEffect, useState, type FormEvent } from 'react';
import { publicPost } from '../lib/api';
import { passwordRules } from '../lib/format';
import type { PasswordPolicy } from '../lib/types';
import { ErrorText, Spinner } from '../ui/bits';
import { AuthCard } from './Auth';

type Kind = 'activate' | 'reset';

// ⚠ Токен приходит во ФРАГМЕНТЕ адреса и стирается из адресной строки сразу, ещё до первого
// рендера: фрагмент не уходит на сервер и не оседает в логах прокси, а стёртый — в истории вкладки.
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
        const r = await fetch('/api/activation?token=' + encodeURIComponent(token), { cache: 'no-store' });
        if (!r.ok) return setState('invalid');
        const info = await r.json() as { email: string; policy: PasswordPolicy };
        setWho(info.email);
        setPolicy(info.policy);
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
    if (password !== repeat) return setError('Пароли не совпадают.');
    setBusy(true);
    const r = await publicPost(kind === 'activate' ? '/api/activation' : '/api/password-reset', { token, password });
    setBusy(false);
    if (r.ok) setState('done'); else setError(r.error.message);
  };

  const title = kind === 'activate' ? 'Добро пожаловать' : 'Новый пароль';

  if (state === 'loading') return <AuthCard title={title}><Spinner /></AuthCard>;

  if (state === 'invalid') {
    return (
      <AuthCard title={title}>
        <ErrorText text={kind === 'activate'
          ? 'Ссылка недействительна или уже использована. Попросите новую у администратора.'
          : 'Ссылка недействительна или уже использована. Запросите новую.'} />
      </AuthCard>
    );
  }

  if (state === 'done') {
    return (
      <AuthCard title={title}>
        <p className="ok-text">
          {kind === 'activate'
            ? 'Пароль задан. При первом входе подключите приложение-аутентификатор.'
            : 'Пароль изменён, прежние сессии закрыты.'}
        </p>
        <a className="button primary" href="/">Войти</a>
      </AuthCard>
    );
  }

  return (
    <AuthCard title={title}>
      <form autoComplete="off" onSubmit={submit}>
        <p className="muted">{who || 'Придумайте новый пароль.'}</p>
        <input type="password" placeholder="Новый пароль" autoComplete="new-password" required autoFocus
          value={password} onChange={e => setPassword(e.target.value)} />
        <input type="password" placeholder="Ещё раз" autoComplete="new-password" required
          value={repeat} onChange={e => setRepeat(e.target.value)} />
        {policy && <p className="muted small">{passwordRules(policy)}</p>}
        <ErrorText text={error} />
        <button type="submit" className="primary" disabled={busy}>{kind === 'activate' ? 'Задать пароль' : 'Сменить пароль'}</button>
      </form>
    </AuthCard>
  );
}
