Образ / Image: [`{IMAGE}:{VERSION}`](https://hub.docker.com/r/{IMAGE}) (Docker Hub, открытый / public)

## Установка через Portainer

1. Скачайте **`email-ssl-sender-{TAG}.zip`** и распакуйте. В нём: `docker-compose.yml`,
   `.env.example` и `README.md` с подробностями; образ `email-ssl-sender-image-{TAG}.tar` — только для
   сервера без интернета (**Images → Import**).
2. Заполните `.env.example`: мастер-ключ, два пароля базы, токен Cloudflare Tunnel, первый
   администратор (команды генерации — в самом файле).
3. **Stacks → Add stack** → **Upload** → `docker-compose.yml` → **Load variables from .env file**
   → **Deploy the stack**. Образ Portainer скачает из Docker Hub сам.
4. В Cloudflare у туннеля: Public Hostname → `HTTP` `api:8001`.

## Обновление

`SENDER_IMAGE={IMAGE}:latest` — **Update the stack** с **Re-pull image**; закреплённая версия —
`SENDER_IMAGE={IMAGE}:{VERSION}` → **Update the stack**. Миграции выполнятся сами.

## Командная строка

`email-ssl-sender-cli-{TAG}.zip` — вариант с секретами в файлах:
[инструкция](https://github.com/{REPOSITORY}/blob/main/README.ru.md#установка).

## In English

- **Portainer:** download **`email-ssl-sender-{TAG}.zip`**, fill in `.env.example`, then **Stacks →
  Add stack → Upload** the `docker-compose.yml` with **Load variables from .env file**. The image is
  pulled from Docker Hub; the `.tar` is only for servers without internet access. In Cloudflare,
  point the tunnel's Public Hostname to `HTTP` `api:8001`.
- **Update:** with `SENDER_IMAGE={IMAGE}:latest`, **Update the stack** with **Re-pull image**; or set
  `SENDER_IMAGE={IMAGE}:{VERSION}` — migrations run by themselves.
- **Command line:** `email-ssl-sender-cli-{TAG}.zip` keeps secrets in files —
  [instructions](https://github.com/{REPOSITORY}#installation-from-the-command-line).
- Free to use, no code copying — see [LICENSE](https://github.com/{REPOSITORY}/blob/main/LICENSE).

Контрольные суммы / Checksums — `SHA256SUMS`.
