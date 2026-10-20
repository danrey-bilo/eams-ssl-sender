import { useState, type FormEvent } from 'react';
import { api, Cancelled, errorText } from '../lib/api';
import { fmtDate, fmtDateTime } from '../lib/format';
import { useLoad } from '../lib/hooks';
import { loadSettings, saveSettings, text, type Settings } from '../lib/settings';
import type { KeyManagerStatus, KnownCertificate } from '../lib/types';
import { Check, ErrorText, Field, PageHead, Spinner, Status, type DotKind } from '../ui/bits';
import { Icon } from '../ui/Icon';
import { useOverlay } from '../ui/Overlay';

export function KeyManagerPage() {
  const { data, error, reload } = useLoad(() => Promise.all([
    api.get<KeyManagerStatus>('/api/keymanager'),
    loadSettings(),
    api.get<KnownCertificate[]>('/api/keymanager/certificates'),
  ]));
  const { toast, run, confirm } = useOverlay();

  if (error) return <div className="card"><p className="error">{error}</p></div>;
  if (!data) return <Spinner />;

  const [km, settings, known] = data;

  const state: [DotKind, string] = !km.configured ? (km.enabled ? ['warn', 'Не настроен'] : ['', 'Выключен'])
    : km.last_poll_error ? ['err', 'Ошибка'] : km.last_poll_at ? ['ok', 'Работает'] : ['warn', 'Ждёт первого опроса'];

  const test = async () => {
    try {
      const r = await api.post<{ certificates: number }>('/api/keymanager/test');
      toast('Связь есть · сертификатов: ' + r.certificates);
    } catch (err) {
      if (!(err instanceof Cancelled)) toast(errorText(err), 'err');
    }
  };

  const poll = async () => {
    if (await run(() => api.post('/api/keymanager/poll'), 'Опрос запущен')) setTimeout(reload, 3000);
  };

  const dropKey = async () => {
    if (!await confirm('Снять ключ API?', 'Опрос и рассылка остановятся, пока ключ не задан снова.', 'Снять', true)) return;
    if (await run(() => api.put('/api/keymanager/api-key', { api_key: '' }), 'Ключ снят')) reload();
  };

  return (
    <>
      <PageHead title="Key Manager Plus" />
      <div className="stack">
        <div className="card stack">
          <div className="head flat">
            <Status kind={state[0]} strong>{state[1]}</Status>
            <div className="inline">
              <button onClick={test}>Проверить</button>
              <button onClick={poll} disabled={!km.configured}><Icon name="refresh" />Опросить</button>
            </div>
          </div>
          <div className="facts">
            <span>Опрос: <b>{fmtDateTime(km.last_poll_at)}</b></span>
            <span>Сертификатов: <b>{km.last_poll_count ?? '—'}</b></span>
          </div>
          {km.last_poll_error && <p className="note">{km.last_poll_error}</p>}
        </div>

        <div className="card">
          <ConnectionForm km={km} settings={settings} onSaved={reload} onDropKey={dropKey} />
        </div>

        <Certificates known={known} />
      </div>
    </>
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
  const [pin, setPin] = useState(text(settings, 'keymanager.server_sha256'));
  const [minutes, setMinutes] = useState(text(settings, 'keymanager.poll_minutes'));
  const [error, setError] = useState('');
  const [busy, setBusy] = useState(false);

  const submit = async (event: FormEvent) => {
    event.preventDefault();
    setError('');
    // Ключ привязан к адресу и отпечатку: сервер не отдаст его на новый адрес без повторного ввода.
    const moved = url.trim() !== text(settings, 'keymanager.base_url').trim()
      || pin.trim() !== text(settings, 'keymanager.server_sha256').trim();
    if (km.api_key_set && moved && !apiKey.trim()) {
      setError('Адрес изменился — введите ключ API заново.');
      return;
    }
    setBusy(true);
    try {
      await saveSettings(settings, {
        'keymanager.enabled': enabled,
        'keymanager.base_url': url.trim(),
        'keymanager.server_sha256': pin.trim(),
        'keymanager.poll_minutes': minutes ? Number(minutes) : '',
      });
      if (apiKey.trim()) await api.put('/api/keymanager/api-key', { api_key: apiKey.trim() });
      setApiKey('');
      toast('Сохранено');
      // Новые настройки проверяются сразу, а не через срок опроса.
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
      <h2>Подключение</h2>
      <Check label="Опрашивать Key Manager" checked={enabled} onChange={setEnabled} />
      <Field label="Адрес">
        <input type="url" placeholder="https://kmp.example.local:6565" value={url} onChange={e => setUrl(e.target.value)} />
      </Field>
      <Field label="Ключ API">
        <input type="password" autoComplete="off" placeholder={km.api_key_set ? '•••••••• задан' : 'Settings → API Key'}
          value={apiKey} onChange={e => setApiKey(e.target.value)} />
      </Field>
      <div className="row">
        <Field label="Отпечаток SHA-256">
          <input type="text" className="mono" placeholder="только для самоподписанного" value={pin} onChange={e => setPin(e.target.value)} />
        </Field>
        <Field label="Опрос, мин">
          <input type="number" min={1} max={1440} value={minutes} onChange={e => setMinutes(e.target.value)} />
        </Field>
      </div>
      <ErrorText text={error} />
      <div className="actions-row">
        {km.api_key_set && <button type="button" className="danger" onClick={onDropKey}>Снять ключ</button>}
        <button type="submit" className="primary" disabled={busy}>Сохранить</button>
      </div>
    </form>
  );
}

function Certificates({ known }: { known: KnownCertificate[] }) {
  const [query, setQuery] = useState('');
  const q = query.trim().toLowerCase();
  const rows = known.filter(c => !q || c.common_name.toLowerCase().includes(q) || c.serial_number.includes(q));

  return (
    <div className="card flush">
      <div className="card-head">
        <h2>Сертификаты</h2>
        <input type="text" className="search" placeholder="Поиск" value={query} onChange={e => setQuery(e.target.value)} />
      </div>
      <table>
        <thead>
          <tr>
            <th>Common Name</th>
            <th className="hide-sm">Серийный</th>
            <th>Действует до</th>
            <th className="hide-sm">Замечен</th>
          </tr>
        </thead>
        <tbody>
          {rows.length === 0 && <tr><td colSpan={4} className="muted">Пусто</td></tr>}
          {rows.map(c => (
            <tr key={c.common_name + c.serial_number}>
              <td>{c.common_name}</td>
              <td className="mono muted hide-sm">{c.serial_number}</td>
              <td>{fmtDate(c.expires_on)}</td>
              <td className="muted hide-sm">{fmtDateTime(c.first_seen_at)}</td>
            </tr>
          ))}
        </tbody>
      </table>
    </div>
  );
}
