Образ: `{IMAGE}:{VERSION}` (также `:latest`)

## Установка

1. Скачайте `email-ssl-sender-deploy-{TAG}.zip` и распакуйте на сервере.
2. Войдите в реестр (нужен токен GitHub с правом `read:packages`):
   ```bash
   echo ТОКЕН | docker login ghcr.io -u ВАШ_ЛОГИН --password-stdin
   ```
   Нет доступа к ghcr.io — загрузите образ из файла релиза:
   ```bash
   docker load -i email-ssl-sender-image-{TAG}.tar.gz
   ```
3. Дальше — по шагам из [инструкции](https://github.com/{REPOSITORY}#установка):
   ```bash
   cd email-ssl-sender
   ./scripts/gen-secrets.sh
   cp .env.example .env
   ./scripts/set-tunnel-token.sh   # токен Cloudflare Tunnel
   docker compose pull api         # не нужно, если образ загружен из файла
   docker compose run --rm migrate
   docker compose run --rm api create-superadmin --email admin@example.com
   docker compose up -d
   ```

## Обновление

```bash
./scripts/gen-secrets.sh
docker compose pull
docker compose run --rm migrate
docker compose up -d
```

Контрольные суммы файлов — в `SHA256SUMS`.
