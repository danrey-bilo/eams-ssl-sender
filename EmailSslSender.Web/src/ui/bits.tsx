import { useState, type ReactNode } from 'react';

export type DotKind = '' | 'ok' | 'err' | 'warn';

export const Dot = ({ kind = '' }: { kind?: DotKind }) => <span className={'dot ' + kind} />;

export function Status({ kind, children, strong }: { kind: DotKind; children: ReactNode; strong?: boolean }) {
  return <span className={'status' + (strong ? ' strong' : '')}><Dot kind={kind} />{children}</span>;
}

export const Spinner = () => <div className="spinner" role="status" aria-label="Загрузка" />;

export function Field({ label, children }: { label: string; children: ReactNode }) {
  return <label className="field"><span>{label}</span>{children}</label>;
}

/** Группа без <label>: внутри свои переключатели, а метки внутри метки HTML не допускает. */
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

export function CopyField({ value }: { value: string }) {
  const [copied, setCopied] = useState(false);
  return (
    <div className="copy">
      <input type="text" readOnly value={value} onFocus={e => e.target.select()} />
      <button
        type="button"
        onClick={async () => {
          try { await navigator.clipboard.writeText(value); } catch { /* небезопасный контекст: выделено, Ctrl+C */ }
          setCopied(true);
        }}
      >
        {copied ? 'Скопировано' : 'Копировать'}
      </button>
    </div>
  );
}

export const ErrorText = ({ text }: { text: string }) => (text ? <p className="error" role="alert">{text}</p> : null);

export function PageHead({ title, children }: { title: string; children?: ReactNode }) {
  return <div className="head"><h1>{title}</h1><div className="inline">{children}</div></div>;
}
