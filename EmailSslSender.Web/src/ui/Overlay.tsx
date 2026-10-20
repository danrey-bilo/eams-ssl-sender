import { createContext, useCallback, useContext, useMemo, useRef, useState, type ReactNode } from 'react';
import { Cancelled, errorText } from '../lib/api';
import { CopyField } from './bits';
import { FormModal } from './Modal';

type Render = (close: (done: boolean) => void) => ReactNode;

interface Overlay {
  /** Показать окно; промис — закрыто ли оно действием (true) или отменой (false). */
  show: (render: Render) => Promise<boolean>;
  confirm: (title: string, note: ReactNode, submit: string, danger?: boolean) => Promise<boolean>;
  showLink: (title: string, note: string, url: string) => Promise<boolean>;
  toast: (text: string, kind?: 'err') => void;
  /** Выполнить действие: удача — уведомление, отказ — его текст; отменённый код — тишина. */
  run: (action: () => Promise<unknown>, done?: string) => Promise<boolean>;
}

const Context = createContext<Overlay | null>(null);

export function useOverlay(): Overlay {
  const value = useContext(Context);
  if (!value) throw new Error('OverlayProvider не подключён');
  return value;
}

interface Toast { id: number; text: string; kind?: 'err' }

/** Окна и уведомления поверх любой страницы — вызываются из кода: `await confirm(...)`. */
export function OverlayProvider({ children }: { children: ReactNode }) {
  const [windows, setWindows] = useState<Array<{ id: number; node: ReactNode }>>([]);
  const [toasts, setToasts] = useState<Toast[]>([]);
  const next = useRef(0);

  const show = useCallback((render: Render) => new Promise<boolean>(resolve => {
    const id = ++next.current;
    const close = (done: boolean) => {
      setWindows(list => list.filter(w => w.id !== id));
      resolve(done);
    };
    setWindows(list => [...list, { id, node: render(close) }]);
  }), []);

  const toast = useCallback((text: string, kind?: 'err') => {
    const id = ++next.current;
    setToasts(list => [...list, { id, text, kind }]);
    setTimeout(() => setToasts(list => list.filter(t => t.id !== id)), kind === 'err' ? 6000 : 3000);
  }, []);

  const run = useCallback(async (action: () => Promise<unknown>, done?: string) => {
    try {
      await action();
      if (done) toast(done);
      return true;
    } catch (err) {
      if (!(err instanceof Cancelled)) toast(errorText(err), 'err');
      return false;
    }
  }, [toast]);

  const value = useMemo<Overlay>(() => ({
    show,
    toast,
    run,
    confirm: (title, note, submit, danger = false) =>
      show(close => <FormModal title={title} note={note} submit={submit} danger={danger} onClose={close} />),
    showLink: (title, note, url) =>
      show(close => (
        <FormModal title={title} note={note} submit="Готово" cancel={null} onClose={close}>
          <CopyField value={url} />
        </FormModal>
      )),
  }), [show, toast, run]);

  return (
    <Context.Provider value={value}>
      {children}
      {windows.map(w => <div key={w.id}>{w.node}</div>)}
      {toasts.length > 0 && (
        <div className="toasts" role="status" aria-live="polite">
          {toasts.map(t => <div key={t.id} className={'toast ' + (t.kind ?? '')}>{t.text}</div>)}
        </div>
      )}
    </Context.Provider>
  );
}
