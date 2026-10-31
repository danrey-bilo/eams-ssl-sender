# Установка через Portainer

В архиве релиза:

| Файл | Что это |
|---|---|
| `docker-compose.yml` | Стек: база, миграции, сервис, Cloudflare Tunnel |
| `.env.example` | Переменные стека с подсказками — секреты, токен туннеля, первый администратор |
| `email-ssl-sender-image-vX.Y.Z.tar` | Образ сервиса — только для сервера без интернета |

## 1. Образ

Открытый образ в Docker Hub — [`danreero/eams-ssl-sender`](https://hub.docker.com/r/danreero/eams-ssl-sender):
стек скачает его сам. `SENDER_IMAGE` в `.env.example` — `danreero/eams-ssl-sender:latest` (всегда
свежий) или закреплённая версия `danreero/eams-ssl-sender:X.Y.Z`.

Сервер без интернета: **Portainer → Images → Import** → `email-ssl-sender-image-vX.Y.Z.tar` из архива
релиза → **Upload**; образ появится под тем же именем.

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

1. `SENDER_IMAGE=danreero/eams-ssl-sender:latest` — **Stacks → стек → Update the stack** с включённым
   **Re-pull image**: Portainer скачает свежий образ из Docker Hub.
2. Закреплённая версия — в переменных стека новый `SENDER_IMAGE` (например
   `danreero/eams-ssl-sender:X.Y.Z`) → **Update the stack**.

Миграции выполнятся сами (служба `migrate` стартует раньше сервиса).

Перед обновлением снимите копию базы (ниже).

**С 1.x на 2.0** (переход на модули EAMS) копия базы обязательна. В логе `migrate` должны быть строки
«переложено в конверт модулей N из N» (аутентификаторы, пароль SMTP, ключ Key Manager Plus); строка
«НЕ ОТКРЫЛОСЬ» — не тот мастер-ключ: верните прежний образ и копию базы. После перехода все входят
заново, а ссылки приглашений и сброса пароля, выданные раньше, нужно выдать снова.

## Восстановление доступа

Забыт пароль или потерян телефон: в переменных стека заполните `SENDER_RESET_ACCESS_EMAIL` и
`SENDER_RESET_ACCESS_PASSWORD` → **Update the stack**. Пароль станет новым, второй фактор,
блокировка и все сессии будут сняты. Сброс с этой парой «адрес + пароль» применяется **один раз**:
следующие обновления стека его не повторят. Понадобится снова — задайте другой пароль. **После входа
всё равно очистите оба поля и обновите стек**: пароль в переменных виден в Portainer и `docker inspect`.

## Сеть туннеля

API верит заголовкам `X-Forwarded-*` только от адреса cloudflared: `SENDER_TUNNEL_PROXY_IP`
(`172.31.250.2`), сам API — `SENDER_TUNNEL_API_IP` (`172.31.250.3`). Сменили подсеть
`SENDER_TUNNEL_SUBNET` — задайте оба адреса внутри неё.

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
