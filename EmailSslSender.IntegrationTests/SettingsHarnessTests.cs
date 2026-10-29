namespace EmailSslSender.IntegrationTests;

/// <summary>Обвязка настроек убирает за собой: иначе тихо краснеют чужие тесты.</summary>
[Collection(SenderHost.Collection)]
public sealed class SettingsHarnessTests(SenderHost host)
{
    [Fact]
    public async Task Фикстура_убирает_строку_оставленную_тестом()
    {
        await using (var _ = await SystemSettingsFixture.НачатьAsync(host))
        {
            await host.ЗаписатьМимоФикстурыAsync();
        }

        (await host.СтрокНастроекAsync(TestContext.Current.CancellationToken)).ShouldBe(0);
    }

    [Fact]
    public async Task Подмена_читателя_возвращается_после_фикстуры()
    {
        // ⚠ Оставленный поднятым флаг заражает не хуже строки.
        await using (var _ = await SystemSettingsFixture.НачатьAsync(host))
        {
            host.НастоящиеНастройки.ShouldBeTrue();
        }

        host.НастоящиеНастройки.ShouldBeFalse();
    }
}
