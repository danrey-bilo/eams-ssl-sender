import { useEffect, useRef, useState, type FormEvent, type ReactNode } from 'react';
import { t } from '../i18n/common';
import { Cancelled, errorText } from '../lib/api';

/** Окно с формой на <dialog>: ошибка onSubmit остаётся в окне, Cancelled не показывается. */
export function FormModal({
  title,
  note,
  children,
  submit = t('Сохранить'),
  danger = false,
  cancel = t('Отмена'),
  wide = false,
  onSubmit,
  onClose,
}: {
  title: string;
  note?: ReactNode;
  children?: ReactNode;
  submit?: string;
  danger?: boolean;
  cancel?: string | null;
  wide?: boolean;
  onSubmit?: () => Promise<unknown> | void;
  onClose: (done: boolean) => void;
}) {
  const ref = useRef<HTMLDialogElement>(null);
  const [error, setError] = useState('');
  const [busy, setBusy] = useState(false);

  useEffect(() => {
    const dialog = ref.current;
    if (dialog && !dialog.open) dialog.showModal();
    return () => dialog?.close();
  }, []);

  const handle = async (event: FormEvent) => {
    event.preventDefault();
    setError('');
    setBusy(true);
    try {
      await onSubmit?.();
      onClose(true);
    } catch (err) {
      if (!(err instanceof Cancelled)) setError(err instanceof Invalid ? err.message : errorText(err));
    } finally {
      setBusy(false);
    }
  };

  return (
    <dialog ref={ref} className={wide ? 'wide' : undefined} onCancel={e => { e.preventDefault(); onClose(false); }}>
      <form noValidate onSubmit={handle}>
        <div>
          <h2>{title}</h2>
          {note && <p className="muted">{note}</p>}
        </div>
        {children}
        {error && <p className="error" role="alert">{error}</p>}
        <div className="actions-row">
          {cancel && <button type="button" onClick={() => onClose(false)}>{cancel}</button>}
          <button type="submit" className={'primary' + (danger ? ' danger' : '')} disabled={busy}>{submit}</button>
        </div>
      </form>
    </dialog>
  );
}

/** Ошибка проверки формы до отправки — показывается в окне как есть. */
export class Invalid extends Error {}
