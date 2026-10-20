import { useEffect, useState } from 'react';
import { api } from '../lib/api';
import type { TwoFactorStatus } from '../lib/types';
import { CodeInput } from './bits';
import { FormModal } from './Modal';

/** Окно свежего второго фактора: опасное действие повторится после кода. */
export function StepUpDialog({ onClose }: { onClose: (done: boolean) => void }) {
  const [code, setCode] = useState('');
  const [viaEmail, setViaEmail] = useState(false);
  const [sent, setSent] = useState(false);

  useEffect(() => {
    api.get<TwoFactorStatus>('/api/me/two-factor')
      .then(f => setViaEmail((f.available_methods ?? []).includes('email')), () => null);
  }, []);

  return (
    <FormModal
      title="Подтвердите действие"
      note="Введите код из приложения-аутентификатора."
      submit="Подтвердить"
      onSubmit={() => api.stepUp(code.trim())}
      onClose={onClose}
    >
      <CodeInput value={code} onChange={setCode} />
      {viaEmail && (
        <div>
          <button type="button" className="link" onClick={() => api.post('/api/me/step-up/email').then(() => setSent(true), () => null)}>
            Прислать код на почту
          </button>
        </div>
      )}
      {sent && <p className="muted small">Код отправлен на почту.</p>}
    </FormModal>
  );
}
