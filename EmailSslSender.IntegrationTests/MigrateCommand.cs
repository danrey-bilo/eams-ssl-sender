using EmailSslSender.Api.Infrastructure;

namespace EmailSslSender.IntegrationTests;

/// <summary>Команда <c>migrate</c> хоста — тем же входом, что у службы migrate в бою.</summary>
/// <remarks>
/// ⚠ Точка входа сборки хоста, а не повтор её шагов в тесте: проход наборов, перекладка секретов и роль
/// сервиса проверяются ровно такими, какими их выполнит установка.
/// </remarks>
internal static class MigrateCommand
{
    /// <summary>Выполнить <c>migrate</c> на базе <paramref name="connection"/>; вернуть код завершения.</summary>
    public static Task<int> RunAsync(string connection, string masterKey, params string[] settings)
    {
        var вход = typeof(Program).Assembly.EntryPoint
            ?? throw new InvalidOperationException("У сборки хоста нет точки входа.");

        string[] команда =
        [
            Bootstrap.Migrate,
            $"--ConnectionStrings:Postgres={connection}",
            $"--{MasterKeyFile.InlineKey}={masterKey}",
            .. settings,
        ];

        return Task.Run(() => (int)вход.Invoke(null, [команда])!);
    }

    /// <summary>Одноразовый мастер-ключ прогона.</summary>
    public static string NewMasterKey() =>
        Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
}
