using System.Net;
using System.Text;
using EmailSslSender.Modules.KeyManager.Api;
using EmailSslSender.Modules.KeyManager.Client;
using EmailSslSender.Modules.KeyManager.Contracts;
using EmailSslSender.Modules.KeyManager.Domain;
using EmailSslSender.Modules.KeyManager.Export;
using Modules.Core.Data;
using Modules.Core.Errors;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace EmailSslSender.Modules.KeyManager.Tests;

/// <summary>
/// Выгрузка сертификата и показ закрытого ключа администратору: что отдаётся, кому и что остаётся в журнале.
/// </summary>
/// <remarks>
/// ⚠ ⚠ <b>Главные факты файла — «только то, что видел опрос» и «содержимое не оседает нигде».</b>
/// Первое не даёт выгрузке стать сквозной трубой к Key Manager Plus по любому Common Name; второе
/// проверяется по журналу действий И по логу: ключ, попавший в строку лога, уезжает в тикеты
/// целиком.
/// </remarks>
[Collection(KeyManagerDatabaseCollection.Name)]
public sealed class ExportTests(KeyManagerDatabase база) : IAsyncLifetime
{
    private const string Имя = "site.example.com";
    private const string Серийный = "aa01";

    private const string Ключ =
        "-----BEGIN PRIVATE KEY-----\nMIIEvQIBADANBgkqhkiG9w0BAQEFAASC-тест-не-ключ\n-----END PRIVATE KEY-----\n";

