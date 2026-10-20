import { useCallback, useEffect, useState } from 'react';
import { Cancelled, errorText } from './api';

/** Данные страницы: загрузка, ошибка и перезагрузка после действий. */
export function useLoad<T>(load: () => Promise<T>, deps: unknown[] = []) {
  const [data, setData] = useState<T | null>(null);
  const [error, setError] = useState('');
  const [version, setVersion] = useState(0);

  useEffect(() => {
    let alive = true;
    load().then(
      value => { if (alive) { setData(value); setError(''); } },
      err => { if (alive && !(err instanceof Cancelled)) setError(errorText(err)); },
    );
    return () => { alive = false; };
  }, [version, ...deps]);

  const reload = useCallback(() => setVersion(v => v + 1), []);

  return { data, error, reload };
}

/** Текущий маршрут панели из хэша: `#/settings/mail` → `['settings', 'mail']`. */
export function useHashRoute(): string[] {
  const read = () => location.hash.replace(/^#\//, '').split('/').filter(Boolean);
  const [route, setRoute] = useState(read);

  useEffect(() => {
    const changed = () => setRoute(read());
    window.addEventListener('hashchange', changed);
    return () => window.removeEventListener('hashchange', changed);
  }, []);

  return route;
}
