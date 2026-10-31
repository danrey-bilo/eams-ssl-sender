import { useMemo, useState } from 'react';
import { common } from '../i18n/common';
import { delivery } from '../i18n/delivery';
import { api } from '../lib/api';
import { isMobile } from '../lib/device';
import { fmtDate, fmtDateTime } from '../lib/format';
import { useLoad } from '../lib/hooks';
import { lang, LANGS, translator } from '../lib/i18n';
import { serverMessage } from '../lib/serverText';
import type { KeyManagerStatus, KnownCertificate, MailStatus, Subscription } from '../lib/types';
import { Check, Dot, Empty, Failed, Field, Group, PageHead, Spinner, Status, type DotKind } from '../ui/bits';
import { Icon } from '../ui/Icon';
import { Menu } from '../ui/Menu';
import { FormModal, Invalid } from '../ui/Modal';
import { useOverlay } from '../ui/Overlay';

const t = translator({ ...common, ...delivery });

const FORMATS = ['PEM', 'KEY', 'CRT', 'CER', 'DER', 'P7B', 'PKCS', 'JKS'];
/** Как у сервера: `CertificateSubscription.MaxDescription`. */
const DESCRIPTION_MAX = 500;

/** Для заголовков окон: первый и «+N». */
function namesText(s: Subscription): string {
  return s.common_names[0] + (s.common_names.length > 1 ? ' +' + (s.common_names.length - 1) : '');
}

/** Все сертификаты со сроком; имя не из снимка помечено — так видна опечатка. */
function Names({ s, expiry }: { s: Subscription; expiry: Map<string, string | null> }) {
  return (
    <ul className="names">
      {s.common_names.map(n => {
        const until = expiry.get(n.toLowerCase());
        return (
          <li key={n}>
            <span className="strong">{n}</span>
            {until ? <span className="until">{t('до {date}', { date: fmtDate(until) })}</span>
              : expiry.size > 0 && !expiry.has(n.toLowerCase()) && <span className="until">{t('нет в Key Manager')}</span>}
          </li>
        );
      })}
    </ul>
  );
}

/** Разобрать ввод имён: через пробел, запятую, точку с запятой или с новой строки. */
function splitNames(text: string): string[] {
  return text.split(/[\s,;]+/).map(v => v.trim()).filter(Boolean);
}

/** Добавить имена без повторов (без учёта регистра — это имена DNS). */
function withNames(list: string[], more: string[]): string[] {
  const result = [...list];
  for (const name of more) {
    if (!result.some(x => x.toLowerCase() === name.toLowerCase())) result.push(name);
  }
  return result;
}

function scheduleText(s: Subscription): string {
  const parts: string[] = [];
  if (s.on_change) parts.push(t('при обновлении'));
  if (s.interval_days) parts.push(t('раз в {n} дн.', { n: s.interval_days }));
  if (!parts.length) parts.push(t('вручную'));
  if (s.send_time) parts.push(t('в {time}', { time: s.send_time }));
  return parts.join(' · ');
}

/** Период «с — по», если он есть, и где подписка относительно него. */
function Period({ s }: { s: Subscription }) {
  if (!s.active_from && !s.active_until) return null;
  const range = s.active_from && s.active_until
    ? t('с {from} по {until}', { from: fmtDate(s.active_from), until: fmtDate(s.active_until) })
    : s.active_from ? t('с {from}', { from: fmtDate(s.active_from) }) : t('по {until}', { until: fmtDate(s.active_until) });
  const state = s.period === 'before' ? t('ещё не началась') : s.period === 'after' ? t('окончена') : null;
  return <span className="period">{range}{state && <span className="state"> · {state}</span>}</span>;
}

