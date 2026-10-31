import { useMemo, useState } from 'react';
import { certificates } from '../i18n/certificates';
import { common } from '../i18n/common';
import { api, saveFile } from '../lib/api';
import { isMobile } from '../lib/device';
import { fmtDate, fmtDateTime } from '../lib/format';
import { useLoad } from '../lib/hooks';
import { translator } from '../lib/i18n';
import type { KeyManagerStatus, KnownCertificate, PrivateKey } from '../lib/types';
import { CopyField, Empty, Failed, Group, PageHead, Spinner, useCopy } from '../ui/bits';
import { Icon } from '../ui/Icon';
import { FormModal } from '../ui/Modal';
import { useOverlay } from '../ui/Overlay';

const t = translator({ ...common, ...certificates });

/** Фраза словаря: подписи в константах переводятся при показе. */
type Phrase = Parameters<typeof t>[0];

/** Типы файлов Key Manager Plus словами. */
const FORMATS: Array<[format: string, title: string, hint: Phrase]> = [
  ['PEM', 'PEM', 'сертификат'],
  ['CRT', 'CRT', 'сертификат'],
  ['CER', 'CER', 'сертификат'],
  ['DER', 'DER', 'двоичный'],
  ['P7B', 'P7B', 'с цепочкой'],
  ['PKCS', 'PFX', 'с ключом'],
  ['JKS', 'JKS', 'для Java'],
  ['KEY', 'KEY', 'закрытый ключ'],
];

const DAY = 86_400_000;

type Order = 'name' | 'expiry-asc' | 'expiry-desc';

const ORDERS: Array<[Order, Phrase]> = [
  ['name', 'По имени'],
  ['expiry-asc', 'Сначала истекающие'],
  ['expiry-desc', 'Сначала дальние'],
];

/** Порядок помнит браузер, не сервер. */
const ORDER_KEY = 'certificates.order';

function savedOrder(): Order {
  try {
    const value = localStorage.getItem(ORDER_KEY);
    return value === 'expiry-asc' || value === 'expiry-desc' ? value : 'name';
  } catch {
    return 'name';
  }
}

const byName = (a: KnownCertificate, b: KnownCertificate) =>
  a.common_name.localeCompare(b.common_name) || (b.expires_on ?? '').localeCompare(a.expires_on ?? '');

/** Без срока — в конце при любом направлении. */
const byExpiry = (direction: 1 | -1) => (a: KnownCertificate, b: KnownCertificate) => {
  if (!a.expires_on || !b.expires_on) return a.expires_on ? -1 : b.expires_on ? 1 : byName(a, b);
  return direction * a.expires_on.localeCompare(b.expires_on) || byName(a, b);
};

const COMPARE: Record<Order, (a: KnownCertificate, b: KnownCertificate) => number> = {
  name: byName,
  'expiry-asc': byExpiry(1),
  'expiry-desc': byExpiry(-1),
};

/** «*» в имени файла недопустим. */
const fileBase = (c: KnownCertificate) => c.common_name.replace('*', 'wildcard');

function daysLeft(ymd: string | null): number | null {
  if (!ymd) return null;
  const end = new Date(ymd + 'T00:00:00').getTime();
  const today = new Date(new Date().toDateString()).getTime();
  return Math.round((end - today) / DAY);
}

function Expiry({ ymd }: { ymd: string | null }) {
  const days = daysLeft(ymd);
  if (days === null) return <span className="muted">—</span>;
  const kind = days <= 14 ? 'err' : days <= 30 ? 'warn' : '';
  const label = days < 0 ? t('истёк') : days === 0 ? t('сегодня') : t('{n} дн.', { n: days });
  return (
    <span className="expiry">
      {fmtDate(ymd)}
      <span className={'badge ' + kind}>{label}</span>
    </span>
  );
}

