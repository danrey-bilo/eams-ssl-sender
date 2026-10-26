using System.Reflection;
using System.Text;
using Modules.Identity.Contracts;
using Modules.Core.Errors;

namespace EmailSslSender.Api.Infrastructure;

/// <summary>
/// Команды, которые выполняются вместо запуска сервера.
/// </summary>
/// <remarks>
/// Публичной регистрации нет: первая учётная запись заводится командой, и другого способа
/// начать работу с системой не существует. Отсюда и место — тот же образ, тот же
/// исполняемый файл, та же конфигурация и та же строка подключения.
///
/// Вызывается так: <c>docker compose run --rm api create-superadmin --email admin@example.com</c>.
/// </remarks>
internal static class Bootstrap
{
    /// <summary>Имя команды заведения первого администратора.</summary>
    public const string CreateSuperadmin = "create-superadmin";

    /// <summary>Имя команды смены пароля учётной записи из консоли.</summary>
    public const string SetSuperadminPassword = "set-superadmin-password";

    /// <summary>Имя команды снятия второго фактора из консоли — потерян телефон.</summary>
    public const string ResetSuperadminTwoFactor = "reset-superadmin-2fa";

    /// <summary>
    /// Имя команды полного восстановления доступа: новый пароль, снятый второй фактор, снятая
    /// блокировка, закрытые сессии — разом.
    /// </summary>
    /// <remarks>
    /// Вызывается так: <c>docker compose run --rm api reset-access --email admin@example.com</c>
    /// или обёрткой <c>./scripts/reset-access.sh admin@example.com</c>. Пароль — скрытым вводом
    /// дважды или <c>--password-file</c>, как у <see cref="SetSuperadminPassword"/>.
    /// </remarks>
    public const string ResetAccess = "reset-access";

    /// <summary>
    /// Имя команды перекладывания обёрток под действующую версию мастер-ключа.
    /// </summary>
    /// <remarks>
    /// ⚠ Команда, а не фоновая задача и не перешифровка при чтении. Ротация — редкое,
    /// намеренное действие администратора, у которого есть повод: подозрение на утечку либо
    /// срок. Фоновой задаче пришлось бы где-то держать, докуда она дошла, и договариваться
    /// между экземплярами; ленивому перекладыванию — никогда не отвечать на вопрос «уже
    /// можно выбросить старый ключ?», а ради этого ответа ротацию и делают.
    ///
    /// Здесь же ответ есть: команда отработала до конца — старую версию можно убирать из
    /// файла ключей.
    /// </remarks>
    public const string RewrapKeys = "rewrap-keys";

    /// <summary>
    /// Выгрузить документ OpenAPI в файл и выйти.
    /// </summary>
    /// <remarks>
    /// ⚠ <b>Команда, а не выгрузка при сборке, и это осознанный выбор.</b>
    /// Штатный <c>Microsoft.Extensions.ApiDescription.Server</c> запускает точку входа
    /// приложения прямо во время <c>dotnet build</c> — то есть сборка начала бы читать
    /// мастер-ключ и строку подключения. Требовать
    /// секретов от сборки нельзя: собирают её и там, где базы нет вовсе.
    ///
    /// Здесь документ строится <b>без сервера и без базы</b>:
    /// <c>IOpenApiDocumentProvider</c> (.NET 10) собирает его из описаний эндпоинтов.
    /// </remarks>
    public const string ExportOpenApi = "export-openapi";


    /// <summary>
    /// Имя команды применения миграций.
    /// </summary>
    /// <remarks>
    /// ⚠ <b>Отдельный шаг, а не побочный эффект старта</b> (этап 11.5, правило 16). Пока
    /// миграции накатывал старт, требование «перед миграцией на живой базе — копия» было
    /// неисполнимо: <c>docker compose up -d --build</c> <b>и есть</b> миграция.
    ///
    /// Вторая причина жёстче первой: с этапа 11.5 приложение ходит <b>узкой ролью</b>,
    /// которой создавать таблицы нечем. Миграции накатывает владелец схемы, и его строка
    /// подключения — <c>ConnectionStrings:PostgresOwner</c> — задаётся только для этой
    /// команды.
    /// </remarks>
    public const string Migrate = "migrate";



