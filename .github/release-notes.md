Образ / Image: `{IMAGE}:{VERSION}`

## Установка через Portainer

1. Скачайте **`email-ssl-sender-{TAG}.zip`** и распакуйте. В нём: `docker-compose.yml`,
   `.env.example`, образ `email-ssl-sender-image-{TAG}.tar` и `README.md` с подробностями.
2. **Portainer → Images → Import** → `email-ssl-sender-image-{TAG}.tar`.
3. Заполните `.env.example`: мастер-ключ, два пароля базы, токен Cloudflare Tunnel, первый
   администратор (команды генерации — в самом файле).
4. **Stacks → Add stack** → **Upload** → `docker-compose.yml` → **Load variables from .env file**
   → **Deploy the stack**.
5. В Cloudflare у туннеля: Public Hostname → `HTTP` `api:8001`.

## Обновление

**Images → Import** нового образа → в переменных стека `SENDER_IMAGE={IMAGE}:{VERSION}` →
**Update the stack**. Миграции выполнятся сами.

## Командная строка

`email-ssl-sender-cli-{TAG}.zip` — вариант с секретами в файлах:
[инструкция](https://github.com/{REPOSITORY}/blob/main/README.ru.md#установка).

## In English

- **Portainer:** download **`email-ssl-sender-{TAG}.zip`**, import `email-ssl-sender-image-{TAG}.tar`
  in **Images → Import**, fill in `.env.example`, then **Stacks → Add stack → Upload** the
  `docker-compose.yml` with **Load variables from .env file**. In Cloudflare, point the tunnel's
  Public Hostname to `HTTP` `api:8001`.
- **Update:** import the new image, set `SENDER_IMAGE={IMAGE}:{VERSION}` in the stack variables,
  **Update the stack** — migrations run by themselves.
- **Command line:** `email-ssl-sender-cli-{TAG}.zip` keeps secrets in files —
  [instructions](https://github.com/{REPOSITORY}#installation-from-the-command-line).
- Free to use, no code copying — see [LICENSE](https://github.com/{REPOSITORY}/blob/main/LICENSE).

Контрольные суммы / Checksums — `SHA256SUMS`.