/** Пояс браузера — для дней и времени отправки. */
function browserZone(): string {
  try {
    return Intl.DateTimeFormat().resolvedOptions().timeZone || 'UTC';
  } catch {
    return 'UTC';
  }
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

  const kmState: [DotKind, string] = !km.configured ? ['', km.enabled ? t('Key Manager не настроен') : t('Key Manager выключен')]
    : km.last_poll_error ? ['err', t('Key Manager: ошибка')] : ['ok', 'Key Manager'];
  const mailState: [DotKind, string] = !mail.configured ? ['', t('Почта не настроена')]
    : mail.failed && mail.last_error ? ['err', t('Почта: ошибка')] : ['ok', t('Почта')];

  const edit = async (s: Subscription | null) => {
    const saved = await show(close => <SubscriptionDialog subscription={s} known={known} onClose={close} />);
    if (saved) { toast(s ? t('Сохранено') : t('Подписка добавлена')); reload(); }
  };

  const send = async (s: Subscription) => {
    const question = s.common_names.length > 1
      ? t('Отправить сертификаты ({n})?', { n: s.common_names.length })
      : t('Отправить {name}?', { name: s.common_names[0] ?? '' });
    if (!await confirm(question, t('Файлы уйдут сейчас: {recipients}', { recipients: s.recipients.join(', ') }), t('Отправить'))) return;
    await run(() => api.post('/api/certificate-subscriptions/' + s.id + '/send'), t('Отправлено в очередь'));
    reload();
  };

  const toggle = async (s: Subscription) => {
    const done = s.enabled ? t('Подписка отключена') : t('Подписка включена');
    if (await run(() => api.patch('/api/certificate-subscriptions/' + s.id, { enabled: !s.enabled }), done)) reload();
  };

  const remove = async (s: Subscription) => {
    if (!await confirm(t('Удалить подписку?'), t('{names} больше не будет рассылаться.', { names: s.common_names.join(', ') }), t('Удалить'), true)) return;
    if (await run(() => api.del('/api/certificate-subscriptions/' + s.id), t('Подписка удалена'))) reload();
  };

  const add = <button className="primary" onClick={() => edit(null)}><Icon name="plus" />{t('Добавить')}</button>;

  return (
    <>
      <PageHead title={t('Рассылка')} sub={t('Кому и когда уходят файлы сертификатов')}>{subscriptions.length > 0 && add}</PageHead>
      <div className="pills">
        <a className="pill" href="#/settings/keymanager"><Dot kind={kmState[0]} />{kmState[1]}</a>
        <a className="pill" href="#/settings/mail"><Dot kind={mailState[0]} />{mailState[1]}</a>
      </div>

      {subscriptions.length === 0 ? (
        <div className="card">
          <Empty title={t('Подписок пока нет')} hint={t('Подписка — какие сертификаты, кому и когда отправлять.')}>{add}</Empty>
        </div>
      ) : isMobile ? (
        <div className="card flush">
          <div className="list">
            {subscriptions.map(s => (
              <div key={s.id} className={'item' + (s.enabled && s.period !== 'after' ? '' : ' off')}>
                <div className="item-main">
                  {s.description && <span className="desc">{s.description}</span>}
                  <div className="item-title"><Names s={s} expiry={expiry} /></div>
                  <div className="item-meta">{s.recipients.join(', ')}</div>
                  <div className="item-meta">{scheduleText(s)}</div>
                  {(s.active_from || s.active_until) && <div className="item-meta"><Period s={s} /></div>}
                  <div className="item-status"><LastDelivery s={s} /></div>
                </div>
                <Menu title={namesText(s)} items={[
                  { text: t('Отправить сейчас'), action: () => send(s) },
                  { text: t('Изменить'), action: () => edit(s) },
                  { text: s.enabled ? t('Отключить') : t('Включить'), action: () => toggle(s) },
                  { text: t('Удалить'), action: () => remove(s), danger: true },
                ]} />
              </div>
            ))}
          </div>
        </div>
      ) : (
        <div className="card flush">
          <table>
            <thead>
              <tr>
                <th>{t('Сертификат')}</th>
                <th>{t('Кому')}</th>
                <th>{t('Файлы')}</th>
                <th>{t('Когда')}</th>
                <th>{t('Последняя отправка')}</th>
                <th />
              </tr>
            </thead>
            <tbody>
              {subscriptions.map(s => (
                <tr key={s.id} className={s.enabled && s.period !== 'after' ? '' : 'off'}>
                  <td>
                    {s.description && <span className="desc">{s.description}</span>}
                    <Names s={s} expiry={expiry} />
                  </td>
                  <td>
                    <ul className="lines">{s.recipients.map(r => <li key={r}>{r}</li>)}</ul>
                  </td>
                  <td>
                    <div className="chips">{s.formats.map(f => <span key={f} className="chip mono">{f}</span>)}</div>
                  </td>
                  <td className="muted">{scheduleText(s)}<Period s={s} /></td>
                  <td><LastDelivery s={s} /></td>
                  <td className="actions">
                    <button className="icon" title={t('Отправить сейчас')} onClick={() => send(s)}><Icon name="send" /></button>
                    <button className="icon" title={t('Изменить')} onClick={() => edit(s)}><Icon name="edit" /></button>
                    <button className="icon" title={s.enabled ? t('Отключить') : t('Включить')} onClick={() => toggle(s)}>
                      <Icon name={s.enabled ? 'pause' : 'play'} />
                    </button>
                    <button className="icon danger" title={t('Удалить')} onClick={() => remove(s)}><Icon name="trash" /></button>
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}
    </>
  );
}

function LastDelivery({ s }: { s: Subscription }) {
  const { show } = useOverlay();

  if (!s.enabled) {
    const last = s.last_sent_at ? t('Последняя отправка {when}', { when: fmtDateTime(s.last_sent_at) }) : undefined;
    return <span title={last}><Status kind="">{t('Выключена')}</Status></span>;
  }

  if (s.period === 'after') {
    const last = s.last_sent_at ? t('Последняя отправка {when}', { when: fmtDateTime(s.last_sent_at) }) : undefined;
    return <span title={last}><Status kind="">{t('Период окончен')}</Status></span>;
  }

  if (s.period === 'before' && s.delivery === 'none' && s.active_from) {
    return <span className="muted">{t('начнётся {date}', { date: fmtDate(s.active_from) })}</span>;
  }

  switch (s.delivery) {
    case 'failed':
      return (
        <button
          className="link status"
          title={serverMessage(s.last_error ?? '', lang())}
          onClick={() => show(close => <FormModal title={namesText(s)} note={serverMessage(s.last_error ?? '', lang())} submit={t('Понятно')} cancel={null} onClose={close} />)}
        >
          <Dot kind="err" />{t('Ошибка')}
        </button>
      );
    case 'queued':
      return <span title={t('Письма ждут отправки')}><Status kind="warn">{t('В очереди')}</Status></span>;
    case 'sent':
      return (
        <span title={Object.entries(s.sent_serials).map(([n, serial]) => n + ': ' + serial).join('\n')}>
          <Status kind="ok">{fmtDateTime(s.last_sent_at)}</Status>
        </span>
      );
    default:
      return <span className="muted">{t('ещё не отправлялся')}</span>;
  }
}

function SubscriptionDialog({ subscription: s, known, onClose }: {
  subscription: Subscription | null;
  known: KnownCertificate[];
  onClose: (done: boolean) => void;
}) {
  const [names, setNames] = useState<string[]>(s?.common_names ?? []);
  const [draft, setDraft] = useState('');
  const [recipients, setRecipients] = useState((s?.recipients ?? []).join('\n'));
  const [formats, setFormats] = useState<string[]>(s?.formats ?? ['PEM', 'KEY']);
  const [onChange, setOnChange] = useState(s ? s.on_change : true);
  const [days, setDays] = useState(s?.interval_days ? String(s.interval_days) : '');
  const [passphrase, setPassphrase] = useState(s?.include_passphrase ?? false);
  const [enabled, setEnabled] = useState(s ? s.enabled : true);
  // Новая подписка пишет на языке интерфейса; у существующей — свой.
  const [locale, setLocale] = useState<string>(s?.locale ?? lang());
  const [description, setDescription] = useState(s?.description ?? '');
  const [sendTime, setSendTime] = useState(s?.send_time ?? '');
  const [planned, setPlanned] = useState(!!(s?.active_from || s?.active_until));
  const [from, setFrom] = useState(s?.active_from ?? '');
  const [until, setUntil] = useState(s?.active_until ?? '');
  // Правка из другого пояса не сдвигает дни и время подписки.
  const zone = s?.time_zone ?? browserZone();
  const knownNames = useMemo(() => [...new Set(known.map(c => c.common_name))].sort(), [known]);
  const offered = knownNames.filter(n => !names.some(x => x.toLowerCase() === n.toLowerCase()));

  const toggle = (f: string) => setFormats(list => (list.includes(f) ? list.filter(x => x !== f) : [...list, f]));

  const addDraft = () => {
    setNames(list => withNames(list, splitNames(draft)));
    setDraft('');
  };

  const submit = async () => {
    // Набранное без Enter имя тоже идёт в подписку.
    const all = withNames(names, splitNames(draft));
    if (all.length === 0) throw new Invalid(t('Добавьте хотя бы один сертификат.'));
    if (formats.length === 0) throw new Invalid(t('Выберите хотя бы один файл.'));
    if (!onChange && !days) throw new Invalid(t('Выберите, когда отправлять: при обновлении, раз в N дней или оба варианта.'));
    if (planned && !from && !until) throw new Invalid(t('Укажите первый или последний день отправки.'));
    if (planned && from && until && until < from) throw new Invalid(t('Последний день раньше первого.'));
    const body = {
      common_names: all,
      recipients: recipients.split(/[\s,;]+/).map(v => v.trim()).filter(Boolean),
      formats: FORMATS.filter(f => formats.includes(f)),
      on_change: onChange,
      interval_days: days ? Number(days) : 0,
      include_passphrase: passphrase,
      locale,
      enabled,
      description: description.trim(),
      // Пустые строки снимают описание, период и время.
      active_from: planned ? from : '',
      active_until: planned ? until : '',
      send_time: sendTime,
      time_zone: zone,
    };
    if (s) await api.patch('/api/certificate-subscriptions/' + s.id, body);
    else await api.post('/api/certificate-subscriptions', body);
  };

  return (
    <FormModal title={s ? t('Подписка') : t('Новая подписка')} submit={s ? t('Сохранить') : t('Добавить')} onSubmit={submit} onClose={onClose}>
      <datalist id="known-names">{offered.map(n => <option key={n} value={n} />)}</datalist>
      <Field label={t('Описание')} hint={t('Видно только здесь, в письма не уходит.')}>
        <textarea rows={2} maxLength={DESCRIPTION_MAX} placeholder={t('Для кого и зачем эта рассылка')}
          value={description} onChange={e => setDescription(e.target.value)} />
      </Field>
      <Group label={t('Сертификаты (Common Name)')}>
        <div className="stack tight">
          {names.length > 0 && (
            <div className="chips">
              {names.map(n => (
                <span key={n} className="chip removable">
                  {n}
                  <button type="button" className="chip-x" aria-label={t('Убрать {name}', { name: n })} title={t('Убрать')}
                    onClick={() => setNames(list => list.filter(x => x !== n))}>×</button>
                </span>
              ))}
            </div>
          )}
          <div className="add-row">
            <input type="text" list="known-names" placeholder="*.example.com" autoFocus={!s} value={draft}
              aria-label={t('Common Name сертификата')}
              onChange={e => setDraft(e.target.value)}
              onKeyDown={e => { if (e.key === 'Enter') { e.preventDefault(); addDraft(); } }} />
            <button type="button" onClick={addDraft} disabled={!draft.trim()}><Icon name="plus" />{t('Добавить')}</button>
          </div>
          <small className="muted">{t('Один или несколько. При обновлении уходит только обновлённый, по расписанию и вручную — все.')}</small>
        </div>
      </Group>
      <Field label={t('Кому — по адресу в строке')}>
        <textarea rows={3} placeholder={'admin@example.com\nops@example.com'} value={recipients} onChange={e => setRecipients(e.target.value)} />
      </Field>
      <Group label={t('Файлы')}>
        <div className="toggle-group">
          {FORMATS.map(f => (
            <label key={f}>
              <input type="checkbox" checked={formats.includes(f)} onChange={() => toggle(f)} />
              <span>{f}</span>
            </label>
          ))}
        </div>
      </Group>
      <Group label={t('Когда отправлять')}>
        <div className="stack tight">
          <Check label={t('При обновлении в Key Manager')} checked={onChange} onChange={setOnChange} />
          <div className="inline">
            {t('Повторять раз в')}
            <input type="number" min={1} max={365} placeholder="—" aria-label={t('Дней')} value={days} onChange={e => setDays(e.target.value)} />
            {t('дн.')}
          </div>
          <div className="inline">
            {t('Время отправки')}
            <input type="time" aria-label={t('Время отправки')} value={sendTime} onChange={e => setSendTime(e.target.value)} />
            {sendTime && <button type="button" className="link" onClick={() => setSendTime('')}>{t('без времени')}</button>}
          </div>
          <Check label={t('Запланированный старт и окончание')} checked={planned} onChange={setPlanned} />
          {planned && (
            <div className="inline">
              {/* «по» не отрывается от своего дня. */}
              <span className="inline">
                {t('с')}
                <input type="date" aria-label={t('Первый день')} value={from} max={until || undefined} onChange={e => setFrom(e.target.value)} />
              </span>
              <span className="inline">
                {t('по')}
                <input type="date" aria-label={t('Последний день')} value={until} min={from || undefined} onChange={e => setUntil(e.target.value)} />
              </span>
            </div>
          )}
          <small className="muted">
            {sendTime ? t('Письма уходят в {time}, замеченное позже — на следующий день.', { time: sendTime }) : t('Без времени — сразу, как заметит опрос.')}
            {(sendTime || planned) && ' ' + t('Время и дни — по поясу {zone}; вручную можно отправить всегда.', { zone })}
          </small>
        </div>
      </Group>
      <div className="row">
        <Field label={t('Язык письма')}>
          <select value={locale} onChange={e => setLocale(e.target.value)}>
            {LANGS.map(([code, name]) => <option key={code} value={code}>{name}</option>)}
          </select>
        </Field>
        <div className="stack tight align-end">
          <Check label={t('Пароль ключа в письме')} checked={passphrase} onChange={setPassphrase} />
          <Check label={t('Включена')} checked={enabled} onChange={setEnabled} />
        </div>
      </div>
    </FormModal>
  );
}
