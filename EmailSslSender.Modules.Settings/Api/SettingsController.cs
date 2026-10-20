using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Mvc;

namespace EmailSslSender.Modules.Settings.Api;

/// <summary>
/// Настройка на экране: объявление, действующее значение и откуда оно взялось.
/// </summary>
/// <remarks>
/// ⚠ <b>Имена полей в <c>snake_case</c></b> — как у соседей и как у Python. Хук экрана
/// настроек в вебе читает поля по этим именам; каждое, ушедшее в <c>camelCase</c>, — это одно
/// поле, которое экран перестанет находить, причём молча.
///
/// ⚠ <b><c>updated_by</c> наружу НЕ уходит.</b> Захочет экран показать «кто поднял лимит» —
/// имя берётся через <c>IResourceNameResolver</c> с типом <c>user</c> у <c>Identity</c>, а не
/// своей выдачей: одна функция — один владелец (решение 0016).
/// </remarks>
internal sealed record SettingOut(
    [property: JsonPropertyName("key")] string Key,
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("description")] string Description,
    [property: JsonPropertyName("category")] string Category,
    [property: JsonPropertyName("type")] SettingType Type,
    [property: JsonPropertyName("is_security")] bool IsSecurity,
    [property: JsonPropertyName("nullable")] bool Nullable,
    [property: JsonPropertyName("requires_restart")] bool RequiresRestart,
    [property: JsonPropertyName("minimum")] double? Minimum,
    [property: JsonPropertyName("maximum")] double? Maximum,
    [property: JsonPropertyName("choices")] IReadOnlyList<string>? Choices,
    [property: JsonPropertyName("value")] object? Value,
    [property: JsonPropertyName("default")] object? Default,
    [property: JsonPropertyName("source")] string Source);

/// <summary>Что править и что сбросить.</summary>
/// <remarks>
/// ⚠ <b>Два поля, а не одно, и это несущее различие.</b> Означай <c>null</c> в <c>values</c>
/// сброс, задать <c>null</c> обнуляемой настройке стало бы нечем — а «хранить журнал
/// бессрочно» это именно заданный <c>null</c>, а не отсутствие значения.
/// </remarks>
internal sealed record SettingsPatchIn(
    [property: JsonPropertyName("values")] Dictionary<string, JsonElement>? Values,
    [property: JsonPropertyName("reset")] List<string>? Reset);

/// <summary>
/// Настройки установки.
/// </summary>
/// <remarks>
/// ⚠ <b>Справочник отдаётся ВЕСЬ, а не только заданное.</b> Иначе «настройки по умолчанию»
/// существуют только в коде, и показать их экрану нечем: администратор увидел бы пустой список
/// и решил, что настроек нет.
///
/// ⚠ Читать и править может любой вошедший — каждая учётная запись здесь администратор. Правка
/// ключа защиты (<c>is_security</c>) требует свежего второго фактора: иначе
/// <c>403 SECOND_FACTOR_REQUIRED</c>, и не применяется ничего. Спрашивает его служба
/// (<see cref="SettingsService"/>), а не атрибут: какие ключи пришли, видно лишь из тела.
/// </remarks>
[ApiController]
[Route("api/settings/system")]
internal sealed class SettingsController(SettingsService settings) : ControllerBase
{
    /// <summary>Весь справочник вместе с действующими значениями и источниками.</summary>
    [HttpGet]
    public Task<IReadOnlyList<SettingOut>> List(CancellationToken cancellationToken) =>
        settings.ListAsync(cancellationToken);

    /// <summary>Изменить и сбросить настройки; ответ — тот же справочник.</summary>
    /// <remarks>
    /// ⚠ Ответом идёт <b>весь</b> справочник, а не только тронутое: экран настроек после
    /// сохранения перерисовывается целиком, и второй запрос за тем же ради экономии полей
    /// стоил бы дороже, чем эти поля.
    ///
    /// ⚠ Есть среди правок или сбросов хоть один ключ защиты — нужен код из приложения,
    /// подтверждённый на этой сессии за последние <c>auth.step_up_minutes</c> минут; иначе
    /// <c>403 SECOND_FACTOR_REQUIRED</c> (<c>details.window_minutes</c>) и ни одной записи.
    /// </remarks>
    [HttpPatch]
    public Task<IReadOnlyList<SettingOut>> Update(
        [FromBody] SettingsPatchIn body, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(body);

        return settings.UpdateAsync(body.Values, body.Reset, cancellationToken);
    }
}
