using EmailSslSender.Modules.KeyManager.Settings;
using Modules.Core.Contracts.Settings;
using Modules.Core.Errors;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace EmailSslSender.Modules.KeyManager.Delivery;

/// <summary>Сигнал «опросить сейчас»; будит опрос только в своём процессе.</summary>
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
                // Гонка двух вызовов: сигнал уже взведён.
            }
        }
    }

    public Task<bool> WaitAsync(TimeSpan сколько, CancellationToken cancellationToken) =>
        _сигнал.WaitAsync(сколько, cancellationToken);
}

/// <summary>Фоновый опрос: раз в <c>keymanager.poll_minutes</c> (перечитывается каждый проход) и по сигналу.</summary>
/// <remarks>⚠ Ошибка прохода не роняет службу — это лишь пропущенный проход.</remarks>
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
