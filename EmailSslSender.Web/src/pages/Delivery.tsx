import { useMemo, useState } from 'react';
import { api } from '../lib/api';
import { isMobile } from '../lib/device';
import { fmtDate, fmtDateTime } from '../lib/format';
import { useLoad } from '../lib/hooks';
import type { KeyManagerStatus, KnownCertificate, MailStatus, Subscription } from '../lib/types';
import { Check, Dot, Empty, Failed, Field, Group, PageHead, Spinner, Status, type DotKind } from '../ui/bits';
import { Icon } from '../ui/Icon';
import { Menu } from '../ui/Menu';
import { FormModal, Invalid } from '../ui/Modal';
import { useOverlay } from '../ui/Overlay';

const FORMATS = ['PEM', 'KEY', 'CRT', 'CER', 'DER', 'P7B', 'PKCS', 'JKS'];
const LOCALES: Array<[string, string]> = [['ru', 'Русский'], ['uk', 'Українська'], ['en', 'English']];

function scheduleText(s: Subscription): string {
  const parts: string[] = [];
  if (s.on_change) parts.push('при обновлении');
  if (s.interval_days) parts.push('раз в ' + s.interval_days + ' дн.');
  return parts.length ? parts.join(' · ') : 'вручную';
}

export function DeliveryPage() {
  const { data, error, reload } = useLoad(() => Promise.all([
    api.get<Subscription[]>('/api/certificate-subscriptions'),
    api.get<KeyManagerStatus>('/api/keymanager'),
    api.get<MailStatus>('/api/mail'),
    api.get<KnownCertificate[]>('/api/keymanager/certificates'),
  ]));
  const { show, confirm, toast, run } = useOverlay();

  const expiry = useMemo(() => {
    const map = new Map<string, string | null>();
    for (const c of data?.[3] ?? []) {
      const key = c.common_name.toLowerCase();
      if (!map.has(key)) map.set(key, c.expires_on);
    }
    return map;
  }, [data]);

  if (error) return <Failed text={error} />;
  if (!data) return <Spinner />;

  const [subscriptions, km, mail, known] = data;

  const kmState: [DotKind, string] = !km.configured ? ['', km.enabled ? 'Key Manager не настроен' : 'Key Manager выключен']
    : km.last_poll_error ? ['err', 'Key Manager: ошибка'] : ['ok', 'Key Manager'];
  const mailState: [DotKind, string] = !mail.configured ? ['', 'Почта не настроена']
    : mail.failed && mail.last_error ? ['err', 'Почта: ошибка'] : ['ok', 'Почта'];

  const edit = async (s: Subscription | null) => {
    const saved = await show(close => <SubscriptionDialog subscription={s} known={known} onClose={close} />);
    if (saved) { toast(s ? 'Сохранено' : 'Подписка добавлена'); reload(); }
  };

  const send = async (s: Subscription) => {
    if (!await confirm('Отправить ' + s.common_name + '?', 'Файлы уйдут сейчас: ' + s.recipients.join(', '), 'Отправить')) return;
    await run(() => api.post('/api/certificate-subscriptions/' + s.id + '/send'), 'Отправлено в очередь');
    reload();
  };

  const remove = async (s: Subscription) => {
    if (!await confirm('Удалить подписку?', s.common_name + ' больше не будет рассылаться.', 'Удалить', true)) return;
    if (await run(() => api.del('/api/certificate-subscriptions/' + s.id), 'Подписка удалена')) reload();
  };

  const add = <button className="primary" onClick={() => edit(null)}><Icon name="plus" />Добавить</button>;

  return (
    <>
      <PageHead title="Рассылка" sub="Кому и когда уходят файлы сертификатов">{subscriptions.length > 0 && add}</PageHead>
      <div className="pills">
        <a className="pill" href="#/settings/keymanager"><Dot kind={kmState[0]} />{kmState[1]}</a>
        <a className="pill" href="#/settings/mail"><Dot kind={mailState[0]} />{mailState[1]}</a>
      </div>

      {subscriptions.length === 0 ? (
        <div className="card">
          <Empty title="Подписок пока нет" hint="Подписка — какой сертификат, кому и когда отправлять.">{add}</Empty>
        </div>
      ) : isMobile ? (
        <div className="card flush">
          <div className="list">
            {subscriptions.map(s => {
              const expires = expiry.get(s.common_name.toLowerCase());
              return (
                <div key={s.id} className={'item' + (s.enabled ? '' : ' off')}>
                  <div className="item-main">
                    <div className="item-title">{s.common_name}</div>
                    <div className="item-meta">
                      {s.recipients[0]}{s.recipients.length > 1 && ' +' + (s.recipients.length - 1)}
                    </div>
                    <div className="item-meta">{scheduleText(s)}{expires && ' · до ' + fmtDate(expires)}</div>
                    <div className="item-status"><LastDelivery s={s} /></div>
                  </div>
                  <Menu items={[
                    { text: 'Отправить сейчас', action: () => send(s) },
                    { text: 'Изменить', action: () => edit(s) },
                    { text: 'Удалить', action: () => remove(s), danger: true },
                  ]} />
                </div>
              );
            })}
          </div>
        </div>
      ) : (
        <div className="card flush">
          <table>
            <thead>
              <tr>
                <th>Сертификат</th>
                <th>Кому</th>
                <th>Файлы</th>
                <th>Когда</th>
                <th>Последняя отправка</th>
                <th />
              </tr>
            </thead>
            <tbody>
              {subscriptions.map(s => {
                const expires = expiry.get(s.common_name.toLowerCase());
                return (
                  <tr key={s.id} className={s.enabled ? '' : 'off'}>
                    <td>
                      <span className="strong">{s.common_name}</span>
                      {expires && <span className="sub">до {fmtDate(expires)}</span>}
                    </td>
                    <td title={s.recipients.join('\n')}>
                      {s.recipients[0]}
                      {s.recipients.length > 1 && <span className="muted"> +{s.recipients.length - 1}</span>}
                    </td>
                    <td>
                      <div className="chips">{s.formats.map(f => <span key={f} className="chip mono">{f}</span>)}</div>
                    </td>
                    <td className="muted">{scheduleText(s)}</td>
                    <td><LastDelivery s={s} /></td>
                    <td className="actions">
                      <button className="icon" title="Отправить сейчас" onClick={() => send(s)}><Icon name="send" /></button>
                      <button className="icon" title="Изменить" onClick={() => edit(s)}><Icon name="edit" /></button>
                      <button className="icon danger" title="Удалить" onClick={() => remove(s)}><Icon name="trash" /></button>
                    </td>
                  </tr>
                );
              })}
            </tbody>
          </table>
        </div>
      )}
    </>
  );
}

