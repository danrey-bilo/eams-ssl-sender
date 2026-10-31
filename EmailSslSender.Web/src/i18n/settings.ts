import { type Dict } from '../lib/i18n';

/** Настройки: почта и Key Manager. */
export const settings = {
  'Откуда берутся сертификаты и как уходят письма': {
    uk: 'Звідки беруться сертифікати й як надсилаються листи',
    en: 'Where certificates come from and how emails are sent',
  },
  'Почта': { uk: 'Пошта', en: 'Mail' },

  // Почта
  'Без шифрования': { uk: 'Без шифрування', en: 'No encryption' },
  'Есть неотправленные': { uk: 'Є ненадіслані', en: 'Some emails failed' },
  'Работает': { uk: 'Працює', en: 'Working' },
  'Не настроена': { uk: 'Не налаштовано', en: 'Not configured' },
  'Сервер принял письмо': { uk: 'Сервер прийняв лист', en: 'The server accepted the email' },
  'Снять пароль SMTP?': { uk: 'Зняти пароль SMTP?', en: 'Remove the SMTP password?' },
  'Почта будет уходить без авторизации.': {
    uk: 'Пошта надсилатиметься без авторизації.',
    en: 'Mail will be sent without authentication.',
  },
  'Снять': { uk: 'Зняти', en: 'Remove' },
  'Пароль снят': { uk: 'Пароль знято', en: 'Password removed' },
  'Тестовое письмо': { uk: 'Тестовий лист', en: 'Test email' },
  'В очереди': { uk: 'У черзі', en: 'Queued' },
  'Не ушло': { uk: 'Не надіслано', en: 'Failed' },
  'Сервер изменился — введите пароль заново.': {
    uk: 'Сервер змінився — введіть пароль ще раз.',
    en: 'The server has changed — enter the password again.',
  },
  'Отправлять письма': { uk: 'Надсилати листи', en: 'Send emails' },
  'Сервер': { uk: 'Сервер', en: 'Server' },
  'Порт': { uk: 'Порт', en: 'Port' },
  'Защита': { uk: 'Захист', en: 'Security' },
  'Логин': { uk: 'Логін', en: 'Username' },
  'Пароль': { uk: 'Пароль', en: 'Password' },
  '•••••••• задан': { uk: '•••••••• задано', en: '•••••••• set' },
  'Адрес отправителя': { uk: 'Адреса відправника', en: 'Sender address' },
  'Имя отправителя': { uk: 'Ім’я відправника', en: 'Sender name' },
  'Адрес панели': { uk: 'Адреса панелі', en: 'Panel address' },
  'Для ссылок в приглашениях и сбросе пароля': {
    uk: 'Для посилань у запрошеннях і скиданні пароля',
    en: 'For links in invitations and password resets',
  },
  'Снять пароль': { uk: 'Зняти пароль', en: 'Remove password' },
  'Отправить': { uk: 'Надіслати', en: 'Send' },
  'Кому': { uk: 'Кому', en: 'To' },

  // Key Manager
  'Не настроен': { uk: 'Не налаштовано', en: 'Not configured' },
  'Выключен': { uk: 'Вимкнено', en: 'Off' },
  'Ошибка': { uk: 'Помилка', en: 'Error' },
  'Ждёт первого опроса': { uk: 'Очікує першого опитування', en: 'Waiting for the first poll' },
  'Связь есть · сертификатов: {n}': { uk: 'Зв’язок є · сертифікатів: {n}', en: 'Connected · certificates: {n}' },
  'Опрос запущен': { uk: 'Опитування запущено', en: 'Poll started' },
  'Снять ключ API?': { uk: 'Зняти ключ API?', en: 'Remove the API key?' },
  'Опрос и рассылка остановятся, пока ключ не задан снова.': {
    uk: 'Опитування й розсилка зупиняться, доки ключ не задано знову.',
    en: 'Polling and delivery will stop until the key is set again.',
  },
  'Ключ снят': { uk: 'Ключ знято', en: 'Key removed' },
  'Проверить': { uk: 'Перевірити', en: 'Test' },
  'Опросить': { uk: 'Опитати', en: 'Poll now' },
  'Опрос': { uk: 'Опитування', en: 'Last poll' },
  'Сертификатов': { uk: 'Сертифікатів', en: 'Certificates' },
  'Адрес изменился — введите ключ API заново.': {
    uk: 'Адреса змінилася — введіть ключ API ще раз.',
    en: 'The address has changed — enter the API key again.',
  },
  'Подключение': { uk: 'Підключення', en: 'Connection' },
  'Опрашивать': { uk: 'Опитувати', en: 'Enable polling' },
  'Адрес': { uk: 'Адреса', en: 'Address' },
  'Имя сервера из его сертификата, а не IP': {
    uk: 'Ім’я сервера з його сертифіката, а не IP',
    en: 'The server name from its certificate, not an IP',
  },
  'Опрос, мин': { uk: 'Опитування, хв', en: 'Poll interval, min' },
  'Ключ API': { uk: 'Ключ API', en: 'API key' },
  'Снять ключ': { uk: 'Зняти ключ', en: 'Remove key' },
} satisfies Dict;
