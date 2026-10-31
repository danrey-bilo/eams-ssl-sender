// Тексты ошибок, которые сервис хранит по-русски (last_error подписки, опрос Key Manager, почта), — на языке
// интерфейса. Имена, адреса и ответы серверов внутри текста не переводятся; незнакомый текст остаётся как есть.

type Lang = 'ru' | 'uk' | 'en';
type Text = { uk: string; en: string };

// Шаблон без последней точки; $1… — подстановки (сами переводятся, если в них есть русский текст).
const RULES: Array<[RegExp, Text]> = [
  [/^Сертификата (.+) нет в Key Manager Plus$/, { uk: 'Сертифіката $1 немає в Key Manager Plus', en: 'Certificate $1 is not in Key Manager Plus' }],
  [/^Сертификатов (.+) нет в Key Manager Plus$/, { uk: 'Сертифікатів $1 немає в Key Manager Plus', en: 'Certificates $1 are not in Key Manager Plus' }],
  [/^(.+): в Key Manager Plus только выпуск старее отправленного \(серийный (.+)\) — сам не отправляется, отправьте вручную, если это верно$/, {
    uk: '$1: у Key Manager Plus лише випуск, старіший за надісланий (серійний $2), — сам не надсилається; надішліть вручну, якщо це правильно',
    en: '$1: Key Manager Plus only has an issue older than the one sent (serial $2) — it is not sent automatically; send it manually if that is correct',
  }],
  [/^Почта не настроена: письма с сертификатом некуда отправить$/, {
    uk: 'Пошту не налаштовано: листи із сертифікатом нікуди надсилати',
    en: 'Mail is not configured: there is nowhere to send the certificate emails',
  }],
  [/^Почта настроена без шифрования \(mail\.smtp_security = none\): закрытый ключ и его пароль по ней не отправляются\. Выберите tls или starttls в настройках почты или уберите из подписки KEY, PKCS, JKS и пароль ключа$/, {
    uk: 'Пошту налаштовано без шифрування (mail.smtp_security = none): закритий ключ і його пароль нею не надсилаються. Оберіть tls або starttls у налаштуваннях пошти або приберіть із підписки KEY, PKCS, JKS і пароль ключа',
    en: 'Mail is set up without encryption (mail.smtp_security = none): the private key and its passphrase are not sent over it. Choose tls or starttls in the mail settings or remove KEY, PKCS, JKS and the key passphrase from the subscription',
  }],
  [/^Почта отказала в постановке письма: (.+)$/, { uk: 'Пошта не поставила лист у чергу: $1', en: 'Mail refused to queue the email: $1' }],
  [/^Отправка не удалась: (.+)$/, { uk: 'Надсилання не вдалося: $1', en: 'Sending failed: $1' }],
  [/^Письмо не доставлено: (.+)\. Отправьте повторно, когда причина устранена$/, {
    uk: 'Лист не доставлено: $1. Надішліть повторно, коли причину усунено',
    en: 'The email was not delivered: $1. Send it again once the cause is fixed',
  }],
  [/^Письмо не доставлено: (.+)$/, { uk: 'Лист не доставлено: $1', en: 'The email was not delivered: $1' }],
  [/^Опрос не удался: (.+)$/, { uk: 'Опитування не вдалося: $1', en: 'The poll failed: $1' }],
  [/^Key Manager Plus недоступен \(([^)]+)\): (.+)$/, { uk: 'Key Manager Plus недоступний ($1): $2', en: 'Key Manager Plus is unavailable ($1): $2' }],
  [/^имя сервера не разрешается$/, { uk: 'ім’я сервера не розпізнається', en: 'the server name does not resolve' }],
  [/^соединение не устанавливается$/, { uk: 'з’єднання не встановлюється', en: 'the connection cannot be established' }],
  [/^TLS не согласован — сертификат сервера не прошёл проверку \(цепочка, имя, отзыв\): нужен сертификат удостоверяющего центра на имя из адреса, самоподписанный не принимается$/, {
    uk: 'TLS не узгоджено — сертифікат сервера не пройшов перевірку (ланцюжок, ім’я, відкликання): потрібен сертифікат центру сертифікації на ім’я з адреси, самопідписаний не приймається',
    en: 'TLS failed — the server certificate did not pass validation (chain, name, revocation): a certificate authority certificate for the name in the address is required; a self-signed one is not accepted',
  }],
  [/^прокси не пропустил соединение$/, { uk: 'проксі не пропустив з’єднання', en: 'the proxy did not let the connection through' }],
  [/^сбой HTTP \((.+)\)$/, { uk: 'збій HTTP ($1)', en: 'HTTP failure ($1)' }],
  [/^Key Manager Plus не ответил вовремя \((.+)\)$/, { uk: 'Key Manager Plus не відповів вчасно ($1)', en: 'Key Manager Plus did not respond in time ($1)' }],
  [/^Key Manager Plus не принял ключ API \((.+)\)$/, { uk: 'Key Manager Plus не прийняв ключ API ($1)', en: 'Key Manager Plus did not accept the API key ($1)' }],
  [/^Key Manager Plus перенаправляет запрос \((\d+)\): проверьте адрес — по перенаправлениям ключ API не отправляется$/, {
    uk: 'Key Manager Plus перенаправляє запит ($1): перевірте адресу — за перенаправленнями ключ API не надсилається',
    en: 'Key Manager Plus redirects the request ($1): check the address — the API key is never sent along redirects',
  }],
  [/^Key Manager Plus ответил не объектом JSON$/, { uk: 'Key Manager Plus відповів не об’єктом JSON', en: 'Key Manager Plus did not answer with a JSON object' }],
  [/^Key Manager Plus ответил не JSON$/, { uk: 'Key Manager Plus відповів не JSON', en: 'Key Manager Plus did not answer with JSON' }],
  [/^Key Manager Plus ответил на запрос списка сертификатов без поля details — формат ответа не распознан \(поля ответа: (.+)\)$/, {
    uk: 'Key Manager Plus відповів на запит списку сертифікатів без поля details — формат відповіді не розпізнано (поля відповіді: $1)',
    en: 'Key Manager Plus answered the certificate list request without the details field — the response format is not recognised (response fields: $1)',
  }],
  [/^Key Manager Plus ответил (\d+) на ([^:]+)(: .+)?$/, { uk: 'Key Manager Plus відповів $1 на $2$3', en: 'Key Manager Plus answered $1 to $2$3' }],
  [/^Key Manager Plus отказал \(([^)]+)\): (.+)$/, { uk: 'Key Manager Plus відмовив ($1): $2', en: 'Key Manager Plus refused ($1): $2' }],
  [/^статус «нет»$/, { uk: 'статус «немає»', en: 'status «none»' }],
  [/^статус «(.*)»$/, { uk: 'статус «$1»', en: 'status «$1»' }],
  [/^Key Manager Plus вернул (\d+) записей о сертификатах, но ни в одной нет полей «Common Name» и «serialNumber» — формат ответа не распознан \(поля записи: (.+)\)$/, {
    uk: 'Key Manager Plus повернув записів про сертифікати: $1, але в жодному немає полів «Common Name» і «serialNumber» — формат відповіді не розпізнано (поля запису: $2)',
    en: 'Key Manager Plus returned $1 certificate records, but none has the «Common Name» and «serialNumber» fields — the response format is not recognised (record fields: $2)',
  }],
  [/^Key Manager Plus вернул пустой файл (\S+) для сертификата (.+)$/, { uk: 'Key Manager Plus повернув порожній файл $1 для сертифіката $2', en: 'Key Manager Plus returned an empty $1 file for certificate $2' }],
  [/^Key Manager Plus вернул не файл сертификата для (\S+) (\S+) \((.+), (\d+) байт\)$/, {
    uk: 'Key Manager Plus повернув не файл сертифіката для $1 $2 ($3, $4 байт)',
    en: 'Key Manager Plus returned something other than a certificate file for $1 $2 ($3, $4 bytes)',
  }],
  [/^keymanager\.base_url должен быть адресом https:\/\/: ключ API едет в заголовке, и по открытому HTTP его прочтёт любой на пути$/, {
    uk: 'keymanager.base_url має бути адресою https://: ключ API передається в заголовку, і відкритим HTTP його прочитає будь-хто на шляху',
    en: 'keymanager.base_url must be an https:// address: the API key travels in a header, and over plain HTTP anyone on the way could read it',
  }],
  [/^Ключ API задан до привязки к адресу\. Выполните docker compose run --rm migrate — команда привяжет его к текущему адресу Key Manager Plus — или введите ключ заново$/, {
    uk: 'Ключ API задано до прив’язки до адреси. Виконайте docker compose run --rm migrate — команда прив’яже його до поточної адреси Key Manager Plus — або введіть ключ заново',
    en: 'The API key was set before it was bound to an address. Run docker compose run --rm migrate — it binds the key to the current Key Manager Plus address — or enter the key again',
  }],
  [/^Ключ API вводили для другого адреса Key Manager Plus: адрес сменился после ввода ключа\. Введите ключ API заново — он уходит только туда, для чего его ввели$/, {
    uk: 'Ключ API вводили для іншої адреси Key Manager Plus: адреса змінилася після введення ключа. Введіть ключ API заново — він надсилається лише туди, для чого його ввели',
    en: 'The API key was entered for a different Key Manager Plus address: the address changed after the key was entered. Enter the API key again — it is only sent where it was entered for',
  }],
  [/^Key Manager Plus не настроен: задайте keymanager\.base_url и ключ API$/, {
    uk: 'Key Manager Plus не налаштовано: вкажіть keymanager.base_url і ключ API',
    en: 'Key Manager Plus is not configured: set keymanager.base_url and the API key',
  }],
  [/^Ключ API Key Manager Plus не задан$/, { uk: 'Ключ API Key Manager Plus не задано', en: 'The Key Manager Plus API key is not set' }],
  [/^Пароль SMTP вводили для другого сервера, порта, защиты или учётной записи: настройки почты сменились после ввода пароля\. Введите пароль SMTP заново — он уходит только туда, для чего его ввели$/, {
    uk: 'Пароль SMTP вводили для іншого сервера, порту, захисту або облікового запису: налаштування пошти змінилися після введення пароля. Введіть пароль SMTP заново — він надсилається лише туди, для чого його ввели',
    en: 'The SMTP password was entered for a different server, port, security mode or account: the mail settings changed after the password was entered. Enter the SMTP password again — it is only sent where it was entered for',
  }],
];

