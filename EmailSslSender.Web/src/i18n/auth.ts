import { type Dict } from '../lib/i18n';

/** Вход, второй фактор, обязательная смена пароля, страницы ссылок из писем. */
export const auth = {
  'Вход': { uk: 'Вхід', en: 'Sign in' },
  'Почта': { uk: 'Пошта', en: 'Email' },
  'Пароль': { uk: 'Пароль', en: 'Password' },
  'Войти': { uk: 'Увійти', en: 'Sign in' },
  'Забыли пароль?': { uk: 'Забули пароль?', en: 'Forgot password?' },
  'Сброс пароля': { uk: 'Скидання пароля', en: 'Password reset' },
  'Если адрес есть в системе, ссылка придёт на почту.': {
    uk: 'Якщо адреса є в системі, посилання надійде на пошту.',
    en: 'If this address is in the system, a link will arrive by email.',
  },
  'Отправить ссылку': { uk: 'Надіслати посилання', en: 'Send link' },
  'Назад': { uk: 'Назад', en: 'Back' },
  'Время на ввод кода вышло. Войдите снова.': {
    uk: 'Час на введення коду минув. Увійдіть знову.',
    en: 'Time to enter the code is up. Please sign in again.',
  },
  'Подтверждение входа': { uk: 'Підтвердження входу', en: 'Confirm sign-in' },
  'Код из письма': { uk: 'Код із листа', en: 'Code from the email' },
  'Код из приложения-аутентификатора': { uk: 'Код із застосунку-автентифікатора', en: 'Code from your authenticator app' },
  'Код отправлен на почту.': { uk: 'Код надіслано на пошту.', en: 'The code has been sent to your email.' },
  'Нет доступа к приложению — попросите другого администратора сбросить второй фактор.': {
    uk: 'Немає доступу до застосунку — попросіть іншого адміністратора скинути другий фактор.',
    en: 'No access to the app? Ask another administrator to reset your second factor.',
  },
  'Отправить ещё раз': { uk: 'Надіслати ще раз', en: 'Send again' },
  'Код на почту': { uk: 'Код на пошту', en: 'Email me a code' },
  'Аутентификатор': { uk: 'Автентифікатор', en: 'Authenticator' },
  'Отсканируйте код в Google Authenticator, Aegis или 1Password и введите шесть цифр.': {
    uk: 'Відскануйте код у Google Authenticator, Aegis або 1Password і введіть шість цифр.',
    en: 'Scan the code with Google Authenticator, Aegis or 1Password and enter the six digits.',
  },
  'QR-код для приложения-аутентификатора': { uk: 'QR-код для застосунку-автентифікатора', en: 'QR code for the authenticator app' },
  'Подключить': { uk: 'Підключити', en: 'Connect' },
  'Пароли не совпадают.': { uk: 'Паролі не збігаються.', en: 'Passwords do not match.' },
  'Новый пароль': { uk: 'Новий пароль', en: 'New password' },
  'Администратор попросил задать новый пароль.': {
    uk: 'Адміністратор попросив установити новий пароль.',
    en: 'An administrator has asked you to set a new password.',
  },
  'Текущий пароль': { uk: 'Поточний пароль', en: 'Current password' },
  'Ещё раз': { uk: 'Ще раз', en: 'Repeat' },
  'Сменить пароль': { uk: 'Змінити пароль', en: 'Change password' },
  'Добро пожаловать': { uk: 'Ласкаво просимо', en: 'Welcome' },
  'Ссылка недействительна или уже использована. Попросите новую у администратора.': {
    uk: 'Посилання недійсне або вже використане. Попросіть нове в адміністратора.',
    en: 'The link is invalid or has already been used. Ask an administrator for a new one.',
  },
  'Ссылка недействительна или уже использована. Запросите новую.': {
    uk: 'Посилання недійсне або вже використане. Запросіть нове.',
    en: 'The link is invalid or has already been used. Request a new one.',
  },
  'Пароль задан. При первом входе подключите приложение-аутентификатор.': {
    uk: 'Пароль установлено. Під час першого входу підключіть застосунок-автентифікатор.',
    en: 'Password set. Connect an authenticator app when you first sign in.',
  },
  'Пароль изменён, прежние сессии закрыты.': {
    uk: 'Пароль змінено, попередні сесії завершено.',
    en: 'Password changed; previous sessions have been closed.',
  },
  'Придумайте новый пароль.': { uk: 'Придумайте новий пароль.', en: 'Choose a new password.' },
  'Задать пароль': { uk: 'Установити пароль', en: 'Set password' },
} satisfies Dict;
