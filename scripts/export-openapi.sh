#!/usr/bin/env bash
# Выгрузить документ OpenAPI в файл: без сервера и без базы.
#
#   ./scripts/export-openapi.sh [путь]     # по умолчанию ./openapi.json
#
# ⚠ Хост без мастер-ключа и строки подключения не стартует даже ради выгрузки, поэтому
# скрипт подставляет одноразовый ключ и строку, по которой никто не ходит: документ
# строится из описаний эндпоинтов, к базе команда не обращается.
set -euo pipefail

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
target="${1:-$root/openapi.json}"

target="$(cd "$(dirname "$target")" && pwd)/$(basename "$target")"

key="$(head -c 32 /dev/urandom | base64)"

SENDER_MASTER_KEK="$key" \
ConnectionStrings__Postgres="Host=127.0.0.1;Database=none;Username=none;Password=none" \
    dotnet run --project "$root/EmailSslSender.Api/EmailSslSender.Api.csproj" -- \
    export-openapi --out "$target"
