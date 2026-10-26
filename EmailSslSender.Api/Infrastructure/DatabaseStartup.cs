using Modules.Core.Composition;
using Modules.Core.Data;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace EmailSslSender.Api.Infrastructure;

/// <summary>
/// Подключение базы, применение наборов миграций и проверка схемы при старте.
/// </summary>
public static class DatabaseStartup
{
    /// <summary>Строка подключения, которой хост ходит в базу, — для наборов миграций.</summary>
    /// <remarks>
    /// ⚠ Наборы миграций создают свои контексты схемы сами (решение 0114), и им нужна та же строка,
    /// что у общего контекста: у команды <c>migrate</c> — владельца схемы, у сервера — рабочая.
    /// </remarks>
    /// <param name="Value">Строка с подставленным паролем.</param>
    internal sealed record SchemaConnection(string Value)
    {
        /// <summary>Печать без строки: в ней пароль.</summary>
        private bool PrintMembers(System.Text.StringBuilder builder)
        {
            builder.Append("строка скрыта");

            return true;
        }
    }

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

        // Два способа получить контекст, и оба нужны.
        //
        // Обычный (scoped) — единица работы запроса: действие и его запись в журнал уезжают
        // одной транзакцией, ради чего всё и затевалось.
        //
        // Фабрика — для записи, которая обязана пережить откат этой транзакции: отказ в
        // правах пишется ровно перед 403, а 403 откатывает запрос. Своё подключение —
        // единственный способ не потерять журнал отказов молча.
        //
        // ⚠ `optionsLifetime: Singleton` обязателен при таком сочетании: фабрика по
        // умолчанию регистрирует `DbContextOptions` синглтоном, и без выравнивания две
        // регистрации одного типа с разным временем жизни перетирают друг друга.
        // ⚠ Перехватчики берутся ИЗ КОНТЕЙНЕРА, а не перечисляются здесь. Хост не знает,
        // что они делают и чьи они, — ровно как не знает, что внутри модулей: он связывает
        // контракты. Модулю нужно это затем, что о фиксации ЧУЖОЙ транзакции узнать иначе
        // нечем, а работа «после фиксации» бывает не только у того, кто транзакцию открыл.
        services.AddDbContextFactory<AppDbContext>(
            (provider, options) => Configure(options, connectionString, provider));

        services.AddDbContext<AppDbContext>(
            (provider, options) => Configure(options, connectionString, provider),
            optionsLifetime: ServiceLifetime.Singleton);

        services.AddSingleton(new SchemaConnection(connectionString));

        return services;
    }

    /// <summary>Настройка провайдера — одна на оба способа получить контекст.</summary>
    private static void Configure(
        DbContextOptionsBuilder options, string connectionString, IServiceProvider provider)
    {
        // ⚠ Перехватчики — синглтоны, и потому берутся из корневого контейнера безопасно.
        // Областной перехватчик потребовал бы пересобирать настройки контекста на каждую
        // область; это поддерживается, но стоит заметно дороже, а нужды в этом нет.
        options.AddInterceptors(provider.GetServices<IInterceptor>());

        // ⚠ ⚠ Ключ кэша модели — свой (подэтап 27.4). Модель этого контекста собирается из
        // внедрённых конфигураций модулей, а ключ по умолчанию видит только ТИП контекста:
        // два контекста одного типа с разными наборами делили одну модель. Разбор и цена —
        // в докстринге `AppModelCacheKeyFactory`.
        options.ReplaceService<IModelCacheKeyFactory, AppModelCacheKeyFactory>();

        options
            .UseNpgsql(connectionString, npgsql => npgsql
                // Повтор при обрыве: контейнер базы перезапускается, и падать из-за
                // мгновения недоступности приложению незачем. На ошибках, которые повтор не
                // лечит (нарушение ограничения), это не сказывается — Npgsql различает.
                .EnableRetryOnFailure(maxRetryCount: 3, TimeSpan.FromSeconds(2), null));

        // ⚠ Сборки миграций у общего контекста нет (решение 0114): он не мигрирует. Схему несут наборы
        // модулей — каждый своим контекстом схемы, — а прежние миграции хоста применяет только
        // SchemaMigrator, своим контекстом.
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
    /// <returns>Что сделано — строками «набор: миграция».</returns>
    public static Task<IReadOnlyList<string>> MigrateAsync(this WebApplication app, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(app);

        return SchemaMigrator.MigrateAsync(
            app.Services.GetRequiredService<SchemaConnection>().Value,
            Наборы(app),
            app.Services.GetRequiredService<AdvisoryLockSpace>(),
            cancellationToken);
    }

    /// <summary>Наборы миграций в порядке регистрации: модули по порядку подключения, проект последним.</summary>
    internal static IReadOnlyList<ModuleSchema> Наборы(WebApplication app) =>
        [.. app.Services.GetServices<ModuleSchema>()];

    /// <summary>Какие миграции ещё не применены.</summary>
    /// <remarks>
    /// Отдельным методом, потому что спрашивают дважды и по-разному: команда <c>migrate</c>
    /// печатает список до применения, старт сервера отказывается работать при непустом. Две
    /// копии одного запроса разошлись бы — например, одна научилась бы игнорировать
    /// какую-нибудь миграцию, а вторая нет.
    /// </remarks>
    public static Task<IReadOnlyList<string>> PendingMigrationsAsync(
        this WebApplication app, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(app);

        return SchemaMigrator.PendingAsync(
            app.Services.GetRequiredService<SchemaConnection>().Value, Наборы(app), cancellationToken);
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
            + "docker compose run --rm migrate");
    }
}
