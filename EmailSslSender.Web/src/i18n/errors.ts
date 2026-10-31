import { lang } from '../lib/i18n';

type Text = { uk: string; en: string };

// Отказы сервера приходят по-русски; украинский и английский — по коду, у ошибки поля — по полю.

const CODES: Record<string, Text> = {
  INTERNAL_ERROR: { uk: 'Внутрішня помилка сервера.', en: 'Internal server error.' },
  VALIDATION_ERROR: { uk: 'Перевірте введені дані.', en: 'Please check the entered data.' },
  UNAUTHENTICATED: { uk: 'Потрібно увійти.', en: 'Please sign in.' },
  PERMISSION_DENIED: { uk: 'Недостатньо прав.', en: 'Permission denied.' },
  PERMISSION_UNKNOWN: { uk: 'Недостатньо прав.', en: 'Permission denied.' },
  PERMISSION_ESCALATION: { uk: 'Недостатньо прав.', en: 'Permission denied.' },
  SUPERUSER_FLAG_FORBIDDEN: { uk: 'Недостатньо прав.', en: 'Permission denied.' },
  NOT_FOUND: { uk: 'Не знайдено.', en: 'Not found.' },
  NOT_READY: { uk: 'Сервіс ще запускається, спробуйте за хвилину.', en: 'The service is still starting; try again in a minute.' },
  RATE_LIMITED: { uk: 'Забагато спроб. Зачекайте трохи й повторіть.', en: 'Too many attempts. Wait a little and try again.' },
  PAYLOAD_TOO_LARGE: { uk: 'Запит завеликий.', en: 'The request is too large.' },
  METHOD_NOT_ALLOWED: { uk: 'Ця дія недоступна.', en: 'This action is not allowed.' },
  INVALID_CURSOR: { uk: 'Список змінився — оновіть сторінку.', en: 'The list has changed; reload the page.' },
  BUILD_NOT_TRUSTED: { uk: 'Збірка сервера не довірена.', en: 'The server build is not trusted.' },
  PASSWORD_CHANGE_REQUIRED: { uk: 'Спочатку змініть пароль.', en: 'Change your password first.' },
  TWO_FACTOR_ENROLLMENT_REQUIRED: { uk: 'Спочатку підключіть автентифікатор.', en: 'Set up your authenticator first.' },
  API_TOKEN_NOT_ALLOWED: { uk: 'Ця дія недоступна для ключа API.', en: 'This action is not available with an API key.' },
  SESSION_EXPIRED: { uk: 'Сесія завершилася. Увійдіть знову.', en: 'Your session has ended. Please sign in again.' },
  SESSION_REVOKED: { uk: 'Сесію завершено. Увійдіть знову.', en: 'Your session was ended. Please sign in again.' },
  TOKEN_EXPIRED: { uk: 'Сесія завершилася. Увійдіть знову.', en: 'Your session has ended. Please sign in again.' },
  USER_INACTIVE: { uk: 'Обліковий запис вимкнено.', en: 'The account is disabled.' },
  DEVICE_REVOKED: { uk: 'Пристрій відкликано.', en: 'The device was revoked.' },
  SECOND_FACTOR_REQUIRED: { uk: 'Підтвердьте дію кодом.', en: 'Confirm the action with a code.' },
  QUOTA_EXCEEDED: { uk: 'Перевищено ліміт.', en: 'Limit exceeded.' },
  PASSWORD_POLICY_VIOLATION: { uk: 'Пароль не відповідає вимогам.', en: 'The password does not meet the requirements.' },
  MAIL_NOT_CONFIGURED: { uk: 'Пошту не налаштовано.', en: 'Mail is not configured.' },
  INVALID_EXPIRY: { uk: 'Неправильний строк дії.', en: 'Invalid expiry.' },
  INVALID_CREDENTIALS: { uk: 'Неправильна пошта або пароль.', en: 'Wrong email or password.' },
  USER_ALREADY_EXISTS: { uk: 'Користувач із такою поштою вже є.', en: 'A user with this email already exists.' },
  USER_ALREADY_ACTIVATED: { uk: 'Обліковий запис уже активовано.', en: 'The account is already activated.' },
  ACTIVATION_TOKEN_INVALID: { uk: 'Посилання недійсне або застаріло.', en: 'The link is invalid or has expired.' },
  LAST_SUPERADMIN: { uk: 'Не можна вимкнути останнього адміністратора.', en: 'You cannot disable the last administrator.' },
  TWO_FACTOR_REQUIRED: { uk: 'Потрібен код автентифікатора.', en: 'An authenticator code is required.' },
  TWO_FACTOR_INVALID: { uk: 'Код не підійшов.', en: 'The code is incorrect.' },
  TWO_FACTOR_ALREADY_ENABLED: { uk: 'Автентифікатор уже підключено.', en: 'The authenticator is already set up.' },
  TWO_FACTOR_NOT_ENROLLED: { uk: 'Автентифікатор не підключено.', en: 'No authenticator is set up.' },
  TWO_FACTOR_UNAVAILABLE: { uk: 'Підключення автентифікатора недоступне.', en: 'Authenticator setup is unavailable.' },
  MAIL_SEND_FAILED: { uk: 'Лист не надіслано.', en: 'The email could not be sent.' },
  UNKNOWN_SETTING: { uk: 'Невідоме налаштування.', en: 'Unknown setting.' },
  INVALID_SETTING_VALUE: { uk: 'Неправильне значення налаштування.', en: 'Invalid setting value.' },
  SETTING_LEVEL_FORBIDDEN: { uk: 'Це налаштування тут не змінюється.', en: 'This setting cannot be changed here.' },
  KEYMANAGER_NOT_CONFIGURED: {
    uk: 'Key Manager Plus не налаштовано: вкажіть адресу та ключ API.',
    en: 'Key Manager Plus is not configured: set the address and the API key.',
  },
  KEYMANAGER_UNAVAILABLE: { uk: 'Key Manager Plus недоступний.', en: 'Key Manager Plus is unavailable.' },
  KEYMANAGER_CERTIFICATE_NOT_FOUND: { uk: 'Сертифіката немає в Key Manager Plus.', en: 'The certificate is not in Key Manager Plus.' },
  DELIVERY_IN_PROGRESS: {
    uk: 'Цю підписку зараз надсилає опитування — повторіть за хвилину.',
    en: 'This subscription is being sent right now; try again in a minute.',
  },
  NETWORK: { uk: 'Сервер недоступний.', en: 'The server is unavailable.' },
};

