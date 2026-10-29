import { useState, type ReactNode } from 'react';
import { Icon } from './Icon';

export type DotKind = '' | 'ok' | 'err' | 'warn';

export const Dot = ({ kind = '' }: { kind?: DotKind }) => <span className={'dot ' + kind} />;

export function Status({ kind, children, strong }: { kind: DotKind; children: ReactNode; strong?: boolean }) {
  return <span className={'status' + (strong ? ' strong' : '')}><Dot kind={kind} />{children}</span>;
}

export const Spinner = () => <div className="spinner" role="status" aria-label="Загрузка" />;

export function Field({ label, hint, children }: { label: string; hint?: string; children: ReactNode }) {
  return (
    <label className="field">
      <span>{label}</span>
      {children}
      {hint && <small>{hint}</small>}
    </label>
  );
}

/** Без <label>: метка внутри метки недопустима. */
export function Group({ label, children }: { label: string; children: ReactNode }) {
  return <div className="field" role="group" aria-label={label}><span>{label}</span>{children}</div>;
}

export function Check({ label, checked, onChange }: { label: string; checked: boolean; onChange: (value: boolean) => void }) {
  return (
    <label className="check">
      <input type="checkbox" checked={checked} onChange={e => onChange(e.target.checked)} />
      {label}
    </label>
  );
}

export function CodeInput({ value, onChange, autoFocus = true }: { value: string; onChange: (value: string) => void; autoFocus?: boolean }) {
  return (
    <input
      type="text"
      className="code"
      inputMode="numeric"
      autoComplete="one-time-code"
      maxLength={8}
      placeholder="000000"
      aria-label="Код"
      required
      autoFocus={autoFocus}
      value={value}
      onChange={e => onChange(e.target.value.replace(/\s/g, ''))}
    />
  );
}

/** Скопировать; без буфера (небезопасный контекст) — выделить. */
export function useCopy(): [boolean, (text: string) => Promise<void>] {
  const [copied, setCopied] = useState(false);
  const copy = async (text: string) => {
    try { await navigator.clipboard.writeText(text); } catch { /* выделено — Ctrl+C */ }
    setCopied(true);
    setTimeout(() => setCopied(false), 2000);
  };
  return [copied, copy];
}

export function CopyField({ value }: { value: string }) {
  const [copied, copy] = useCopy();
  return (
    <div className="copy">
      <input type="text" readOnly value={value} onFocus={e => e.target.select()} />
      <button type="button" onClick={() => copy(value)}>
        <Icon name={copied ? 'check' : 'copy'} />{copied ? 'Скопировано' : 'Копировать'}
      </button>
    </div>
  );
}

export const ErrorText = ({ text }: { text: string }) => (text ? <p className="error" role="alert">{text}</p> : null);

export function PageHead({ title, sub, children }: { title: string; sub?: ReactNode; children?: ReactNode }) {
  return (
    <div className="head">
      <div className="head-text">
        <h1>{title}</h1>
        {sub && <p className="head-sub">{sub}</p>}
      </div>
      {children && <div className="head-actions">{children}</div>}
    </div>
  );
}

/** Переключатель разделов страницы ссылками на хэш — назад/вперёд браузера работают. */
export function Tabs({ items, value }: { items: Array<[href: string, title: string]>; value: string }) {
  return (
    <nav className="tabs" aria-label="Разделы">
      {items.map(([href, title]) => (
        <a key={href} href={href} className={href === value ? 'active' : ''} aria-current={href === value ? 'page' : undefined}>
          {title}
        </a>
      ))}
    </nav>
  );
}

export function Empty({ title, hint, children }: { title: string; hint?: string; children?: ReactNode }) {
  return (
    <div className="empty">
      <p className="empty-title">{title}</p>
      {hint && <p className="muted">{hint}</p>}
      {children}
    </div>
  );
}

export const Failed = ({ text }: { text: string }) => <div className="card"><p className="error">{text}</p></div>;
