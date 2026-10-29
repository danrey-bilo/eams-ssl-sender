namespace EmailSslSender.IntegrationTests;

/// <summary>Тест с настоящими настройками; <c>system_settings</c> чистится до и после.</summary>
/// <remarks>⚠ Уборка — в фикстуре, не в тесте: упавший посередине тест иначе заражает всю коллекцию.</remarks>
public sealed class SystemSettingsFixture(SenderHost host) : IAsyncDisposable
{
    /// <summary>Убрать оставшееся от прежних тестов и включить настоящие настройки.</summary>
    public static async Task<SystemSettingsFixture> НачатьAsync(SenderHost host)
    {
        ArgumentNullException.ThrowIfNull(host);

        await host.ОчиститьНастройкиAsync();
        host.НастоящиеНастройки = true;

        return new SystemSettingsFixture(host);
    }

    public async ValueTask DisposeAsync()
    {
        host.НастоящиеНастройки = false;
        await host.ОчиститьНастройкиAsync();
    }
}
