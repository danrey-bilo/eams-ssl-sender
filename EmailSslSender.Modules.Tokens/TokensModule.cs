using EmailSslSender.Modules.Tokens.Api;
using EmailSslSender.Modules.Tokens.Retention;
using EmailSslSender.Modules.Tokens.Settings;
using EmailSslSender.Modules.Tokens.Tokens;
using EmailSslSender.Shared.Contracts.Settings;
using EmailSslSender.Shared.Contracts.Tokens;
using Microsoft.Extensions.DependencyInjection.Extensions;
using EmailSslSender.Modules.Tokens.Storage;
using EmailSslSender.Shared.Contracts.Data;
using Microsoft.Extensions.DependencyInjection;

namespace EmailSslSender.Modules.Tokens;

/// <summary>
/// Подключение модуля предъявляемых токенов.
/// </summary>
/// <remarks>
/// <b>Модуль отвечает на один вопрос — «чем ты это доказал»</b> — и держит сессии входа:
/// пару «токен доступа + токен обновления», их ротацию, отзыв и обнаружение кражи.
///
/// ⚠ <b>Место ротации, место обнаружения кражи и список «где я вошёл» — одни на систему.</b>
/// Разведи их по модулям — и обнаружение повторного предъявления окажется написанным
/// дважды, а разойдутся эти две реализации молча.
///
/// ⚠ <b>Про человека модуль не знает ничего</b>: пароль, блокировка и второй фактор — у
/// модуля учётных записей, который зовёт <see cref="ITokenIssuer"/>, когда решил пускать.
/// </remarks>
public static class TokensModule
{
    /// <summary>Подключить предъявляемые токены.</summary>
    public static IMvcBuilder AddTokensModule(this IMvcBuilder mvc)
    {
        ArgumentNullException.ThrowIfNull(mvc);

        var services = mvc.Services;

        // Свои таблицы, события журнала и настройки.
        services.AddSingleton<IEntityConfigurationProvider, AuthTokenEntities>();
        services.AddSingleton<ISettingDeclarationProvider, TokenSettingDeclarations>();

        // TryAdd: соседи регистрируют те же часы, и второй Add молча заменил бы подмену,
        // поставленную тестом.
        services.TryAddSingleton(TimeProvider.System);

        // Выдача, обмен и проверка — одна служба на все три контракта: место ротации и место
        // обнаружения кражи обязаны быть одним местом.
        services.AddScoped<TokenService>();
        services.AddScoped<ITokenIssuer>(sp => sp.GetRequiredService<TokenService>());
        services.AddScoped<ITokenIntrospector>(sp => sp.GetRequiredService<TokenService>());

        services.AddScoped<ITokenRevoker, TokenRevoker>();

        // Отметка второго фактора на сессии: код проверяет модуль учётных записей, хранит — этот.
        services.AddScoped<IStepUpMarker, StepUpMarker>();

        // Уборка мёртвых сессий раз в сутки.
        services.AddScoped<TokenCleanup>();
        services.AddHostedService<CleanupService>();

        mvc.ConfigureApplicationPartManager(parts =>
        {
            if (parts.FeatureProviders.All(provider => provider is not InternalControllers))
            {
                parts.FeatureProviders.Add(new InternalControllers());
            }
        });

        // ⚠ Без этой строки контроллеры модуля не найдутся, и получится 404 БЕЗ единой
        // ошибки при старте. Регистрирует модуль сам, чтобы забыть было нельзя.
        return mvc.AddApplicationPart(typeof(TokensModule).Assembly);
    }

}
