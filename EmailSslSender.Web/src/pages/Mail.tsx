import { useState, type FormEvent } from 'react';
import { api, Cancelled, errorText } from '../lib/api';
import { useLoad } from '../lib/hooks';
import { loadSettings, saveSettings, text, type Settings } from '../lib/settings';
import type { MailStatus } from '../lib/types';
import { Check, ErrorText, Field, PageHead, Spinner, Status, type DotKind } from '../ui/bits';
import { FormModal } from '../ui/Modal';
import { useOverlay } from '../ui/Overlay';

const SECURITY: Array<[string, string]> = [['tls', 'SSL/TLS'], ['starttls', 'STARTTLS'], ['none', 'Без шифрования']];

/** Сервер SMTP — имя: «https://mail.example.com/» сводится к «mail.example.com». */
const hostName = (value: string) => value.trim().replace(/^[a-z][a-z0-9+.-]*:\/\//i, '').replace(/\/+$/, '');

export function MailPage({ myEmail }: { myEmail: string }) {
  const { data, error, reload } = useLoad(() => Promise.all([api.get<MailStatus>('/api/mail'), loadSettings()]));
  const { show, toast, run, confirm } = useOverlay();

  if (error) return <div className="card"><p className="error">{error}</p></div>;
  if (!data) return <Spinner />;

  const [mail, settings] = data;
  const state: [DotKind, string] = mail.configured ? (mail.failed ? ['err', 'Есть неотправленные'] : ['ok', 'Работает']) : ['', 'Не настроена'];

  const sendTest = async () => {
    if (await show(close => <TestDialog to={myEmail} onClose={close} />)) toast('Сервер принял письмо');
  };

  const dropPassword = async () => {
    if (!await confirm('Снять пароль SMTP?', 'Почта будет уходить без авторизации.', 'Снять', true)) return;
    if (await run(() => api.put('/api/mail/password', { password: '' }), 'Пароль снят')) reload();
  };

  return (
    <>
      <PageHead title="Почта" />
      <div className="stack">
        <div className="card stack">
          <div className="head flat">
            <Status kind={state[0]} strong>{state[1]}</Status>
            <button onClick={sendTest} disabled={!mail.configured}>Тестовое письмо</button>
          </div>
          <div className="facts">
            <span>В очереди: <b>{mail.pending}</b></span>
            <span>Не ушло: <b>{mail.failed}</b></span>
          </div>
          {mail.last_error && <p className="note">{mail.last_error}</p>}
        </div>
        <div className="card">
          <SmtpForm mail={mail} settings={settings} onSaved={reload} onDropPassword={dropPassword} />
        </div>
      </div>
    </>
  );
}

function SmtpForm({ mail, settings, onSaved, onDropPassword }: {
  mail: MailStatus;
  settings: Settings;
  onSaved: () => void;
  onDropPassword: () => void;
}) {
  const { toast } = useOverlay();
  const [enabled, setEnabled] = useState(settings['mail.enabled']?.value === true);
  const [host, setHost] = useState(text(settings, 'mail.smtp_host'));
  const [port, setPort] = useState(text(settings, 'mail.smtp_port'));
  const [security, setSecurity] = useState(text(settings, 'mail.smtp_security') || 'starttls');
  const [user, setUser] = useState(text(settings, 'mail.smtp_username'));
  const [password, setPassword] = useState('');
  const [from, setFrom] = useState(text(settings, 'mail.from_address'));
  const [fromName, setFromName] = useState(text(settings, 'mail.from_name'));
  const [publicUrl, setPublicUrl] = useState(text(settings, 'server.public_url'));
  const [error, setError] = useState('');
  const [busy, setBusy] = useState(false);

  const submit = async (event: FormEvent) => {
    event.preventDefault();
    setError('');
    setBusy(true);
    try {
      await saveSettings(settings, {
        'mail.enabled': enabled,
        'mail.smtp_host': hostName(host),
        'mail.smtp_port': port ? Number(port) : '',
        'mail.smtp_security': security,
        'mail.smtp_username': user.trim(),
        'mail.from_address': from.trim(),
        'mail.from_name': fromName.trim(),
        'server.public_url': publicUrl.trim(),
      });
      if (password) await api.put('/api/mail/password', { password });
      setHost(hostName(host));
      setPassword('');
      toast('Сохранено');
      onSaved();
    } catch (err) {
      if (!(err instanceof Cancelled)) setError(errorText(err));
    } finally {
      setBusy(false);
    }
  };

  return (
    <form onSubmit={submit}>
      <h2>SMTP</h2>
      <Check label="Отправлять письма" checked={enabled} onChange={setEnabled} />
      <div className="row">
        <Field label="Сервер"><input type="text" placeholder="smtp.example.com" value={host} onChange={e => setHost(e.target.value)} /></Field>
        <Field label="Порт"><input type="number" min={1} max={65535} value={port} onChange={e => setPort(e.target.value)} /></Field>
        <Field label="Защита">
          <select value={security} onChange={e => setSecurity(e.target.value)}>
            {SECURITY.map(([v, t]) => <option key={v} value={v}>{t}</option>)}
          </select>
        </Field>
      </div>
      <div className="row">
        <Field label="Логин"><input type="text" autoComplete="off" value={user} onChange={e => setUser(e.target.value)} /></Field>
        <Field label="Пароль">
          <input type="password" autoComplete="new-password" placeholder={mail.password_set ? '•••••••• задан' : ''}
            value={password} onChange={e => setPassword(e.target.value)} />
        </Field>
      </div>
      <div className="row">
        <Field label="Адрес отправителя"><input type="email" placeholder="noreply@example.com" value={from} onChange={e => setFrom(e.target.value)} /></Field>
        <Field label="Имя отправителя"><input type="text" value={fromName} onChange={e => setFromName(e.target.value)} /></Field>
      </div>
      <Field label="Адрес сервиса (для ссылок в письмах)">
        <input type="url" value={publicUrl} onChange={e => setPublicUrl(e.target.value)} />
      </Field>
      <ErrorText text={error} />
      <div className="actions-row">
        {mail.password_set && <button type="button" className="danger" onClick={onDropPassword}>Снять пароль</button>}
        <button type="submit" className="primary" disabled={busy}>Сохранить</button>
      </div>
    </form>
  );
}

function TestDialog({ to: initial, onClose }: { to: string; onClose: (done: boolean) => void }) {
  const [to, setTo] = useState(initial);
  return (
    <FormModal title="Тестовое письмо" submit="Отправить" onSubmit={() => api.post('/api/mail/test', { to: to.trim(), locale: 'ru' })} onClose={onClose}>
      <Field label="Кому"><input type="email" required autoFocus value={to} onChange={e => setTo(e.target.value)} /></Field>
    </FormModal>
  );
}
