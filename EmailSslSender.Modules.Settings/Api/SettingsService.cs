using System.Net;
using System.Text.Json;
using EmailSslSender.Modules.Settings.Catalog;
using EmailSslSender.Modules.Settings.Contracts;
using EmailSslSender.Modules.Settings.Reading;
using EmailSslSender.Modules.Settings.Storage;
using EmailSslSender.Modules.Settings.Values;
using EmailSslSender.Shared.Contracts.Audit;
using EmailSslSender.Shared.Contracts.Authz;
using EmailSslSender.Shared.Contracts.Settings;
using EmailSslSender.Shared.Data;
using EmailSslSender.Shared.Errors;

namespace EmailSslSender.Modules.Settings.Api;

/// <summary>
/// Правка системных настроек: проверка, запись и строка журнала.
/// </summary>
/// <remarks>
/// ⚠ ⚠ <b>Отказ ЦЕЛИКОМ, до первой записи.</b> Присланное проверяется всё, и только потом
/// пишется первый ключ. Половина новой политики паролей вместе с половиной старой хуже, чем
/// отклонённое изменение: администратор увидит «сохранено с ошибкой», а какая половина
/// применилась — узнает перебором.
///
/// ⚠ <b>Ключи защиты (<c>IsSecurity</c>) отдельного права здесь не требуют</b> — прав в
/// установке нет вовсе. Их защищает вход со вторым фактором: повторного кода на самом действии
/// нет (решение владельца, 2026-10-20).
///
/// ⚠ <b>Журнал пишется рядом с записью</b>: одна строка на применённую правку. Ничего не поменялось — строки нет: журнал не должен наполняться
/// нажатиями «Сохранить».
/// </remarks>
internal sealed class SettingsService(
    SenderDbContext db,
    SettingsRepository repository,
    SettingsCatalog catalog,
    SettingsReader reader,
    ICurrentUser current,
    IAuditWriter audit)
{
    /// <summary>Весь справочник вместе с действующими значениями.</summary>
    public async Task<IReadOnlyList<SettingOut>> ListAsync(CancellationToken cancellationToken = default)
    {
        var всё = await reader.AllAsync(cancellationToken);

        return [.. всё.Select(пара => Наружу(пара.Declaration, пара.Value))];
    }

    /// <summary>
    /// Применить правки и вернуть обновлённый справочник.
    /// </summary>
    /// <remarks>
    /// ⚠ <b><c>values</c> и <c>reset</c> — два разных поля, а не одно.</b> Означай <c>null</c> в
    /// <c>values</c> сброс, задать <c>null</c> обнуляемой настройке стало бы нечем — а «хранить
    /// журнал бессрочно» это именно заданный <c>null</c>, а не отсутствие значения.
    /// </remarks>
    public async Task<IReadOnlyList<SettingOut>> UpdateAsync(
        IReadOnlyDictionary<string, JsonElement>? values,
        IReadOnlyList<string>? reset,
        CancellationToken cancellationToken = default)
    {
        var правки = values ?? new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        var сбросы = reset ?? [];

        // --- 1. Всё присланное существует ---------------------------------------------------
        var объявления = new Dictionary<string, ISettingDeclaration>(StringComparer.Ordinal);

        foreach (var имя in правки.Keys.Concat(сбросы))
        {
            if (объявления.ContainsKey(имя))
            {
                continue;
            }

            var объявление = catalog.Find(имя)
                ?? throw new AppException(
                    ErrorCodes.UnknownSetting,
                    "Такой настройки не существует.",
                    HttpStatusCode.UnprocessableContent,
                    new Dictionary<string, object?>(StringComparer.Ordinal) { ["key"] = имя });

            // ⚠ Личное предпочтение системным адресом не правится. Не «нет прав» — «не туда»:
            // выпрашивать право, которое ничего не изменит, человека отправлять нельзя.
            if (объявление.Scope != SettingScope.System)
            {
                throw new AppException(
                    ErrorCodes.SettingLevelForbidden,
                    "Это личное предпочтение: оно правится под /api/me, а не настройками установки.",
                    HttpStatusCode.UnprocessableContent,
                    new Dictionary<string, object?>(StringComparer.Ordinal) { ["key"] = имя });
            }

            объявления[имя] = объявление;
        }

        // --- 2. Все значения годны --------------------------------------------------------
        var упаковано = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var (имя, значение) in правки)
        {
            упаковано[имя] = SettingValues.Pack(объявления[имя], значение);
        }

        // --- 4. И только теперь пишем ------------------------------------------------------
        var прежние = await Прежние(объявления.Values, cancellationToken);

        await db.InTransactionAsync(
            async token =>
            {
                var изменения = new Dictionary<string, object?>(StringComparer.Ordinal);

                foreach (var (имя, json) in упаковано)
                {
                    // ⚠ ⚠ Строка пишется ВСЕГДА, даже если значение совпало с действующим, и
                    // это не лишняя работа. Совпасть оно могло с УМОЛЧАНИЕМ: администратор,
                    // выбравший «хранить журнал бессрочно», выбрал именно это — а не «пусть
                    // будет как в коде». Не записав строку, мы отдали бы ему `source: default`
                    // сразу после сохранения, а следующая выкатка, сменившая умолчание,
                    // молча сменила бы и его настройку.
                    await repository.SetSystemAsync(имя, json, current.Id, token);

                    var стало = SettingValues.Read(объявления[имя], json);

                    // ⚠ А вот СТРОКА ЖУРНАЛА — только при настоящем изменении: журнал не должен
                    // наполняться нажатиями «Сохранить». Сравниваются РАЗОБРАННЫЕ значения, а не
                    // строки JSON: `jsonb` нормализует запись, и сравнение текстом писало бы
                    // событие там, где ничего не поменялось, — и молчало бы там, где поменялось.
                    if (Equals(прежние[имя], стало))
                    {
                        continue;
                    }

                    изменения[имя] = new Dictionary<string, object?>(StringComparer.Ordinal)
                    {
                        ["from"] = прежние[имя],
                        ["to"] = стало,
                    };
                }

                foreach (var имя in сбросы)
                {
                    if (!await repository.ResetSystemAsync(имя, token))
                    {
                        continue;
                    }

                    изменения[имя] = new Dictionary<string, object?>(StringComparer.Ordinal)
                    {
                        ["from"] = прежние[имя],
                        ["to"] = null,
                        ["reset"] = true,
                    };
                }

                // ⚠ Ничего не изменилось — строки нет. Журнал не должен наполняться нажатиями
                // «Сохранить»: тот же довод, по которому со стенда убрали `permission_check`.
                //
                // ⚠ ⚠ Ключи и значения «было → стало» уходят в строку лога (`LogChanges`): без
                // них журнал не ответил бы, кто и куда переставил `mail.smtp_host` или
                // `keymanager.base_url`. Поручиться можно: секретов в настройках нет по
                // устройству — пароль SMTP и ключ API лежат запечатанными в своих таблицах, а
                // системные настройки — параметры установки, а не сведения о людях.
                if (изменения.Count > 0)
                {
                    audit.Write(new AuditEntry(
                        SettingsActions.SystemUpdate,
                        Changes: изменения,
                        LogChanges: true));
                }

                await db.SaveChangesAsync(token);

                return изменения.Count;
            },
            cancellationToken);

        // ⚠ Снимок области устарел в момент записи — читаем заново, иначе экран показал бы
        // прежние значения сразу после «Сохранить».
        reader.Забыть();

        return await ListAsync(cancellationToken);
    }

    private async Task<Dictionary<string, object?>> Прежние(
        IEnumerable<ISettingDeclaration> объявления, CancellationToken cancellationToken)
    {
        var итог = new Dictionary<string, object?>(StringComparer.Ordinal);

        foreach (var объявление in объявления)
        {
            итог[объявление.Name] = (await reader.ResolveAsync(объявление, cancellationToken)).Value;
        }

        return итог;
    }

    private static SettingOut Наружу(ISettingDeclaration объявление, SettingValue значение) =>
        new(
            объявление.Name,
            объявление.Title,
            объявление.Description,
            объявление.Category,
            SettingValues.Имя(объявление),
            объявление.Scope == SettingScope.User ? "user" : "system",
            объявление.IsSecurity,
            объявление.IsNullable,
            объявление.RequiresRestart,
            объявление.Minimum,
            объявление.Maximum,
            объявление.Choices.Count > 0 ? объявление.Choices : null,
            значение.Value,
            объявление.DefaultValue,
            значение.Source switch
            {
                SettingsSource.System => "system",
                SettingsSource.Configuration => "configuration",
                _ => "default",
            });
}
