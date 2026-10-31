import { useState } from 'react';
import { common } from '../i18n/common';
import { users as dict } from '../i18n/users';
import { api } from '../lib/api';
import { isMobile } from '../lib/device';
import { useLoad } from '../lib/hooks';
import { translator } from '../lib/i18n';
import type { ResetIssued, User, UserCreated, UsersPage } from '../lib/types';
import { Failed, Field, PageHead, Spinner, Status, type DotKind } from '../ui/bits';
import { Icon } from '../ui/Icon';
import { Menu, type MenuItem } from '../ui/Menu';
import { FormModal } from '../ui/Modal';
import { useOverlay } from '../ui/Overlay';

const t = translator({ ...common, ...dict });

async function loadAll(): Promise<User[]> {
  const users: User[] = [];
  let after: string | null = null;
  do {
    const page: UsersPage = await api.get<UsersPage>('/api/users?limit=100' + (after ? '&after=' + encodeURIComponent(after) : ''));
    users.push(...page.items);
    after = page.next_cursor;
  } while (after && users.length < 1000);
  return users;
}

const locked = (u: User) => !!u.locked_until && new Date(u.locked_until) > new Date();

function userState(u: User): [DotKind, string] {
  if (locked(u)) return ['err', t('Заблокирован')];
  if (!u.is_active) return ['', t('Отключён')];
  if (!u.activated_at) return ['warn', t('Приглашён')];
  return ['ok', t('Активен')];
}

// Функция, а не строка: текст — на языке в момент показа.
const noMail = () => t('Почта не настроена — передайте ссылку сами.');

