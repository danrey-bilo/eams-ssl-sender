using EmailSslSender.Modules.Identity.Settings;
using System.Net;
using System.Text;
using EmailSslSender.Shared.Contracts.Settings;
using EmailSslSender.Shared.Errors;
using EmailSslSender.Modules.Identity.Passwords;

namespace EmailSslSender.Modules.Identity.Users;

/// <summary>
/// Требования к паролю.
/// </summary>
/// <remarks>
/// <b>Все нарушения возвращаются разом</b>, а не первое найденное. Иначе человек узнаёт
/// требования по одному за попытку, и форма смены пароля превращается в игру «угадай
/// следующее правило».
///
/// Рядом с русскими строками уезжают <b>машинные коды</b>. На Python-версии этого сначала
/// не было: русские строки подставлялись в переведённый шаблон, и на английском выходило
/// «The password does not meet the requirements: минимум 12 символов».
///
/// ⚠ Проверяется при заведении, активации и смене пароля — но <b>никогда при входе</b>.
/// Проверка на входе означала бы, что ужесточение требований выкидывает из системы всех,
/// чей пароль перестал им соответствовать, — включая администратора, который их и ужесточил.
/// </remarks>
internal sealed class PasswordPolicy(ISettingsReader settings)
{
    /// <summary>Проверить пароль. Не подошёл — исключение со всем списком нарушений.</summary>
    public async Task EnsureAsync(string password, CancellationToken cancellationToken = default)
    {
        var codes = new List<string>();
        var reasons = new List<string>();

        var minLength = await settings.GetAsync(IdentitySettings.PasswordMinLength, cancellationToken);

        if (password.Length < minLength)
        {
            codes.Add("min_length");
            reasons.Add($"минимум {minLength} символов");
        }

        // ⚠ ⚠ ⚠ ВЕРХНЯЯ ГРАНИЦА — В БАЙТАХ, А НЕ В СИМВОЛАХ, и это не педантизм. Предел хешера
        // (`PasswordHasher.MaxPasswordBytes`) считает байты UTF-8: кириллический пароль из
        // шестисот символов — это тысяча двести байт, то есть он проходит любую проверку по
        // длине строки и падает уже внутри argon2.
        //
        // ⚠ ⚠ До этапа 59.6 верхней границы здесь не было вовсе, и превышение давало
        // `ArgumentOutOfRangeException` из библиотеки — то есть **пятисотку вместо конверта**
        // (правило 17), а в лог уезжала строка `unhandled_exception`. Уткнуться в это проще
        // всего не злым умыслом: менеджер паролей вставляет в поле ключ вместо пароля.
        //
        // ⚠ Атрибуты `[StringLength(1024)]` на моделях запроса **не заменяют** эту проверку, а
        // дополняют её: они дешевле (отказ до argon2) и точнее по месту (поле в конверте
        // валидации), но считают символы. Настоящую границу знает только эта строка.
        if (Encoding.UTF8.GetByteCount(password) > PasswordHasher.MaxPasswordBytes)
        {
            codes.Add("max_length");
            reasons.Add($"не длиннее {PasswordHasher.MaxPasswordBytes} байт");
        }

        await CheckAsync(
            IdentitySettings.RequireUppercase, char.IsUpper, "uppercase", "заглавная буква",
            password, codes, reasons, cancellationToken);
        await CheckAsync(
            IdentitySettings.RequireLowercase, char.IsLower, "lowercase", "строчная буква",
            password, codes, reasons, cancellationToken);
        await CheckAsync(
            IdentitySettings.RequireDigit, char.IsDigit, "digit", "цифра",
            password, codes, reasons, cancellationToken);
        await CheckAsync(
            // «Не буква и не цифра» — а не список знаков: перечислять их значит однажды
            // забыть тот, что человек и набрал.
            IdentitySettings.RequireSymbol, symbol => !char.IsLetterOrDigit(symbol), "symbol", "спецсимвол",
            password, codes, reasons, cancellationToken);

        if (codes.Count == 0)
        {
            return;
        }

        // ⚠ Сам пароль в текст ошибки не попадает ни при каких обстоятельствах: ответы
        // валидации уходят в логи клиента и в отчёты об ошибках.
        throw new AppException(
            ErrorCodes.PasswordPolicyViolation,
            "Пароль не соответствует требованиям: " + string.Join(", ", reasons),
            HttpStatusCode.BadRequest,
            new Dictionary<string, object?> { ["codes"] = codes, ["unmet"] = reasons });
    }

    /// <summary>
    /// Действующие требования — чтобы экран показал их ДО попытки.
    /// </summary>
    /// <remarks>
    /// ⚠ ⚠ <b>Читается тем же читателем настроек, что и проверка</b> (этап 37.1). Собери список
    /// требований отдельно — и он разойдётся с проверкой в тот день, когда добавят седьмое
    /// правило: экран покажет шесть, отказ назовёт седьмое, и человек будет прав, решив, что
    /// программа врёт.
    ///
    /// ⚠ Возвращается <b>описание</b>, а не тексты: слова подбирает клиент по своим словарям, и
    /// коды здесь те же, что в отказе.
    /// </remarks>
    public async Task<PasswordRules> CurrentAsync(CancellationToken cancellationToken = default) =>
        new(
            await settings.GetAsync(IdentitySettings.PasswordMinLength, cancellationToken),
            PasswordHasher.MaxPasswordBytes,
            await settings.GetAsync(IdentitySettings.RequireUppercase, cancellationToken),
            await settings.GetAsync(IdentitySettings.RequireLowercase, cancellationToken),
            await settings.GetAsync(IdentitySettings.RequireDigit, cancellationToken),
            await settings.GetAsync(IdentitySettings.RequireSymbol, cancellationToken));

    private async Task CheckAsync(
        SettingKey<bool> key,
        Func<char, bool> predicate,
        string code,
        string reason,
        string password,
        List<string> codes,
        List<string> reasons,
        CancellationToken cancellationToken)
    {
        if (await settings.GetAsync(key, cancellationToken) && !password.Any(predicate))
        {
            codes.Add(code);
            reasons.Add(reason);
        }
    }
}

/// <summary>Действующие требования к паролю. Спутник <see cref="PasswordPolicy"/>.</summary>
/// <remarks>
/// ⚠ Поля повторяют коды отказа один в один: <c>min_length</c>, <c>max_length</c>,
/// <c>uppercase</c>, <c>lowercase</c>, <c>digit</c>, <c>symbol</c>. Иначе список требований и
/// список нарушений — два разных списка про одно и то же.
/// </remarks>
internal sealed record PasswordRules(
    int MinLength,
    int MaxLengthBytes,
    bool RequireUppercase,
    bool RequireLowercase,
    bool RequireDigit,
    bool RequireSymbol);