// После разбора «…; …»: иначе жадный шаблон захватил бы соседние части.
const TAIL: Array<[RegExp, Text]> = [
  [/^(.+) не выгрузился: (.+)$/, { uk: '$1 не завантажився: $2', en: '$1 was not downloaded: $2' }],
];

const CYR = /[А-Яа-яЁё]/;
const REST: Text = { uk: ' — решту надіслано.', en: ' — the others were sent.' };

function apply(rules: Array<[RegExp, Text]>, text: string, lang: 'uk' | 'en'): string | null {
  for (const [rule, out] of rules) {
    const m = rule.exec(text);
    if (m) return out[lang].replace(/\$(\d)/g, (_, n: string) => {
      const part = m[Number(n)] ?? '';
      return CYR.test(part) ? core(part, lang) : part;
    });
  }
  return null;
}

function core(text: string, lang: 'uk' | 'en'): string {
  // Ошибка подписки из нескольких частей: «…; …».
  return apply(RULES, text, lang)
    ?? (text.includes('; ') ? text.split('; ').map(part => core(part, lang)).join('; ') : null)
    ?? apply(TAIL, text, lang)
    ?? text;
}

/** Текст сервиса на языке интерфейса; русский и незнакомый — как есть. */
export function serverMessage(text: string, lang: Lang): string {
  if (lang === 'ru' || !CYR.test(text)) return text;
  const rest = ' — отправлены остальные.';
  if (text.endsWith(rest)) return core(text.slice(0, -rest.length), lang) + REST[lang];
  if (text.endsWith('.')) return core(text.slice(0, -1), lang) + '.';
  return core(text, lang);
}
