import { useEffect, useRef, useState } from 'react';
import { Icon } from './Icon';

export type MenuItem = { text: string; action: () => void; danger?: boolean } | false | null | undefined;

/** Кнопка «⋯» со списком действий; закрывается щелчком мимо и клавишей Esc. */
export function Menu({ items }: { items: MenuItem[] }) {
  const [open, setOpen] = useState(false);
  const ref = useRef<HTMLDivElement>(null);

  useEffect(() => {
    if (!open) return;
    const outside = (event: MouseEvent) => { if (!ref.current?.contains(event.target as Node)) setOpen(false); };
    const escape = (event: KeyboardEvent) => { if (event.key === 'Escape') setOpen(false); };
    document.addEventListener('mousedown', outside);
    document.addEventListener('keydown', escape);
    return () => {
      document.removeEventListener('mousedown', outside);
      document.removeEventListener('keydown', escape);
    };
  }, [open]);

  return (
    <div className="menu" ref={ref}>
      <button type="button" className="icon" title="Действия" aria-haspopup="menu" aria-expanded={open} onClick={() => setOpen(!open)}>
        <Icon name="more" />
      </button>
      {open && (
        <div className="menu-list" role="menu">
          {items.filter(Boolean).map(item => item && (
            <button
              key={item.text}
              type="button"
              role="menuitem"
              className={item.danger ? 'danger' : ''}
              onClick={() => { setOpen(false); item.action(); }}
            >
              {item.text}
            </button>
          ))}
        </div>
      )}
    </div>
  );
}
