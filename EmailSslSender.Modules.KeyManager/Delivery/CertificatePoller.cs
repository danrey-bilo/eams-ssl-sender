using EmailSslSender.Modules.KeyManager.Settings;
using EmailSslSender.Shared.Contracts.Settings;
using EmailSslSender.Shared.Errors;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace EmailSslSender.Modules.KeyManager.Delivery;

/// <summary>
/// Сигнал «опросить сейчас»: новая подписка или кнопка «опросить» не ждут полный срок.
/// </summary>
/// <remarks>
/// ⚠ Внутрипроцессный: будит опрос только в том процессе, где он поднят. Тот, что поднят в
/// другом, дождётся своего срока.
/// </remarks>
internal sealed class PollWakeup
{
    private readonly SemaphoreSlim _сигнал = new(0, 1);

    public void Signal()
    {
        if (_сигнал.CurrentCount == 0)
        {
            try
            {
                _сигнал.Release();
            }
            catch (SemaphoreFullException)
            {
                // Гонка двух одновременных вызовов: сигнал уже взведён, и это всё, что нужно.
            }
        }
    }

    public Task<bool> WaitAsync(TimeSpan сколько, CancellationToken cancellationToken) =>
        _сигнал.WaitAsync(сколько, cancellationToken);
}

/// <summary>
/// Фоновый опрос Key Manager Plus: раз в <c>keymanager.poll_minutes</c> и по сигналу.
/// </summary>
/// <remarks>
/// ⚠ <b>Ошибка прохода НЕ роняет службу</b>: недоступная база или Key Manager Plus — это
/// пропущенный проход, а не повод остановить рассылку до перезапуска процесса.
///
/// ⚠ Срок перечитывается каждый проход: настройка правится без перезапуска.
/// </remarks>
internal sealed class CertificatePoller(
    IServiceScopeFactory scopes,
    PollWakeup wakeup,
    ILogger<CertificatePoller> log) : BackgroundService
{
    /// <summary>Срок, если настройку прочитать не удалось.</summary>
    private static readonly TimeSpan Запасной = TimeSpan.FromMinutes(15);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var срок = Запасной;

            try
            {
                await using var scope = scopes.CreateAsyncScope();

                await scope.ServiceProvider.GetRequiredService<CertificatePolling>().RunAsync(stoppingToken);

                срок = TimeSpan.FromMinutes(await scope.ServiceProvider
                    .GetRequiredService<ISettingsReader>()
                    .GetAsync(KeyManagerSettings.PollMinutes, stoppingToken));
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ошибка)
            {
                log.LogError("keymanager_poll_crashed {Error}", SafeError.Describe(ошибка));
            }

            try
            {
                await wakeup.WaitAsync(срок, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
    }
}
