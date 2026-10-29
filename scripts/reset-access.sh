#!/bin/sh
# Вернуть доступ (потерян аутентификатор, забыт пароль, вход заблокирован):  ./scripts/reset-access.sh admin@example.com
# Новый пароль скрытым вводом; второй фактор, блокировка и сессии снимаются. Отключённую запись не включает.
set -eu

usage() {
    echo "Использование: $0 <адрес учётной записи>"
    echo "Пример:        $0 admin@example.com"
}

case "${1:-}" in
    -h|--help)
        usage
        exit 0
        ;;
    ""|-*)
        usage >&2
        exit 2
        ;;
esac

if [ "$#" -ne 1 ]; then
    usage >&2
    exit 2
fi

cd "$(dirname "$0")/.."

exec docker compose run --rm api reset-access --email "$1"
