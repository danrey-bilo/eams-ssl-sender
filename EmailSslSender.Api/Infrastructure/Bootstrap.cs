using System.Reflection;
using System.Text;
using Modules.Identity.Contracts;
using Modules.Core.Errors;

namespace EmailSslSender.Api.Infrastructure;

/// <summary>Консольные команды, выполняемые вместо запуска сервера.</summary>
internal static class Bootstrap
{
    /// <summary>Заведение первого администратора — публичной регистрации нет.</summary>
    public const string CreateSuperadmin = "create-superadmin";

    /// <summary>Смена пароля учётной записи из консоли.</summary>
    public const string SetSuperadminPassword = "set-superadmin-password";

    /// <summary>Снятие второго фактора из консоли — потерян телефон.</summary>
    public const string ResetSuperadminTwoFactor = "reset-superadmin-2fa";

    /// <summary>Восстановление доступа разом: пароль, второй фактор, блокировка, сессии.</summary>
    public const string ResetAccess = "reset-access";

    /// <summary>Переложить обёртки под действующую версию мастер-ключа; после неё старую можно убрать.</summary>
    public const string RewrapKeys = "rewrap-keys";

    /// <summary>Выгрузить документ OpenAPI без сервера и базы.</summary>
    public const string ExportOpenApi = "export-openapi";

    /// <summary>Применить миграции — ходит владельцем схемы, отдельным шагом от старта.</summary>
    public const string Migrate = "migrate";

    /// <summary>Пересчитать цепочку хешей журнала: 0 — цел, 2 — разрыв.</summary>
    public const string VerifyAuditChain = "verify-audit-chain";

    /// <summary>Проба готовности для контейнера: в образе нет <c>curl</c>.</summary>
    /// <remarks>⚠ Не входит в <see cref="IsCommand"/>: проба не должна строить приложение целиком.</remarks>
    public const string Health = "health";

    /// <summary>Спросить у себя <c>/api/ready</c>: 0 — готов, 1 — нет.</summary>
    /// <remarks>⚠ <c>127.0.0.1</c>, а не <c>localhost</c>: тот разрешается и в <c>::1</c>.</remarks>
    public static async Task<int> ProbeAsync()
    {
        var порт = Environment.GetEnvironmentVariable("ASPNETCORE_HTTP_PORTS") is { Length: > 0 } значение
            && int.TryParse(значение.Split(';')[0], out var разобранный)
                ? разобранный
                : 8001;

        using var клиент = new HttpClient { Timeout = TimeSpan.FromSeconds(4) };

        try
        {
            using var ответ = await клиент.GetAsync(new Uri($"http://127.0.0.1:{порт}/api/ready"));

            if (ответ.IsSuccessStatusCode)
            {
                return 0;
            }

            // Тело называет несошедшуюся проверку — иначе «unhealthy» без причины.
            await Console.Error.WriteLineAsync(
                $"{Health}: /api/ready ответил {(int)ответ.StatusCode} — "
                + await ответ.Content.ReadAsStringAsync());

            return 1;
        }
        catch (Exception ошибка) when (ошибка is HttpRequestException or TaskCanceledException)
        {
            await Console.Error.WriteLineAsync($"{Health}: {ошибка.Message}");

            return 1;
        }
    }

    /// <summary>Версия установки из сборки хоста, без метаданных сборки после <c>+</c>.</summary>
    public static string InstallationVersion(Assembly assembly)
    {
        ArgumentNullException.ThrowIfNull(assembly);

        var версия = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? assembly.GetName().Version?.ToString()
            ?? "0.0.0";

        var плюс = версия.IndexOf('+', StringComparison.Ordinal);

        return плюс >= 0 ? версия[..плюс] : версия;
    }

    /// <summary>Есть ли в аргументах команда, а не запуск сервера.</summary>
    public static bool IsCommand(string[] args) =>
        args.Length > 0
        && args[0] is CreateSuperadmin or SetSuperadminPassword or ResetSuperadminTwoFactor or ResetAccess
            or RewrapKeys or ExportOpenApi or Migrate or VerifyAuditChain;

    /// <summary>Отметка сброса доступа из окружения: HMAC-SHA256 от «адрес + пароль» под ключом из мастер-ключа.</summary>
    /// <remarks>⚠ HMAC — без мастер-ключа пароль не подобрать; строка разделения входит в отметку — не менять.</remarks>
    public static string ResetAccessMark(string masterKey, string email, string password)
    {
        ArgumentNullException.ThrowIfNull(masterKey);
        ArgumentNullException.ThrowIfNull(email);
        ArgumentNullException.ThrowIfNull(password);

        var ключ = System.Security.Cryptography.HKDF.DeriveKey(
            System.Security.Cryptography.HashAlgorithmName.SHA256,
            System.Text.Encoding.UTF8.GetBytes(masterKey),
            32,
            info: System.Text.Encoding.UTF8.GetBytes("sender:reset-access-mark:v1"));

        return Convert.ToHexStringLower(System.Security.Cryptography.HMACSHA256.HashData(
            ключ, System.Text.Encoding.UTF8.GetBytes(email.Trim().ToLowerInvariant() + "\n" + password)));
    }

