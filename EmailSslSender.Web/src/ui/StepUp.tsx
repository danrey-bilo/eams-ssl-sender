import { useState } from 'react';
import { t } from '../i18n/common';
import { api } from '../lib/api';
import { CodeInput } from './bits';
import { FormModal } from './Modal';

/** Окно свежего кода: опасное действие повторится само, когда код подойдёт. */
export function StepUpDialog({ minutes, onClose }: { minutes?: number; onClose: (done: boolean) => void }) {
  const [code, setCode] = useState('');

  return (
    <FormModal
      title={t('Подтвердите кодом')}
      note={t('Код из приложения-аутентификатора.') + (minutes ? ' ' + t('Следующие {minutes} мин спрашивать не будем.', { minutes }) : '')}
      submit={t('Подтвердить')}
      onSubmit={() => api.stepUp(code)}
      onClose={onClose}
    >
      <CodeInput value={code} onChange={setCode} />
    </FormModal>
  );
}
