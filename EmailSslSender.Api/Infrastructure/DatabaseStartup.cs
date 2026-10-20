using EmailSslSender.Shared.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace EmailSslSender.Api.Infrastructure;

/// <summary>
/// Подключение базы и применение миграций при старте.
/// </summary>
public static class DatabaseStartup
{
    /// <summary>Зарегистрировать общий контекст.</summary>
    /// <param name="services">Куда регистрировать.</param>
    /// <param name="connectionString">
    /// Готовая строка подключения — <b>с уже подставленным паролем</b>. Собирает её
    /// <see cref="ConnectionSecrets"/>: пароль приезжает файлом секрета, а не переменной
    /// окружения, и место сборки должно быть одно на весь хост.
    /// </param>
    public static IServiceCollection AddSenderDatabase(this IServiceCollection services, string connectionString)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);

        // Контекст — единица работы запроса: действие, его письма и гашение сессий уезжают
        // одной транзакцией.
        services.AddDbContext<SenderDbContext>(options => Configure(options, connectionString));

        return services;
    }

    /// <summary>Настройка провайдера — одна на оба способа получить контекст.</summary>
    private static void Configure(DbContextOptionsBuilder options, string connectionString)
    {
        // ⚠ ⚠ Ключ кэша модели — свой. Модель этого контекста собирается из
        // внедрённых конфигураций модулей, а ключ по умолчанию видит только ТИП контекста:
        // два контекста одного типа с разными наборами делили одну модель. Разбор и цена —
        // в докстринге `SenderModelCacheKeyFactory`.
        options.ReplaceService<IModelCacheKeyFactory, SenderModelCacheKeyFactory>();

        options
            .UseNpgsql(connectionString, npgsql => npgsql
                // Повтор при обрыве: контейнер базы перезапускается, и падать из-за
                // мгновения недоступности приложению незачем. На ошибках, которые повтор не
                // лечит (нарушение ограничения), это не сказывается — Npgsql различает.
                .EnableRetryOnFailure(maxRetryCount: 3, TimeSpan.FromSeconds(2), null)
                // Миграции живут в хосте, а не рядом с контекстом. Контекст в общей
                // библиотеке ничего не знает о провайдере — а миграция без провайдера не
                // пишется. К тому же схема развёрнутой установки это дело хоста, а не
                // библиотеки, которую он подключает.
                .MigrationsAssembly(typeof(DatabaseStartup).Assembly.FullName));
    }

    /// <summary>
    /// Применить миграции.
    /// </summary>
    /// <remarks>
    /// <b>Только <c>MigrateAsync</c>, никогда <c>EnsureCreated</c>.</b> Второй строит схему
    /// по модели, минуя миграции, и <b>не умеет партиционирования</b>: журнал получился бы
    /// обычной таблицей. Тесты на неизменяемость при этом покраснели бы шумно, а тесты на
    /// партиции — <b>зеленели бы тихо</b>, и разошлась бы схема разработки со схемой боя.
    ///
    /// ⚠ <b>Зовётся ТОЛЬКО из команды <c>migrate</c>, а не при старте сервера</b> (этап 11.5,
    /// правило 16). Пока это делал старт, требование «перед миграцией на живой базе — копия»
    /// было неисполнимо: <c>docker compose up -d --build</c> <b>и есть</b> миграция, и к
    /// моменту, когда оператор вспомнит про копию, схема уже изменена. Второе, не менее
    /// важное: миграции накатывает владелец схемы, а приложение с этапа 11.5 ходит узкой
    /// ролью, которой создавать таблицы нечем.
    /// </remarks>
    public static async Task MigrateAsync(this WebApplication app, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(app);

        await using var scope = app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<SenderDbContext>();

        await db.Database.MigrateAsync(cancellationToken);
    }

    /// <summary>Какие миграции ещё не применены.</summary>
    /// <remarks>
    /// Отдельным методом, потому что спрашивают дважды и по-разному: команда <c>migrate</c>
    /// печатает список до применения, старт сервера отказывается работать при непустом. Две
    /// копии одного запроса разошлись бы — например, одна научилась бы игнорировать
    /// какую-нибудь миграцию, а вторая нет.
    /// </remarks>
    public static async Task<IReadOnlyList<string>> PendingMigrationsAsync(
        this WebApplication app, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(app);

        await using var scope = app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<SenderDbContext>();

        return [.. await db.Database.GetPendingMigrationsAsync(cancellationToken)];
    }

    /// <summary>
    /// Не стартовать, если схема отстала от кода.
    /// </summary>
    /// <remarks>
    /// ⚠ <b>Отказ, а не автоматическое применение.</b> Приложение, чинящее схему само,
    /// лишает оператора единственного момента, когда копию ещё можно снять. Отсюда и
    /// сообщение: оно называет недостающие миграции поимённо и печатает команду целиком —
    /// человек, увидевший его в три часа ночи, не должен вспоминать синтаксис.
    ///
    /// ⚠ <b><c>docker compose run --rm migrate</c>, а не <c>exec</c>.</b> Отказавшееся
    /// приложение перезапускается по <c>restart: unless-stopped</c>, и <c>exec</c> в него не
    /// попадёт: контейнера в момент попытки может не быть. Одноразовый контейнер поднимается
    /// на том же образе с той же конфигурацией и не зависит от состояния основного.
    ///
    /// ⚠ И <b>без</b> <c>dotnet EmailSslSender.Api.dll</c> в строке: у образа это <c>ENTRYPOINT</c>, а
    /// <c>run</c> дописывает аргументы к нему. У <c>exec</c> наоборот — он запускает процесс
    /// сам, и там полная строка обязательна. Разница поймана живым прогоном.
    ///
    /// Обратный случай — схема <b>впереди</b> кода — здесь не ошибка: так выглядит откат
    /// выкатки, и ради него правило 16 требует, чтобы прежний код работал со следующей
    /// схемой. Ронять приложение в момент отката значило бы отнимать способ отката.
    /// </remarks>
    public static async Task RequireCurrentSchemaAsync(
        this WebApplication app, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(app);

        var pending = await app.PendingMigrationsAsync(cancellationToken);

        if (pending.Count == 0)
        {
            return;
        }

        throw new InvalidOperationException(
            $"Схема базы отстала от кода: не применено миграций — {pending.Count} "
            + $"({string.Join(", ", pending)}). Приложение не стартует, пока схема не "
            + "накатана: делать это самому означало бы менять базу раньше, чем оператор "
            + "успеет снять копию. Порядок: сначала копия базы, затем "
            + "docker compose run --rm api migrate");
    }
}
