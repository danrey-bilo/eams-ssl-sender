#!/bin/sh
# Создать файлы секретов: мастер-ключ, пароль владельца схемы (только migrate) и пароль роли sender_app.
# Повторный запуск безопасен: существующие файлы не трогает, недостающие досоздаёт.
# Запуск из корня репозитория:  ./scripts/gen-secrets.sh [каталог]   (на Windows — Git Bash)
set -eu

cd "$(dirname "$0")/.."

SECRETS_DIR="${1:-secrets}"

random_base64() {
    # 32 байта в base64 — формат мастер-ключа.
    head -c 32 /dev/urandom | base64 | tr -d '\n'
}

random_urlsafe() {
    # Без знаков, ломающих строку подключения.
    head -c 48 /dev/urandom | base64 | tr '+/' '-_' | tr -d '=\n'
}

write_secret() {
    name="$1"
    value="$2"
    what="$3"
    path="$SECRETS_DIR/$name"

    if [ -e "$path" ]; then
        # ⚠ Не перезаписывать: с новым мастер-ключом старые секреты не откроются (смена — ротацией, README).
        echo "  · $path уже существует — не трогаю ($what)" >&2
        return 0
    fi

    printf '%s' "$value" > "$path"
    # ⚠ 644, не 600: контейнер работает не от вашего uid; защищает каталог 700.
    chmod 644 "$path"
    echo "  + $path  ($what)"
}

mkdir -p "$SECRETS_DIR"
chmod 700 "$SECRETS_DIR"

echo "Создаю секреты в $SECRETS_DIR/:"
write_secret sender_master_kek "$(random_base64)" "мастер-ключ, base64 32 байта"
write_secret sender_postgres_password "$(random_urlsafe)" "пароль владельца схемы — только для службы migrate"
write_secret sender_app_password "$(random_urlsafe)" "пароль роли сервиса sender_app"
echo "Готово. Каталог $SECRETS_DIR/ в git не попадает (.gitignore); сохраните копию мастер-ключа отдельно от копий базы."
