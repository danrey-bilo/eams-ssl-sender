import { common } from '../i18n/common';
import { language } from '../i18n/language';
import { api, Cancelled, errorText } from '../lib/api';
import { LANGS, setLang, translator, useLang, type Lang } from '../lib/i18n';
import { useOverlay } from '../ui/Overlay';

const t = translator({ ...common, ...language });

/** Язык интерфейса в профиле — личное предпочтение, без кода подтверждения. */
export function LanguageCard() {
  const current = useLang();
  const { toast } = useOverlay();

  const choose = async (next: Lang) => {
    try {
      await api.patch('/api/me/preferences', { values: { 'ui.locale': next } });
      setLang(next);
      toast(t('Язык сохранён'));
    } catch (err) {
      if (!(err instanceof Cancelled)) toast(errorText(err), 'err');
    }
  };

  return (
    <section className="card">
      <h2>{t('Язык интерфейса')}</h2>
      <div className="stack">
        <div className="stack tight" role="radiogroup" aria-label={t('Язык интерфейса')}>
          {LANGS.map(([code, name]) => (
            <label key={code} className="check">
              <input type="radio" name="lang" checked={current === code} onChange={() => void choose(code)} />
              {name}
            </label>
          ))}
        </div>
        <p className="muted small">{t('Только для вас: у каждого пользователя свой язык.')}</p>
      </div>
    </section>
  );
}
