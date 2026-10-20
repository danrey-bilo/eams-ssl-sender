import { useState, type FormEvent } from 'react';
import { api, Cancelled, errorText } from '../lib/api';
import { useLoad } from '../lib/hooks';
import { loadSettings, saveSettings, type Settings } from '../lib/settings';
import type { Me } from '../lib/types';
import { Check, ErrorText, Failed, Field, PageHead, Spinner, Status } from '../ui/bits';
import { Icon } from '../ui/Icon';
import { useOverlay } from '../ui/Overlay';
import { StepUpDialog } from '../ui/StepUp';

interface Section {
  title: string;
  note?: string;
  keys: Array<[key: string, label: string]>;
}

/**
 * Настройки защиты — по-русски и сгруппированно. Границы и умолчания приходят с сервера.
 *
 * ⚠ Изменение любой из них сервер пропускает только со свежим кодом: окно кода откроется само.
 */
const SECTIONS: Section[] = [
  {
    title: 'Вход',
    keys: [
      ['auth.lockout_threshold', 'Неудачных попыток до блокировки'],
      ['auth.lockout_minutes', 'Блокировка, мин'],
      ['auth.two_factor_challenge_minutes', 'Время на ввод кода при входе, мин'],
    ],
  },
  {
    title: 'Сессии',
    keys: [
      ['auth.session_idle_minutes', 'Выход при бездействии, мин'],
      ['auth.session_absolute_hours', 'Сессия живёт не дольше, ч'],
    ],
  },
  {
    title: 'Пароли',
    keys: [
      ['auth.password_min_length', 'Минимальная длина'],
      ['auth.password_require_uppercase', 'Заглавная буква'],
      ['auth.password_require_lowercase', 'Строчная буква'],
      ['auth.password_require_digit', 'Цифра'],
      ['auth.password_require_symbol', 'Спецсимвол'],
    ],
  },
  {
    title: 'Ссылки и письма',
    keys: [
      ['auth.invitation_ttl_minutes', 'Ссылка приглашения действует, мин'],
      ['mail.rate_per_hour', 'Писем в час на один адрес'],
    ],
  },
];

const TIME = new Intl.DateTimeFormat('ru-RU', { timeStyle: 'short' });

export function SecurityPage() {
  const { data, error, reload } = useLoad(() => Promise.all([loadSettings(), api.get<Me>('/api/me')]));
  const { show, toast } = useOverlay();

  if (error) return <Failed text={error} />;
  if (!data) return <Spinner />;

  const [settings, me] = data;
  const until = me.step_up_until ? new Date(me.step_up_until) : null;
  const fresh = until !== null && until.getTime() > Date.now();
  const minutes = Number(settings['auth.step_up_minutes']?.value ?? 10);

  const confirmNow = async () => {
    if (await show(close => <StepUpDialog minutes={minutes} onClose={close} />)) { toast('Подтверждено'); reload(); }
  };

  return (
    <>
      <PageHead title="Безопасность" sub="Вход, сессии, пароли. Изменения подтверждаются кодом." />
      <div className="stack">
        <section className="card status-card">
          <div className="status-line">
            <Status kind={fresh ? 'ok' : ''} strong>
              {fresh ? 'Код подтверждён до ' + TIME.format(until!) : 'Опасные действия спросят код'}
            </Status>
            {!fresh && <button onClick={confirmNow}><Icon name="lock" />Подтвердить</button>}
          </div>
          <p className="muted">
            Скачивание сертификатов и ключей, изменения настроек, людей и подписок требуют кода из приложения.
            После кода {minutes} мин он не спрашивается.
          </p>
          <SettingsForm settings={settings} keys={[['auth.step_up_minutes', 'Не спрашивать код повторно, мин']]} onSaved={reload} compact />
        </section>
        <div className="grid-2">
          {SECTIONS.map(section => (
            <section key={section.title} className="card">
              <h2>{section.title}</h2>
              <SettingsForm settings={settings} keys={section.keys} onSaved={reload} />
            </section>
          ))}
        </div>
      </div>
    </>
  );
}

/** Форма из нескольких настроек: числа — полями, флажки — галками; пусто — умолчание. */
function SettingsForm({ settings, keys, onSaved, compact = false }: {
  settings: Settings;
  keys: Array<[string, string]>;
  onSaved: () => void;
  compact?: boolean;
}) {
  const { toast } = useOverlay();
  const present = keys.flatMap(([key, label]) => {
    const meta = settings[key];
    return meta ? [{ key, label, meta }] : [];
  });
  const [values, setValues] = useState<Record<string, string | boolean>>(() =>
    Object.fromEntries(present.map(({ key, meta }) => [key, initial(meta.value)])));
  const [error, setError] = useState('');
  const [busy, setBusy] = useState(false);

  if (present.length === 0) return null;

  const changed = present.some(({ key, meta }) => values[key] !== initial(meta.value));

  const submit = async (event: FormEvent) => {
    event.preventDefault();
    setError('');
    setBusy(true);
    try {
      await saveSettings(settings, Object.fromEntries(present.map(({ key }) => {
        const value = values[key] ?? '';
        return [key, typeof value === 'boolean' ? value : value.trim() === '' ? '' : Number(value)];
      })));
      toast('Сохранено');
      onSaved();
    } catch (err) {
      if (!(err instanceof Cancelled)) setError(errorText(err));
    } finally {
      setBusy(false);
    }
  };

  const numbers = present.filter(({ meta }) => typeof meta.value !== 'boolean');
  const flags = present.filter(({ meta }) => typeof meta.value === 'boolean');

  return (
    <form onSubmit={submit} className={compact ? 'inline-form' : undefined}>
      {numbers.map(({ key, label, meta }) => (
        <Field key={key} label={label}>
          <input
            type="number"
            min={meta.minimum ?? undefined}
            max={meta.maximum ?? undefined}
            placeholder={meta.default === null || meta.default === undefined ? '' : String(meta.default)}
            value={String(values[key] ?? '')}
            onChange={e => setValues(v => ({ ...v, [key]: e.target.value }))}
          />
        </Field>
      ))}
      {flags.length > 0 && (
        <div className="checks">
          {flags.map(({ key, label }) => (
            <Check key={key} label={label} checked={values[key] === true} onChange={v => setValues(all => ({ ...all, [key]: v }))} />
          ))}
        </div>
      )}
      <ErrorText text={error} />
      <div className="actions-row">
        <button type="submit" className="primary" disabled={busy || !changed}>Сохранить</button>
      </div>
    </form>
  );
}

/** Значение настройки для поля: флажок как есть, остальное строкой. */
function initial(value: unknown): string | boolean {
  if (typeof value === 'boolean') return value;
  return value === null || value === undefined ? '' : String(value);
}
