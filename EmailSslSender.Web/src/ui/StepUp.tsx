import { useState } from 'react';
import { api } from '../lib/api';
import { CodeInput } from './bits';
import { FormModal } from './Modal';

/** Окно свежего кода: опасное действие повторится само, когда код подойдёт. */
export function StepUpDialog({ minutes, onClose }: { minutes?: number; onClose: (done: boolean) => void }) {
  const [code, setCode] = useState('');

  return (
    <FormModal
      title="Подтвердите кодом"
      note={'Код из приложения-аутентификатора. ' + (minutes ? `Следующие ${minutes} мин спрашивать не будем.` : '')}
      submit="Подтвердить"
      onSubmit={() => api.stepUp(code)}
      onClose={onClose}
    >
      <CodeInput value={code} onChange={setCode} />
    </FormModal>
  );
}
