using EmailSslSender.Shared.Crypto;

namespace EmailSslSender.Shared.Tests;

/// <summary>
/// Секрет с адресом: упаковка, распознавание старого формата и молчание в логе.
/// </summary>
public sealed class BoundSecretTests
{
    [Fact]
    public void Упакованный_разбирается_обратно()
    {
        var привязка = BoundSecret.Unpack(new BoundSecret("https://kmp:6565", "ключ \"с\" кавычками").Pack())
            .ShouldNotBeNull();

        привязка.Target.ShouldBe("https://kmp:6565");
        привязка.Secret.ShouldBe("ключ \"с\" кавычками");
        привязка.IsFor("https://kmp:6565").ShouldBeTrue();
        привязка.IsFor("https://KMP:6565").ShouldBeFalse("нормализует владелец, сравнение точное");
    }

    [Theory]
    [InlineData("75408B31-C7D6-4F5A-9A0E-1B2C3D4E5F60")]
    [InlineData("{пароль-в-фигурных}")]
    [InlineData("{\"v\":2,\"target\":\"a\",\"secret\":\"b\"}")]
    [InlineData("{\"v\":1,\"target\":\"a\"}")]
    [InlineData("{\"v\":1,\"target\":1,\"secret\":\"b\"}")]
    [InlineData("")]
    public void Старый_голый_секрет_распознаётся_старым(string открыто)
    {
        // ⚠ Старый пароль, похожий на JSON, остаётся старым: иначе он «привязался» бы к адресу,
        // который в нём случайно написан.
        BoundSecret.Unpack(открыто).ShouldBeNull();
    }

    [Fact]
    public void Секрет_не_печатается()
    {
        new BoundSecret("smtp://robot@smtp.example.com:587", "очень-секретный").ToString()
            .ShouldNotContain("очень-секретный");
    }
}
