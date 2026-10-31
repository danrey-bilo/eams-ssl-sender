// Переводчик текстов сервиса (npm test): каждый шаблон сервера → английский без кириллицы, украинский не равен русскому.
// ⚠ Тексты ниже повторяют сообщения сервиса: меняя текст ошибки в C#, поправьте его здесь и в src/lib/serverText.ts.

const { serverMessage } = await import(new URL('../src/lib/serverText.ts', import.meta.url).href);

const cases = [
  'Сертификата shop.example.com нет в Key Manager Plus.',
  'Сертификатов a.example.com, b.example.com нет в Key Manager Plus.',
  'Сертификата wiki.example.local нет в Key Manager Plus — отправлены остальные.',
  'Сертификата wiki.example.local нет в Key Manager Plus; api.example.com не выгрузился: Key Manager Plus не ответил вовремя (getCertificate) — отправлены остальные.',
  'a.example.com не выгрузился: Key Manager Plus отказал (getCertificate): no such certificate; b.example.com не выгрузился: Key Manager Plus недоступен (getCertificate): соединение не устанавливается — отправлены остальные.',
  'vpn.example.com: в Key Manager Plus только выпуск старее отправленного (серийный 0b0c) — сам не отправляется, отправьте вручную, если это верно.',
  'Почта не настроена: письма с сертификатом некуда отправить.',
  'Почта настроена без шифрования (mail.smtp_security = none): закрытый ключ и его пароль по ней не отправляются. Выберите tls или starttls в настройках почты или уберите из подписки KEY, PKCS, JKS и пароль ключа.',
  'Почта отказала в постановке письма: RateLimited.',
  'Отправка не удалась: DbUpdateException → NpgsqlException.',
  'Письмо не доставлено: ops@example.com — 550 5.1.1 User unknown; dev@example.com. Отправьте повторно, когда причина устранена.',
  'Письмо не доставлено: ops@example.com — 550 5.1.1 User unknown.',
  'Опрос не удался: TaskCanceledException.',
  'Key Manager Plus недоступен (getAllSSLCertsExpiryDate): имя сервера не разрешается.',
  'Key Manager Plus недоступен (getAllSSLCertsExpiryDate): TLS не согласован — сертификат сервера не прошёл проверку (цепочка, имя, отзыв): нужен сертификат удостоверяющего центра на имя из адреса, самоподписанный не принимается.',
  'Key Manager Plus недоступен (getAllSSLCertsExpiryDate): прокси не пропустил соединение.',
  'Key Manager Plus недоступен (getAllSSLCertsExpiryDate): сбой HTTP (InvalidResponse).',
  'Key Manager Plus не ответил вовремя (getAllSSLCertsExpiryDate).',
  'Key Manager Plus не принял ключ API (401: Invalid API key).',
  'Key Manager Plus не принял ключ API (AuthenticationFailure).',
  'Key Manager Plus перенаправляет запрос (302): проверьте адрес — по перенаправлениям ключ API не отправляется.',
  'Key Manager Plus ответил 500 на getCertificate: Internal error.',
  'Key Manager Plus ответил 404 на getCertificate.',
  'Key Manager Plus ответил не объектом JSON.',
  'Key Manager Plus ответил не JSON.',
  'Key Manager Plus отказал (getCertificate): статус «нет»',
  'Key Manager Plus отказал (getCertificate): статус «failed»',
  'Key Manager Plus ответил на запрос списка сертификатов без поля details — формат ответа не распознан (поля ответа: result, status).',
  'Key Manager Plus вернул 3 записей о сертификатах, но ни в одной нет полей «Common Name» и «serialNumber» — формат ответа не распознан (поля записи: name, serial).',
  'Key Manager Plus вернул пустой файл PEM для сертификата api.example.com.',
  'Key Manager Plus вернул не файл сертификата для PEM api.example.com (text/html, 512 байт).',
  'keymanager.base_url должен быть адресом https://: ключ API едет в заголовке, и по открытому HTTP его прочтёт любой на пути.',
  'Ключ API задан до привязки к адресу. Выполните docker compose run --rm migrate — команда привяжет его к текущему адресу Key Manager Plus — или введите ключ заново.',
  'Ключ API вводили для другого адреса Key Manager Plus: адрес сменился после ввода ключа. Введите ключ API заново — он уходит только туда, для чего его ввели.',
  'Key Manager Plus не настроен: задайте keymanager.base_url и ключ API.',
  'Ключ API Key Manager Plus не задан.',
  'Пароль SMTP вводили для другого сервера, порта, защиты или учётной записи: настройки почты сменились после ввода пароля. Введите пароль SMTP заново — он уходит только туда, для чего его ввели.',
  '550 5.7.1 Relaying denied',
];

let bad = 0;
for (const ru of cases) {
  const en = serverMessage(ru, 'en');
  const uk = serverMessage(ru, 'uk');
  const fail = /[А-Яа-яЁё]/.test(en) || (/[А-Яа-яЁё]/.test(ru) && uk === ru) || serverMessage(ru, 'ru') !== ru;
  if (fail) bad++;
  console.log((fail ? '✗ ' : '✓ ') + en + '\n    ' + uk);
}
console.log(`шаблонов ${cases.length}, не переведено ${bad}`);
process.exit(bad ? 1 : 0);
