using EmailSslSender.Api.Infrastructure;
using EmailSslSender.Modules.Identity;
using EmailSslSender.Shared.Errors;

namespace EmailSslSender.IntegrationTests;

/// <summary>Разбор команд восстановления: нет паролей в аргументах и лишних параметров.</summary>
public sealed class BootstrapArgumentsTests
{
    [Theory]
    [InlineData(Bootstrap.CreateSuperadmin)]
    [InlineData(Bootstrap.SetSuperadminPassword)]
    [InlineData(Bootstrap.ResetSuperadminTwoFactor)]
    [InlineData(Bootstrap.ResetAccess)]
    public void Команды_распознаются(string command)
    {
        var args = new[] { command, "--email", "admin@example.com" };
        Bootstrap.IsCommand(args).ShouldBeTrue();
        Should.NotThrow(() => Bootstrap.ValidateSuperuserArguments(args));
    }

    [Theory]
    [InlineData(Bootstrap.SetSuperadminPassword, "--password", "test-not-secret")]
    [InlineData(Bootstrap.ResetSuperadminTwoFactor, "--password-file", "test.txt")]
    [InlineData(Bootstrap.SetSuperadminPassword, "--display-name", "ignored")]
    [InlineData(Bootstrap.CreateSuperadmin, "--unknown", "ignored")]
    [InlineData(Bootstrap.SetSuperadminPassword, "--email", "second@example.com")]
    [InlineData(Bootstrap.ResetAccess, "--password", "test-not-secret")]
    [InlineData(Bootstrap.ResetAccess, "--display-name", "ignored")]
    [InlineData(Bootstrap.ResetAccess, "--email", "second@example.com")]
    [InlineData(Bootstrap.ResetAccess, "--unknown", "ignored")]
    public void Лишний_или_повторный_параметр_отклоняется(string command, string option, string value)
    {
        var error = Should.Throw<AppException>(() => Bootstrap.ValidateSuperuserArguments(
            [command, "--email", "admin@example.com", option, value]));
        error.Code.ShouldBe(ErrorCodes.Validation);
        error.Message.ShouldNotContain(value);
    }

    [Theory]
    [InlineData(Bootstrap.CreateSuperadmin)]
    [InlineData(Bootstrap.SetSuperadminPassword)]
    [InlineData(Bootstrap.ResetSuperadminTwoFactor)]
    [InlineData(Bootstrap.ResetAccess)]
    public void Нет_адреса_или_значения_параметра_нет_запроса_пароля(string command)
    {
        Should.Throw<AppException>(() => Bootstrap.ValidateSuperuserArguments([command]));
        Should.Throw<AppException>(() => Bootstrap.ValidateSuperuserArguments([command, "--email"]));
        Should.Throw<AppException>(() => Bootstrap.ValidateSuperuserArguments([command, "--email", ""]));
        Should.Throw<AppException>(() => Bootstrap.ValidateSuperuserArguments([command, "--email", "--password-file", "test.txt"]));
    }

    /// <summary>Пароль восстановления — из файла, как у смены пароля; адрес — в любом месте строки.</summary>
    [Fact]
    public void Восстановление_доступа_принимает_файл_пароля()
    {
        Should.NotThrow(() => Bootstrap.ValidateSuperuserArguments(
            [Bootstrap.ResetAccess, "--password-file", "/run/secrets/new_password", "--email", "admin@example.com"]));

        Bootstrap.Argument(
                [Bootstrap.ResetAccess, "--password-file", "/run/secrets/new_password", "--email", "admin@example.com"],
                "--email")
            .ShouldBe("admin@example.com");
    }

    [Fact]
    public void Файл_пароля_сохраняет_значимые_пробелы()
    {
        var file = Path.GetTempFileName();
        try
        {
            File.WriteAllText(file, " Пароль-с-пробелами-42! \r\n", System.Text.Encoding.UTF8);
            Bootstrap.ReadPassword(file).ShouldBe(" Пароль-с-пробелами-42! ");
            Bootstrap.ReadPassword(file, "Новый пароль: ").ShouldBe(" Пароль-с-пробелами-42! ");
        }
        finally
        {
            File.Delete(file);
        }
    }

    /// <summary>
    /// Итог восстановления называет всё сделанное — и прямо говорит об отключённой учётной записи.
    /// </summary>
    /// <remarks>
    /// ⚠ ⚠ Команда отключённую учётную запись не включает. Итог без этой строки читался бы как
    /// «доступ восстановлен», и человек упёрся бы в «неверный адрес или пароль».
    /// </remarks>
    [Fact]
    public void Итог_восстановления_называет_сделанное_и_отключённую_учётную_запись()
    {
        var id = Guid.CreateVersion7();

        var включена = Bootstrap.ResetAccessSummary(new AccessResetResult(id, "admin@example.com", true, true, 3));

        включена.ShouldContain(Bootstrap.ResetAccess);
        включена.ShouldContain("admin@example.com");
        включена.ShouldContain(id.ToString());
        включена.ShouldContain("пароль заменён");
        включена.ShouldContain("второй фактор снят");
        включена.ShouldContain("блокировка входа снята");
        включена.ShouldContain("закрыто сессий: 3");
        включена.ShouldContain("Можно входить");
        включена.ShouldNotContain("ОТКЛЮЧЕНА");

        var отключена = Bootstrap.ResetAccessSummary(new AccessResetResult(id, "admin@example.com", false, false, 0));

        отключена.ShouldContain("второго фактора не было");
        отключена.ShouldContain("ОТКЛЮЧЕНА");
        отключена.ShouldContain("не включает");
        отключена.ShouldNotContain("Можно входить");
    }
}
