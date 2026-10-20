# Email SSL Sender

Готовый серверный каркас из трёх частей: **учётные записи, вход с двухфакторной
аутентификацией и отправка почты по SMTP через SSL/TLS** с очередью. Код перенесён из EAMS
(модули личностей, токенов, настроек и почты) вместе с тестами. Всё про хранилища, роли,
права, устройства и журнал-таблицу из него убрано.

Стек: .NET 10, ASP.NET Core, PostgreSQL 16, EF Core, MailKit. Запускается в Docker.

---

## Содержание

1. [Что внутри](#что-внутри)
2. [Модель доступа](#модель-доступа)
3. [Запуск на сервере](#запуск-на-сервере)
4. [Первый вход и подключение аутентификатора](#первый-вход-и-подключение-аутентификатора)
5. [Настройка почты по SSL](#настройка-почты-по-ssl)
6. [Учётные записи](#учётные-записи)
7. [Справочник API](#справочник-api)
8. [Настройки](#настройки)
9. [Своё письмо из кода](#своё-письмо-из-кода)
10. [Свой эндпоинт](#свой-эндпоинт)
11. [Разработка и тесты](#разработка-и-тесты)
12. [Обслуживание](#обслуживание)
13. [Что учесть](#что-учесть)

---

## Что внутри

| Проект | Что делает | Откуда в EAMS |
|---|---|---|
| `EmailSslSender.Api` | Хост: конвейер запросов, фильтр входа, команды консоли, миграции, страницы `/activate` и `/password-reset` | `Eams.Api` |
| `EmailSslSender.Shared` | Контракты между модулями, коды ошибок, шифрование секретов (мастер-ключ, конверты AES-GCM), общий `SenderDbContext` | `Eams.Shared` |
| `EmailSslSender.Modules.Identity` | Учётные записи, пароли (Argon2id), вход, TOTP, коды письмом, приглашения, сброс пароля, блокировка после неудачных попыток | `Eams.Modules.Identity` |
| `EmailSslSender.Modules.Tokens` | Сессии: непрозрачные токены доступа и обновления, ротация, обнаружение кражи, отметка переподтверждения | `Eams.Modules.ApiTokens` |
| `EmailSslSender.Modules.Settings` | Настройки установки в базе (`system_settings`), справочник с границами и умолчаниями | `Eams.Modules.Settings` |
| `EmailSslSender.Modules.Mail` | SMTP через MailKit, очередь писем в базе, пределы частоты, шаблоны на трёх языках, зашифрованный пароль SMTP | `Eams.Modules.Mail` |
| `*.Tests`, `EmailSslSender.IntegrationTests` | Около 530 тестов: модульные и живые на PostgreSQL в Testcontainers | тесты тех же модулей |

Модули подключаются в [Program.cs](EmailSslSender.Api/Program.cs) цепочкой
`AddIdentityModule()` → `AddTokensModule()` → `AddSettingsModule()` → `AddMailModule()`.
Модуль не трогает хост: он объявляет свои таблицы, настройки и шаблоны писем через интерфейсы
из `EmailSslSender.Shared`.

---

## Модель доступа

- **Каждая учётная запись — администратор.** Ролей и прав нет. Кто вошёл, тот может всё.
- **Закрыто по умолчанию.** Любое действие требует входа, если оно не помечено
  `[PublicEndpoint]`. Публичны только вход, обновление токена, второй шаг входа, активация,
  сброс пароля и проверки здоровья. Проверяет это
  [AuthenticationFilter.cs](EmailSslSender.Api/Infrastructure/AuthenticationFilter.cs), а тест
  `ЗакрытоПоУмолчанию` обходит все действия и требует 401 от каждого непомеченного.
- **Второй фактор обязателен.** Основной фактор — приложение-аутентификатор (TOTP: Google
  Authenticator, Aegis, 1Password и подобные).
  - Первый вход по паролю даёт **сессию настройки**: в ней открыто только подключение
    аутентификатора и смена своего пароля. Всё остальное отвечает
    `403 TWO_FACTOR_ENROLLMENT_REQUIRED`.
  - Коды письмом — **запасной способ**. Они работают только когда почта настроена и только
    у того, кто уже хотя бы раз подтвердил аутентификатор.
- **Опасные действия требуют свежего второго фактора.** Код должен быть подтверждён
  в **этой** сессии за последние **5 минут**, иначе ответ `403 SECOND_FACTOR_REQUIRED`.
  Свежий фактор нужен для:
  - изменения настроек;
  - пароля SMTP и проверочного письма;
  - создания, изменения, отключения, включения и удаления учётных записей;
  - снятия блокировки, сброса чужого второго фактора, закрытия чужих сессий;
  - выдачи ссылки сброса пароля и повторного приглашения.

  Свежесть даёт вход с кодом или `POST /api/me/step-up`.
- **Последнюю активную учётную запись нельзя отключить или удалить** (`409 LAST_SUPERADMIN`):
  иначе в систему некому будет войти.
- **Пароль администратор не задаёт.** Новую учётную запись заводят приглашением, и пароль
  человек задаёт сам по ссылке. Исключение — первая учётная запись: её заводит консольная
  команда.

---

## Запуск на сервере

Нужны Docker с Compose v2 и bash (на Windows подойдёт Git Bash или WSL). Все команды
выполняются из корня репозитория.

**1. Секреты.** Скрипт создаёт `secrets/sender_master_kek` (мастер-ключ, 32 случайных байта)
и `secrets/sender_postgres_password`. Существующие файлы он не перезаписывает никогда.

```bash
./scripts/gen-secrets.sh
```

> ⚠ Папка `secrets/` в git не попадает. Мастер-ключ храните отдельно от копий базы: без него
> пароль SMTP и тела писем в очереди не расшифровать, а вместе с ним — расшифровать.

**2. Сборка образа.**

```bash
docker compose build
```

**3. Схема базы.** Обычный старт на отставшей схеме отказывается работать, поэтому схему
накатывают только этой командой, в том числе после каждого обновления кода.

```bash
docker compose run --rm api migrate
```

**4. Первая учётная запись.** Пароль спрашивается скрытым вводом дважды. Для сценариев есть
`--password-file путь`, а для имени — `--display-name "Имя"`.

```bash
docker compose run --rm api create-superadmin --email admin@example.com
```

**5. Запуск.**

```bash
docker compose up -d
```

API слушает только `127.0.0.1:8001`. Проверка:

```bash
curl -s http://127.0.0.1:8001/api/health
```

`/api/health` отвечает, что процесс жив. `/api/ready` дополнительно проверяет базу.

**6. Обратный прокси с TLS.** Наружу API выставляют через nginx, Caddy или Traefik с
сертификатом. Кука обновления — `__Host-sender-refresh` с атрибутами `Secure`, `HttpOnly` и
`SameSite=Strict`, поэтому по голому HTTP браузер её не сохранит. Пример для Caddy:

```
sender.example.com {
    reverse_proxy 127.0.0.1:8001
}
```

Чтобы предел попыток входа считался по адресу клиента, а не прокси, сообщите приложению,
какому прокси верить заголовку `X-Forwarded-For`. В файле `.env` рядом с
`docker-compose.yml` задайте адрес, с которого прокси приходит **в контейнер**. Обычно это
шлюз сети Docker, например `172.18.0.1/32`; можно и список через запятую.

```
SENDER_TRUSTED_PROXY_NETWORKS=172.18.0.1/32
```

---

## Первый вход и подключение аутентификатора

Примеры ниже — для скрипта и `curl`: токены приходят в теле ответа (`"use_cookie": false`).
Браузерный клиент передаёт `"use_cookie": true` и получает токен обновления кукой.

```bash
API=http://127.0.0.1:8001
```

**1. Вход паролем.** Аутентификатора ещё нет, поэтому ответ — `200` с токенами сессии
настройки. В `GET /api/me` у неё `"must_enroll_two_factor": true`.

```bash
curl -s -X POST $API/api/auth/login -H 'Content-Type: application/json' -d '{"email":"admin@example.com","password":"ВАШ_ПАРОЛЬ","use_cookie":false}'
```

```bash
TOKEN='значение access_token из ответа'
```

**2. Завести секрет.** Ответ: `{"secret": "...", "uri": "otpauth://totp/..."}`. Значение `uri`
превратите в QR-код (например, `qrencode -t ansiutf8 'otpauth://...'`) или введите `secret`
в приложение вручную. Секрет показывается один раз.

```bash
curl -s -X POST $API/api/me/two-factor/totp/setup -H "Authorization: Bearer $TOKEN"
```

**3. Подтвердить кодом из приложения.** Ответ `204`. С этого момента сессия полноценная,
и фактор в ней свежий.

```bash
curl -s -X POST $API/api/me/two-factor/totp/confirm -H "Authorization: Bearer $TOKEN" -H 'Content-Type: application/json' -d '{"code":"123456"}'
```

**Следующие входы** проходят в два шага. Вход паролем отвечает
`401 TWO_FACTOR_REQUIRED`. В `details` лежат `challenge` (расписка на 5 минут), `method`
(`totp`) и `available` (`email` появится, когда настроена почта). С распиской и кодом
идут на второй шаг:

```bash
curl -s -X POST $API/api/auth/two-factor -H 'Content-Type: application/json' -d '{"challenge":"РАСПИСКА","code":"123456","use_cookie":false}'
```

Нет доступа к приложению — запросите код письмом, затем отправьте его тем же
`/api/auth/two-factor`:

```bash
curl -s -X POST $API/api/auth/two-factor/email -H 'Content-Type: application/json' -d '{"challenge":"РАСПИСКА"}'
```

**Обновление пары** выдаёт новый токен доступа и новый токен обновления. Старый токен
обновления после этого недействителен: повторное предъявление считается кражей и закрывает
сессию.

```bash
curl -s -X POST $API/api/auth/refresh -H 'Content-Type: application/json' -d '{"refresh_token":"ТОКЕН_ОБНОВЛЕНИЯ","use_cookie":false}'
```

**Переподтверждение** перед опасным действием, когда прошло больше 5 минут:

```bash
curl -s -X POST $API/api/me/step-up -H "Authorization: Bearer $TOKEN" -H 'Content-Type: application/json' -d '{"code":"123456"}'
```

---

## Настройка почты по SSL

Почта выключена, пока её не настроят. Все шаги требуют свежего второго фактора.

**1. Сервер и отправитель.** `mail.smtp_security` задаётся явно и по порту не угадывается:

| Значение | Что делает | Обычный порт |
|---|---|---|
| `tls` | SSL/TLS с первого байта (SMTPS) | 465 |
| `starttls` | Обычное соединение, затем обязательный STARTTLS | 587 |
| `none` | Без шифрования — только для ловушки писем или релея в той же сети | 25, 1025 |

```bash
curl -s -X PATCH $API/api/settings/system -H "Authorization: Bearer $TOKEN" -H 'Content-Type: application/json' -d '{"values":{"mail.enabled":true,"mail.smtp_host":"smtp.example.com","mail.smtp_port":465,"mail.smtp_security":"tls","mail.smtp_username":"noreply@example.com","mail.from_address":"noreply@example.com","mail.from_name":"Email SSL Sender","server.public_url":"https://sender.example.com"}}'
```

`server.public_url` — внешний адрес установки. Из него собираются ссылки в письмах
приглашения и сброса пароля.

**2. Пароль SMTP.** Он хранится в базе зашифрованным мастер-ключом и обратно не отдаётся
никогда. Пустая строка снимает пароль.

```bash
curl -s -X PUT $API/api/mail/password -H "Authorization: Bearer $TOKEN" -H 'Content-Type: application/json' -d '{"password":"ПАРОЛЬ_SMTP"}'
```

**3. Проверочное письмо** уходит сразу, минуя очередь. Ответ `204` значит, что сервер
письмо принял. При отказе приходит `409 MAIL_SEND_FAILED`, и в `details.reason` лежит ответ
почтового сервера, например `5.7.1 Authentication Required`.

```bash
curl -s -X POST $API/api/mail/test -H "Authorization: Bearer $TOKEN" -H 'Content-Type: application/json' -d '{"to":"you@example.com","locale":"ru"}'
```

**4. Состояние:** `configured`, `password_set`, сколько писем ждёт (`pending`), сколько
не ушло (`failed`) и последняя ошибка (`last_error`).

```bash
curl -s $API/api/mail -H "Authorization: Bearer $TOKEN"
```

**Как работает очередь.** Письма из кода (приглашения, сброс, коды входа) кладутся в таблицу
`mail_outbox` в той же транзакции, что и действие. Если действие откатилось, письма нет.
Насос в фоне забирает строки (`FOR UPDATE SKIP LOCKED`, поэтому безопасно и при нескольких
экземплярах) и повторяет попытки до `mail.max_attempts`. Тела писем в таблице зашифрованы,
отправленные убираются уборкой. Предел `mail.rate_per_hour` считается на пару «адрес + вид
письма» и защищает чужой ящик от форм сброса и кодов.

**Сертификат сервера проверяется полностью, включая отзыв (CRL/OCSP).** У публичных
провайдеров это работает само. Если SMTP-сервер стоит во внутренней сети с собственным
удостоверяющим центром:

- в сертификате должна быть точка распространения CRL, доступная из контейнера, иначе
  отказ будет «unable to get certificate CRL»;
- корневой сертификат передайте контейнеру: смонтируйте файл и задайте
  `SSL_CERT_FILE=/путь/ca-bundle.pem` в `environment` сервиса `api`.

Отключать проверку не нужно и нельзя: пароль SMTP уехал бы тому, кто встал посередине.

---

## Учётные записи

Все действия ниже требуют свежего второго фактора.

**Пригласить.** Если почта настроена, приглашение уходит письмом и `invitation_token` в
ответе пуст. Если нет — ссылка возвращается в ответе один раз, и её передают человеку
самостоятельно: `https://sender.example.com/activate#token=ЗНАЧЕНИЕ`. Ссылка живёт
`auth.invitation_ttl_minutes`. На странице `/activate` человек задаёт пароль. Токен лежит
во фрагменте `#`, на сервер в адресе не уходит и сразу стирается из адресной строки.

```bash
curl -s -X POST $API/api/users -H "Authorization: Bearer $TOKEN" -H 'Content-Type: application/json' -d '{"email":"colleague@example.com","display_name":"Коллега"}'
```

Приглашённый при первом входе тоже получает сессию настройки и подключает свой
аутентификатор.

**Список** (курсорная выдача: `?limit=50&cursor=...`, в ответе `next_cursor`):

```bash
curl -s "$API/api/users?limit=50" -H "Authorization: Bearer $TOKEN"
```

**Остальное** — по `id` из списка:

| Действие | Запрос |
|---|---|
| Изменить имя или потребовать смену пароля | `PATCH /api/users/{id}` с `{"display_name": "...", "must_change_password": true}` |
| Отключить / включить | `POST /api/users/{id}/disable`, `POST /api/users/{id}/enable` |
| Удалить | `DELETE /api/users/{id}` |
| Снять блокировку после неудачных входов | `POST /api/users/{id}/unlock` |
| Снять второй фактор (потерян телефон) | `DELETE /api/users/{id}/two-factor` |
| Закрыть все сессии, не отключая | `DELETE /api/users/{id}/sessions` |
| Ссылка сброса пароля | `POST /api/users/{id}/reset-password` — письмом или в ответе, как приглашение |
| Повторить приглашение | `POST /api/users/{id}/invite` — прежняя ссылка гасится |

**«Забыл пароль»** — `POST /api/password-reset/request` с `{"email": "..."}`. Ответ всегда
`202`, есть адрес или нет, а ссылка уходит **только письмом**. Без настроенной почты этот путь
ничего не делает. Тогда ссылку выдаёт другой администратор или консольная команда
`set-superadmin-password`.

---

## Справочник API

Все тела — JSON. Токен передаётся заголовком `Authorization: Bearer ...`.
Пометки: **П** — публичный, **Н** — открыт и в сессии настройки, **Ф** — нужен свежий второй
фактор.

| Метод | Путь | | Назначение |
|---|---|---|---|
| POST | `/api/auth/login` | П | Вход: `email`, `password`, `use_cookie` |
| POST | `/api/auth/two-factor` | П | Второй шаг: `challenge`, `code`, `use_cookie` |
| POST | `/api/auth/two-factor/email` | П | Прислать код входа письмом: `challenge` |
| POST | `/api/auth/refresh` | П | Новая пара: `refresh_token` (или кука), `use_cookie` |
| POST | `/api/auth/logout` | | Закрыть текущую сессию |
| GET | `/api/auth/password-policy` | Н | Требования к паролю |
| GET | `/api/me` | Н | Кто я: `id`, `email`, `must_change_password`, `must_enroll_two_factor` |
| PATCH | `/api/me` | | Своё имя: `display_name` |
| POST | `/api/me/password` | Н | Сменить свой пароль: `current_password`, `new_password` |
| GET | `/api/me/two-factor` | Н | Состояние фактора и доступные способы |
| PUT | `/api/me/two-factor` | | Предпочитаемый способ: `{"method": "totp" \| "email"}` |
| POST | `/api/me/two-factor/totp/setup` | Н | Завести секрет TOTP |
| POST | `/api/me/two-factor/totp/confirm` | Н | Подтвердить секрет кодом |
| POST | `/api/me/two-factor/totp/disable` | Ф | Отключить приложение (только если работают коды письмом): `password` |
| POST | `/api/me/step-up` | Н | Переподтвердить личность кодом: `code` |
| POST | `/api/me/step-up/email` | Н | Прислать код переподтверждения письмом |
| GET | `/api/me/sessions` | | Мои сессии |
| DELETE | `/api/me/sessions/{id}` | | Закрыть свою сессию |
| GET | `/api/users` | | Список учётных записей |
| GET | `/api/users/{id}` | | Одна учётная запись |
| POST | `/api/users` | Ф | Пригласить: `email`, `display_name` |
| PATCH | `/api/users/{id}` | Ф | `display_name`, `must_change_password` |
| POST | `/api/users/{id}/disable`, `/enable` | Ф | Отключить / включить |
| DELETE | `/api/users/{id}` | Ф | Удалить |
| POST | `/api/users/{id}/unlock` | Ф | Снять блокировку входа |
| DELETE | `/api/users/{id}/two-factor` | Ф | Снять второй фактор |
| DELETE | `/api/users/{id}/sessions` | Ф | Закрыть все сессии человека |
| POST | `/api/users/{id}/reset-password` | Ф | Выдать ссылку сброса |
| POST | `/api/users/{id}/invite` | Ф | Повторить приглашение |
| GET | `/api/activation?token=` | П | Чья ссылка и требования к паролю |
| POST | `/api/activation` | П | Задать пароль по приглашению: `token`, `password` |
| POST | `/api/password-reset/request` | П | «Забыл пароль»: `email` |
| POST | `/api/password-reset/info` | П | Проверить ссылку сброса: `token` |
| POST | `/api/password-reset` | П | Задать новый пароль: `token`, `password` |
| GET | `/api/settings/system` | | Все настройки с текущими значениями |
| PATCH | `/api/settings/system` | Ф | `{"values": {...}, "reset": ["ключ", ...]}` |
| GET | `/api/mail` | | Состояние почты и очереди |
| PUT | `/api/mail/password` | Ф | Пароль SMTP: `password` |
| POST | `/api/mail/test` | Ф | Проверочное письмо: `to`, `locale` (`ru`, `uk`, `en`) |
| GET | `/api/health`, `/api/ready` | П | Жив ли процесс / готова ли база |

В режиме разработки документ OpenAPI доступен по `/openapi/v1.json`. В бою этого маршрута
нет, но документ можно выгрузить в файл без сервера и базы. Скрипт подставляет одноразовый
ключ и пишет `openapi.json` в корень, по нему генерируют клиентов.

```bash
./scripts/export-openapi.sh
```

**Ошибки** приходят одним конвертом:
`{"code": "...", "message": "...", "details": {...}, "request_id": "..."}`.
Клиенту нужно разбирать `code`, а не текст сообщения:

| Код | HTTP | Что значит |
|---|---|---|
| `UNAUTHENTICATED` | 401 | Нет токена или он недействителен |
| `TOKEN_EXPIRED` | 401 | Срок токена доступа вышел — обновите пару |
| `SESSION_REVOKED` | 401 | Сессия закрыта: выход, отзыв, кража — войдите заново |
| `INVALID_CREDENTIALS` | 401 | Неверный адрес или пароль (одинаково для обоих случаев) |
| `TWO_FACTOR_REQUIRED` | 401 | Пароль принят, нужен код: `details.challenge` |
| `TWO_FACTOR_INVALID` | 401 | Код не подошёл |
| `TWO_FACTOR_ENROLLMENT_REQUIRED` | 403 | Сессия настройки: сначала подключите аутентификатор |
| `PASSWORD_CHANGE_REQUIRED` | 403 | Сначала смените пароль |
| `SECOND_FACTOR_REQUIRED` | 403 | Нужен свежий второй фактор: `POST /api/me/step-up` |
| `LAST_SUPERADMIN` | 409 | Это последняя активная учётная запись |
| `PASSWORD_POLICY_VIOLATION` | 400 | Пароль не по правилам, подробности в `details` |
| `MAIL_NOT_CONFIGURED` | 409 | Почта не настроена |
| `MAIL_SEND_FAILED` | 409 | Почтовый сервер отказал: `details.reason` |
| `RATE_LIMITED` | 429 | Слишком часто |
| `VALIDATION_ERROR` | 400 | Тело запроса не прошло проверку |

Полный список — в [ErrorCodes.cs](EmailSslSender.Shared/Errors/ErrorCodes.cs).

---

## Настройки

Настройки хранятся в базе и меняются через `PATCH /api/settings/system`, без перезапуска.
`GET` возвращает все настройки с умолчаниями, границами и допустимыми значениями.
`"reset": ["ключ"]` возвращает настройке умолчание.

| Ключ | По умолчанию | Что задаёт |
|---|---|---|
| `mail.enabled` | `false` | Слать ли почту вообще |
| `mail.smtp_host` | — | Сервер SMTP |
| `mail.smtp_port` | `587` | Порт |
| `mail.smtp_security` | `starttls` | `tls`, `starttls` или `none` |
| `mail.smtp_username` | — | Пользователь SMTP; пусто — без авторизации |
| `mail.from_address` | — | Адрес в `From`; без него почта считается ненастроенной |
| `mail.from_name` | `Email SSL Sender` | Имя в `From` |
| `mail.rate_per_hour` | `5` | Писем в час на адрес и вид |
| `mail.max_attempts` | `5` | Попыток доставки |
| `server.public_url` | `https://sender.example.com` | Внешний адрес для ссылок в письмах |
| `auth.second_factor` | `email` | Унаследована из EAMS и при обязательном аутентификаторе на вход не влияет: кто подтвердил приложение, входит с ним, а письмо выбирает себе сам (`PUT /api/me/two-factor`). Выключить фактор нельзя |
| `auth.two_factor_challenge_minutes` | `5` | Сколько живёт расписка между паролем и кодом |
| `auth.password_min_length` и `auth.password_require_*` | — | Политика паролей |
| `auth.lockout_threshold`, `auth.lockout_minutes` | — | Блокировка после неудачных входов |
| `auth.access_ttl_minutes` | — | Срок токена доступа |
| `auth.session_idle_minutes`, `auth.session_absolute_hours` | — | Простой и предельный срок сессии |
| `auth.refresh_grace_seconds` | — | Окно, в котором повтор обновления не считается кражей (две вкладки) |
| `auth.session_retention_days`, `identity.link_retention_days` | — | Сколько хранить закрытые сессии и погашенные ссылки |
| `auth.invitation_ttl_minutes` | — | Срок ссылок приглашения и сброса |
| `ui.locale` | `ru` | Язык писем: `ru`, `uk` или `en` |

Прочерк значит, что умолчание смотрите в `GET /api/settings/system`.

Пределы частоты на входе лежат в `appsettings.json`, раздел `RateLimits`, и меняются
перезапуском.

---

## Своё письмо из кода

Шаблон объявляет тот, кто шлёт. Почтовый модуль при этом не правится.

**1. Шаблоны.** Нужны все три языка: при старте справочник проверяет, что у каждого вида
есть `ru`, `uk` и `en`. Подстановки пишутся как `{имя}`.

```csharp
using EmailSslSender.Shared.Contracts.Mail;

internal sealed class ReportMailTemplates : IMailTemplateProvider
{
    public const string ReportReady = "report_ready";

    public IReadOnlyList<MailTemplate> Templates { get; } =
    [
        new(ReportReady, MailLocales.Ru, "Отчёт готов", "Отчёт «{name}» готов:\n{link}"),
        new(ReportReady, MailLocales.Uk, "Звіт готовий", "Звіт «{name}» готовий:\n{link}"),
        new(ReportReady, MailLocales.En, "Report ready", "Report \"{name}\" is ready:\n{link}"),
    ];
}
```

**2. Регистрация** — в `Program.cs` или в своём модуле:

```csharp
builder.Services.AddSingleton<IMailTemplateProvider, ReportMailTemplates>();
```

**3. Отправка.** `EnqueueAsync` не сохраняет сам: письмо уезжает в базу с вашим
`SaveChangesAsync`, в одной транзакции с вашим действием. Метод не бросает исключений, а
возвращает результат.

```csharp
public sealed class ReportService(IMailQueue mail, SenderDbContext db)
{
    public async Task NotifyAsync(string to, string name, string link, CancellationToken ct)
    {
        var result = await mail.EnqueueAsync(
            to,
            ReportMailTemplates.ReportReady,
            MailLocales.Ru,
            new Dictionary<string, string> { ["name"] = name, ["link"] = link },
            ct);

        // Queued — уйдёт насосом; NotConfigured — почта не настроена; RateLimited — предел адреса
        await db.SaveChangesAsync(ct);
    }
}
```

`IMailQueue.CanSendAsync()` заранее отвечает, настроена ли почта.

---

## Свой эндпоинт

Обычный контроллер ASP.NET Core в `EmailSslSender.Api` (или в своём модуле по образцу
`MailModule.cs`). Действие **закрыто по умолчанию**: без пометок оно требует входа и
полноценной сессии.

```csharp
using EmailSslSender.Shared.Contracts.Audit;
using EmailSslSender.Shared.Contracts.Authz;
using Microsoft.AspNetCore.Mvc;

[ApiController]
[Route("api/reports")]
public sealed class ReportsController(ICurrentUser me, IAuditWriter audit) : ControllerBase
{
    [HttpGet]
    public object List() => new { owner = me.Id };

    [HttpDelete("{id:guid}")]
    [RequireRecentSecondFactor]          // опасное: нужен код за последние 5 минут
    public IActionResult Delete(Guid id)
    {
        audit.Write(new AuditEntry(new AuditAction("report.delete", "Удалён отчёт"),
            ActorId: me.Id, ResourceType: "report", ResourceId: id));
        return NoContent();
    }

    [HttpGet("public-status")]
    [PublicEndpoint("статус для мониторинга, данных не содержит")]
    public object Status() => new { ok = true };
}
```

- `ICurrentUser.Id` — кто вошёл, `SessionId` — его сессия.
- `IAuditWriter` пишет строку `audit ...` в журнал приложения.
- Тест `ЗакрытоПоУмолчанию` сам проверит, что новое непомеченное действие отвечает
  невошедшему `401`.

---

## Разработка и тесты

Нужны .NET SDK 10.0.400 или новее (см. `global.json`) и Docker.

**Зависимости:** PostgreSQL на `127.0.0.1:5432` (пароль `dev`) и ловушка писем Mailpit
(SMTP на `127.0.0.1:1025`, веб-интерфейс на http://127.0.0.1:8025).

```bash
docker compose -f docker-compose.dev.yml up -d
```

**Переменные окружения** для `dotnet run`. Мастер-ключ берётся из файла, который создаёт
`gen-secrets.sh`:

```bash
export ConnectionStrings__Postgres='Host=127.0.0.1;Port=5432;Database=sender;Username=sender;Password=dev'
```

```bash
export SENDER_MASTER_KEK_FILE="$PWD/secrets/sender_master_kek"
```

**Схема, первая учётная запись и запуск** (http://localhost:8001):

```bash
dotnet run --project EmailSslSender.Api -- migrate
```

```bash
dotnet run --project EmailSslSender.Api -- create-superadmin --email admin@example.com
```

```bash
dotnet run --project EmailSslSender.Api
```

Почта в разработке: `mail.smtp_host` = `127.0.0.1`, `mail.smtp_port` = `1025`,
`mail.smtp_security` = `none`. Письма видны в Mailpit.

**Сборка и тесты.** Тесты с базой сами поднимают PostgreSQL через Testcontainers и требуют
запущенного Docker.

```bash
dotnet build EmailSslSender.slnx
```

```bash
dotnet test --solution EmailSslSender.slnx
```

**Новая миграция** после изменения сущностей. Нужен инструмент `dotnet-ef`
(`dotnet tool install --global dotnet-ef`) и те же две переменные окружения, что для
`dotnet run`: инструмент поднимает хост, а хост без мастер-ключа и строки подключения не
стартует. Базу при этом трогать не нужно.

```bash
dotnet ef migrations add ИмяМиграции --project EmailSslSender.Api --output-dir Migrations
```

---

## Обслуживание

**Потерян пароль единственной учётной записи:**

```bash
docker compose run --rm api set-superadmin-password --email admin@example.com
```

**Потерян телефон с аутентификатором.** Команда снимает второй фактор, закрывает сессии,
и следующий вход снова начнётся с подключения:

```bash
docker compose run --rm api reset-superadmin-2fa --email admin@example.com
```

**Ротация мастер-ключа.**

1. Допишите в `secrets/sender_master_kek` строку `2:` с новым ключом, сгенерированным
   командой `openssl rand -base64 32`. Первая строка без номера — это версия 1. Действующей
   становится наибольшая версия.
2. Перезапустите API (`docker compose up -d`).
3. Переложите обёртки всех секретов под новую версию:

   ```bash
   docker compose run --rm api rewrap-keys
   ```

4. Когда команда сообщит, что переложено всё, старую строку можно убрать. Учтите: копии
   базы, снятые раньше, открываются только старым ключом.

**Резервные копии.** Снимайте `pg_dump` базы `sender` и **отдельно** храните мастер-ключ.
Папка `backups/` в git не попадает.

```bash
mkdir -p backups && docker compose exec -T postgres pg_dump -U sender sender > backups/sender.sql
```

**Журнал действий.** Отдельной таблицы нет. События входа, отзыва сессий, изменений учётных
записей и настроек пишутся строкой `audit <действие> <итог> actor=... resource=... ip=...`
в журнал приложения (JSON, Serilog). Секреты и значения изменений туда не попадают.

```bash
docker compose logs api | grep '"audit '
```

**Обновление кода:** `docker compose build`, затем `docker compose run --rm api migrate`,
затем `docker compose up -d`.

---

## Что учесть

- **Порядок модулей в `Program.cs` несущий.** Токены оборачивают проверку личности, а
  настройки собирают объявления соседей последними. Причины записаны комментариями у
  каждого звена.
- **Изменить правило второго фактора** — то есть разрешить коды письмом без аутентификатора —
  можно в
  [SignInService.cs:151](EmailSslSender.Modules.Identity/Users/SignInService.cs:151): там
  сессия без подтверждённого аутентификатора превращается в сессию настройки. Защита при
  этом ослабнет: почтовый ящик станет вторым паролем.
- **Окно свежего фактора** (5 минут) задано в
  [AuthenticationFilter.cs](EmailSslSender.Api/Infrastructure/AuthenticationFilter.cs),
  `StepUpWindow`.
- **Комментарии в коде** ссылаются на этапы, решения и правила EAMS: «решение 0061»,
  «правило 14», «вскрытие 43». Документы EAMS сюда не перенесены. Ссылки оставлены как
  история причин, и смысл каждого комментария понятен без них.
- **Строки разделения ключей** (`sender:secret:v1`, `sender:mail-body` и подобные) входят в
  зашифрованные данные. После первой установки их менять нельзя: прежние секреты перестанут
  открываться.
- **Лицензия** — собственная: пользоваться бесплатно; копировать, изменять и распространять код нельзя ([LICENSE](LICENSE)).
