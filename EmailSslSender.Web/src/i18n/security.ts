import { type Dict } from '../lib/i18n';

/** Страница «Безопасность». */
export const security = {
  // Категории
  'Подтверждение кодом': { uk: 'Підтвердження кодом', en: 'Code confirmation' },
  'Не спрашивать код повторно': { uk: 'Не питати код повторно', en: 'Remember the code for' },
  'После кода из приложения или входа': { uk: 'Після коду із застосунку або входу', en: 'After an app code or sign-in' },
  'Вход': { uk: 'Вхід', en: 'Sign-in' },
  'Попыток до блокировки': { uk: 'Спроб до блокування', en: 'Attempts before lockout' },
  'Неверный пароль или код подряд': { uk: 'Неправильний пароль або код поспіль', en: 'Wrong password or code in a row' },
  'Блокировка': { uk: 'Блокування', en: 'Lockout' },
  'Время на ввод кода': { uk: 'Час на введення коду', en: 'Time to enter the code' },
  'Между паролем и кодом при входе': { uk: 'Між паролем і кодом під час входу', en: 'Between password and code at sign-in' },
  'Сессии': { uk: 'Сесії', en: 'Sessions' },
  'Выход при бездействии': { uk: 'Вихід після бездіяльності', en: 'Sign out after inactivity' },
  'Сессия живёт не дольше': { uk: 'Сесія триває не довше', en: 'Maximum session length' },
  'Потом — вход заново': { uk: 'Потім — вхід заново', en: 'Then sign in again' },
  'Пароли': { uk: 'Паролі', en: 'Passwords' },
  'Минимальная длина': { uk: 'Мінімальна довжина', en: 'Minimum length' },
  'Заглавная буква': { uk: 'Велика літера', en: 'Uppercase letter' },
  'Строчная буква': { uk: 'Мала літера', en: 'Lowercase letter' },
  'Цифра': { uk: 'Цифра', en: 'Digit' },
  'Спецсимвол': { uk: 'Спецсимвол', en: 'Symbol' },
  'Ссылки и письма': { uk: 'Посилання й листи', en: 'Links and emails' },
  'Ссылка приглашения действует': { uk: 'Посилання-запрошення дійсне', en: 'Invitation link valid for' },
  'Писем в час на один адрес': { uk: 'Листів на годину на одну адресу', en: 'Emails per hour per address' },
  'Защита ящиков от потока писем': { uk: 'Захист скриньок від потоку листів', en: 'Protects mailboxes from email floods' },
  'мин': { uk: 'хв', en: 'min' },
  'ч': { uk: 'год', en: 'h' },
  'симв.': { uk: 'симв.', en: 'chars' },

  // Страница
  'Подтверждено': { uk: 'Підтверджено', en: 'Confirmed' },
  'Вход, сессии и пароли. Изменения подтверждаются кодом из приложения.': {
    uk: 'Вхід, сесії та паролі. Зміни підтверджуються кодом із застосунку.',
    en: 'Sign-in, sessions and passwords. Changes are confirmed with an app code.',
  },
  'Код подтверждён до {time}': { uk: 'Код підтверджено до {time}', en: 'Code confirmed until {time}' },
  'Важные действия спросят код': { uk: 'Важливі дії запитуватимуть код', en: 'Important actions will ask for a code' },
  'Скачивание сертификатов и ключей, изменения настроек, людей и подписок.': {
    uk: 'Завантаження сертифікатів і ключів, зміни налаштувань, людей і підписок.',
    en: 'Downloading certificates and keys, changes to settings, people and subscriptions.',
  },
  'Категории': { uk: 'Категорії', en: 'Categories' },
  'Несохранённые изменения': { uk: 'Незбережені зміни', en: 'Unsaved changes' },
  'Изменено: {n}': { uk: 'Змінено: {n}', en: 'Changed: {n}' },
  'Отменить': { uk: 'Скасувати', en: 'Discard' },
} satisfies Dict;
