import { useState, type FormEvent } from 'react';
import { common } from '../i18n/common';
import { profile } from '../i18n/profile';
import { api, Cancelled, errorText } from '../lib/api';
import { agentText, fmtDateTime, passwordRules } from '../lib/format';
import { useLoad } from '../lib/hooks';
import { translator } from '../lib/i18n';
import type { Me, PasswordPolicy, Session, TwoFactorStatus } from '../lib/types';
import { ErrorText, Failed, Field, PageHead, Spinner, Status } from '../ui/bits';
import { Icon } from '../ui/Icon';
import { useOverlay } from '../ui/Overlay';

const t = translator({ ...common, ...profile });

export function ProfilePage({ onRenamed, onLogout }: { onRenamed: () => void; onLogout: () => void }) {
  const { data, error, reload } = useLoad(() => Promise.all([
    api.get<Me>('/api/me'),
    api.get<Session[]>('/api/me/sessions'),
    api.get<TwoFactorStatus>('/api/me/two-factor'),
    api.get<PasswordPolicy>('/api/auth/password-policy').catch(() => null),
  ]));
  const { run } = useOverlay();

  if (error) return <Failed text={error} />;
  if (!data) return <Spinner />;

  const [me, sessions, factor, policy] = data;

  return (
    <>
      <PageHead title={t('Профиль')}>
        <button onClick={onLogout}><Icon name="logout" />{t('Выйти')}</button>
      </PageHead>
      <div className="stack">
        <div className="card"><NameForm me={me} onSaved={onRenamed} /></div>
        <div className="card"><PasswordForm policy={policy} /></div>
        <div className="card">
          <h2>{t('Второй фактор')}</h2>
          <Status kind={factor.confirmed ? 'ok' : 'warn'}>
            {factor.confirmed ? t('Приложение-аутентификатор подключено') : t('Не подключён')}
          </Status>
        </div>
        <div className="card flush">
          <div className="card-head"><h2>{t('Сессии')}</h2></div>
          <table>
            <tbody>
              {sessions.map(s => (
                <tr key={s.id}>
                  <td>
                    {agentText(s.user_agent)}{s.current && <span className="muted"> · {t('эта')}</span>}
                    <span className="sub">{s.address ?? ''}</span>
                  </td>
                  <td className="muted">{fmtDateTime(s.last_seen_at)}</td>
                  <td className="actions">
                    {!s.current && (
                      <button className="icon danger" title={t('Завершить')}
                        onClick={async () => { if (await run(() => api.del('/api/me/sessions/' + s.id), t('Сессия завершена'))) reload(); }}>
                        <Icon name="logout" />
                      </button>
                    )}
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      </div>
    </>
  );
}

function NameForm({ me, onSaved }: { me: Me; onSaved: () => void }) {
  const { run } = useOverlay();
  const [name, setName] = useState(me.display_name ?? '');

  const submit = async (event: FormEvent) => {
    event.preventDefault();
    if (await run(() => api.patch('/api/me', { display_name: name.trim() }), t('Сохранено'))) onSaved();
  };

  return (
    <form onSubmit={submit}>
      <div className="row">
        <Field label={t('Почта')}><input type="email" readOnly value={me.email} /></Field>
        <Field label={t('Имя')}><input type="text" value={name} onChange={e => setName(e.target.value)} /></Field>
      </div>
      <div className="actions-row"><button type="submit" className="primary">{t('Сохранить')}</button></div>
    </form>
  );
}

function PasswordForm({ policy }: { policy: PasswordPolicy | null }) {
  const { toast } = useOverlay();
  const [current, setCurrent] = useState('');
  const [next, setNext] = useState('');
  const [repeat, setRepeat] = useState('');
  const [error, setError] = useState('');

  const submit = async (event: FormEvent) => {
    event.preventDefault();
    setError('');
    if (next !== repeat) return setError(t('Пароли не совпадают.'));
    try {
      await api.post('/api/me/password', { current_password: current, new_password: next });
      toast(t('Пароль изменён'));
      setCurrent(''); setNext(''); setRepeat('');
    } catch (err) {
      if (!(err instanceof Cancelled)) setError(errorText(err));
    }
  };

  return (
    <form onSubmit={submit}>
      <h2>{t('Пароль')}</h2>
      <div className="row">
        <Field label={t('Текущий')}><input type="password" autoComplete="current-password" required value={current} onChange={e => setCurrent(e.target.value)} /></Field>
        <Field label={t('Новый')}><input type="password" autoComplete="new-password" required value={next} onChange={e => setNext(e.target.value)} /></Field>
        <Field label={t('Ещё раз')}><input type="password" autoComplete="new-password" required value={repeat} onChange={e => setRepeat(e.target.value)} /></Field>
      </div>
      {policy && <p className="muted small">{passwordRules(policy)}</p>}
      <ErrorText text={error} />
      <div className="actions-row"><button type="submit" className="primary">{t('Сменить')}</button></div>
    </form>
  );
}
