import { useEffect, useRef, useState } from 'react';
import { t } from '../i18n/common';
import { isMobile } from '../lib/device';
import { Icon } from './Icon';
import { useOverlay } from './Overlay';

export interface Action {
  text: string;
  action: () => void;
  danger?: boolean;
}

/** false/null/undefined — пункт не показывается. */
export type MenuItem = Action | false | null | undefined;

/** Кнопка «⋯»: на ПК выпадающий список, на телефоне — лист снизу. */
export function Menu({ title, items }: { title?: string; items: MenuItem[] }) {
  const { show } = useOverlay();
  const actions = items.filter((item): item is Action => Boolean(item));

  if (!isMobile) return <Dropdown actions={actions} />;

  return (
    <button
      type="button"
      className="icon"
      title={t('Действия')}
      aria-haspopup="dialog"
      onClick={() => show(close => <ActionSheet title={title} actions={actions} onClose={close} />)}
    >
      <Icon name="more" />
    </button>
  );
}

function Dropdown({ actions }: { actions: Action[] }) {
  const [open, setOpen] = useState<'down' | 'up' | null>(null);
  const ref = useRef<HTMLDivElement>(null);

  useEffect(() => {
    if (!open) return;
    const outside = (event: MouseEvent) => { if (!ref.current?.contains(event.target as Node)) setOpen(null); };
    const escape = (event: KeyboardEvent) => { if (event.key === 'Escape') setOpen(null); };
    document.addEventListener('mousedown', outside);
    document.addEventListener('keydown', escape);
    return () => {
      document.removeEventListener('mousedown', outside);
      document.removeEventListener('keydown', escape);
    };
  }, [open]);

  const toggle = () => {
    if (open) { setOpen(null); return; }
    const below = window.innerHeight - (ref.current?.getBoundingClientRect().bottom ?? 0);
    setOpen(below < 24 + 40 * actions.length ? 'up' : 'down');
  };

  return (
    <div className="menu" ref={ref}>
      <button type="button" className="icon" title={t('Действия')} aria-haspopup="menu" aria-expanded={open !== null} onClick={toggle}>
        <Icon name="more" />
      </button>
      {open && (
        <div className={'menu-list ' + open} role="menu">
          {actions.map(a => (
            <button
              key={a.text}
              type="button"
              role="menuitem"
              className={a.danger ? 'danger' : undefined}
              onClick={() => { setOpen(null); a.action(); }}
            >
              {a.text}
            </button>
          ))}
        </div>
      )}
    </div>
  );
}

/** Лист действий снизу (телефон). */
function ActionSheet({ title, actions, onClose }: {
  title?: string;
  actions: Action[];
  onClose: (done: boolean) => void;
}) {
  const ref = useRef<HTMLDialogElement>(null);

  useEffect(() => {
    const dialog = ref.current;
    if (dialog && !dialog.open) dialog.showModal();
    return () => dialog?.close();
  }, []);

  const choose = (a: Action) => {
    onClose(true);
    a.action();
  };

  // ⚠ Щелчок с целью самим <dialog> — это щелчок по фону.
  return (
    <dialog
      ref={ref}
      className="sheet"
      aria-label={title ?? t('Действия')}
      onCancel={e => { e.preventDefault(); onClose(false); }}
      onClick={e => { if (e.target === e.currentTarget) onClose(false); }}
    >
      <div className="sheet-body">
        {title && <div className="sheet-title">{title}</div>}
        <div className="sheet-list" role="menu">
          {actions.map(a => (
            <button key={a.text} type="button" role="menuitem" className={a.danger ? 'danger' : undefined} onClick={() => choose(a)}>
              {a.text}
            </button>
          ))}
        </div>
        <button type="button" className="sheet-cancel" onClick={() => onClose(false)}>{t('Отмена')}</button>
      </div>
    </dialog>
  );
}