    private const string Пароль = "пароль-ключа-не-для-лога";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        база.Сброс();
        await Снимок(Имя, Серийный);
    }

    public async ValueTask DisposeAsync()
    {
        await using var scope = база.Scope();
        await scope.ServiceProvider.GetRequiredService<AppDbContext>()
            .Set<KnownCertificate>().ExecuteDeleteAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Файл_выгружается_из_Key_Manager_Plus_под_именем_вложения()
    {
        var файл = await Выгрузить(Имя, Серийный, "pem");

        файл.FileName.ShouldBe("site.example.com.pem");
        файл.ContentType.ShouldBe("application/x-pem-file");
        Encoding.ASCII.GetString(файл.Content).ShouldBe("PEM:aa01");
        база.Kmp.Выгрузок.ShouldBe(1);

        var строка = база.Аудит.Записи.ShouldHaveSingleItem();
        строка.Action.ShouldBe(KeyManagerActions.CertificateDownloaded);
        строка.ResourceType.ShouldBe(KeyManagerActions.CertificateResource);
        строка.Action.Name.ShouldStartWith("keymanager.", Case.Sensitive, "значения изменений журнал хоста пишет по приставке события");
        строка.Changes!["common_name"].ShouldBe(Имя);
        строка.Changes["serial_number"].ShouldBe(Серийный);
        строка.Changes["format"].ShouldBe("PEM");
    }

    [Fact]
    public async Task Подстановочное_имя_становится_именем_файла_как_у_вложения()
    {
        await Снимок("*.example.com", "bb02");

        (await Выгрузить("*.example.com", "bb02", "KEY")).FileName.ShouldBe("wildcard.example.com.key");
    }

    /// <summary>
    /// Пары нет в снимке — 404, и в Key Manager Plus не уходит ни одного запроса.
    /// </summary>
    /// <remarks>
    /// ⚠ ⚠ Ключ API отдаёт любые сертификаты; без этой проверки выгрузка спрашивала бы у Key
    /// Manager Plus всё, что ей назовут. Чужой серийный номер у известного имени — тот же отказ.
    /// </remarks>
    // мутация 2026-10-20: снял проверку снимка (выгрузка по присланной паре) → покраснел этот факт.
    [Theory]
    [InlineData("other.example.com", Серийный)]
    [InlineData(Имя, "ff99")]
    [InlineData("SITE.EXAMPLE.COM", Серийный)]
    public async Task Пары_нет_в_снимке_404_без_запроса_к_Key_Manager_Plus(string имя, string серийный)
    {
        var отказ = await Should.ThrowAsync<AppException>(() => Выгрузить(имя, серийный, "PEM"));

        отказ.Code.ShouldBe(KeyManagerErrorCodes.CertificateNotFound);
        отказ.Status.ShouldBe(HttpStatusCode.NotFound);

        var ключом = await Should.ThrowAsync<AppException>(() => Показать(имя, серийный));
        ключом.Code.ShouldBe(KeyManagerErrorCodes.CertificateNotFound);

        база.Kmp.Выгрузок.ShouldBe(0, "до Key Manager Plus дошёл запрос по паре, которой нет в снимке");
        база.Аудит.Записи.ShouldBeEmpty();
    }

    [Theory]
    [InlineData(null, Серийный, "PEM", "common_name")]
    [InlineData("  ", Серийный, "PEM", "common_name")]
    [InlineData(Имя, null, "PEM", "serial_number")]
    [InlineData(Имя, Серийный, "PFX", "format")]
    [InlineData(Имя, Серийный, null, "format")]
    public async Task Негодный_запрос_отвергается_с_именем_поля(string? имя, string? серийный, string? тип, string поле)
    {
        var отказ = await Should.ThrowAsync<AppException>(() => Выгрузить(имя, серийный, тип));

        отказ.Code.ShouldBe(ErrorCodes.Validation);
        отказ.Status.ShouldBe(HttpStatusCode.BadRequest);
        отказ.Details!["field"].ShouldBe(поле);
        база.Kmp.Выгрузок.ShouldBe(0);
    }

    [Fact]
    public async Task Без_адреса_или_ключа_API_409_не_настроено()
    {
        база.Kmp.Настройки = база.Kmp.Настройки with { ApiKeySet = false };

        (await Should.ThrowAsync<AppException>(() => Выгрузить(Имя, Серийный, "PEM"))).Code
            .ShouldBe(KeyManagerErrorCodes.NotConfigured);
        (await Should.ThrowAsync<AppException>(() => Показать(Имя, Серийный))).Code
            .ShouldBe(KeyManagerErrorCodes.NotConfigured);
    }

    [Fact]
    public async Task Отказ_Key_Manager_Plus_доходит_до_эндпоинта_и_журнала_не_оставляет()
    {
        база.Kmp.ПадатьНаФормате = CertificateFormats.Pem;

        await Should.ThrowAsync<KeyManagerException>(() => Выгрузить(Имя, Серийный, "PEM"));

        база.Аудит.Записи.ShouldBeEmpty("выгрузка не состоялась — строки «выгружено» быть не должно");
    }

    [Fact]
    public async Task Ключ_отдаётся_текстом_PEM_вместе_с_паролем()
    {
        // BOM и перевод строки перед PEM — так его отдают некоторые сборки Key Manager Plus.
        база.Kmp.Файлы[CertificateFormats.Key] = [0xEF, 0xBB, 0xBF, (byte)'\r', (byte)'\n', .. Encoding.UTF8.GetBytes(Ключ)];
        база.Kmp.Пароли[Серийный] = Пароль;

        var ключ = await Показать(Имя, Серийный);

        ключ.PrivateKey.ShouldBe(Ключ);
        ключ.Passphrase.ShouldBe(Пароль);
        ключ.ToString().ShouldNotContain("BEGIN");
        ключ.ToString().ShouldNotContain(Пароль);

        var строка = база.Аудит.Записи.ShouldHaveSingleItem();
        строка.Action.ShouldBe(KeyManagerActions.PrivateKeyViewed);
        строка.Changes!["common_name"].ShouldBe(Имя);
        строка.Changes["serial_number"].ShouldBe(Серийный);
        строка.Changes.ContainsKey("format").ShouldBeFalse();
    }

    /// <summary>
    /// Ни ключ, ни пароль не оседают ни в журнале действий, ни в логе — и при отказе в пароле тоже.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Ключ_и_пароль_не_попадают_ни_в_журнал_ни_в_лог(bool парольПадает)
    {
        база.Kmp.Файлы[CertificateFormats.Key] = Encoding.UTF8.GetBytes(Ключ);
        база.Kmp.Пароли[Серийный] = Пароль;
        база.Kmp.ПарольПадает = парольПадает;

        var ключ = await Показать(Имя, Серийный);

        ключ.PrivateKey.ShouldBe(Ключ);
        ключ.Passphrase.ShouldBe(парольПадает ? null : Пароль, "отказ в пароле не отменяет выдачу ключа");

        var журнал = string.Join(
            '\n',
            база.Аудит.Записи.SelectMany(запись => (запись.Changes ?? new Dictionary<string, object?>()).Values
                .Select(значение => значение?.ToString()).Append(запись.Reason)));
        var лог = string.Join('\n', база.Логи.Строки);

        foreach (var след in new[] { "BEGIN PRIVATE KEY", "MIIEvQIBADANBgkqhkiG9w0BAQEFAASC", Пароль })
        {
            журнал.ShouldNotContain(след);
            лог.ShouldNotContain(след);
        }

        if (парольПадает)
        {
            лог.ShouldContain("keymanager_passphrase_unavailable", Case.Sensitive, "отказ в пароле прошёл молча");
        }
    }

    /// <summary>Ключ не текстом PEM — отказ с понятной причиной, без содержимого и без строки журнала.</summary>
    // мутация 2026-10-20: `Pem` вернул текст без проверки начала → покраснел случай «KEY:aa01»;
    // DER и не-UTF-8 держит строгий декодер.
    [Theory]
    [InlineData(new byte[] { 0x30, 0x82, 0x04, 0xBD, 0x02, 0x01, 0x00 })]
    [InlineData(new byte[] { 0xFF, 0xFE, 0x2D, 0x2D, 0x2D, 0x2D, 0x2D })]
    [InlineData(new byte[] { 0x4B, 0x45, 0x59, 0x3A, 0x61, 0x61, 0x30, 0x31 })]
    [InlineData(new byte[] { 0x2D, 0x2D, 0x2D, 0x2D, 0x2D, 0x42, 0x45, 0x47, 0x49, 0x4E, 0xC3, 0x28 })]
    public async Task Ключ_не_в_виде_PEM_отвергается(byte[] содержимое)
    {
        база.Kmp.Файлы[CertificateFormats.Key] = содержимое;

        var отказ = await Should.ThrowAsync<KeyManagerException>(() => Показать(Имя, Серийный));

        отказ.Message.ShouldBe(CertificateExport.НеPem);
        база.Аудит.Записи.ShouldBeEmpty();
    }

    [Fact]
    public void Имя_вложения_уходит_в_кавычках_а_не_латиница_вторым_параметром()
    {
        KeyManagerController.Вложение("site.example.com.pem")
            .ShouldBe("attachment; filename=\"site.example.com.pem\"");

        KeyManagerController.Вложение("сайт.пример.pem")
            .ShouldBe("attachment; filename=\"____.______.pem\"; filename*=UTF-8''"
                + Uri.EscapeDataString("сайт.пример.pem"));

        KeyManagerController.Вложение("a\"b\\c.pem").ShouldBe("attachment; filename=\"a_b_c.pem\"; filename*=UTF-8''"
            + Uri.EscapeDataString("a\"b\\c.pem"));
    }

    // --- обвязка ---------------------------------------------------------------------------

    private async Task Снимок(string имя, string серийный)
    {
        await using var scope = база.Scope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        db.Set<KnownCertificate>().Add(new KnownCertificate
        {
            CommonName = имя,
            SerialNumber = серийный,
            ExpiresOn = new DateOnly(2027, 1, 1),
            FirstSeenAt = база.Часы.GetUtcNow(),
            LastSeenAt = база.Часы.GetUtcNow(),
        });

        await db.SaveChangesAsync(Ct);
    }

    private async Task<KmpFile> Выгрузить(string? имя, string? серийный, string? тип)
    {
        await using var scope = база.Scope();

        return await scope.ServiceProvider.GetRequiredService<CertificateExport>()
            .DownloadAsync(имя, серийный, тип, Ct);
    }

    private async Task<PrivateKeyText> Показать(string? имя, string? серийный)
    {
        await using var scope = база.Scope();

        return await scope.ServiceProvider.GetRequiredService<CertificateExport>()
            .PrivateKeyAsync(имя, серийный, Ct);
    }
}
