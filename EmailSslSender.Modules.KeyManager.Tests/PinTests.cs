using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using EmailSslSender.Modules.KeyManager.Client;

namespace EmailSslSender.Modules.KeyManager.Tests;

/// <summary>
/// Проверка сертификата сервера Key Manager Plus: цепочка или закреплённый отпечаток — и никогда «как-нибудь».
/// </summary>
public sealed class PinTests
{
    private static readonly X509Certificate2 Самоподписанный = Выпустить("kmp.example.test");

    private static readonly X509Certificate2 Чужой = Выпустить("evil.example.test");

    private static string Отпечаток(X509Certificate2 сертификат) =>
        Convert.ToHexString(SHA256.HashData(сертификат.RawData));

    [Fact]
    public void Прошедший_проверку_цепочки_принимается_без_отпечатка()
    {
        ServerCertificatePin.Accept(Самоподписанный, SslPolicyErrors.None, null).ShouldBeTrue();
    }

    [Fact]
    public void Самоподписанный_без_отпечатка_отвергается()
    {
        ServerCertificatePin.Accept(Самоподписанный, SslPolicyErrors.RemoteCertificateChainErrors, null)
            .ShouldBeFalse();
    }

    [Fact]
    public void Самоподписанный_с_верным_отпечатком_принимается_в_любом_написании()
    {
        // Так его показывает `openssl x509 -fingerprint -sha256`: двоеточия, верхний регистр.
        var сДвоеточиями = string.Join(':', Отпечаток(Самоподписанный).Chunk(2).Select(п => new string(п)));

        ServerCertificatePin.Accept(
                Самоподписанный, SslPolicyErrors.RemoteCertificateChainErrors, сДвоеточиями.ToLowerInvariant())
            .ShouldBeTrue();
    }

    [Fact]
    public void Чужой_сертификат_с_закреплённым_отпечатком_отвергается()
    {
        ServerCertificatePin.Accept(
                Чужой,
                SslPolicyErrors.RemoteCertificateChainErrors | SslPolicyErrors.RemoteCertificateNameMismatch,
                Отпечаток(Самоподписанный))
            .ShouldBeFalse();
    }

    [Fact]
    public void Годный_по_цепочке_но_чужой_при_закреплённом_отпечатке_отвергается()
    {
        // ⚠ ⚠ Закрепление СУЖАЕТ: сертификат, выданный на имя сервера публичным центром (цепочка
        // без ошибок), но не тот, что сверил администратор, — отказ. Прежде годный по цепочке
        // принимался раньше сверки, и отпечаток не закреплял ничего.
        ServerCertificatePin.Accept(Чужой, SslPolicyErrors.None, Отпечаток(Самоподписанный))
            .ShouldBeFalse();
    }

    [Fact]
    public void Закреплённый_принимается_и_когда_цепочка_годна()
    {
        ServerCertificatePin.Accept(Самоподписанный, SslPolicyErrors.None, Отпечаток(Самоподписанный))
            .ShouldBeTrue();
    }

    [Fact]
    public void Без_отпечатка_несовпадение_имени_отвергается()
    {
        ServerCertificatePin.Accept(Самоподписанный, SslPolicyErrors.RemoteCertificateNameMismatch, null)
            .ShouldBeFalse();
    }

    [Fact]
    public void Нет_сертификата_отказ()
    {
        ServerCertificatePin.Accept(null, SslPolicyErrors.RemoteCertificateNotAvailable, Отпечаток(Самоподписанный))
            .ShouldBeFalse();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("abcd")]
    [InlineData("SHA1:AA:BB")]
    public void Не_SHA256_за_отпечаток_не_считается(string значение)
    {
        ServerCertificatePin.Normalize(значение).ShouldBeNull();
    }

    private static X509Certificate2 Выпустить(string имя)
    {
        using var ключ = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var запрос = new CertificateRequest($"CN={имя}", ключ, HashAlgorithmName.SHA256);

        return запрос.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30));
    }
}