    /// <summary>Проверить параметры команд суперпользователя до чтения пароля.</summary>
    public static void ValidateSuperuserArguments(string[] args)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 1; index < args.Length; index += 2)
        {
            var option = args[index];
            if (option == "--password")
            {
                throw new AppException(ErrorCodes.Validation,
                    "Пароль нельзя передавать аргументом: используйте скрытый ввод или --password-file",
                    System.Net.HttpStatusCode.BadRequest);
            }

            var allowed = option == "--email"
                || (option == "--password-file" && args[0] != ResetSuperadminTwoFactor)
                || (option == "--display-name" && args[0] == CreateSuperadmin);
            if (!allowed || !names.Add(option) || index + 1 >= args.Length
                || string.IsNullOrWhiteSpace(args[index + 1]) || args[index + 1].StartsWith("--", StringComparison.Ordinal))
            {
                throw new AppException(ErrorCodes.Validation, "Недопустимый, повторный или незаполненный параметр команды",
                    System.Net.HttpStatusCode.BadRequest);
            }
        }

        if (!names.Contains("--email"))
        {
            throw new AppException(ErrorCodes.Validation, "Не задан --email", System.Net.HttpStatusCode.BadRequest);
        }
    }

    /// <summary>Прочитать пароль из файла или скрытым вводом.</summary>
    /// <remarks>⚠ Никогда из аргументов: они видны в списке процессов и истории оболочки.</remarks>
    /// <param name="file">Файл с паролем; <c>null</c> — спросить скрытым вводом дважды.</param>
    /// <param name="label">Что спросить при скрытом вводе.</param>
    public static string ReadPassword(string? file, string label = "Пароль администратора: ")
    {
        if (file is { Length: > 0 })
        {
            string content;

            try
            {
                content = File.ReadAllText(file, Encoding.UTF8);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                // Тип, а не текст исключения.
                throw new AppException(
                    "PASSWORD_FILE_UNREADABLE",
                    $"Не удалось прочитать файл с паролем ({SafeError.Describe(error)})",
                    System.Net.HttpStatusCode.BadRequest);
            }

            // Только перевод строки редактора: пробелы — часть пароля.
            var password = content.TrimEnd('\r', '\n');

            return password.Length > 0
                ? password
                : throw new AppException(
                    "PASSWORD_FILE_EMPTY", "Файл с паролем пуст", System.Net.HttpStatusCode.BadRequest);
        }

        if (Console.IsInputRedirected)
        {
            throw new AppException(ErrorCodes.Validation,
                "Для скрытого ввода нужен интерактивный терминал; иначе используйте --password-file",
                System.Net.HttpStatusCode.BadRequest);
        }

        var first = Prompt(label);
        var second = Prompt("Повторите пароль: ");

        return first == second
            ? first
            : throw new AppException(
                "PASSWORD_MISMATCH", "Пароли не совпадают", System.Net.HttpStatusCode.BadRequest);
    }

    /// <summary>Итог <see cref="ResetAccess"/> для консоли; отключённая учётная запись называется прямо.</summary>
    public static string ResetAccessSummary(AccessResetResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        var строки = new List<string>
        {
            $"{ResetAccess}: учётная запись {result.Email} ({result.UserId})",
            "  - пароль заменён новым;",
            result.HadTwoFactor
                ? "  - второй фактор снят: приложение-аутентификатор подключается заново при следующем входе;"
                : "  - второго фактора не было: приложение-аутентификатор подключается при следующем входе;",
            "  - блокировка входа снята, счётчик неудачных попыток обнулён;",
            $"  - закрыто сессий: {result.SessionsRevoked}; ссылки приглашения и сброса, коды входа погашены.",
        };

        строки.Add(result.IsActive
            ? "Можно входить: адрес и новый пароль (если почта настроена — ещё код из письма), "
              + "затем подключение приложения-аутентификатора."
            : "ВНИМАНИЕ: учётная запись ОТКЛЮЧЕНА, и команда её не включает. Войти нельзя, пока её "
              + "не включит другой администратор (панель: «Пользователи» → «Включить»).");

        return string.Join(Environment.NewLine, строки);
    }

    /// <summary>Значение именованного аргумента или <c>null</c>.</summary>
    public static string? Argument(string[] args, string name)
    {
        var index = Array.IndexOf(args, name);

        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }

    private static string Prompt(string label)
    {
        Console.Write(label);

        var buffer = new StringBuilder();

        while (true)
        {
            var key = Console.ReadKey(intercept: true);

            if (key.Key == ConsoleKey.Enter)
            {
                Console.WriteLine();

                return buffer.ToString();
            }

            if (key.Key == ConsoleKey.Backspace)
            {
                if (buffer.Length > 0)
                {
                    buffer.Length--;
                }

                continue;
            }

            if (!char.IsControl(key.KeyChar))
            {
                buffer.Append(key.KeyChar);
            }
        }
    }
}
