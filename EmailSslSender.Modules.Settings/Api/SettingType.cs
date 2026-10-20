using System.Text.Json.Serialization;

namespace EmailSslSender.Modules.Settings.Api;

/// <summary>
/// Каким полем настройка рисуется на экране.
/// </summary>
/// <remarks>
/// ⚠ ⚠ <b>Перечисление, а не строка, и это починка настоящего дефекта (вскрытие 44).</b>
/// Сервер отдавал тип свободной строкой <c>"boolean"</c>, экран сравнивал с <c>'bool'</c> — и
/// не совпадало НИКОГДА. Отсюда булева настройка, нарисованная текстовым полем со словом
/// <c>false</c> внутри: администратор видел не флажок, а строку, которую можно заменить чем
/// угодно. Касалось всех булевых и целочисленных настроек установки.
///
/// ⚠ <b>Почему это не поймал ни один сторож и почему теперь поймает.</b> В контракте поле
/// было объявлено просто <c>string</c>: и <c>"boolean"</c>, и <c>'bool'</c> — одинаково
/// законные строки, сверять было не с чем. Перечисление уезжает в <c>openapi.json</c>
/// списком значений, оттуда — в типы веба, и сравнение с <c>'bool'</c> становится
/// **ошибкой компиляции**, а не молчаливым <c>false</c>.
///
/// ⚠ <b>Имена значений менять нельзя</b> — по ним рисует клиент. Новое поле ввода это новое
/// значение, а не переименование существующего (правило 30).
/// </remarks>
[JsonConverter(typeof(JsonStringEnumConverter<SettingType>))]
internal enum SettingType
{
    /// <summary>Флажок.</summary>
    [JsonStringEnumMemberName("boolean")]
    Boolean,

    /// <summary>Целое число.</summary>
    [JsonStringEnumMemberName("integer")]
    Integer,

    /// <summary>Выбор из закрытого списка.</summary>
    [JsonStringEnumMemberName("enum")]
    Enum,

    /// <summary>Строка.</summary>
    [JsonStringEnumMemberName("string")]
    String,
}

/// <summary>Имя вида настройки таким, каким его видит клиент.</summary>
internal static class SettingTypeNames
{
    /// <summary>
    /// Проводное имя вида.
    /// </summary>
    /// <remarks>
    /// ⚠ <b>Нужно там, где значение уезжает не полем DTO, а внутрь `details` конверта
    /// ошибки</b> (правило 17): там тип свойства — `object?`, и класть туда значение
    /// перечисления значит полагаться на то, как его сериализует чужой код. Клиенту в обоих
    /// местах должна приезжать одна и та же строка.
    /// </remarks>
    public static string Wire(this SettingType type) => type switch
    {
        SettingType.Boolean => "boolean",
        SettingType.Integer => "integer",
        SettingType.Enum => "enum",
        SettingType.String => "string",
        _ => throw new ArgumentOutOfRangeException(nameof(type), type, "Неизвестный вид настройки"),
    };
}
