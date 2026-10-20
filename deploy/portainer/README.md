# Установка через Portainer

В архиве релиза:

| Файл | Что это |
|---|---|
| `docker-compose.yml` | Стек: база, миграции, сервис, Cloudflare Tunnel |
| `.env.example` | Переменные стека с подсказками — секреты, токен туннеля, первый администратор |
| `email-ssl-sender-image-vX.Y.Z.tar` | Образ сервиса для загрузки в Portainer |

## 1. Образ

**Portainer → Images → Import** → выберите `email-ssl-sender-image-vX.Y.Z.tar` → **Upload**.
Образ появится как `ghcr.io/danrey-bilo/email_ssl_sender:X.Y.Z`.

## 2. Переменные

Откройте `.env.example` и заполните (команды генерации — в самом файле):

| Переменная | Что вписать |
|---|---|
| `SENDER_MASTER_KEK` | мастер-ключ: `openssl rand -base64 32` |
| `POSTGRES_PASSWORD` | пароль владельца базы: `openssl rand -hex 24` |
| `SENDER_APP_PASSWORD` | пароль роли сервиса — другой: `openssl rand -hex 24` |
| `CLOUDFLARE_TUNNEL_TOKEN` | токен туннеля из Cloudflare |
| `SENDER_INIT_ADMIN_EMAIL`, `SENDER_INIT_ADMIN_PASSWORD` | первый администратор |

> ⚠ Сохраните копию `SENDER_MASTER_KEK` в надёжном месте. Без него не открыть пароль SMTP,
> ключ API и коды 2FA, даже если база цела.

## 3. Стек

**Portainer → Stacks → Add stack**:

1. **Name:** `email-ssl-sender`.
2. **Build method:** **Upload** → **Select a file** → `docker-compose.yml`.
3. **Environment variables:** **Load variables from .env file** → ваш заполненный файл.
4. **Deploy the stack**.

Порядок запуска сам: `postgres` → `migrate` (схема, роль базы, первый администратор; завершается
со статусом `exited (0)` — так и должно быть) → `api` → `cloudflared`.

## 4. Cloudflare

В туннеле: **Public Hostname → Add** — домен панели, **Service:** `HTTP`, **URL:** `api:8001`.
Рекомендуется **Cloudflare Access** перед панелью (только сотрудники) — README, шаг 5.

## 5. Первый вход

Откройте адрес панели, войдите администратором из шага 2 и подключите приложение-аутентификатор.
Затем в стеке **очистите `SENDER_INIT_ADMIN_PASSWORD`** → **Update the stack**.

## Обновление

1. **Images → Import** — образ новой версии.
2. В переменных стека — новый `SENDER_IMAGE` (например `ghcr.io/danrey-bilo/email_ssl_sender:1.3.0`).
3. **Update the stack**. Миграции выполнятся сами (служба `migrate`).

Перед обновлением снимите копию базы (ниже).

## Восстановление доступа

Забыт пароль или потерян телефон: в переменных стека заполните `SENDER_RESET_ACCESS_EMAIL` и
`SENDER_RESET_ACCESS_PASSWORD` → **Update the stack**. Пароль станет новым, второй фактор,
блокировка и все сессии будут сняты. **После входа очистите оба поля и обновите стек снова**.

## Копия базы

На сервере:

```bash
docker exec email-ssl-sender-postgres-1 pg_dump -U sender sender > sender.sql
```

Мастер-ключ храните отдельно от копий.

## Если что-то не так

- **`migrate` завершился с ошибкой** — Containers → `email-ssl-sender-migrate-1` → Logs: там
  причина (не заполнена переменная, слабый пароль администратора, нет доступа к базе).
- **`api` не стартует** — Logs контейнера `api`; чаще всего не тот `SENDER_APP_PASSWORD` или
  `SENDER_MASTER_KEK`.
- **Туннель не поднимается** — Logs `cloudflared`: строка `Registered tunnel connection` значит,
  что всё в порядке.
