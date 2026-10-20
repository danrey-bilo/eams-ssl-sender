using EmailSslSender.Modules.Identity.Api;
using EmailSslSender.Modules.Identity.Settings;
using EmailSslSender.Modules.Identity.Retention;
using EmailSslSender.Modules.Identity.Storage;
using EmailSslSender.Modules.Identity.TwoFactor;
using EmailSslSender.Modules.Identity.Mail;
using EmailSslSender.Modules.Identity.Users;
using EmailSslSender.Shared.Contracts.Authz;
using EmailSslSender.Shared.Contracts.Data;
using EmailSslSender.Shared.Contracts.Mail;
using EmailSslSender.Shared.Contracts.Settings;
using EmailSslSender.Shared.Crypto;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using EmailSslSender.Modules.Identity.Passwords;

namespace EmailSslSender.Modules.Identity;

/// <summary>
/// Подключение модуля личностей.
/// </summary>
/// <remarks>
/// Одна строка у хоста. Что внутри — его не касается.
/// </remarks>
public static class IdentityModule
{
    /// <summary>Подключить учётные записи.</summary>
    public static IMvcBuilder AddIdentityModule(
        this IMvcBuilder mvc, Action<IdentityModuleOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(mvc);

        var options = new IdentityModuleOptions();
        configure?.Invoke(options);

        var services = mvc.Services;

        services.AddSingleton(options);

        // Предел попыток входа на пару «адрес + почта» — один на процесс.
        services.AddSingleton<SignInAttempts>();

        // Свои объявления: настройки и таблицы.
        services.AddSingleton<ISettingDeclarationProvider, IdentitySettingDeclarations>();
        services.AddSingleton<IEntityConfigurationProvider, IdentityEntities>();

        // ⚠ Часы регистрирует модуль, которому они нужны: модуль объявляет всё, что ему нужно,
        // сам. TryAdd, а не Add: соседи регистрируют то же самое, и второй Add молча заменил бы
        // подмену часов, поставленную тестом.
        services.TryAddSingleton(TimeProvider.System);

        // Хешер один на процесс: он без состояния, а внутри держит ограничитель
        // одновременности — по экземпляру на запрос ограничивать было бы нечего.
        services.AddSingleton<IPasswordHasher>(_ => new PasswordHasher());

        services.AddScoped<UserRepository>();
        services.AddScoped<Invitations>();
        services.AddScoped<PasswordPolicy>();
        services.AddScoped<UserService>();

        // ⚠ Письма модуля: тексты объявляет тот, кто их шлёт, а не почта — иначе добавление
        // письма требовало бы правки чужого модуля.
        services.AddSingleton<IMailTemplateProvider, IdentityMailTemplates>();
        services.AddScoped<MailNotices>();

        // Второй фактор: приложение-аутентификатор (TOTP) и код письмом.
        services.AddScoped<SecondFactorPolicy>();
        services.AddScoped<LoginCodes>();
        services.AddScoped<LoginFailures>();
        services.AddScoped<SignInService>();
        services.AddScoped<TwoFactorService>();

        // Переподтверждение личности: код проверяем мы, отметку хранит модуль токенов.
        services.AddScoped<StepUpService>();

        // ⚠ Перекладывание обёрток при смене мастер-ключа. Найти запечатанное умеет только
        // владелец таблицы — общая крипта знает, КАК переложить, но не знает, ГДЕ лежит.
        services.AddScoped<IRewrapTarget, TwoFactorRewrap>();

        // Распознаватель личности по токену доступа. Хост выстраивает такие цепочкой.
        services.AddScoped<IIdentityResolver, SessionIdentityResolver>();

        // Уборка одноразовых ссылок и кодов. Раз в сутки, с отсрочкой после старта.
        services.AddScoped<LinkCleanup>();
        services.AddHostedService<CleanupService>();

        // ⚠ Модуль не выдаёт токенов сам. Забытый AddTokensModule() означал бы систему,
        // которая поднялась и умирает на первом входе.
        services.AddHostedService<TokenContractCheck>();

        mvc.ConfigureApplicationPartManager(parts =>
        {
            if (parts.FeatureProviders.All(provider => provider is not InternalControllers))
            {
                parts.FeatureProviders.Add(new InternalControllers());
            }
        });

        // ⚠ Без этой строки контроллеры модуля не найдутся, и получится 404 БЕЗ единой
        // ошибки при старте. Регистрирует модуль сам, чтобы забыть было нельзя.
        return mvc.AddApplicationPart(typeof(IdentityModule).Assembly);
    }


    /// <summary>
    /// Завести первого администратора.
    /// </summary>
    /// <remarks>
    /// Операция установки из консоли: публичной регистрации нет. Служба и хранилище
    /// остаются внутренними; хост вызывает только точку входа модуля.
    /// </remarks>
    /// <returns>Идентификатор заведённого человека.</returns>
    public static async Task<Guid> BootstrapSuperuserAsync(
        IServiceProvider services,
        string email,
        string? displayName,
        string password,
        CancellationToken cancellationToken = default)
    {
        await using var scope = services.CreateAsyncScope();

        var user = await scope.ServiceProvider
            .GetRequiredService<UserService>()
            .BootstrapSuperuserAsync(email, displayName, password, cancellationToken);

        return user.Id;
    }

    /// <summary>Сменить пароль учётной записи из консоли установки.</summary>
    public static async Task<Guid> SetSuperuserPasswordAsync(
        IServiceProvider services, string email, string password, CancellationToken cancellationToken = default)
    {
        await using var scope = services.CreateAsyncScope();
        var user = await scope.ServiceProvider.GetRequiredService<UserService>()
            .SetSuperuserPasswordAsync(email, password, cancellationToken);
        return user.Id;
    }

    /// <summary>Снять второй фактор из консоли — потерян телефон; все сессии закрываются.</summary>
    public static async Task<Guid> ResetSuperuserTwoFactorAsync(
        IServiceProvider services, string email, CancellationToken cancellationToken = default)
    {
        await using var scope = services.CreateAsyncScope();
        var user = await scope.ServiceProvider.GetRequiredService<UserService>()
            .RequireSuperuserForRecoveryAsync(email, cancellationToken);
        await scope.ServiceProvider.GetRequiredService<TwoFactorService>()
            .ResetSuperuserForConsoleAsync(user.Id, cancellationToken);
        return user.Id;
    }
}

/// <summary>Настройки подключения модуля, которые задаёт хост.</summary>
public sealed class IdentityModuleOptions
{
    /// <summary>Попыток входа в минуту на пару «адрес + почта».</summary>
    /// <remarks>
    /// Предел по адресу у хоста — потолок против потока, а перебор одной учётной записи держит
    /// этот предел и блокировка после неудач. Значение даёт хост из
    /// <c>RateLimits:LoginPerMinute</c>.
    /// </remarks>
    public int SignInAttemptsPerMinute { get; set; } = 10;
}
