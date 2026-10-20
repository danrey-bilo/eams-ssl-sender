using EmailSslSender.Modules.Identity.Contracts;
using EmailSslSender.Modules.Identity.Domain;
using EmailSslSender.Modules.Identity.Users;
using EmailSslSender.Shared.Errors;
using Microsoft.EntityFrameworkCore;

namespace EmailSslSender.Modules.Identity.Tests;

/// <summary>
/// Инварианты, которые нельзя нарушить ни одним путём.
/// </summary>
/// <remarks>
/// Каждый из них закрывает состояние, из которого система сама не выберется: без активной
/// учётной записи её некому починить, а без записи в журнале некому объяснить, кто что
/// сделал.
/// </remarks>
[Collection(IdentityDatabaseCollection.Name)]
public sealed class UserInvariantTests(IdentityDatabase database)
{
    [Fact]
    public async Task Последнюю_активную_учётную_запись_нельзя_отключить()
    {
        // ⚠ Останься система без единого администратора — вернуть его будет нечем, кроме
        // консольной команды на машине. Это единственное состояние, из которого нет выхода
        // средствами самой системы.
        await using var only = await SoleActiveAsync();

        var error = await Should.ThrowAsync<AppException>(() =>
            database.InScopeAsync(users => users.DisableAsync(only.Id, Ct)));

        error.Code.ShouldBe("LAST_SUPERADMIN");
    }

    [Fact]
    public async Task Последнюю_активную_учётную_запись_нельзя_удалить()
    {
        await using var only = await SoleActiveAsync();

        var error = await Should.ThrowAsync<AppException>(() =>
            database.InScopeAsync(users => users.DeleteAsync(only.Id, Ct)));

        error.Code.ShouldBe("LAST_SUPERADMIN");
    }

    [Fact]
    public async Task Предпоследнюю_отключить_можно()
    {
        // Обратная сторона: инвариант про систему, а не про отдельного человека. Запрет,
        // срабатывающий всегда, не дал бы закрыть учётную запись уволенного никогда.
        await using var only = await SoleActiveAsync();

        database.Actor.AsSuperuser();
        var (second, _) = await CreateAsync();

        var disabled = await database.InScopeAsync(users => users.DisableAsync(second.Id, Ct));

        disabled.IsActive.ShouldBeFalse();
        _ = only;
    }

