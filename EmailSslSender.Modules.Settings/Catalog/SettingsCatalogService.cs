using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace EmailSslSender.Modules.Settings.Catalog;

/// <summary>
/// Собирает справочник настроек при старте.
/// </summary>
/// <remarks>
/// ⚠ <b>Ради того, чтобы спор двух модулей за одно имя ронял запуск, а не первый запрос.</b>
/// Проверка на дубли живёт в конструкторе справочника, но синглтон в этом контейнере строится
/// <b>лениво</b>: первым его тронул бы экран настроек, то есть первое открытие страницы —
/// и упало бы оно у того, кто её открыл, а не у того, кто выкатывал.
///
/// Тот же приём и тот же разбор, что у справочника событий журнала
/// (<c>EmailSslSender.Modules.Audit/Catalog/AuditCatalogService.cs</c>): отказ при старте останавливает
/// выкатку и виден одной строкой; отказ при первом запросе оставляет систему поднятой,
/// проверку живости зелёной, а страницу — пятисоткой из фабрики синглтона, которая после
/// броска даже не запоминает неудачу и пробует собрать справочник заново.
///
/// <see cref="IHostedService"/>, а не <c>BackgroundService</c>: работа разовая и должна
/// закончиться <b>до</b> того, как хост начнёт принимать запросы, — фоновая служба по
/// определению работает параллельно им, и её исключение запуск не остановит.
/// </remarks>
internal sealed class SettingsCatalogService(
    IServiceProvider services, ILogger<SettingsCatalogService> log) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        var catalog = services.GetRequiredService<SettingsCatalog>();

        log.LogInformation(
            "settings_catalog_ready {Settings} {Categories}",
            catalog.All.Count,
            catalog.All.Select(declaration => declaration.Category).Distinct(StringComparer.Ordinal).Count());

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
