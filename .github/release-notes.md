Образ: `{IMAGE}:{VERSION}`

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
[инструкция](https://github.com/{REPOSITORY}#установка).

Контрольные суммы — в `SHA256SUMS`.