    /// <summary>
    /// Проба готовности для контейнера: спросить у себя же <c>/api/ready</c>.
    /// </summary>
    /// <remarks>
    /// ⚠ ⚠ <b>Заведена потому, что в образе нечем спросить</b> (этап 25.3). Рантайм
    /// <c>mcr.microsoft.com/dotnet/aspnet</c> не несёт ни <c>curl</c>, ни <c>wget</c>, ни
    /// <c>nc</c> — то есть <c>HEALTHCHECK</c> написать было не из чего, и его не было ни у
    /// одной своей службы: <c>docker compose ps</c> показывал «Up» на процессе, который
    /// поднялся и не готов. Ставить ради пробы <c>curl</c> в образ, обслуживающий секреты, —
    /// плата пакетом и поверхностью там, где хватает уже вшитого <c>dotnet</c>.
    ///
    /// ⚠ <b>Команда НЕ входит в <see cref="IsCommand"/>, и это намеренно.</b> Тот путь строит
    /// приложение целиком — контейнер внедрения, секреты, подключения, — то есть проба
    /// здоровья стала бы дороже самой работы и падала бы по причинам, к здоровью отношения не
    /// имеющим. Ветка стоит в самом начале <c>Program.cs</c>, до <c>CreateBuilder</c>.
    ///
    /// ⚠ <b>Годится обеим ролям.</b> <c>/api/health</c> и <c>/api/ready</c> отображаются ДО
    /// ветки роли, поэтому <c>worker</c> отвечает на них так же, как <c>api</c>, — при том что
    /// контроллеров у него нет вовсе.
    /// </remarks>
    public const string Health = "health";

    /// <summary>Спросить у себя <c>/api/ready</c>: 0 — готов, 1 — нет.</summary>
    /// <remarks>
    /// ⚠ <c>127.0.0.1</c>, а не <c>localhost</c>: имя разрешается и в <c>::1</c>, а Kestrel
    /// слушает то, что ему велели, — попадание в неслушаемый адрес дало бы «не готов» на
    /// здоровом процессе.
    /// </remarks>
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

            // Тело готовности называет ИМЕННО ту проверку, что не сошлась (сейчас она одна — база);
            // без него в журнале контейнера осталось бы «unhealthy» без причины.
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

    /// <summary>
    /// Версия установки — из сборки хоста, с обрезкой хвоста после <c>+</c>.
    /// </summary>
    /// <remarks>
    /// ⚠ ⚠ <b>Живёт у ХОСТА, а не у модуля раздачи, и это не мелочь.</b> Наружу отдаётся версия
    /// <b>установки</b>, а не одного из её модулей: они совпадают, пока собираются вместе, и
    /// разойдутся в тот день, когда перестанут. Модулю остаётся принять строку и не думать, чья
    /// она.
    ///
    /// ⚠ <b>Хвост после <c>+</c> — метаданные сборки</b> (номер прогона, хеш коммита). Клиент
    /// сравнивает версии, и хвост, меняющийся от сборки к сборке, делал бы равные версии
    /// неравными.
    /// </remarks>
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
            or RewrapKeys or ExportOpenApi or Migrate;

    /// <summary>Проверить параметры команд суперпользователя до чтения пароля.</summary>
    /// <remarks>
    /// Допустимы <c>--email</c> (обязателен), <c>--password-file</c> — всем, кроме
    /// <see cref="ResetSuperadminTwoFactor"/>, которому пароль не нужен, и <c>--display-name</c> —
    /// только <see cref="CreateSuperadmin"/>. Каждый не больше раза и со значением.
    /// </remarks>
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

    /// <summary>
    /// Прочитать пароль.
    /// </summary>
    /// <remarks>
    /// ⚠ <b>Из файла или с клавиатуры, но никогда из аргументов команды.</b> Аргументы
    /// видны любому пользователю машины в списке процессов, оседают в истории оболочки и в
    /// журналах сборки. Флага <c>--password</c> здесь нет и не должно быть — правило 14
    /// проекта, и оно не про удобство.
    ///
    /// Ввод с клавиатуры не отображается: пароль, набранный на общем экране, — тот же
    /// пароль, записанный на бумаге.
    /// </remarks>
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
                // Тип, а не текст: путь к файлу в сообщении — мелочь, но привычка печатать
                // исключения целиком однажды напечатает строку подключения.
                throw new AppException(
                    "PASSWORD_FILE_UNREADABLE",
                    $"Не удалось прочитать файл с паролем ({SafeError.Describe(error)})",
                    System.Net.HttpStatusCode.BadRequest);
            }

            // Хвостовой перевод строки почти всегда лишний — его дописывает любой редактор.
            // Пробелы и табуляция — часть пароля. Убирается только перевод строки редактора.
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

    /// <summary>
    /// Итог <see cref="ResetAccess"/> для человека у консоли.
    /// </summary>
    /// <remarks>
    /// ⚠ ⚠ <b>Отключённая учётная запись называется прямо.</b> Команда её не включает (включение —
    /// решение администратора со своей строкой журнала), и «доступ восстановлен» без этой строки
    /// было бы неправдой: человек ввёл бы новый пароль и упёрся в «неверный адрес или пароль».
    ///
    /// Отдельным методом, а не строкой в <c>Program</c>: текст проверяется тестом.
    /// </remarks>
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

    /// <summary>Ввод без отображения набранного.</summary>
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
