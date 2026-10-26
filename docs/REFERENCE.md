# EAMS ssl sender — справочник

Подробности для администратора и разработчика: устройство, API, настройки, расширение кода,
обслуживание. Установка и первая настройка — в [README](../README.md).

---

## Содержание

1. [Что внутри](#что-внутри)
2. [Модель доступа](#модель-доступа)
3. [Запуск на сервере](#запуск-на-сервере)
4. [Панель](#панель)
5. [Первый вход и подключение аутентификатора](#первый-вход-и-подключение-аутентификатора)
6. [Настройка почты по SSL](#настройка-почты-по-ssl)
7. [Сертификаты из Key Manager Plus](#сертификаты-из-key-manager-plus)
8. [Учётные записи](#учётные-записи)
9. [Справочник API](#справочник-api)
10. [Настройки](#настройки)
11. [Своё письмо из кода](#своё-письмо-из-кода)
12. [Свой эндпоинт](#свой-эндпоинт)
13. [Разработка и тесты](#разработка-и-тесты)
14. [Обслуживание](#обслуживание)
15. [Что учесть](#что-учесть)

---

## Что внутри

| Проект | Что делает | Откуда в EAMS |
|---|---|---|
| `EmailSslSender.Api` | Хост: конвейер запросов, фильтр входа, команды консоли, миграции, отдача веб-панели | `Eams.Api` |
| `EmailSslSender.Web` | Веб-панель: React + TypeScript (Vite) — вход с 2FA, рассылка, Key Manager, почта, пользователи | — |
| `EmailSslSender.Shared` | Контракты между модулями, коды ошибок, шифрование секретов (мастер-ключ, конверты AES-GCM), общий `SenderDbContext` | `Eams.Shared` |
| `EmailSslSender.Modules.Identity` | Учётные записи, пароли (Argon2id), вход, TOTP, коды письмом, приглашения, сброс пароля, блокировка после неудачных попыток | `Eams.Modules.Identity` |
| `EmailSslSender.Modules.Tokens` | Сессии: непрозрачные токены доступа и обновления, ротация, обнаружение кражи, отметка переподтверждения | `Eams.Modules.ApiTokens` |
| `EmailSslSender.Modules.Settings` | Настройки установки в базе (`system_settings`), справочник с границами и умолчаниями | `Eams.Modules.Settings` |
| `EmailSslSender.Modules.Mail` | SMTP через MailKit, очередь писем в базе, вложения, пределы частоты, шаблоны на трёх языках, зашифрованный пароль SMTP | `Eams.Modules.Mail` |
| `EmailSslSender.Modules.KeyManager` | Опрос ManageEngine Key Manager Plus, подписки на сертификаты, рассылка файлов сертификата письмом | — |
| `*.Tests`, `EmailSslSender.IntegrationTests` | Около 600 тестов: модульные и живые на PostgreSQL в Testcontainers | тесты тех же модулей |

Модули подключаются в [Program.cs](../EmailSslSender.Api/Program.cs) цепочкой
`AddIdentityModule()` → `AddTokensModule()` → `AddSettingsModule()` → `AddMailModule()` →
`AddKeyManagerModule()`.
Модуль не трогает хост: он объявляет свои таблицы, настройки и шаблоны писем через интерфейсы
из `EmailSslSender.Shared`.

### Модули EAMS подмодулями

Модули EAMS живут каждый в своём репозитории `danrey-bilo/module-<имя>` и подключены подмодулями
git в каталог `modules/<имя>`. Версия каждого **закреплена коммитом метки** и сама не обновляется:

| Каталог | Репозиторий | Метка |
|---|---|---|
| `modules/core` | `module-core` | `v0.8.0` |
| `modules/crypto` | `module-crypto` | `v0.5.0` |
| `modules/hosting` | `module-hosting` | `v0.5.0` |
| `modules/identity` | `module-identity` | `v0.9.0` |
| `modules/tokens` | `module-tokens` | `v0.8.0` |
| `modules/settings` | `module-settings` | `v0.8.0` |
| `modules/mail` | `module-mail` | `v0.8.0` |

Клонировать вместе с модулями (репозитории модулей закрытые — нужен доступ к ним):

```bash
git clone --recurse-submodules https://github.com/danrey-bilo/email_ssl_sender.git
# или в уже склонированном
git submodule update --init
```

Перейти на другую версию модуля — только явно: перейти в подмодуле на метку и закоммитить новый
коммит подмодуля в этом репозитории.

```bash
git -C modules/mail fetch --tags
git -C modules/mail checkout v0.9.0
git add modules/mail
```

> Переход проекта с собственных модулей `EmailSslSender.Modules.*` на модули из `modules/` идёт
> по шагам; пока сборка сервиса их не использует. Ход работы — в [CHANGELOG.md](../CHANGELOG.md).

---

## Модель доступа

- **Каждая учётная запись — администратор.** Ролей и прав нет. Кто вошёл, тот может всё.
- **Закрыто по умолчанию.** Любое действие требует входа, если оно не помечено
  `[PublicEndpoint]`. Публичны только вход, обновление токена, второй шаг входа, активация,
  сброс пароля и проверки здоровья. Проверяет это
  [AuthenticationFilter.cs](../EmailSslSender.Api/Infrastructure/AuthenticationFilter.cs), а тест
  `ЗакрытоПоУмолчанию` обходит все действия и требует 401 от каждого непомеченного.
- **Второй фактор обязателен.** Основной фактор — приложение-аутентификатор (TOTP: Google
  Authenticator, Aegis, 1Password и подобные).
  - Первый вход (аутентификатора ещё нет) даёт **сессию настройки**: в ней открыто только
    подключение аутентификатора и смена своего пароля. Всё остальное отвечает
    `403 TWO_FACTOR_ENROLLMENT_REQUIRED`. Если почта настроена, перед сессией настройки
    приходит код письмом: одного пароля для неё мало.
  - **У кого аутентификатор подключён, код письмом не принимается.** Иначе почтовый ящик
    давал бы и сброс пароля, и второй фактор — то есть весь вход. Потерян телефон — второй
    фактор снимает другой администратор («Пользователи») или команда `reset-superadmin-2fa`.
  - На одну расписку входа — не больше 5 кодов, дальше вход начинается заново с пароля.
    Неудачи считаются атомарно до проверки: параллельные запросы лишних догадок не дают.
- **Свежий код перед важными действиями** (решение владельца, 2026-10-20). Действия с пометкой
  `[RequireRecentSecondFactor]` — выгрузка сертификата и ключа, ключ API, пароль SMTP, подписки,
  управление пользователями — и изменение настроек защиты (`PATCH /api/settings/system` с любым
  ключом, кроме `ui.*`) требуют кода, подтверждённого в ЭТОЙ сессии за последние
  `auth.step_up_minutes` минут (по умолчанию 10); иначе `403 SECOND_FACTOR_REQUIRED`. Вход с
  кодом уже свежий; обновить — `POST /api/me/step-up` с кодом из приложения. Почему и подписки:
  иначе код на скачивание обходился бы добавлением своего адреса в рассылку.
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

**2. Туннель Cloudflare.** Наружу сервис выходит через Cloudflare Tunnel: служба
`cloudflared` в том же `docker compose` держит исходящее соединение с Cloudflare, и открытых
портов на сервере не нужно. Токен берётся в Cloudflare: Zero Trust → Networks → Tunnels →
туннель → Install connector, строка после `--token`. Скрипт спрашивает его скрытым вводом и
кладёт в файл секрета `secrets/cloudflare_tunnel_token`:

```bash
cp .env.example .env              # в нём COMPOSE_PROFILES=tunnel
./scripts/set-tunnel-token.sh
```

> ⚠ Токен — только файлом секрета: переменная окружения видна в `docker inspect` и
> `/proc/1/environ`, а с токеном любой поднимет второй коннектор туннеля и получит часть трафика.

В Cloudflare у туннеля добавьте **Public Hostname**:

| Поле | Значение |
|---|---|
| Subdomain / Domain | ваш адрес, например `sender.example.com` |
| Service → Type | `HTTP` |
| Service → URL | `api:8001` |

`api:8001` — это имя службы API во внутренней сети compose, а не адрес сервера: `cloudflared`
стоит в одной сети с API. TLS для браузера даёт Cloudflare. Адрес клиента приложение берёт из
заголовков туннеля: им верит только сеть `edge`, где есть лишь `cloudflared` и API. Поэтому
предел попыток входа считается по настоящему адресу человека.

После первого входа в панели задайте **Почта → Адрес сервиса** = `https://sender.example.com`:
из него собираются ссылки в письмах.

**3. Образ.** Готовый — из релиза (`docker compose pull api`); из исходников — `docker compose build`.

**4. Схема базы.** Обычный старт на отставшей схеме отказывается работать, поэтому схему
накатывают только этой командой, в том числе после каждого обновления кода.

```bash
docker compose run --rm migrate
```

Служба `migrate` — одноразовая (профиль `tools`) и единственная, у которой есть пароль
владельца схемы `sender` (суперпользователь в образе `postgres`). Кроме миграций она заводит
или обновляет роль `sender_app`, которой ходит служба `api`:

| Что может `sender_app` | Чего не может |
|---|---|
| `SELECT`, `INSERT`, `UPDATE`, `DELETE` в схеме `public`, последовательности | `CREATE`, `ALTER`, `DROP`, `COPY … TO PROGRAM`, создавать роли и базы |

Права на таблицы будущих миграций выдаются сами (`ALTER DEFAULT PRIVILEGES`). Пароль роли —
`secrets/sender_app_password`; сменили файл — следующий `migrate` сменит пароль в базе.
Служба `api` на попытку `migrate` ответит, что схему меняет служба `migrate`.

**5. Первая учётная запись.** Пароль спрашивается скрытым вводом дважды. Для сценариев есть
`--password-file путь`, а для имени — `--display-name "Имя"`.

```bash
docker compose run --rm api create-superadmin --email admin@example.com
```

**6. Запуск.** Поднимутся API, база и туннель (`cloudflared` — если в `.env` есть
`COMPOSE_PROFILES=tunnel`). Туннель жив, когда в его журнале есть
`Registered tunnel connection` (`docker compose logs cloudflared`).

```bash
docker compose up -d
```

На самом сервере API слушает только `127.0.0.1:8001`. Проверка:

```bash
curl -s http://127.0.0.1:8001/api/health
```

`/api/health` отвечает, что процесс жив. `/api/ready` дополнительно проверяет базу.

**Без туннеля — свой обратный прокси с TLS.** Уберите из `.env` строки туннеля и выставьте API
наружу через nginx, Caddy или Traefik с сертификатом. Кука обновления — `__Host-sender-refresh` с атрибутами `Secure`, `HttpOnly` и
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

**Обновление `cloudflared`.** Образ взят как `latest`. Свежую версию подтягивают так:

```bash
docker compose pull cloudflared && docker compose up -d
```

---

## Панель

Веб-панель открывается в корне сервиса: `https://sender.example.com/`, локально —
http://127.0.0.1:8001. Всё, что описано ниже через `curl`, делается и в ней. Это React + TypeScript
(`EmailSslSender.Web`): образ Docker собирает её сам, отдельной стадией, и отдаёт вместе с API.

| Вкладка | Что там |
|---|---|
| **Рассылка** | Подписки: какой сертификат, кому, какие файлы, когда. Состояние последней отправки; действия — отправить сейчас, изменить, включить / отключить, удалить (на телефоне — «⋯») |
| **Сертификаты** | Сертификаты из последнего опроса Key Manager Plus, со сроками; скачать файл в нужном формате, показать закрытый ключ |
| **Пользователи** | Приглашение, сброс пароля и 2FA, блокировка, удаление |
| **Настройки** | Разделы «Почта» (SMTP, пароль, тестовое письмо, очередь) и «Key Manager» (адрес, ключ API, отпечаток, проверка, опрос) |
| **Безопасность** | Блокировка, сессии, пароли, срок ссылок, окно подтверждения кодом |
| **Профиль** (инициалы справа вверху) | Своё имя, пароль, сессии, выход |

- **Первый вход:** почта и пароль, затем QR-код для приложения-аутентификатора. Дальше вход идёт
  с шестизначным кодом.
- **Свежий код перед важными действиями.** На отказ `403 SECOND_FACTOR_REQUIRED` панель
  открывает окно кода (`POST /api/me/step-up`) и повторяет действие. После кода или входа окно
  длится `auth.step_up_minutes` (по умолчанию 10 минут) и привязано к сессии, а не к человеку.
- **Режим телефона.** Раскладку выбирает режим браузера (`navigator.userAgentData.mobile`, иначе
  строка агента), а не ширина экрана: `html[data-device="mobile"]` — нижние вкладки, списки вместо
  таблиц, окна листом снизу; `desktop` — верхнее меню и таблицы при любой ширине окна.
- **Сессия.** Перезагрузка страницы сессию не теряет. Токен доступа живёт в памяти вкладки, токен
  обновления — в куке `HttpOnly`.
- **Без внешних ресурсов.** Панель работает во внутренней сети без выхода в интернет. Страницы
  закрыты строгой CSP: только свои скрипты и стили, без встраивания во фрейм.

---

## Первый вход и подключение аутентификатора

Примеры ниже — для скрипта и `curl`: токены приходят в теле ответа (`"use_cookie": false`).
Браузерный клиент передаёт `"use_cookie": true` и получает токен обновления кукой.

```bash
API=http://127.0.0.1:8001
```

**1. Вход паролем.** Аутентификатора ещё нет. Без настроенной почты ответ — `200` с токенами
сессии настройки; с почтой — `401 TWO_FACTOR_REQUIRED` и код письмом (второй шаг ниже), а
сессия настройки выдаётся после него. В `GET /api/me` у неё `"must_enroll_two_factor": true`.

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

**3. Подтвердить кодом из приложения.** Ответ `204`. С этого момента сессия полноценная.

```bash
curl -s -X POST $API/api/me/two-factor/totp/confirm -H "Authorization: Bearer $TOKEN" -H 'Content-Type: application/json' -d '{"code":"123456"}'
```

**Следующие входы** проходят в два шага. Вход паролем отвечает
`401 TWO_FACTOR_REQUIRED`. В `details` лежат `challenge` (расписка на 5 минут), `method`
(`totp`) и `available` (`["totp"]`; `email` — только у того, кто аутентификатор ещё не
подключил). С распиской и кодом идут на второй шаг:

```bash
curl -s -X POST $API/api/auth/two-factor -H 'Content-Type: application/json' -d '{"challenge":"РАСПИСКА","code":"123456","use_cookie":false}'
```

Код письмом — только для входа без подключённого аутентификатора (первый вход при
настроенной почте). Владельцу аутентификатора этот запрос отвечает `409`:

```bash
curl -s -X POST $API/api/auth/two-factor/email -H 'Content-Type: application/json' -d '{"challenge":"РАСПИСКА"}'
```

**Обновление пары** выдаёт новый токен доступа и новый токен обновления. Старый токен
обновления после этого недействителен: повторное предъявление считается кражей и закрывает
сессию.

```bash
curl -s -X POST $API/api/auth/refresh -H 'Content-Type: application/json' -d '{"refresh_token":"ТОКЕН_ОБНОВЛЕНИЯ","use_cookie":false}'
```

---

## Настройка почты по SSL

Почта выключена, пока её не настроят.

**1. Сервер и отправитель.** `mail.smtp_security` задаётся явно и по порту не угадывается:

| Значение | Что делает | Обычный порт |
|---|---|---|
| `tls` | SSL/TLS с первого байта (SMTPS) | 465 |
| `starttls` | Обычное соединение, затем обязательный STARTTLS | 587 |
| `none` | Без шифрования — только ловушка писем или релей в той же сети, **без логина**: пароль по открытому соединению не отправляется | 25, 1025 |

```bash
curl -s -X PATCH $API/api/settings/system -H "Authorization: Bearer $TOKEN" -H 'Content-Type: application/json' -d '{"values":{"mail.enabled":true,"mail.smtp_host":"smtp.example.com","mail.smtp_port":465,"mail.smtp_security":"tls","mail.smtp_username":"noreply@example.com","mail.from_address":"noreply@example.com","mail.from_name":"EAMS ssl sender","server.public_url":"https://sender.example.com"}}'
```

`server.public_url` — внешний адрес установки. Из него собираются ссылки в письмах
приглашения и сброса пароля.

**2. Пароль SMTP.** Он хранится в базе зашифрованным мастер-ключом и обратно не отдаётся
никогда. Пустая строка снимает пароль. Пароль привязан к серверу, порту, защите и логину,
действующим в момент ввода: после их смены письма не уходят с ошибкой «введите пароль SMTP
заново» — так вошедший не перенаправит пароль на свой сервер. Задавайте сначала настройки,
потом пароль.

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

## Сертификаты из Key Manager Plus

Сервис опрашивает ManageEngine Key Manager Plus (проверено по документации REST API для 7.1.1,
build 7110) и рассылает файлы SSL-сертификатов по **подпискам**: какой сертификат (Common Name),
кому, в каких файлах и когда.

**Когда уходит письмо.**

- **Сертификат обновился.** Вебхуков и истории у API Key Manager Plus нет, поэтому обновление
  определяется опросом раз в `keymanager.poll_minutes`. Если у того же Common Name появился новый
  серийный номер (продление, в том числе автоматическое, или перевыпуск), сертификат уходит
  один раз. Если в Key Manager Plus лежат и старый, и новый, берётся замеченный позже, даже если
  срок у него тот же или короче. Среди замеченных одновременно выигрывает тот, что действует дольше.
- **По расписанию.** `interval_days` повторяет отправку раз в N дней.
- **Сразу после заведения подписки.** Опрос будится, и текущий сертификат уходит в течение
  секунд.
- **Вручную:** `POST /api/certificate-subscriptions/{id}/send`.

Файлы уходят **одним архивом** `<Common Name>.zip` (звёздочка — `wildcard`): Outlook блокирует
вложения `.crt`, `.cer` и `.der`. Пароль ключа (`include_passphrase`) лежит в том же архиве файлом
`passphrase.txt`. Текст письма: приветствие, сертификат с форматами и сроком, содержимое архива,
подпись «EAMS ssl sender».

> ⚠ **Письмо несёт закрытый ключ** (тип `KEY`, а также `PKCS` и `JKS`). Любой, кто прочтёт ящик
> получателя или пересланное письмо, получит ключ. В очереди вложения лежат запечатанными и
> стираются, как только письмо ушло или исчерпало попытки. Шлите ключ только тем, кому он
> действительно нужен.

**1. Ключ API.** В Key Manager Plus откройте **Settings → General Settings → API Key** и нажмите
**Generate**. Ключ привязан к хосту, на котором его создали. У нас он хранится запечатанным
мастер-ключом и обратно не отдаётся. Пустая строка снимает ключ. Ключ привязан к
`keymanager.base_url` и `keymanager.server_sha256`, действующим в момент ввода: задайте сначала
адрес и отпечаток, потом ключ; после их смены ключ вводится заново.

```bash
curl -s -X PUT $API/api/keymanager/api-key -H "Authorization: Bearer $TOKEN" -H 'Content-Type: application/json' -d '{"api_key":"3E014D78-E603-413A-AC24-6392F0001283"}'
```

**2. Адрес и проверка TLS.** Нужен адрес `https://` с портом (по умолчанию у Key Manager Plus —
6565). Ключ едет в заголовке `AUTHTOKEN`, поэтому открытый HTTP отвергается. Если у сервера
самоподписанный сертификат, закрепите его отпечаток SHA-256. Тогда принимается ровно этот
сертификат и никакой другой:

```bash
openssl s_client -connect kmp.example.local:6565 </dev/null 2>/dev/null | openssl x509 -noout -fingerprint -sha256
```

```bash
curl -s -X PATCH $API/api/settings/system -H "Authorization: Bearer $TOKEN" -H 'Content-Type: application/json' -d '{"values":{"keymanager.base_url":"https://kmp.example.local:6565","keymanager.server_sha256":"AB:CD:..."}}'
```

Если сертификат выдан своим удостоверяющим центром, отпечаток не нужен. Передайте корневой
сертификат контейнеру через `SSL_CERT_FILE`, как для SMTP. Без отпечатка проверяется и отзыв
сертификата: контейнеру нужен доступ к OCSP/CRL удостоверяющего центра.

**3. Проверка связи** работает и до включения интеграции. Ответ — число сертификатов, которые
видит Key Manager Plus. При отказе приходит `409 KEYMANAGER_UNAVAILABLE`, а в `details.reason`
лежит причина: ключ не принят, TLS не согласован, сервер недоступен.

```bash
curl -s -X POST $API/api/keymanager/test -H "Authorization: Bearer $TOKEN"
```

**4. Включить опрос:**

```bash
curl -s -X PATCH $API/api/settings/system -H "Authorization: Bearer $TOKEN" -H 'Content-Type: application/json' -d '{"values":{"keymanager.enabled":true,"keymanager.poll_minutes":15}}'
```

**5. Подписка.** Обязательны только `common_name` и `recipients`. Остальные поля:

| Поле | По умолчанию | Что задаёт |
|---|---|---|
| `formats` | `["PEM","KEY"]` | Типы файлов из Key Manager Plus: `CER`, `CRT`, `DER`, `P7B`, `PKCS`, `JKS`, `PEM`, `KEY` |
| `on_change` | `true` | Слать при новом серийном номере |
| `interval_days` | нет | Повторять раз в N дней (1–365). В `PATCH` значение `0` снимает расписание |
| `include_passphrase` | `false` | Приложить пароль закрытого ключа из Key Manager Plus файлом `passphrase.txt` |
| `locale` | `ru` | Язык письма: `ru`, `uk`, `en` |
| `enabled` | `true` | Отключённую подписку опрос не шлёт; вручную её отправить можно |

```bash
curl -s -X POST $API/api/certificate-subscriptions -H "Authorization: Bearer $TOKEN" -H 'Content-Type: application/json' -d '{"common_name":"*.example.com","recipients":["admin1@example.com","admin2@example.com"],"formats":["PEM","KEY"],"interval_days":30}'
```

Каждый адрес получает своё письмо. Поле `delivery` подписки показывает судьбу последней
отправки:
- `queued` — письма в очереди;
- `sent` — сервер их принял;
- `failed` — не ушли, причина в `last_error`;
- `none` — подписка ещё не отправлялась.

Если письмо исчерпало попытки, подписка показывает адрес и ответ почтового сервера. **Повторно она
не отправляется сама:** иначе при одном несуществующем ящике остальные получатели получали бы
дубли каждый час. Устраните причину и нажмите «Отправить».

Состояние опроса — `GET /api/keymanager`. Сертификаты, которые видел опрос, и когда каждый замечен
впервые — `GET /api/keymanager/certificates`.

Почта проверяется до выгрузки: пока она не настроена, закрытые ключи из Key Manager Plus не
выгружаются вовсе. Удаление подписки, удаление получателя или смена Common Name отменяют её
неотправленные письма. Письмо с вложениями, не ушедшее за 24 часа, теряет вложения и считается
неотправленным — после починки почты отправьте подписку заново.

Какие методы REST API Key Manager Plus вызывает сервис, с какими параметрами и что берёт из
ответа — [KEYMANAGER-API.md](KEYMANAGER-API.md).

**Что сверить на своём Key Manager Plus.** Документация не описывает, в каком виде
`getCertificate` отдаёт файлы. Клиент принимает только то, что похоже на файл сертификата или
ключа: PEM, DER/PKCS, JKS или ZIP. JSON или HTML вместо файла считаются отказом. После первой
ручной отправки откройте вложения и убедитесь, что это то, что нужно. Если нет, клиент
дописывается по тексту `last_error`.

---

## Учётные записи

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

**Список** (курсорная выдача: `?limit=50&after=...`, значение берётся из `next_cursor` ответа):

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
Пометки: **П** — публичный, **Н** — открыт и в сессии настройки, **Ф** — нужен свежий код
(подтверждён в этой сессии за последние `auth.step_up_minutes` минут), иначе
`403 SECOND_FACTOR_REQUIRED`. `PATCH /api/settings/system` требует его, если среди ключей есть
настройка защиты (все, кроме `ui.*`).

| Метод | Путь | | Назначение |
|---|---|---|---|
| POST | `/api/auth/login` | П | Вход: `email`, `password`, `use_cookie` |
| POST | `/api/auth/two-factor` | П | Второй шаг: `challenge`, `code`, `use_cookie` |
| POST | `/api/auth/two-factor/email` | П | Прислать код входа письмом: `challenge` |
| POST | `/api/auth/refresh` | П | Новая пара: `refresh_token` (или кука), `use_cookie` |
| POST | `/api/auth/logout` | | Закрыть текущую сессию |
| GET | `/api/auth/password-policy` | Н | Требования к паролю |
| GET | `/api/me` | Н | Кто я: `id`, `email`, `must_change_password`, `must_enroll_two_factor`, `step_up_until` (до какого времени код не спросят; `null` — спросят) |
| PATCH | `/api/me` | | Своё имя: `display_name` |
| POST | `/api/me/password` | Н | Сменить свой пароль: `current_password`, `new_password` |
| GET | `/api/me/two-factor` | Н | Состояние фактора и доступные способы |
| POST | `/api/me/two-factor/totp/setup` | Н | Завести секрет TOTP |
| POST | `/api/me/two-factor/totp/confirm` | Н | Подтвердить секрет кодом |
| POST | `/api/me/two-factor/totp/disable` | | Отключить приложение (только если работают коды письмом): `password` |
| POST | `/api/me/step-up` | Н | Свежий код на этой сессии: `code` из аутентификатора (письмо не принимается). Ответ: `confirmed_at`, `step_up_until` |
| GET | `/api/me/sessions` | | Мои сессии |
| DELETE | `/api/me/sessions/{id}` | | Закрыть свою сессию |
| GET | `/api/users` | | Список учётных записей |
| GET | `/api/users/{id}` | | Одна учётная запись |
| POST | `/api/users` | Ф | Пригласить: `email`, `display_name` |
| PATCH | `/api/users/{id}` | | `display_name`, `must_change_password` |
| POST | `/api/users/{id}/disable`, `/enable` | Ф | Отключить / включить |
| DELETE | `/api/users/{id}` | Ф | Удалить |
| POST | `/api/users/{id}/unlock` | Ф | Снять блокировку входа |
| DELETE | `/api/users/{id}/two-factor` | Ф | Снять второй фактор |
| DELETE | `/api/users/{id}/sessions` | Ф | Закрыть все сессии человека |
| POST | `/api/users/{id}/reset-password` | Ф | Выдать ссылку сброса |
| POST | `/api/users/{id}/invite` | Ф | Повторить приглашение |
| POST | `/api/activation/info` | П | Чья ссылка и требования к паролю: `token` (в теле, не в адресе) |
| POST | `/api/activation` | П | Задать пароль по приглашению: `token`, `password` |
| POST | `/api/password-reset/request` | П | «Забыл пароль»: `email` |
| POST | `/api/password-reset/info` | П | Проверить ссылку сброса: `token` |
| POST | `/api/password-reset` | П | Задать новый пароль: `token`, `password` |
| GET | `/api/settings/system` | | Все настройки с текущими значениями |
| PATCH | `/api/settings/system` | Ф | `{"values": {...}, "reset": ["ключ", ...]}` — шлите только изменённые ключи |
| GET | `/api/mail` | | Состояние почты и очереди |
| PUT | `/api/mail/password` | Ф | Пароль SMTP: `password` |
| POST | `/api/mail/test` | | Проверочное письмо: `to`, `locale` (`ru`, `uk`, `en`) |
| GET | `/api/keymanager` | | Состояние Key Manager Plus: включено, ключ задан, последний опрос |
| PUT | `/api/keymanager/api-key` | Ф | Ключ API Key Manager Plus: `api_key` |
| POST | `/api/keymanager/test` | | Проверить связь: число сертификатов или `409` с причиной |
| POST | `/api/keymanager/poll` | | Опросить сейчас |
| GET | `/api/keymanager/certificates` | | Сертификаты, которые видел опрос |
| POST | `/api/keymanager/certificates/download` | Ф | Файл сертификата: `common_name`, `serial_number`, `format` (`PEM`, `CRT`, `CER`, `DER`, `P7B`, `PKCS`, `JKS`, `KEY`); имя — в `Content-Disposition` |
| POST | `/api/keymanager/certificates/private-key` | Ф | Закрытый ключ PEM и пароль: `common_name`, `serial_number` → `{"private_key", "passphrase"}` |
| GET | `/api/certificate-subscriptions`, `/{id}` | | Подписки на сертификаты |
| POST | `/api/certificate-subscriptions` | Ф | Завести: `common_name`, `recipients`, `formats`, `on_change`, `interval_days`, `include_passphrase`, `locale`, `enabled` |
| PATCH | `/api/certificate-subscriptions/{id}` | Ф | Изменить те же поля |
| DELETE | `/api/certificate-subscriptions/{id}` | Ф | Удалить |
| POST | `/api/certificate-subscriptions/{id}/send` | Ф | Отправить текущий сертификат сейчас |
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
| `SECOND_FACTOR_REQUIRED` | 403 | Нужен свежий код: `POST /api/me/step-up`, затем повторить; `details.window_minutes` |
| `LAST_SUPERADMIN` | 409 | Это последняя активная учётная запись |
| `PASSWORD_POLICY_VIOLATION` | 400 | Пароль не по правилам, подробности в `details` |
| `MAIL_NOT_CONFIGURED` | 409 | Почта не настроена |
| `MAIL_SEND_FAILED` | 409 | Почтовый сервер отказал: `details.reason` |
| `KEYMANAGER_NOT_CONFIGURED` | 409 | Не заданы адрес Key Manager Plus или ключ API |
| `KEYMANAGER_UNAVAILABLE` | 409 | Key Manager Plus не ответил или отказал: `details.reason` |
| `KEYMANAGER_CERTIFICATE_NOT_FOUND` | 409 / 404 | Сертификата с таким Common Name в Key Manager Plus нет; 404 — у выгрузки, когда пары «имя + серийный» нет в последнем снимке |
| `DELIVERY_IN_PROGRESS` | 409 | Эту подписку прямо сейчас отправляет опрос |
| `RATE_LIMITED` | 429 | Слишком часто |
| `VALIDATION_ERROR` | 400 | Тело запроса не прошло проверку |

Полный список — в [ErrorCodes.cs](../EmailSslSender.Shared/Errors/ErrorCodes.cs).

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
| `mail.from_name` | `EAMS ssl sender` | Имя в `From` |
| `mail.rate_per_hour` | `5` | Писем в час на адрес и вид |
| `mail.max_attempts` | `5` | Попыток доставки |
| `server.public_url` | `https://sender.example.com` | Внешний адрес для ссылок в письмах |
| `keymanager.enabled` | `false` | Опрашивать ли Key Manager Plus и рассылать ли сертификаты |
| `keymanager.base_url` | — | Адрес Key Manager Plus, только `https://`, например `https://kmp.example.local:6565` |
| `keymanager.poll_minutes` | `15` | Как часто опрашивать (1–1440 минут) |
| `keymanager.server_sha256` | — | Отпечаток SHA-256 самоподписанного сертификата Key Manager Plus |
| `auth.two_factor_challenge_minutes` | `5` | Сколько живёт расписка между паролем и кодом |
| `auth.step_up_minutes` | `10` | Сколько после кода (или входа с кодом) важные действия его не спрашивают (1–120 минут) |
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

Нужны .NET SDK 10.0.400 или новее (см. `global.json`), Node.js 22+ и Docker.

**Панель** собирается в `EmailSslSender.Api/wwwroot`, откуда её отдаёт `dotnet run`. Для правки с
мгновенной перезагрузкой — `npm run dev` (http://localhost:5173, запросы к `/api` уходят на `:8001`).

```bash
npm --prefix EmailSslSender.Web ci
```

```bash
npm --prefix EmailSslSender.Web run build
```

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

**Вернуть доступ одной командой** (забыт пароль, потерян телефон): новый пароль (скрытый ввод
дважды или `--password-file`), снятие второго фактора, снятие блокировки, закрытие всех сессий
и гашение ссылок — одной транзакцией. Отключённую учётную запись команда не включает и говорит
об этом.

```bash
./scripts/reset-access.sh admin@example.com
```

то же самое без скрипта: `docker compose run --rm api reset-access --email admin@example.com`.

**По отдельности.** Только пароль:

```bash
docker compose run --rm api set-superadmin-password --email admin@example.com
```

Только второй фактор — снимает его, закрывает сессии, и следующий вход начнётся с подключения:

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

4. Когда команда сообщит, что переложено всё (включая неотправленные письма `mail_outbox`),
   старую строку можно убрать. Учтите: копии
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

**Обновление:** `docker compose pull api` (или `docker compose build` из исходников), затем
`docker compose run --rm migrate`, затем `docker compose up -d`.

---

## Что учесть

- **Порядок модулей в `Program.cs` несущий.** Токены оборачивают проверку личности, а
  настройки собирают объявления соседей последними. Причины записаны комментариями у
  каждого звена.
- **Код письмом не заменяет аутентификатор** (ревью безопасности 2026-10-20): правило в
  `SecondFactorPolicy` и `SignInService`. Разрешить его снова — значит сделать почтовый ящик
  единственным ключом ко входу: сброс пароля и код приходят туда же.
- **Свежий код — на важных действиях** (`[RequireRecentSecondFactor]`, `IStepUpFreshness`,
  настройка `auth.step_up_minutes`). Новое опасное действие помечается атрибутом; изменение
  настроек защиты проверяется в `SettingsService` по признаку `IsSecurity`. Отметка свежести —
  на токене сессии (`StepUpAt`), а не на человеке: код в одной вкладке не открывает другие.
- **Комментарии в коде** ссылаются на этапы, решения и правила EAMS: «решение 0061»,
  «правило 14», «вскрытие 43». Документы EAMS сюда не перенесены. Ссылки оставлены как
  история причин, и смысл каждого комментария понятен без них.
- **Строки разделения ключей** (`sender:secret:v1`, `sender:mail-body` и подобные) входят в
  зашифрованные данные. После первой установки их менять нельзя: прежние секреты перестанут
  открываться.
- **Лицензия** — собственная: пользоваться бесплатно; копировать, изменять и распространять код нельзя ([LICENSE](../LICENSE)).
