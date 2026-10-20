#!/bin/sh
# Записать токен Cloudflare Tunnel в файл секрета secrets/cloudflare_tunnel_token.
#
# Токен спрашивается скрытым вводом: в истории оболочки, в `ps` и в `docker inspect` его не
# будет. Повторный запуск заменяет токен (после перевыпуска в Cloudflare), затем:
#   docker compose up -d cloudflared
#
# Запуск из корня установки:  ./scripts/set-tunnel-token.sh [каталог]
set -eu

cd "$(dirname "$0")/.."

SECRETS_DIR="${1:-secrets}"
path="$SECRETS_DIR/cloudflare_tunnel_token"

printf 'Токен туннеля (Zero Trust → Networks → Tunnels → туннель → строка после --token): '
stty -echo 2>/dev/null || true
IFS= read -r token
stty echo 2>/dev/null || true
echo

token=$(printf '%s' "$token" | tr -d ' \r\n')

case "$token" in
    ey*) ;;
    *)
        echo "Это не похоже на токен туннеля (он начинается с «ey»). Файл не изменён." >&2
        exit 1
        ;;
esac

mkdir -p "$SECRETS_DIR"
chmod 700 "$SECRETS_DIR"
umask 022
printf '%s' "$token" > "$path"
# ⚠ 644, а не 600: cloudflared в контейнере работает не от вашего uid и файл 600 не прочтёт.
# Посторонних на машине держит каталог 700.
chmod 644 "$path"
echo "  + $path"
