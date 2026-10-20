import { useState } from 'react';
import { api } from '../lib/api';
import { useLoad } from '../lib/hooks';
import type { ResetIssued, User, UserCreated, UsersPage } from '../lib/types';
import { Field, PageHead, Spinner, Status, type DotKind } from '../ui/bits';
import { Icon } from '../ui/Icon';
import { Menu } from '../ui/Menu';
import { FormModal } from '../ui/Modal';
import { useOverlay } from '../ui/Overlay';

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
  if (locked(u)) return ['err', 'Заблокирован'];
  if (!u.is_active) return ['', 'Отключён'];
  if (!u.activated_at) return ['warn', 'Приглашён'];
  return ['ok', 'Активен'];
}

const NO_MAIL = 'Почта не настроена — передайте ссылку сами.';

export function UsersPage({ myId }: { myId: string }) {
  const { data, error, reload } = useLoad(loadAll);
  const { show, confirm, run, showLink, toast } = useOverlay();

  if (error) return <div className="card"><p className="error">{error}</p></div>;
  if (!data) return <Spinner />;

  const invite = async () => {
    let created: UserCreated | null = null;
    const ok = await show(close => <InviteDialog onClose={close} onCreated={c => { created = c; }} />);
    if (!ok) return;
    const token = (created as UserCreated | null)?.invitation_token;
    if (token) await showLink('Ссылка приглашения', NO_MAIL, location.origin + '/activate#token=' + token);
    else toast('Приглашение отправлено');
    reload();
  };

  const link = async (u: User, action: 'invite' | 'reset-password') => {
    let result: UserCreated & ResetIssued = { invitation_token: null, reset_token: null };
    if (!await run(async () => { result = { ...result, ...await api.post<UserCreated & ResetIssued>(`/api/users/${u.id}/${action}`) }; })) return;
    const token = result.invitation_token ?? result.reset_token;
    if (token) await showLink('Ссылка', NO_MAIL, location.origin + (action === 'invite' ? '/activate' : '/password-reset') + '#token=' + token);
    else toast('Письмо со ссылкой отправлено');
    reload();
  };

  const act = async (action: () => Promise<unknown>, done: string, ask?: [string, string]) => {
    if (ask && !await confirm(ask[0], ask[1], 'Да', true)) return;
    if (await run(action, done)) reload();
  };

  const rename = async (u: User) => {
    if (await show(close => <RenameDialog user={u} onClose={close} />)) { toast('Сохранено'); reload(); }
  };

  return (
    <>
      <PageHead title="Пользователи">
        <button className="primary" onClick={invite}><Icon name="plus" />Пригласить</button>
      </PageHead>
      <div className="card flush">
        <table>
          <thead>
            <tr><th>Пользователь</th><th>Статус</th><th className="hide-sm">Вход</th><th /></tr>
          </thead>
          <tbody>
            {data.map(u => {
              const [kind, label] = userState(u);
              const self = u.id === myId;
              const base = '/api/users/' + u.id;
              return (
                <tr key={u.id} className={u.is_active ? '' : 'off'}>
                  <td>
                    <span>{u.display_name || u.email}{self && <span className="muted"> · вы</span>}</span>
                    {u.display_name && <span className="sub">{u.email}</span>}
                  </td>
                  <td><Status kind={kind}>{label}</Status></td>
                  <td className="hide-sm muted">{u.authenticator_enabled ? '2FA' : '—'}</td>
                  <td className="actions">
                    <Menu items={[
                      { text: 'Изменить имя', action: () => rename(u) },
                      !u.activated_at && { text: 'Пригласить ещё раз', action: () => link(u, 'invite') },
                      !!u.activated_at && { text: 'Ссылка сброса пароля', action: () => link(u, 'reset-password') },
                      locked(u) && { text: 'Разблокировать', action: () => act(() => api.post(base + '/unlock'), 'Разблокирован') },
                      u.authenticator_enabled && !self && {
                        text: 'Сбросить 2FA',
                        action: () => act(() => api.del(base + '/two-factor'), '2FA сброшена',
                          ['Сбросить 2FA?', 'При следующем входе аутентификатор подключат заново.']),
                      },
                      !self && { text: 'Завершить сессии', action: () => act(() => api.del(base + '/sessions'), 'Сессии завершены') },
                      !self && (u.is_active
                        ? {
                          text: 'Отключить', danger: true,
                          action: () => act(() => api.post(base + '/disable'), 'Отключён',
                            ['Отключить ' + u.email + '?', 'Вход будет закрыт, сессии завершены.']),
                        }
                        : { text: 'Включить', action: () => act(() => api.post(base + '/enable'), 'Включён') }),
                      !self && {
                        text: 'Удалить', danger: true,
                        action: () => act(() => api.del(base), 'Удалён',
                          ['Удалить ' + u.email + '?', 'Учётная запись будет удалена без возможности восстановления.']),
                      },
                    ]} />
                  </td>
                </tr>
              );
            })}
          </tbody>
        </table>
      </div>
    </>
  );
}

function InviteDialog({ onClose, onCreated }: { onClose: (done: boolean) => void; onCreated: (created: UserCreated) => void }) {
  const [email, setEmail] = useState('');
  const [name, setName] = useState('');
  return (
    <FormModal
      title="Пригласить"
      note="Человек сам задаст пароль по ссылке."
      submit="Пригласить"
      onSubmit={async () => onCreated(await api.post<UserCreated>('/api/users', { email: email.trim(), display_name: name.trim() || null }))}
      onClose={onClose}
    >
      <Field label="Почта"><input type="email" required autoFocus placeholder="colleague@example.com" value={email} onChange={e => setEmail(e.target.value)} /></Field>
      <Field label="Имя"><input type="text" placeholder="Имя Фамилия" value={name} onChange={e => setName(e.target.value)} /></Field>
    </FormModal>
  );
}

function RenameDialog({ user, onClose }: { user: User; onClose: (done: boolean) => void }) {
  const [name, setName] = useState(user.display_name ?? '');
  return (
    <FormModal title="Имя" onSubmit={() => api.patch('/api/users/' + user.id, { display_name: name.trim() })} onClose={onClose}>
      <Field label={user.email}><input type="text" autoFocus value={name} onChange={e => setName(e.target.value)} /></Field>
    </FormModal>
  );
}