    [Fact]
    public async Task Одновременное_отключение_двух_последних_проходит_только_одним()
    {
        // ⚠ Ровно та гонка, ради которой строки блокируются, а не считаются. Без блокировки
        // обе транзакции увидят «есть ещё один» и обе пройдут — система останется без
        // единого администратора.
        await using var context = await OnlyActiveAsync(count: 2);

        var first = context.Ids[0];
        var second = context.Ids[1];

        var attempts = new[] { first, second }.Select(id => Task.Run(async () =>
        {
            try
            {
                await database.InScopeAsync(users => users.DisableAsync(id, Ct));

                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }));

        var results = await Task.WhenAll(attempts);

        results.Count(ok => ok).ShouldBe(1, "второй должен упереться в инвариант");

        var alive = await database.ReadAsync(db => db.Set<User>()
            .CountAsync(user => user.DeletedAt == null && user.IsActive, Ct));

        alive.ShouldBe(1);
    }

    // --- журнал ---------------------------------------------------------------------------

    [Fact]
    public async Task Каждое_действие_оставляет_запись()
    {
        // ⚠ «Кто кого завёл, отключил и удалил» — ровно тот класс событий, ради которого
        // журнал заводят.
        database.Actor.AsSuperuser();

        var (user, invitation) = await CreateAsync();
        await database.InScopeAsync(users => users.UpdateAsync(user.Id, new UserPatch(DisplayName: "Новое имя"), Ct));
        await database.InScopeAsync(users => users.ActivateAsync(invitation!.Token, Strong, Ct));
        await database.InScopeAsync(users => users.DisableAsync(user.Id, Ct));
        await database.InScopeAsync(users => users.DeleteAsync(user.Id, Ct));

        var actions = database.Audit.About(user.Id).Select(entry => entry.Action.Name).ToList();

        actions.ShouldContain(IdentityActions.UserCreated.Name);
        actions.ShouldContain(IdentityActions.InvitationSent.Name);
        actions.ShouldContain(IdentityActions.UserUpdated.Name);
        actions.ShouldContain(IdentityActions.UserActivated.Name);
        actions.ShouldContain(IdentityActions.UserDisabled.Name);
        actions.ShouldContain(IdentityActions.UserDeleted.Name);
    }

    [Fact]
    public async Task Правка_без_изменений_журнал_не_засоряет()
    {
        // Строка «изменил, но ничего не изменилось» — шум, и в нём тонет то, ради чего журнал
        // читают.
        database.Actor.AsSuperuser();

        var (user, _) = await CreateAsync();
        var before = database.Audit.About(user.Id).Count;

        await database.InScopeAsync(users => users.UpdateAsync(user.Id, new UserPatch(), Ct));

        database.Audit.About(user.Id).Count.ShouldBe(before);
    }

    [Fact]
    public async Task Активацию_записывает_сам_человек_а_не_система()
    {
        // Он ещё не вошёл, но действие совершил он. Запиши мы систему — и вопрос «в какой
        // момент этот адрес превратился в живую учётную запись» остался бы без ответа.
        database.Actor.AsSuperuser();

        var (user, invitation) = await CreateAsync();
        await database.InScopeAsync(users => users.ActivateAsync(invitation!.Token, Strong, Ct));

        var activation = database.Audit.About(user.Id)
            .Single(entry => entry.Action == IdentityActions.UserActivated);

        activation.ActorId.ShouldBe(user.Id);
    }

    [Fact]
    public async Task Ссылка_приглашения_в_журнал_не_попадает()
    {
        // ⚠ Ссылка — это пропуск в систему. В журнале она жила бы вечно, и убрать её оттуда
        // было бы нечем.
        database.Actor.AsSuperuser();

        var (user, invitation) = await CreateAsync();

        var written = System.Text.Json.JsonSerializer.Serialize(
            database.Audit.About(user.Id).Select(entry => entry.Changes));

        written.ShouldNotContain(invitation!.Token);
    }

    // --- первый администратор -------------------------------------------------------------

    [Fact]
    public async Task Первый_администратор_заводится_без_актора()
    {
        // Обычный путь требует вошедшего администратора. Здесь его нет ни у кого, потому что
        // учётных записей ещё нет вовсе.
        database.Actor.AsAdministrator();

        var email = Email();
        var user = await database.InScopeAsync(
            users => users.BootstrapSuperuserAsync(email, "Первый", Strong, Ct));

        user.IsActive.ShouldBeTrue();
        user.ActivatedAt.ShouldNotBeNull("иначе он показывался бы «Приглашён»");
        user.MustChangePassword.ShouldBeFalse();

        database.Audit.About(user.Id)
            .ShouldContain(entry => entry.Action == IdentityActions.SuperuserBootstrapped);
    }

    [Fact]
    public async Task Первый_администратор_подчиняется_той_же_политике_пароля()
    {
        // Первая учётная запись не должна оказаться единственной, заведённой мимо требований.
        var error = await Should.ThrowAsync<AppException>(() =>
            database.InScopeAsync(users => users.BootstrapSuperuserAsync(Email(), null, "1234", Ct)));

        error.Code.ShouldBe("PASSWORD_POLICY_VIOLATION");
    }

    // --- обвязка ---------------------------------------------------------------------------

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private const string Strong = "Очень-Хороший-Пароль-42!";

    private static string Email() => $"user-{Guid.CreateVersion7():N}@example.com";

    private Task<(User User, Invitation? Invitation)> CreateAsync() =>
        database.InScopeAsync(users => users.CreateAsync(
            new UserDraft(Email(), "Тестовый человек", null), Ct));

    /// <summary>
    /// Сделать так, чтобы в системе осталась ровно одна активная учётная запись.
    /// </summary>
    /// <remarks>
    /// ⚠ База одна на прогон, и соседние тесты заводят своих людей. Проверять инвариант
    /// «последний» без этой подготовки значило бы получать зелёный или красный в зависимости
    /// от порядка тестов. Прежнее состояние возвращается при выходе.
    /// </remarks>
    private async Task<SoleActive> SoleActiveAsync() => await OnlyActiveAsync(count: 1);

    private async Task<SoleActive> OnlyActiveAsync(int count)
    {
        var parked = await database.ReadAsync(async db =>
        {
            var existing = await db.Set<User>()
                .Where(user => user.DeletedAt == null && user.IsActive)
                .Select(user => user.Id)
                .ToListAsync(Ct);

            await db.Set<User>()
                .Where(user => existing.Contains(user.Id))
                .ExecuteUpdateAsync(user => user.SetProperty(x => x.IsActive, false), Ct);

            return existing;
        });

        database.Actor.AsSuperuser();

        var ids = new List<Guid>();

        for (var i = 0; i < count; i++)
        {
            var (user, _) = await CreateAsync();
            ids.Add(user.Id);
        }

        return new SoleActive(database, parked, ids);
    }

    /// <summary>Возвращает активность на место, что бы ни случилось в тесте.</summary>
    private sealed class SoleActive(IdentityDatabase database, List<Guid> parked, List<Guid> ids)
        : IAsyncDisposable
    {
        public Guid Id => ids[0];

        public IReadOnlyList<Guid> Ids => ids;

        public async ValueTask DisposeAsync() =>
            await database.ReadAsync(db => db.Set<User>()
                .Where(user => parked.Contains(user.Id))
                .ExecuteUpdateAsync(user => user.SetProperty(x => x.IsActive, true), CancellationToken.None));
    }
}