function LastDelivery({ s }: { s: Subscription }) {
  const { show } = useOverlay();

  // Выключенная не шлёт ничего — зелёная точка у неё обманывала бы.
  if (!s.enabled) {
    const last = s.last_sent_at ? 'Последняя отправка ' + fmtDateTime(s.last_sent_at) : undefined;
    return <span title={last}><Status kind="">Выключена</Status></span>;
  }

  switch (s.delivery) {
    case 'failed':
      return (
        <button
          className="link status"
          title={s.last_error ?? ''}
          onClick={() => show(close => <FormModal title={s.common_name} note={s.last_error} submit="Понятно" cancel={null} onClose={close} />)}
        >
          <Dot kind="err" />Ошибка
        </button>
      );
    case 'queued':
      return <span title="Письма ждут отправки"><Status kind="warn">В очереди</Status></span>;
    case 'sent':
      return <span title={'Серийный ' + (s.last_sent_serial ?? '')}><Status kind="ok">{fmtDateTime(s.last_sent_at)}</Status></span>;
    default:
      return <span className="muted">ещё не отправлялся</span>;
  }
}

function SubscriptionDialog({ subscription: s, known, onClose }: {
  subscription: Subscription | null;
  known: KnownCertificate[];
  onClose: (done: boolean) => void;
}) {
  const [name, setName] = useState(s?.common_name ?? '');
  const [recipients, setRecipients] = useState((s?.recipients ?? []).join('\n'));
  const [formats, setFormats] = useState<string[]>(s?.formats ?? ['PEM', 'KEY']);
  const [onChange, setOnChange] = useState(s ? s.on_change : true);
  const [days, setDays] = useState(s?.interval_days ? String(s.interval_days) : '');
  const [passphrase, setPassphrase] = useState(s?.include_passphrase ?? false);
  const [enabled, setEnabled] = useState(s ? s.enabled : true);
  const [locale, setLocale] = useState(s?.locale ?? 'ru');
  const names = useMemo(() => [...new Set(known.map(c => c.common_name))].sort(), [known]);

  const toggle = (f: string) => setFormats(list => (list.includes(f) ? list.filter(x => x !== f) : [...list, f]));

  const submit = async () => {
    if (formats.length === 0) throw new Invalid('Выберите хотя бы один файл.');
    const body = {
      common_name: name.trim(),
      recipients: recipients.split(/[\s,;]+/).map(v => v.trim()).filter(Boolean),
      formats: FORMATS.filter(f => formats.includes(f)),
      on_change: onChange,
      interval_days: days ? Number(days) : 0,
      include_passphrase: passphrase,
      locale,
      enabled,
    };
    if (s) await api.patch('/api/certificate-subscriptions/' + s.id, body);
    else await api.post('/api/certificate-subscriptions', body);
  };

  return (
    <FormModal title={s ? 'Подписка' : 'Новая подписка'} submit={s ? 'Сохранить' : 'Добавить'} onSubmit={submit} onClose={onClose}>
      <datalist id="known-names">{names.map(n => <option key={n} value={n} />)}</datalist>
      <Field label="Сертификат (Common Name)">
        <input type="text" list="known-names" required placeholder="*.example.com" autoFocus value={name} onChange={e => setName(e.target.value)} />
      </Field>
      <Field label="Кому — по адресу в строке">
        <textarea rows={3} placeholder={'admin@example.com\nops@example.com'} value={recipients} onChange={e => setRecipients(e.target.value)} />
      </Field>
      <Group label="Файлы">
        <div className="toggle-group">
          {FORMATS.map(f => (
            <label key={f}>
              <input type="checkbox" checked={formats.includes(f)} onChange={() => toggle(f)} />
              <span>{f}</span>
            </label>
          ))}
        </div>
      </Group>
      <Group label="Когда отправлять">
        <div className="stack tight">
          <Check label="При обновлении в Key Manager" checked={onChange} onChange={setOnChange} />
          <div className="inline">
            Повторять раз в
            <input type="number" min={1} max={365} placeholder="—" aria-label="Дней" value={days} onChange={e => setDays(e.target.value)} />
            дн.
          </div>
        </div>
      </Group>
      <div className="row">
        <Field label="Язык письма">
          <select value={locale} onChange={e => setLocale(e.target.value)}>
            {LOCALES.map(([v, t]) => <option key={v} value={v}>{t}</option>)}
          </select>
        </Field>
        <div className="stack tight end">
          <Check label="Пароль ключа в письме" checked={passphrase} onChange={setPassphrase} />
          <Check label="Включена" checked={enabled} onChange={setEnabled} />
        </div>
      </div>
    </FormModal>
  );
}
