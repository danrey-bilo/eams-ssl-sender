using EmailSslSender.Api.Infrastructure;
using EmailSslSender.Shared.Errors;

namespace EmailSslSender.IntegrationTests;

/// <summary>Разбор команд восстановления: нет паролей в аргументах и лишних параметров.</summary>
public sealed class BootstrapArgumentsTests
{
    [Theory]
    [InlineData(Bootstrap.CreateSuperadmin)]
    [InlineData(Bootstrap.SetSuperadminPassword)]
    [InlineData(Bootstrap.ResetSuperadminTwoFactor)]
    public void Команды_распознаются(string command)
    {
        var args = new[] { command, "--email", "admin@example.com" };
        Bootstrap.IsCommand(args).ShouldBeTrue();
        Bootstrap.NeedsDatabase(args).ShouldBeTrue();
        Should.NotThrow(() => Bootstrap.ValidateSuperuserArguments(args));
    }

    [Theory]
    [InlineData(Bootstrap.SetSuperadminPassword, "--password", "test-not-secret")]
    [InlineData(Bootstrap.ResetSuperadminTwoFactor, "--password-file", "test.txt")]
    [InlineData(Bootstrap.SetSuperadminPassword, "--display-name", "ignored")]
    [InlineData(Bootstrap.CreateSuperadmin, "--unknown", "ignored")]
    [InlineData(Bootstrap.SetSuperadminPassword, "--email", "second@example.com")]
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
    public void Нет_адреса_или_значения_параметра_нет_запроса_пароля(string command)
    {
        Should.Throw<AppException>(() => Bootstrap.ValidateSuperuserArguments([command]));
        Should.Throw<AppException>(() => Bootstrap.ValidateSuperuserArguments([command, "--email"]));
        Should.Throw<AppException>(() => Bootstrap.ValidateSuperuserArguments([command, "--email", ""]));
        Should.Throw<AppException>(() => Bootstrap.ValidateSuperuserArguments([command, "--email", "--password-file", "test.txt"]));
    }

    [Fact]
    public void Файл_пароля_сохраняет_значимые_пробелы()
    {
        var file = Path.GetTempFileName();
        try
        {
            File.WriteAllText(file, " Пароль-с-пробелами-42! \r\n", System.Text.Encoding.UTF8);
            Bootstrap.ReadPassword(file).ShouldBe(" Пароль-с-пробелами-42! ");
        }
        finally
        {
            File.Delete(file);
        }
    }
}
