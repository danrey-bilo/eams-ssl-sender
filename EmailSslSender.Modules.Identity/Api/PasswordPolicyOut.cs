using System.Text.Json.Serialization;

namespace EmailSslSender.Modules.Identity.Api;

/// <summary>
/// Требования к паролю — так, как их можно показать человеку до попытки.
/// </summary>
/// <remarks>
/// ⚠ ⚠ ⚠ <b>ЗАВЕДЕНО ПОТОМУ, ЧТО ТРЕБОВАНИЯ УЗНАВАЛИСЬ ТОЛЬКО ОТКАЗОМ (этап 37.1).</b> Веб-экраны
/// смены пароля и активации <b>обещали чек-лист комментарием в разметке</b> — «правила показаны до
/// попытки и отмечаются по мере набора», — а показать их было нечем: этого адреса на сервере не
/// существовало вовсе, и <c>ActivationInfoOut</c> нёс только адрес почты и имя. Человек придумывал
/// пароль, получал список требований отказом и придумывал заново.
///
/// ⚠ ⚠ <b>Коды те же, что в отказе</b> (<c>min_length</c>, <c>uppercase</c>, …): «нужно X» и «не
/// хватает X» обязаны звучать одинаково, иначе человек читает два разных списка про одно и то же.
/// Разойдись они — и второй перестанет быть подсказкой к первому.
///
/// ⚠ <b>Длина в СИМВОЛАХ, а верхняя граница в БАЙТАХ</b>, и обе названы отдельными полями. Предел
/// хешера считает байты UTF-8: кириллический пароль из шестисот символов — это тысяча двести байт.
/// Показывать одно число вместо двух значило бы соврать одной из двух половин.
/// </remarks>
/// <param name="MinLength">Наименьшая длина в символах.</param>
/// <param name="MaxLengthBytes">Наибольшая длина в байтах UTF-8 — предел хешера.</param>
/// <param name="RequireUppercase">Нужна заглавная буква.</param>
/// <param name="RequireLowercase">Нужна строчная буква.</param>
/// <param name="RequireDigit">Нужна цифра.</param>
/// <param name="RequireSymbol">Нужен знак, который не буква и не цифра.</param>
internal sealed record PasswordPolicyOut(
    [property: JsonPropertyName("min_length")] int MinLength,
    [property: JsonPropertyName("max_length_bytes")] int MaxLengthBytes,
    [property: JsonPropertyName("require_uppercase")] bool RequireUppercase,
    [property: JsonPropertyName("require_lowercase")] bool RequireLowercase,
    [property: JsonPropertyName("require_digit")] bool RequireDigit,
    [property: JsonPropertyName("require_symbol")] bool RequireSymbol);