export function CertificatesPage() {
  const { data, error, reload } = useLoad(() => Promise.all([
    api.get<KeyManagerStatus>('/api/keymanager'),
    api.get<KnownCertificate[]>('/api/keymanager/certificates'),
  ]));
  const { show, run, toast } = useOverlay();
  const [query, setQuery] = useState('');
  const [order, setOrder] = useState<Order>(savedOrder);

  const choose = (next: Order) => {
    setOrder(next);
    try { localStorage.setItem(ORDER_KEY, next); } catch { /* без хранилища — порядок до перезагрузки */ }
  };

  // Только сертификаты последнего опроса.
  const rows = useMemo(() => {
    if (!data) return [];
    const q = query.trim().toLowerCase();
    return data[1]
      .filter(c => !q || c.common_name.toLowerCase().includes(q) || c.serial_number.toLowerCase().includes(q))
      .sort(COMPARE[order]);
  }, [data, query, order]);

  if (error) return <Failed text={error} />;
  if (!data) return <Spinner />;

  const [km, known] = data;

  const poll = async () => {
    if (await run(() => api.post('/api/keymanager/poll'), t('Опрос запущен'))) setTimeout(reload, 3000);
  };

  const download = (c: KnownCertificate) => show(close => <DownloadDialog certificate={c} onClose={close} />)
    .then(done => { if (done) toast(t('Файл скачан')); });

  const reveal = async (c: KnownCertificate) => {
    let key: PrivateKey | null = null;
    // ⚠ Ключ — до окна: без свежего кода откроется окно кода, а не пустое окно ключа.
    if (!await run(async () => {
      key = await api.post<PrivateKey>('/api/keymanager/certificates/private-key', {
        common_name: c.common_name, serial_number: c.serial_number,
      });
    })) return;
    const shown = key as PrivateKey | null;
    if (shown) await show(close => <KeyDialog certificate={c} keyData={shown} onClose={close} />);
  };

  const time = fmtDateTime(km.last_poll_at);
  const sub = km.configured
    ? (km.last_poll_error ? t('Key Manager Plus · опрос {time} · ошибка', { time }) : t('Key Manager Plus · опрос {time}', { time }))
    : <>{t('Key Manager Plus не подключён')} — <a href="#/settings/keymanager">{t('настроить')}</a></>;

  const actions = (c: KnownCertificate) => (
    <div className="row-actions">
      <button onClick={() => download(c)} title={t('Скачать файл')}><Icon name="download" />{t('Скачать')}</button>
      <button onClick={() => reveal(c)} title={t('Показать закрытый ключ')}><Icon name="key" />{t('Ключ')}</button>
    </div>
  );

  return (
    <>
      <PageHead title={t('Сертификаты')} sub={sub}>
        <button onClick={poll} disabled={!km.configured}><Icon name="refresh" />{t('Обновить')}</button>
      </PageHead>

      {known.length === 0 ? (
        <div className="card">
          <Empty title={t('Сертификатов пока нет')} hint={t('Они появятся после опроса Key Manager Plus.')} />
        </div>
      ) : (
        <div className="card flush">
          <div className="toolbar">
            <label className="search">
              <Icon name="search" />
              <input type="search" placeholder={t('Поиск по имени или серийному')} value={query} onChange={e => setQuery(e.target.value)} />
            </label>
            <div className="inline">
              {/* На телефоне нет заголовков — порядок списком. */}
              {isMobile && (
                <select className="order" aria-label={t('Порядок')} value={order} onChange={e => choose(e.target.value as Order)}>
                  {ORDERS.map(([value, title]) => <option key={value} value={value}>{t(title)}</option>)}
                </select>
              )}
              <span className="muted small">{t('{shown} из {total}', { shown: rows.length, total: known.length })}</span>
            </div>
          </div>
          {rows.length === 0 && <Empty title={t('Ничего не найдено')} />}
          {rows.length > 0 && isMobile && (
            <div className="list">
              {rows.map(c => (
                <div key={c.common_name + c.serial_number} className="item">
                  <div className="item-main">
                    <div className="item-title">{c.common_name}</div>
                    <div className="item-meta"><Expiry ymd={c.expires_on} /></div>
                    <div className="item-meta mono">{c.serial_number}</div>
                  </div>
                  {actions(c)}
                </div>
              ))}
            </div>
          )}
          {rows.length > 0 && !isMobile && (
            <table>
              <thead>
                <tr>
                  <th aria-sort={order === 'name' ? 'ascending' : 'none'}>
                    <button type="button" className="sort" title={t('По имени')} onClick={() => choose('name')}>
                      {t('Сертификат')}<Icon name={order === 'name' ? 'up' : 'sort'} />
                    </button>
                  </th>
                  <th aria-sort={order === 'expiry-asc' ? 'ascending' : order === 'expiry-desc' ? 'descending' : 'none'}>
                    <button
                      type="button"
                      className="sort"
                      title={order === 'expiry-asc' ? t('Сначала дальние') : t('Сначала истекающие')}
                      onClick={() => choose(order === 'expiry-asc' ? 'expiry-desc' : 'expiry-asc')}
                    >
                      {t('Действует до')}<Icon name={order === 'expiry-asc' ? 'up' : order === 'expiry-desc' ? 'down' : 'sort'} />
                    </button>
                  </th>
                  <th>{t('Замечен')}</th>
                  <th />
                </tr>
              </thead>
              <tbody>
                {rows.map(c => (
                  <tr key={c.common_name + c.serial_number}>
                    <td>
                      <span className="strong">{c.common_name}</span>
                      <span className="sub mono">{c.serial_number}</span>
                    </td>
                    <td><Expiry ymd={c.expires_on} /></td>
                    <td className="muted">{fmtDateTime(c.first_seen_at)}</td>
                    <td className="actions">{actions(c)}</td>
                  </tr>
                ))}
              </tbody>
            </table>
          )}
        </div>
      )}
    </>
  );
}

