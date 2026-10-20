using EmailSslSender.Modules.Mail.Sending;
using EmailSslSender.Modules.Mail.Settings;
using EmailSslSender.Shared.Contracts.Settings;

namespace EmailSslSender.Modules.Mail.Tests;

/// <summary>
/// Адрес отправителя задаётся, а не угадывается.
/// </summary>
/// <remarks>
/// ⚠ ⚠ <b>Сьют заведён вскрытием 44 по замечанию владельца о виде экрана.</b> Он положил наш
/// экран настройки почты рядом с чужим и спросил, почему у нас столько непонятных настроек. У
/// чужого при этом оказалось поле, которого у нас <b>не было вовсе</b>, — «Sender email
/// address», отдельно от «User Name». Отсутствие поля и было дефектом.
///
/// ⚠ <b>Как адрес угадывался до починки и почему оба варианта неверны.</b> Задана учётная
/// запись SMTP — брали её; не задана — сочиняли <c>eams@домен-получателя</c>. Первое ломается
/// в самом обычном случае: у множества провайдеров пользователь SMTP не является адресом (у
/// владельца это <c>d.balochi</c>), и заголовок <c>From</c> выходил невалидным. Второе хуже:
/// письмо «от» домена получателя — гарантированный отказ SPF и прямая дорога в спам.
///
/// ⚠ <b>Отсюда форма починки: не «подставить получше», а «считать почту ненастроенной».</b>
/// Молчание с внятной причиной на экране лучше письма, которое молча съест спам-фильтр.
/// </remarks>
public sealed class FromAddressTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Без_адреса_отправителя_почта_не_настроена()
    {
        var политика = await MailPolicy.ReadAsync(
            Настройки(host: "smtp.example.com", from: null), Ct);

        политика.Configured.ShouldBeFalse(
            "сервер задан, адреса отправителя нет — отправлять не с чего");
    }

    [Fact]
    public async Task С_адресом_отправителя_настроена()
    {
        var политика = await MailPolicy.ReadAsync(
            Настройки(host: "smtp.example.com", from: "vault@example.com"), Ct);

        политика.Configured.ShouldBeTrue();
        политика.FromAddress.ShouldBe("vault@example.com");
    }

    [Fact]
    public async Task Учётная_запись_SMTP_адресом_отправителя_не_считается()
    {
        // ⚠ Тот самый живой случай: пользователь провайдера — не адрес. До вскрытия 44 именно
        // он уезжал в заголовок `From`, и письмо отвергал сервер или съедал спам-фильтр.
        var политика = await MailPolicy.ReadAsync(
            Настройки(host: "smtp.example.com", from: null, username: "d.balochi"), Ct);

        политика.Configured.ShouldBeFalse(
            "учётная запись задана, но адресом отправителя она не является");
    }

    [Fact]
    public async Task Пустой_адрес_из_пробелов_адресом_не_считается()
    {
        // Пустая строка и строка из пробелов — обычный итог «стёр поле и сохранил». Она не
        // должна выглядеть заданным значением.
        var политика = await MailPolicy.ReadAsync(
            Настройки(host: "smtp.example.com", from: "   "), Ct);

        политика.Configured.ShouldBeFalse();
    }

    private static ISettingsReader Настройки(string? host, string? from, string? username = null)
    {
        var значения = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            [MailSettings.Enabled.Name] = true,
            [MailSettings.SmtpHost.Name] = host,
            [MailSettings.FromAddress.Name] = from,
            [MailSettings.SmtpUsername.Name] = username,
        };

        return new ПоддельныеНастройки(значения);
    }

    /// <summary>Снимок настроек без базы: политика читает их и больше ничего не делает.</summary>
    private sealed class ПоддельныеНастройки(Dictionary<string, object?> значения) : ISettingsReader
    {
        public ValueTask<T> GetAsync<T>(SettingKey<T> key, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(key);

            return ValueTask.FromResult(значения.TryGetValue(key.Name, out var значение) && значение is T typed
                ? typed
                : key.Default);
        }
    }
}
