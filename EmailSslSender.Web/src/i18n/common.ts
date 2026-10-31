import { translator, type Dict } from '../lib/i18n';

/** Общие фразы: каркас, окна, кнопки, даты. */
export const common = {
  'Рассылка': { uk: 'Розсилка', en: 'Delivery' },
  'Сертификаты': { uk: 'Сертифікати', en: 'Certificates' },
  'Пользователи': { uk: 'Користувачі', en: 'Users' },
  'Настройки': { uk: 'Налаштування', en: 'Settings' },
  'Безопасность': { uk: 'Безпека', en: 'Security' },
  'Профиль': { uk: 'Профіль', en: 'Profile' },
  'Разделы': { uk: 'Розділи', en: 'Sections' },
  'Выйти': { uk: 'Вийти', en: 'Sign out' },
  'Действия': { uk: 'Дії', en: 'Actions' },
  'Загрузка': { uk: 'Завантаження', en: 'Loading' },
  'Сохранить': { uk: 'Зберегти', en: 'Save' },
  'Сохранено': { uk: 'Збережено', en: 'Saved' },
  'Отмена': { uk: 'Скасувати', en: 'Cancel' },
  'Готово': { uk: 'Готово', en: 'Done' },
  'Да': { uk: 'Так', en: 'Yes' },
  'Код': { uk: 'Код', en: 'Code' },
  'Скопировано': { uk: 'Скопійовано', en: 'Copied' },
  'Копировать': { uk: 'Копіювати', en: 'Copy' },
  'Подтвердите кодом': { uk: 'Підтвердьте кодом', en: 'Confirm with a code' },
  'Код из приложения-аутентификатора.': { uk: 'Код із застосунку-автентифікатора.', en: 'A code from your authenticator app.' },
  'Следующие {minutes} мин спрашивать не будем.': {
    uk: 'Наступні {minutes} хв не питатимемо.',
    en: "We won't ask again for the next {minutes} min.",
  },
  'Подтвердить': { uk: 'Підтвердити', en: 'Confirm' },
  'Сервер недоступен.': { uk: 'Сервер недоступний.', en: 'The server is unavailable.' },
  'Что-то пошло не так.': { uk: 'Щось пішло не так.', en: 'Something went wrong.' },
  'Сессия закончилась. Войдите снова.': { uk: 'Сесія завершилася. Увійдіть знову.', en: 'Your session has ended. Please sign in again.' },
  'от {n} символов': { uk: 'від {n} символів', en: 'at least {n} characters' },
  'заглавная': { uk: 'велика літера', en: 'an uppercase letter' },
  'строчная': { uk: 'мала літера', en: 'a lowercase letter' },
  'цифра': { uk: 'цифра', en: 'a digit' },
  'спецсимвол': { uk: 'спецсимвол', en: 'a symbol' },
  'Неизвестное устройство': { uk: 'Невідомий пристрій', en: 'Unknown device' },
  'Браузер': { uk: 'Браузер', en: 'Browser' },
} satisfies Dict;

export const t = translator(common);
