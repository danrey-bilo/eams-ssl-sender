import { useState, type FormEvent } from 'react';
import { common } from '../i18n/common';
import { settings as settingsDict } from '../i18n/settings';
import { api, Cancelled, errorText } from '../lib/api';
import { fmtDateTime } from '../lib/format';
import { useLoad } from '../lib/hooks';
import { lang, translator } from '../lib/i18n';
import { loadSettings, saveSettings, text, type Settings } from '../lib/settings';
import type { KeyManagerStatus, MailStatus } from '../lib/types';
import { Check, ErrorText, Failed, Field, PageHead, Spinner, Status, Tabs, type DotKind } from '../ui/bits';
import { Icon } from '../ui/Icon';
import { FormModal } from '../ui/Modal';
import { useOverlay } from '../ui/Overlay';

const t = translator({ ...common, ...settingsDict });

const sections = (): Array<[string, string]> => [['#/settings/mail', t('Почта')], ['#/settings/keymanager', 'Key Manager']];

export function SettingsPage({ section, myEmail }: { section?: string; myEmail: string }) {
  const current = section === 'keymanager' ? 'keymanager' : 'mail';
  return (
    <>
      <PageHead title={t('Настройки')} sub={t('Откуда берутся сертификаты и как уходят письма')} />
      <Tabs items={sections()} value={'#/settings/' + current} />
      {current === 'mail' ? <MailSettings myEmail={myEmail} /> : <KeyManagerSettings />}
    </>
  );
}

// Почта

const securityChoices = (): Array<[string, string]> => [['tls', 'SSL/TLS'], ['starttls', 'STARTTLS'], ['none', t('Без шифрования')]];

/** Сервер SMTP — имя: «https://mail.example.com/» сводится к «mail.example.com». */
const hostName = (value: string) => value.trim().replace(/^[a-z][a-z0-9+.-]*:\/\//i, '').replace(/\/+$/, '');

function MailSettings({ myEmail }: { myEmail: string }) {
  const { data, error, reload } = useLoad(() => Promise.all([api.get<MailStatus>('/api/mail'), loadSettings()]));
  const { show, toast, run, confirm } = useOverlay();

  if (error) return <Failed text={error} />;
  if (!data) return <Spinner />;

  const [mail, settings] = data;
  const state: [DotKind, string] = mail.configured ? (mail.failed ? ['err', t('Есть неотправленные')] : ['ok', t('Работает')]) : ['', t('Не настроена')];

  const sendTest = async () => {
    if (await show(close => <TestDialog to={myEmail} onClose={close} />)) toast(t('Сервер принял письмо'));
  };

  const dropPassword = async () => {
    if (!await confirm(t('Снять пароль SMTP?'), t('Почта будет уходить без авторизации.'), t('Снять'), true)) return;
    if (await run(() => api.put('/api/mail/password', { password: '' }), t('Пароль снят'))) reload();
  };

  return (
    <div className="stack">
      <section className="card status-card">
        <div className="status-line">
          <Status kind={state[0]} strong>{state[1]}</Status>
          <button onClick={sendTest} disabled={!mail.configured}><Icon name="mail" />{t('Тестовое письмо')}</button>
        </div>
        <div className="facts">
          <span>{t('В очереди')} <b>{mail.pending}</b></span>
          <span>{t('Не ушло')} <b>{mail.failed}</b></span>
        </div>
        {mail.last_error && <p className="note">{mail.last_error}</p>}
      </section>
      <section className="card">
        <SmtpForm mail={mail} settings={settings} onSaved={reload} onDropPassword={dropPassword} />
      </section>
    </div>
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
    // Пароль привязан к серверу, порту, защите и логину.
    const moved = hostName(host).toLowerCase() !== hostName(text(settings, 'mail.smtp_host')).toLowerCase()
      || port.trim() !== text(settings, 'mail.smtp_port').trim()
      || security !== (text(settings, 'mail.smtp_security') || 'starttls')
      || user.trim() !== text(settings, 'mail.smtp_username').trim();
    if (mail.password_set && moved && user.trim() && !password) {
      setError(t('Сервер изменился — введите пароль заново.'));
      return;
    }
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
      toast(t('Сохранено'));
      onSaved();
    } catch (err) {
      if (!(err instanceof Cancelled)) setError(errorText(err));
    } finally {
      setBusy(false);
    }
  };

  return (
    <form onSubmit={submit}>
      <div className="form-head">
        <h2>SMTP</h2>
        <Check label={t('Отправлять письма')} checked={enabled} onChange={setEnabled} />
      </div>
      <div className="row three">
        <Field label={t('Сервер')}><input type="text" placeholder="smtp.example.com" value={host} onChange={e => setHost(e.target.value)} /></Field>
        <Field label={t('Порт')}><input type="number" min={1} max={65535} placeholder="465" value={port} onChange={e => setPort(e.target.value)} /></Field>
        <Field label={t('Защита')}>
          <select value={security} onChange={e => setSecurity(e.target.value)}>
            {securityChoices().map(([v, title]) => <option key={v} value={v}>{title}</option>)}
          </select>
        </Field>
      </div>
      <div className="row">
        <Field label={t('Логин')}><input type="text" autoComplete="off" value={user} onChange={e => setUser(e.target.value)} /></Field>
        <Field label={t('Пароль')}>
          <input type="password" autoComplete="new-password" placeholder={mail.password_set ? t('•••••••• задан') : ''}
            value={password} onChange={e => setPassword(e.target.value)} />
        </Field>
      </div>
      <div className="row">
        <Field label={t('Адрес отправителя')}><input type="email" placeholder="noreply@example.com" value={from} onChange={e => setFrom(e.target.value)} /></Field>
        <Field label={t('Имя отправителя')}><input type="text" value={fromName} onChange={e => setFromName(e.target.value)} /></Field>
      </div>
      <Field label={t('Адрес панели')} hint={t('Для ссылок в приглашениях и сбросе пароля')}>
        <input type="url" placeholder="https://certs.example.com" value={publicUrl} onChange={e => setPublicUrl(e.target.value)} />
      </Field>
      <ErrorText text={error} />
      <div className="actions-row">
        {mail.password_set && <button type="button" className="danger" onClick={onDropPassword}>{t('Снять пароль')}</button>}
        <button type="submit" className="primary" disabled={busy}>{t('Сохранить')}</button>
      </div>
    </form>
  );
}