function DownloadDialog({ certificate: c, onClose }: { certificate: KnownCertificate; onClose: (done: boolean) => void }) {
  const [format, setFormat] = useState('PEM');

  return (
    <FormModal
      title={t('Скачать')}
      note={c.common_name}
      submit={t('Скачать')}
      onSubmit={() => api.download('/api/keymanager/certificates/download',
        { common_name: c.common_name, serial_number: c.serial_number, format }, fileBase(c) + '.' + format.toLowerCase())}
      onClose={onClose}
    >
      <Group label={t('Формат')}>
        <div className="choices">
          {FORMATS.map(([value, title, hint]) => (
            <label key={value} className={format === value ? 'checked' : ''}>
              <input type="radio" name="format" value={value} checked={format === value} onChange={() => setFormat(value)} />
              <b>{title}</b>
              <span>{t(hint)}</span>
            </label>
          ))}
        </div>
      </Group>
    </FormModal>
  );
}

function KeyDialog({ certificate: c, keyData, onClose }: {
  certificate: KnownCertificate;
  keyData: PrivateKey;
  onClose: (done: boolean) => void;
}) {
  const [copied, copy] = useCopy();

  const save = () => saveFile(new Blob([keyData.private_key]), fileBase(c) + '.key');

  return (
    <FormModal title={t('Закрытый ключ')} note={c.common_name} submit={t('Готово')} cancel={null} wide onClose={onClose}>
      <textarea className="mono key" readOnly rows={10} value={keyData.private_key} onFocus={e => e.target.select()} />
      <div className="inline">
        <button type="button" onClick={() => copy(keyData.private_key)}>
          <Icon name={copied ? 'check' : 'copy'} />{copied ? t('Скопировано') : t('Копировать')}
        </button>
        <button type="button" onClick={save}><Icon name="download" />{t('Сохранить .key')}</button>
      </div>
      {keyData.passphrase && (
        <Group label={t('Пароль ключа')}><CopyField value={keyData.passphrase} /></Group>
      )}
    </FormModal>
  );
}
