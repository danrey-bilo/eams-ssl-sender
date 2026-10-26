// РАЗРУШАЮЩИЕ ОПЕРАЦИИ: снимается `mail_outbox.attachments` прежнего формата; неотправленные письма отмечаются
// умершими. ОБРАТИМОСТЬ: `Down` возвращает прежнюю схему, но не данные — вложения и письма, умершие здесь, не
// возвращаются. Перед `migrate` снимается копия базы (README, «Обновление до 2.0»).
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Modules.Core.Data;

#nullable disable

namespace EmailSslSender.Api.Migrations
{
    /// <summary>
    /// Мост к модулям EAMS: прежняя схема хоста доводится до базовых миграций модулей.
    /// </summary>
    /// <remarks>
    /// ⚠ ⚠ <b>Последняя миграция прежнего набора</b> (<c>SchemaMigrator.LegacyLast</c>). После неё базовые
    /// миграции модулей identity, tokens, settings, mail и keymanager отмечаются применёнными без выполнения:
    /// схема, которую они создают, здесь уже есть. Что две дороги — новая установка и обновлённая — дают
    /// одну схему, проверяет тест сравнением каталога.
    ///
    /// ⚠ <b>Каждая учётная запись — суперпользователь.</b> Ролей в проекте нет: все администраторы равны, а
    /// модуль учётных записей разрешает распоряжаться суперпользователем только суперпользователю.
    ///
    /// ⚠ <b>Неотправленные письма умирают</b>: их тела и вложения запечатаны прежним конвертом, который
    /// модуль почты не открывает, а вложения прежнего формата модуль не читает. Насос перед обновлением
    /// успевает отправить очередь за секунды; оставшееся видно в журнале доставки, сертификат
    /// отправляется заново кнопкой «Отправить».
    /// </remarks>
    [DbContext(typeof(AppDbContext))]
    [Migration("20261026200000_ModulesBridge")]
    public partial class ModulesBridge : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                ALTER TABLE users
                    ADD COLUMN is_superuser boolean NOT NULL DEFAULT false,
                    ADD COLUMN department character varying(128),
                    ADD COLUMN second_factor_method text;

                UPDATE users SET is_superuser = true;

                ALTER TABLE identity_tokens
                    ADD COLUMN client_name character varying(300),
                    ADD COLUMN client_kind character varying(32),
                    ADD COLUMN user_agent character varying(512),
                    ADD COLUMN address inet,
                    ADD COLUMN last_seen_at timestamp with time zone,
                    ADD COLUMN device_id uuid,
                    DROP CONSTRAINT ck_identity_tokens_purpose,
                    ADD CONSTRAINT ck_identity_tokens_purpose CHECK (purpose IN (
                        'activation', 'reset', 'two_factor', 'login_code', 'step_up_code',
                        'trusted_device', 'superuser_setup'));

                ALTER TABLE auth_tokens
                    ADD COLUMN device_ref character varying(256),
                    ADD COLUMN scopes jsonb,
                    DROP CONSTRAINT ck_auth_tokens_kind,
                    ADD CONSTRAINT ck_auth_tokens_kind CHECK (kind IN ('session', 'device', 'api_key', 'download_ticket')),
                    ADD CONSTRAINT ck_auth_tokens_scopes CHECK (
                        (kind = 'api_key' AND scopes IS NOT NULL AND jsonb_array_length(scopes) > 0)
                        OR (kind = 'download_ticket' AND (scopes IS NULL OR jsonb_array_length(scopes) = 0))
                        OR (kind NOT IN ('api_key', 'download_ticket') AND scopes IS NULL));

                CREATE INDEX ix_auth_tokens_device ON auth_tokens (device_ref)
                    WHERE device_ref IS NOT NULL AND revoked_at IS NULL;

                CREATE UNIQUE INDEX ux_auth_tokens_user_name ON auth_tokens (user_id, name)
                    WHERE kind = 'api_key' AND revoked_at IS NULL;

                ALTER TABLE auth_token_secrets
                    ADD COLUMN selector character varying(64),
                    DROP CONSTRAINT ck_auth_token_secrets_kind,
                    ADD CONSTRAINT ck_auth_token_secrets_kind CHECK (kind IN ('access', 'refresh', 'key'));

                CREATE UNIQUE INDEX ux_auth_token_secrets_selector ON auth_token_secrets (selector)
                    WHERE selector IS NOT NULL;

                ALTER TABLE mail_outbox ADD COLUMN expires_at timestamp with time zone;

                UPDATE mail_outbox
                SET expires_at = now(),
                    last_error = 'Не отправлено до перехода на модули EAMS (версия 2.0): письмо запечатано прежним конвертом'
                WHERE sent_at IS NULL;

                ALTER TABLE mail_outbox DROP COLUMN attachments;

                CREATE TABLE user_preferences (
                    user_id uuid NOT NULL,
                    key character varying(128) NOT NULL,
                    value jsonb NOT NULL,
                    updated_at timestamp with time zone NOT NULL DEFAULT now(),
                    CONSTRAINT pk_user_preferences PRIMARY KEY (user_id, key));
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                DROP TABLE user_preferences;

                ALTER TABLE mail_outbox ADD COLUMN attachments bytea;
                ALTER TABLE mail_outbox DROP COLUMN expires_at;

                DROP INDEX ux_auth_token_secrets_selector;
                DELETE FROM auth_token_secrets WHERE kind NOT IN ('access', 'refresh');
                ALTER TABLE auth_token_secrets
                    DROP CONSTRAINT ck_auth_token_secrets_kind,
                    ADD CONSTRAINT ck_auth_token_secrets_kind CHECK (kind IN ('access', 'refresh')),
                    DROP COLUMN selector;

                DROP INDEX ux_auth_tokens_user_name;
                DROP INDEX ix_auth_tokens_device;
                DELETE FROM auth_tokens WHERE kind <> 'session';
                ALTER TABLE auth_tokens
                    DROP CONSTRAINT ck_auth_tokens_scopes,
                    DROP CONSTRAINT ck_auth_tokens_kind,
                    ADD CONSTRAINT ck_auth_tokens_kind CHECK (kind = 'session'),
                    DROP COLUMN scopes,
                    DROP COLUMN device_ref;

                DELETE FROM identity_tokens WHERE purpose IN ('trusted_device', 'superuser_setup');
                ALTER TABLE identity_tokens
                    DROP CONSTRAINT ck_identity_tokens_purpose,
                    ADD CONSTRAINT ck_identity_tokens_purpose CHECK (purpose IN (
                        'activation', 'reset', 'two_factor', 'login_code', 'step_up_code')),
                    DROP COLUMN device_id,
                    DROP COLUMN last_seen_at,
                    DROP COLUMN address,
                    DROP COLUMN user_agent,
                    DROP COLUMN client_kind,
                    DROP COLUMN client_name;

                ALTER TABLE users
                    DROP COLUMN second_factor_method,
                    DROP COLUMN department,
                    DROP COLUMN is_superuser;
                """);
        }
    }
}
