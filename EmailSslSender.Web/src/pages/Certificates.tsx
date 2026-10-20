import { useMemo, useState } from 'react';
import { api } from '../lib/api';
import { isMobile } from '../lib/device';
import { fmtDate, fmtDateTime } from '../lib/format';
import { useLoad } from '../lib/hooks';
import type { KeyManagerStatus, KnownCertificate, PrivateKey } from '../lib/types';
import { CopyField, Empty, Failed, Group, PageHead, Spinner, useCopy } from '../ui/bits';
import { Icon } from '../ui/Icon';
import { FormModal } from '../ui/Modal';
import { useOverlay } from '../ui/Overlay';

/** Типы файлов Key Manager Plus и что в них лежит — словами для выбора. */
const FORMATS: Array<[format: string, title: string, hint: string]> = [
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

function daysLeft(ymd: string | null): number | null {
  if (!ymd) return null;
  const end = new Date(ymd + 'T00:00:00').getTime();
  const today = new Date(new Date().toDateString()).getTime();
  return Math.round((end - today) / DAY);
}

function Expiry({ ymd }: { ymd: string | null }) {
  const days = daysLeft(ymd);
  if (days === null) return <span className="muted">—</span>;
  const kind = days < 0 ? 'err' : days <= 14 ? 'err' : days <= 30 ? 'warn' : '';
  const label = days < 0 ? 'истёк' : days === 0 ? 'сегодня' : days + ' дн.';
  return (
    <span className="expiry">
      {fmtDate(ymd)}
      <span className={'badge ' + kind}>{label}</span>
    </span>
  );
}

/** Сертификат виден в последнем опросе — иначе в Key Manager Plus его уже нет. */
const isCurrent = (c: KnownCertificate, lastPoll: string | null) =>
  !lastPoll || new Date(c.last_seen_at).getTime() >= new Date(lastPoll).getTime() - 60_000;

export function CertificatesPage() {
  const { data, error, reload } = useLoad(() => Promise.all([
    api.get<KeyManagerStatus>('/api/keymanager'),
    api.get<KnownCertificate[]>('/api/keymanager/certificates'),
  ]));
  const { show, run, toast } = useOverlay();
  const [query, setQuery] = useState('');

  const rows = useMemo(() => {
    if (!data) return [];
    const [km, known] = data;
    const q = query.trim().toLowerCase();
    return known
      .filter(c => !q || c.common_name.toLowerCase().includes(q) || c.serial_number.toLowerCase().includes(q))
      .map(c => ({ ...c, current: isCurrent(c, km.last_poll_at) }))
      .sort((a, b) => Number(b.current) - Number(a.current)
        || a.common_name.localeCompare(b.common_name)
        || (b.expires_on ?? '').localeCompare(a.expires_on ?? ''));
  }, [data, query]);

  if (error) return <Failed text={error} />;
  if (!data) return <Spinner />;

  const [km, known] = data;

  const poll = async () => {
    if (await run(() => api.post('/api/keymanager/poll'), 'Опрос запущен')) setTimeout(reload, 3000);
  };

  const download = (c: KnownCertificate) => show(close => <DownloadDialog certificate={c} onClose={close} />)
    .then(done => { if (done) toast('Файл скачан'); });

  const reveal = async (c: KnownCertificate) => {
    let key: PrivateKey | null = null;
    // ⚠ Ключ запрашивается ДО окна: без свежего кода сервер откажет, и поверх страницы
    // откроется окно кода, а не пустое окно ключа под ним.
    if (!await run(async () => {
      key = await api.post<PrivateKey>('/api/keymanager/certificates/private-key', {
        common_name: c.common_name, serial_number: c.serial_number,
      });
    })) return;
    const shown = key as PrivateKey | null;
    if (shown) await show(close => <KeyDialog certificate={c} keyData={shown} onClose={close} />);
  };

  const sub = km.configured
    ? <>Key Manager Plus · опрос {fmtDateTime(km.last_poll_at)}{km.last_poll_error ? ' · ошибка' : ''}</>
    : <>Key Manager Plus не подключён — <a href="#/settings/keymanager">настроить</a></>;

  const actions = (c: KnownCertificate & { current: boolean }) => c.current && (
    <div className="row-actions">
      <button onClick={() => download(c)} title="Скачать файл"><Icon name="download" />Скачать</button>
      <button onClick={() => reveal(c)} title="Показать закрытый ключ"><Icon name="key" />Ключ</button>
    </div>
  );

  return (
    <>
      <PageHead title="Сертификаты" sub={sub}>
        <button onClick={poll} disabled={!km.configured}><Icon name="refresh" />Обновить</button>
      </PageHead>

      {known.length === 0 ? (
        <div className="card">
          <Empty title="Сертификатов пока нет" hint="Они появятся после опроса Key Manager Plus." />
        </div>
      ) : (
        <div className="card flush">
          <div className="toolbar">
            <label className="search">
              <Icon name="search" />
              <input type="search" placeholder="Поиск по имени или серийному" value={query} onChange={e => setQuery(e.target.value)} />
            </label>
            <span className="muted small">{rows.length} из {known.length}</span>
          </div>
          {rows.length === 0 && <Empty title="Ничего не найдено" />}
          {rows.length > 0 && isMobile && (
            <div className="list">
              {rows.map(c => (
                <div key={c.common_name + c.serial_number} className={'item' + (c.current ? '' : ' off')}>
                  <div className="item-main">
                    <div className="item-title">{c.common_name}</div>
                    <div className="item-meta"><Expiry ymd={c.expires_on} /></div>
                    <div className="item-meta mono">{c.serial_number}</div>
                    {!c.current && <div className="item-meta">нет в Key Manager Plus</div>}
                  </div>
                  {actions(c)}
                </div>
              ))}
            </div>
          )}
          {rows.length > 0 && !isMobile && (
            <table>
              <thead>
                <tr><th>Сертификат</th><th>Действует до</th><th>Замечен</th><th /></tr>
              </thead>
              <tbody>
                {rows.map(c => (
                  <tr key={c.common_name + c.serial_number} className={c.current ? '' : 'off'}>
                    <td>
                      <span className="strong">{c.common_name}</span>
                      <span className="sub mono">{c.serial_number}</span>
                    </td>
                    <td><Expiry ymd={c.expires_on} /></td>
                    <td className="muted">{c.current ? fmtDateTime(c.first_seen_at) : 'нет в Key Manager Plus'}</td>
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
  const name = c.common_name.replace('*', 'wildcard');

  return (
    <FormModal
      title="Скачать"
      note={c.common_name}
      submit="Скачать"
      onSubmit={() => api.download('/api/keymanager/certificates/download',
        { common_name: c.common_name, serial_number: c.serial_number, format }, name + '.' + format.toLowerCase())}
      onClose={onClose}
    >
      <Group label="Формат">
        <div className="choices">
          {FORMATS.map(([value, title, hint]) => (
            <label key={value} className={format === value ? 'checked' : ''}>
              <input type="radio" name="format" value={value} checked={format === value} onChange={() => setFormat(value)} />
              <b>{title}</b>
              <span>{hint}</span>
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

  const save = () => {
    const url = URL.createObjectURL(new Blob([keyData.private_key], { type: 'application/x-pem-file' }));
    const link = document.createElement('a');
    link.href = url;
    link.download = c.common_name.replace('*', 'wildcard') + '.key';
    document.body.append(link);
    link.click();
    link.remove();
    setTimeout(() => URL.revokeObjectURL(url), 10_000);
  };

  return (
    <FormModal title="Закрытый ключ" note={c.common_name} submit="Готово" cancel={null} wide onClose={onClose}>
      <textarea className="mono key" readOnly rows={10} value={keyData.private_key} onFocus={e => e.target.select()} />
      <div className="inline">
        <button type="button" onClick={() => copy(keyData.private_key)}>
          <Icon name={copied ? 'check' : 'copy'} />{copied ? 'Скопировано' : 'Копировать'}
        </button>
        <button type="button" onClick={save}><Icon name="download" />Сохранить .key</button>
      </div>
      {keyData.passphrase && (
        <Group label="Пароль ключа"><CopyField value={keyData.passphrase} /></Group>
      )}
    </FormModal>
  );
}
