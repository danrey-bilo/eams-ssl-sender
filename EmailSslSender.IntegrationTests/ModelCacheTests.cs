using EmailSslSender.Api.Infrastructure;
using EmailSslSender.Shared.Contracts.Data;
using EmailSslSender.Shared.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace EmailSslSender.IntegrationTests;

/// <summary>
/// Кэш модели EF не делится между разными наборами конфигураций (подэтап 27.4).
/// </summary>
/// <remarks>
/// ⚠ ⚠ <b>Заведён по следу настоящего дефекта, который локальный прогон не ловит по
/// построению.</b> EF кэширует модель по ТИПУ контекста, а модель <see cref="SenderDbContext"/>
/// собирается из внедрённых <see cref="IEntityConfigurationProvider"/> — то есть зависит от
/// содержимого контейнера, о котором ключ по умолчанию не знает. Два контекста одного типа с
/// разными наборами делили одну модель, и доставалась она тому, кто построил её первым.
///
/// В CI обвязка тестов выигрывала эту гонку контекстом БЕЗ конфигураций модулей, и настоящее
/// приложение получало <b>пустую модель</b>: 721 сбой из 751 при зелёном локальном прогоне
/// (дважды локально на Windows и дважды в Linux-контейнере — 391 тест, ноль сбоев).
///
/// ⚠ <b>Проверка намеренно НЕ опирается на порядок тестов.</b> Воспроизводить дефект гонкой
/// значило бы завести проверку, которая сама зеленеет через раз. Здесь обе модели строятся в
/// одном методе, подряд, и второй обязан отличаться от первой.
///
/// ⚠ <b>Базы не нужно вовсе:</b> модель строится без подключения. Отсюда и цена проверки —
/// доли секунды, без контейнеров.
/// </remarks>
public sealed class ModelCacheTests
{
    /// <summary>Строка подключения, по которой никто не ходит: модель строится без базы.</summary>
    private const string Строка = "Host=нет;Database=нет;Username=нет;Password=нет";

    /// <summary>Конфигурация-пустышка: одна таблица, которой нет ни в одном модуле.</summary>
    private sealed class Отличие : IEntityConfigurationProvider
    {
        public void Configure(ModelBuilder modelBuilder)
        {
            ArgumentNullException.ThrowIfNull(modelBuilder);

            modelBuilder.Entity<Метка>().ToTable("проба_кэша_модели").HasKey(строка => строка.Id);
        }
    }

    /// <summary>Вторая конфигурация — ДРУГАЯ, но такая же по счёту. См. второй факт.</summary>
    private sealed class ДругоеОтличие : IEntityConfigurationProvider
    {
        public void Configure(ModelBuilder modelBuilder)
        {
            ArgumentNullException.ThrowIfNull(modelBuilder);

            modelBuilder.Entity<ВтораяМетка>().ToTable("проба_кэша_вторая").HasKey(строка => строка.Id);
        }
    }

    /// <summary>Сущность-метка. Существует только ради этой проверки.</summary>
    public sealed class Метка
    {
        public Guid Id { get; set; }
    }

    /// <summary>Метка второго набора.</summary>
    public sealed class ВтораяМетка
    {
        public Guid Id { get; set; }
    }

    [Fact]
    public void Модель_собирается_по_своему_набору_конфигураций_а_не_по_чужому()
    {
        // Шаг 1 — как обвязка миграций: контекст БЕЗ конфигураций модулей. Его модель пуста
        // по построению, и это законно: применять миграции модель не нужна.
        using var мигратор = new ServiceCollection()
            .AddSenderDatabase(Строка)
            .BuildServiceProvider();

        var пустая = мигратор.GetRequiredService<SenderDbContext>().Model;

        // Шаг 2 — как приложение: та же регистрация, но конфигурация есть.
        using var приложение = new ServiceCollection()
            .AddSenderDatabase(Строка)
            .AddSingleton<IEntityConfigurationProvider, Отличие>()
            .BuildServiceProvider();

        var своя = приложение.GetRequiredService<SenderDbContext>().Model;

        // Зеркальные факты: без первого проверка зеленела бы и от «первая модель тоже полная»,
        // без второго — от «кэш вообще выключен», а это другая цена и другой дефект.
        Assert.Empty(пустая.GetEntityTypes());
        Assert.NotEmpty(своя.GetEntityTypes());

        // мутация 2026-09-01: снял `ReplaceService<IModelCacheKeyFactory, ...>` в
        // `DatabaseStartup.Configure` → покраснел второй факт («Collection was empty»).
        Assert.Contains(своя.GetEntityTypes(), тип => тип.ClrType == typeof(Метка));
    }

    /// <summary>
    /// Наборы РАЗНЫЕ, а по счёту одинаковые — и модели обязаны различаться.
    /// </summary>
    /// <remarks>
    /// ⚠ ⚠ <b>Заведён мутацией, а не планом.</b> Первая редакция сторожа проверяла только
    /// «пустой набор против непустого», и мутация «ключ = ЧИСЛО конфигураций вместо их имён»
    /// прошла её зелёной. То есть докстринг фабрики утверждал про имена, а проверялось число
    /// — ровно тот разрыв, против которого правило 0. Два модуля, поменянные местами, дают
    /// тот же счёт и другую модель; здесь это и проверяется.
    /// </remarks>
    [Fact]
    public void Наборы_одного_размера_но_разного_состава_не_делят_модель()
    {
        using var первый = new ServiceCollection()
            .AddSenderDatabase(Строка)
            .AddSingleton<IEntityConfigurationProvider, Отличие>()
            .BuildServiceProvider();

        using var второй = new ServiceCollection()
            .AddSenderDatabase(Строка)
            .AddSingleton<IEntityConfigurationProvider, ДругоеОтличие>()
            .BuildServiceProvider();

        var модельПервого = первый.GetRequiredService<SenderDbContext>().Model;
        var модельВторого = второй.GetRequiredService<SenderDbContext>().Model;

        Assert.Contains(модельПервого.GetEntityTypes(), тип => тип.ClrType == typeof(Метка));
        Assert.Contains(модельВторого.GetEntityTypes(), тип => тип.ClrType == typeof(ВтораяМетка));
        Assert.DoesNotContain(модельВторого.GetEntityTypes(), тип => тип.ClrType == typeof(Метка));
    }
}