/** Ошибка поля подписки — по имени поля. */
const FIELDS: Record<string, Text> = {
  common_name: { uk: 'Перевірте сертифікати.', en: 'Check the certificates.' },
  common_names: { uk: 'Перевірте сертифікати.', en: 'Check the certificates.' },
  recipients: { uk: 'Перевірте адреси одержувачів: від 1 до 20.', en: 'Check the recipient addresses: 1 to 20.' },
  format: { uk: 'Перевірте формати файлів.', en: 'Check the file formats.' },
  formats: { uk: 'Перевірте формати файлів.', en: 'Check the file formats.' },
  interval_days: { uk: 'Повтор — від 1 до 365 днів.', en: 'Repeat every 1 to 365 days.' },
  locale: { uk: 'Мова листа — українська, російська або англійська.', en: 'The email language must be Ukrainian, Russian or English.' },
  description: { uk: 'Опис — до 500 знаків.', en: 'The description is limited to 500 characters.' },
  send_time: { uk: 'Час надсилання — у вигляді ГГ:ХХ.', en: 'The send time must be HH:MM.' },
  time_zone: { uk: 'Невідомий часовий пояс.', en: 'Unknown time zone.' },
  active_until: { uk: 'Останній день раніше за перший.', en: 'The last day is before the first one.' },
  on_change: { uk: 'Оберіть, коли надсилати.', en: 'Choose when to send.' },
  serial_number: { uk: 'Потрібен серійний номер сертифіката.', en: 'A certificate serial number is required.' },
};

/** Текст отказа на языке интерфейса; русский — как прислал сервер. */
export function serverText(code: string | undefined, message: string, field: unknown): string {
  const current = lang();
  if (current === 'ru' || !code) return message;
  const byField = code === 'VALIDATION_ERROR' && typeof field === 'string' ? FIELDS[field] : undefined;
  return (byField ?? CODES[code])?.[current] ?? message;
}
