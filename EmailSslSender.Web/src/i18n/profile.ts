import { type Dict } from '../lib/i18n';

/** Профиль: имя, пароль, второй фактор, сессии. */
export const profile = {
  'Второй фактор': { uk: 'Другий фактор', en: 'Second factor' },
  'Приложение-аутентификатор подключено': { uk: 'Застосунок-автентифікатор підключено', en: 'Authenticator app connected' },
  'Не подключён': { uk: 'Не підключено', en: 'Not connected' },
  'Сессии': { uk: 'Сесії', en: 'Sessions' },
  'эта': { uk: 'ця', en: 'current' },
  'Завершить': { uk: 'Завершити', en: 'End session' },
  'Сессия завершена': { uk: 'Сесію завершено', en: 'Session ended' },
  'Почта': { uk: 'Пошта', en: 'Email' },
  'Имя': { uk: 'Ім’я', en: 'Name' },
  'Пароли не совпадают.': { uk: 'Паролі не збігаються.', en: 'Passwords do not match.' },
  'Пароль изменён': { uk: 'Пароль змінено', en: 'Password changed' },
  'Пароль': { uk: 'Пароль', en: 'Password' },
  'Текущий': { uk: 'Поточний', en: 'Current' },
  'Новый': { uk: 'Новий', en: 'New' },
  'Ещё раз': { uk: 'Ще раз', en: 'Repeat' },
  'Сменить': { uk: 'Змінити', en: 'Change' },
} satisfies Dict;