function TestDialog({ to: initial, onClose }: { to: string; onClose: (done: boolean) => void }) {
  const [to, setTo] = useState(initial);
  return (
    <FormModal title={t('Тестовое письмо')} submit={t('Отправить')} onSubmit={() => api.post('/api/mail/test', { to: to.trim(), locale: lang() })} onClose={onClose}>
      <Field label={t('Кому')}><input type="email" required autoFocus value={to} onChange={e => setTo(e.target.value)} /></Field>
    </FormModal>
  );
}

// Key Manager

function KeyManagerSettings() {
  const { data, error, reload } = useLoad(() => Promise.all([api.get<KeyManagerStatus>('/api/keymanager'), loadSettings()]));
  const { toast, run, confirm } = useOverlay();

  if (error) return <Failed text={error} />;
  if (!data) return <Spinner />;

  const [km, settings] = data;
  const state: [DotKind, string] = !km.configured ? (km.enabled ? ['warn', t('Не настроен')] : ['', t('Выключен')])
    : km.last_poll_error ? ['err', t('Ошибка')] : km.last_poll_at ? ['ok', t('Работает')] : ['warn', t('Ждёт первого опроса')];

  const test = async () => {
    try {
      const r = await api.post<{ certificates: number }>('/api/keymanager/test');
      toast(t('Связь есть · сертификатов: {n}', { n: r.certificates }));
    } catch (err) {
      if (!(err instanceof Cancelled)) toast(errorText(err), 'err');
    }
  };

  const poll = async () => {
    if (await run(() => api.post('/api/keymanager/poll'), t('Опрос запущен'))) setTimeout(reload, 3000);
  };

  const dropKey = async () => {
    if (!await confirm(t('Снять ключ API?'), t('Опрос и рассылка остановятся, пока ключ не задан снова.'), t('Снять'), true)) return;
    if (await run(() => api.put('/api/keymanager/api-key', { api_key: '' }), t('Ключ снят'))) reload();
  };

  return (
    <div className="stack">
      <section className="card status-card">
        <div className="status-line">
          <Status kind={state[0]} strong>{state[1]}</Status>
          <div className="inline">
            <button onClick={test}>{t('Проверить')}</button>
            <button onClick={poll} disabled={!km.configured}><Icon name="refresh" />{t('Опросить')}</button>
          </div>
        </div>
        <div className="facts">
          <span>{t('Опрос')} <b>{fmtDateTime(km.last_poll_at)}</b></span>
          <span>{t('Сертификатов')} <b>{km.last_poll_count ?? '—'}</b></span>
        </div>
        {km.last_poll_error && <p className="note">{km.last_poll_error}</p>}
      </section>
      <section className="card">
        <ConnectionForm km={km} settings={settings} onSaved={reload} onDropKey={dropKey} />
      </section>
    </div>
  );
}

