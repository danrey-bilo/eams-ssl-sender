using EmailSslSender.Modules.Identity.Domain;
using EmailSslSender.Modules.Identity.TwoFactor;
using EmailSslSender.Modules.Identity.Users;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace EmailSslSender.Modules.Identity.Tests;

/// <summary>
/// Таблица способов второго фактора — по тесту на строку.
/// </summary>
/// <remarks>
/// ⚠ ⚠ <b>Входов у правила два, и строк ровно четыре:</b> подтверждён ли аутентификатор ×
/// настроена ли почта. Собственного выбора способа и настройки установки больше нет — они ни на
/// что не влияли и удалены, — так что каждая комбинация проверяется своим фактом.
///
/// ⚠ <b>Ни одна строка не даёт «заперт».</b> Последний столбец таблицы — отдельное условие на
/// каждой строке: фактор не может требовать того, чего у человека нет.
/// </remarks>
[Collection(IdentityDatabaseCollection.Name)]
public sealed class ТаблицаСпособов(IdentityDatabase database) : IAsyncLifetime
{
    private static readonly string Strong = "Correct-Horse-Battery-9";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    /// <summary>
    /// Вернуть подставную почту в исходное — «не настроена».
    /// </summary>
    /// <remarks>
    /// ⚠ ⚠ <b>Обязательно.</b> Обвязка одна на коллекцию: оставленная настроенной почта делает
    /// второй фактор письмом у всех, и тесты, входящие по паролю, получают «нужен второй фактор» —
    /// при том что про почту ни один из них ничего не знает. Признак такой заразы узнаётся по
    /// месту: краснеют файлы, которых правка не касалась.
    /// </remarks>
    public ValueTask DisposeAsync()
    {
        database.Почта.Письма.Clear();
        database.Почта.Настроена = false;

        return ValueTask.CompletedTask;
    }

    // ⚠ ⚠ ⚠ Аутентификатор подтверждён → только он, почта не важна; взяты оба значения почты.
    // Ревью безопасности 2026-10-20: письмо подтверждённый аутентификатор не заменяет никогда.
    [Theory]
    [InlineData(true, true, "totp")]
    [InlineData(true, false, "totp")]
    // Аутентификатора нет, почта есть → письмо.
    [InlineData(false, true, "email")]
    // Аутентификатора нет, почты нет → не спрашивается (принятый риск: только сессия настройки).
    [InlineData(false, false, "none")]
    public async Task Строка_таблицы(bool аутентификатор, bool почта, string ожидается)
    {
        var id = await ЧеловекAsync(аутентификатор);

        database.Почта.Настроена = почта;

        var действует = await РешитьAsync(id);

        действует.ShouldBe(ожидается);

        // ⚠ Последний столбец таблицы: «заперт ли». Ни одна строка не имеет права требовать
        // того, чего у человека нет, — иначе он не войдёт вовсе.
        if (действует == "totp")
        {
            аутентификатор.ShouldBeTrue("фактор требует аутентификатора, которого нет");
        }

        if (действует == "email")
        {
            почта.ShouldBeTrue("фактор требует письма, а слать его некуда");
        }
    }

    /// <summary>Завести человека и, если просят, подтвердить ему аутентификатор.</summary>
    private async Task<Guid> ЧеловекAsync(bool аутентификатор)
    {
        database.Actor.AsSuperuser();

        var email = $"методы-{Guid.CreateVersion7():N}@example.com";
        var (user, _) = await database.InScopeAsync(users => users.CreateAsync(
            new UserDraft(email, "Тестовый человек", Strong, MustChangePassword: false), Ct));

        if (аутентификатор)
        {
            database.Actor.Become(user.Id);

            await using var scope = database.Scope();
            var factors = scope.ServiceProvider.GetRequiredService<TwoFactorService>();
            var setup = await factors.SetupAsync(Ct);

            await factors.ConfirmAsync(TestTotp.Code(setup.Secret), Ct);
        }

        return user.Id;
    }

    private async Task<string> РешитьAsync(Guid id)
    {
        await using var scope = database.Scope();
        var db = scope.ServiceProvider.GetRequiredService<EmailSslSender.Shared.Data.SenderDbContext>();

        var user = await db.Set<User>().SingleAsync(row => row.Id == id, Ct);

        return await scope.ServiceProvider
            .GetRequiredService<SecondFactorPolicy>()
            .ResolveAsync(user, Ct);
    }
}
