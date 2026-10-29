using EmailSslSender.Api.Infrastructure;

namespace EmailSslSender.IntegrationTests;

/// <summary>Команда <c>migrate</c> через точку входа хоста — как у службы migrate, без повтора шагов.</summary>
internal static class MigrateCommand
{
    /// <summary>Выполнить <c>migrate</c>; вернуть код завершения.</summary>
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