function ConnectionForm({ km, settings, onSaved, onDropKey }: {
  km: KeyManagerStatus;
  settings: Settings;
  onSaved: () => void;
  onDropKey: () => void;
}) {
  const { toast } = useOverlay();
  const [enabled, setEnabled] = useState(settings['keymanager.enabled']?.value === true);
  const [url, setUrl] = useState(text(settings, 'keymanager.base_url'));
  const [apiKey, setApiKey] = useState('');
  const [minutes, setMinutes] = useState(text(settings, 'keymanager.poll_minutes'));
  const [error, setError] = useState('');
  const [busy, setBusy] = useState(false);

  const submit = async (event: FormEvent) => {
    event.preventDefault();
    setError('');
    // Ключ привязан к адресу.
    const moved = url.trim() !== text(settings, 'keymanager.base_url').trim();
    if (km.api_key_set && moved && !apiKey.trim()) {
      setError(t('Адрес изменился — введите ключ API заново.'));
      return;
    }
    setBusy(true);
    try {
      await saveSettings(settings, {
        'keymanager.enabled': enabled,
        'keymanager.base_url': url.trim(),
        'keymanager.poll_minutes': minutes ? Number(minutes) : '',
      });
      if (apiKey.trim()) await api.put('/api/keymanager/api-key', { api_key: apiKey.trim() });
      setApiKey('');
      toast(t('Сохранено'));
      // Проверить сразу, не дожидаясь опроса.
      if (enabled) {
        await api.post('/api/keymanager/poll').catch(() => null);
        setTimeout(onSaved, 3000);
      }
      onSaved();
    } catch (err) {
      if (!(err instanceof Cancelled)) setError(errorText(err));
    } finally {
      setBusy(false);
    }
  };

  return (
    <form onSubmit={submit}>
      <div className="form-head">
        <h2>{t('Подключение')}</h2>
        <Check label={t('Опрашивать')} checked={enabled} onChange={setEnabled} />
      </div>
      <div className="row three">
        <Field label={t('Адрес')} hint={t('Имя сервера из его сертификата, а не IP')}>
          <input type="url" placeholder="https://kmp.example.com:6565" value={url} onChange={e => setUrl(e.target.value)} />
        </Field>
        <Field label={t('Опрос, мин')}>
          <input type="number" min={1} max={1440} value={minutes} onChange={e => setMinutes(e.target.value)} />
        </Field>
      </div>
      <Field label={t('Ключ API')} hint="Key Manager Plus → Settings → General Settings → API Key">
        <input type="password" autoComplete="off" placeholder={km.api_key_set ? t('•••••••• задан') : ''}
          value={apiKey} onChange={e => setApiKey(e.target.value)} />
      </Field>
      <ErrorText text={error} />
      <div className="actions-row">
        {km.api_key_set && <button type="button" className="danger" onClick={onDropKey}>{t('Снять ключ')}</button>}
        <button type="submit" className="primary" disabled={busy}>{t('Сохранить')}</button>
      </div>
    </form>
  );
}
