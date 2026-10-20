using EmailSslSender.Shared.Errors;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace EmailSslSender.Modules.Identity.Retention;

/// <summary>
/// Фоновая уборка одноразовых ссылок и кодов.
/// </summary>
/// <remarks>
/// Устроена так же, как уборка сессий и писем в соседних модулях: отсрочка перед первым
/// проходом и сутки между прогонами.
///
/// <b>Отсрочка.</b> Единственное необратимое действие модуля не должно случаться в первые
/// секунды после старта: ошиблись в настройке при выкатке — есть минута откатить, пока
/// ничего не удалено.
///
/// <b>Падение прохода не роняет процесс.</b> Уборка — не то, ради чего система работает;
/// невыполненная, она стоит лишних строк в таблице, а не отказа в обслуживании.
/// </remarks>
internal sealed class CleanupService(
    IServiceScopeFactory scopes,
    TimeProvider clock,
    ILogger<CleanupService> log) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromHours(24);

    private static readonly TimeSpan StartupDelay = TimeSpan.FromMinutes(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(StartupDelay, clock, stoppingToken);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        using var timer = new PeriodicTimer(Interval, clock);

        do
        {
            await RunOnceAsync(stoppingToken);
        }
        while (await SafeWaitAsync(timer, stoppingToken));
    }

    private async Task RunOnceAsync(CancellationToken stoppingToken)
    {
        try
        {
            await using var scope = scopes.CreateAsyncScope();

            var cleanup = scope.ServiceProvider.GetRequiredService<LinkCleanup>();
            var result = await cleanup.RunAsync(stoppingToken);

            if (result.Any)
            {
                log.LogInformation("identity_cleanup {Links}", result.Links);
            }
        }
        catch (OperationCanceledException)
        {
            // Останов процесса, а не сбой.
        }
        catch (Exception error)
        {
            // Тип, а не текст: строка подключения Npgsql содержит пароль целиком, и в
            // сообщении исключения она появляется целиком тоже (правило 14).
            log.LogError("session_cleanup_failed {Error}", SafeError.Describe(error));
        }
    }

    private static async Task<bool> SafeWaitAsync(PeriodicTimer timer, CancellationToken stoppingToken)
    {
        try
        {
            return await timer.WaitForNextTickAsync(stoppingToken);
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }
}