export function UsersPage({ myId }: { myId: string }) {
  const { data, error, reload } = useLoad(loadAll);
  const { show, confirm, run, showLink, toast } = useOverlay();

  if (error) return <Failed text={error} />;
  if (!data) return <Spinner />;

  const invite = async () => {
    let created: UserCreated | null = null;
    const ok = await show(close => <InviteDialog onClose={close} onCreated={c => { created = c; }} />);
    if (!ok) return;
    const token = (created as UserCreated | null)?.invitation_token;
    if (token) await showLink(t('Ссылка приглашения'), noMail(), location.origin + '/activate#token=' + token);
    else toast(t('Приглашение отправлено'));
    reload();
  };

  const link = async (u: User, action: 'invite' | 'reset-password') => {
    let result: UserCreated & ResetIssued = { invitation_token: null, reset_token: null };
    if (!await run(async () => { result = { ...result, ...await api.post<UserCreated & ResetIssued>(`/api/users/${u.id}/${action}`) }; })) return;
    const token = result.invitation_token ?? result.reset_token;
    if (token) await showLink(t('Ссылка'), noMail(), location.origin + (action === 'invite' ? '/activate' : '/password-reset') + '#token=' + token);
    else toast(t('Письмо со ссылкой отправлено'));
    reload();
  };

  const act = async (action: () => Promise<unknown>, done: string, ask?: [string, string]) => {
    if (ask && !await confirm(ask[0], ask[1], t('Да'), true)) return;
    if (await run(action, done)) reload();
  };

  const rename = async (u: User) => {
    if (await show(close => <RenameDialog user={u} onClose={close} />)) { toast(t('Сохранено')); reload(); }
  };

  const menu = (u: User): MenuItem[] => {
    const self = u.id === myId;
    const base = '/api/users/' + u.id;
    return [
      { text: t('Изменить имя'), action: () => rename(u) },
      !u.activated_at && { text: t('Пригласить ещё раз'), action: () => link(u, 'invite') },
      !!u.activated_at && { text: t('Ссылка сброса пароля'), action: () => link(u, 'reset-password') },
      locked(u) && { text: t('Разблокировать'), action: () => act(() => api.post(base + '/unlock'), t('Разблокирован')) },
      u.authenticator_enabled && !self && {
        text: t('Сбросить 2FA'),
        action: () => act(() => api.del(base + '/two-factor'), t('2FA сброшена'),
          [t('Сбросить 2FA?'), t('При следующем входе аутентификатор подключат заново.')]),
      },
      !self && { text: t('Завершить сессии'), action: () => act(() => api.del(base + '/sessions'), t('Сессии завершены')) },
      !self && (u.is_active
        ? {
          text: t('Отключить'), danger: true,
          action: () => act(() => api.post(base + '/disable'), t('Отключён'),
            [t('Отключить {email}?', { email: u.email }), t('Вход будет закрыт, сессии завершены.')]),
        }
        : { text: t('Включить'), action: () => act(() => api.post(base + '/enable'), t('Включён')) }),
      !self && {
        text: t('Удалить'), danger: true,
        action: () => act(() => api.del(base), t('Удалён'),
          [t('Удалить {email}?', { email: u.email }), t('Учётная запись будет удалена без возможности восстановления.')]),
      },
    ];
  };

  const name = (u: User) => (
    <>
      <span className="strong">{u.display_name || u.email}{u.id === myId && <span className="muted"> · {t('вы')}</span>}</span>
      {u.display_name && <span className="sub">{u.email}</span>}
    </>
  );

  return (
    <>
      <PageHead title={t('Пользователи')} sub={t('Каждый пользователь — администратор')}>
        <button className="primary" onClick={invite}><Icon name="plus" />{t('Пригласить')}</button>
      </PageHead>
      <div className="card flush">
        {isMobile ? (
          <div className="list">
            {data.map(u => {
              const [kind, label] = userState(u);
              return (
                <div key={u.id} className={'item' + (u.is_active ? '' : ' off')}>
                  <div className="item-main">
                    <div className="item-title">{name(u)}</div>
                    <div className="item-status"><Status kind={kind}>{label}</Status>{u.authenticator_enabled && <span className="chip">2FA</span>}</div>
                  </div>
                  <Menu title={u.email} items={menu(u)} />
                </div>
              );
            })}
          </div>
        ) : (
          <table>
            <thead>
              <tr><th>{t('Пользователь')}</th><th>{t('Статус')}</th><th>{t('Второй фактор')}</th><th /></tr>
            </thead>
            <tbody>
              {data.map(u => {
                const [kind, label] = userState(u);
                return (
                  <tr key={u.id} className={u.is_active ? '' : 'off'}>
                    <td>{name(u)}</td>
                    <td><Status kind={kind}>{label}</Status></td>
                    <td className="muted">{u.authenticator_enabled ? t('Приложение') : '—'}</td>
                    <td className="actions"><Menu items={menu(u)} /></td>
                  </tr>
                );
              })}
            </tbody>
          </table>
        )}
      </div>
    </>
  );
}

function InviteDialog({ onClose, onCreated }: { onClose: (done: boolean) => void; onCreated: (created: UserCreated) => void }) {
  const [email, setEmail] = useState('');
  const [name, setName] = useState('');
  return (
    <FormModal
      title={t('Пригласить')}
      note={t('Человек сам задаст пароль по ссылке.')}
      submit={t('Пригласить')}
      onSubmit={async () => onCreated(await api.post<UserCreated>('/api/users', {
        email: email.trim(),
        display_name: name.trim() || null,
        // Ролей нет: суперпользователем распоряжается только суперпользователь.
        is_superuser: true,
      }))}
      onClose={onClose}
    >
      <Field label={t('Почта')}><input type="email" required autoFocus placeholder="colleague@example.com" value={email} onChange={e => setEmail(e.target.value)} /></Field>
      <Field label={t('Имя')}><input type="text" placeholder={t('Имя Фамилия')} value={name} onChange={e => setName(e.target.value)} /></Field>
    </FormModal>
  );
}

function RenameDialog({ user, onClose }: { user: User; onClose: (done: boolean) => void }) {
  const [name, setName] = useState(user.display_name ?? '');
  return (
    <FormModal title={t('Имя')} onSubmit={() => api.patch('/api/users/' + user.id, { display_name: name.trim() })} onClose={onClose}>
      <Field label={user.email}><input type="text" autoFocus value={name} onChange={e => setName(e.target.value)} /></Field>
    </FormModal>
  );
}
