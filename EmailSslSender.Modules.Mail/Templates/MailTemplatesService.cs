using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace EmailSslSender.Modules.Mail.Templates;

/// <summary>
/// Собирает справочник писем при старте.
/// </summary>
/// <remarks>
/// ⚠ <b>Ради того, чтобы спор за пару «вид + язык» и неполнота языков роняли запуск, а не
/// первую отправку.</b> Справочник — синглтон, а синглтон в этом контейнере строится лениво:
/// первым его тронул бы тот, кто шлёт письмо, — то есть человек, нажавший «сбросить пароль».
/// Он получил бы пятисотку вместо письма, а система при этом числилась бы исправной.
///
/// Тот же приём и тот же разбор, что у справочника событий журнала
/// (<c>EmailSslSender.Modules.Audit/Catalog/AuditCatalogService.cs</c>).
/// </remarks>
internal sealed class MailTemplatesService(
    IServiceProvider services, ILogger<MailTemplatesService> log) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        var справочник = services.GetRequiredService<MailTemplates>();

        log.LogInformation("mail_templates_ready {Kinds}", справочник.Kinds.Count);

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
