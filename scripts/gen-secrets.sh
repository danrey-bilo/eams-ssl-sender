#!/bin/sh
# Создать файлы секретов установки: мастер-ключ и пароль базы.
#
# Значения намеренно не живут в переменных окружения: переменная видна в `docker inspect`
# и в /proc/<pid>/environ, а вывод inspect пересылают в тикет целиком.
#
# Скрипт безопасно запускать повторно: существующие файлы он НЕ трогает.
#
# Запуск из корня репозитория:  ./scripts/gen-secrets.sh [каталог]
# На Windows — через Git Bash.
set -eu

cd "$(dirname "$0")/.."

SECRETS_DIR="${1:-secrets}"

random_base64() {
    # 32 байта из /dev/urandom в base64 — ровно то, чего ждёт MasterKeyRing.Parse.
    head -c 32 /dev/urandom | base64 | tr -d '\n'
}

random_urlsafe() {
    # 48 байт, безопасные для строки подключения: ни @, ни :, ни /, ни #, ни ;.
    head -c 48 /dev/urandom | base64 | tr '+/' '-_' | tr -d '=\n'
}

write_secret() {
    name="$1"
    value="$2"
    what="$3"
    path="$SECRETS_DIR/$name"

    if [ -e "$path" ]; then
        # ⚠ Перезапись — это не «обновление», а потеря. Перезаписанный мастер-ключ означает,
        # что ни пароль SMTP, ни секреты TOTP больше не откроются: их ключи данных обёрнуты
        # СТАРЫМ ключом. Смена ключа — это ротация (README, раздел «Мастер-ключ»).
        echo "  · $path уже существует — не трогаю ($what)" >&2
        return 0
    fi

    printf '%s' "$value" > "$path"
    # ⚠ 644 на файл и 700 на каталог, а не 600 на файл. Docker монтирует секрет файлом с
    # хозяйскими правами, а приложение в контейнере работает НЕ от вашего uid ($APP_UID=1654):
    # файл 600 оно не прочтёт и не стартует. Посторонних на машине держит каталог 700.
    chmod 644 "$path"
    echo "  + $path  ($what)"
}

mkdir -p "$SECRETS_DIR"
chmod 700 "$SECRETS_DIR"

echo "Создаю секреты в $SECRETS_DIR/:"
write_secret sender_master_kek "$(random_base64)" "мастер-ключ, base64 32 байта"
write_secret sender_postgres_password "$(random_urlsafe)" "пароль базы"
echo "Готово. Каталог $SECRETS_DIR/ в git не попадает (.gitignore); сохраните копию мастер-ключа отдельно от копий базы."
