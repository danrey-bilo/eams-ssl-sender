#!/usr/bin/env bash
# Выгрузить OpenAPI без сервера и базы:  ./scripts/export-openapi.sh [путь]   # по умолчанию ./openapi.json
# Ключ и строка подключения — фиктивные: без них хост не стартует, к базе команда не ходит.
set -euo pipefail

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
target="${1:-$root/openapi.json}"

target="$(cd "$(dirname "$target")" && pwd)/$(basename "$target")"

key="$(head -c 32 /dev/urandom | base64)"

SENDER_MASTER_KEK="$key" \
ConnectionStrings__Postgres="Host=127.0.0.1;Database=none;Username=none;Password=none" \
    dotnet run --project "$root/EmailSslSender.Api/EmailSslSender.Api.csproj" -- \
    export-openapi --out "$target"
