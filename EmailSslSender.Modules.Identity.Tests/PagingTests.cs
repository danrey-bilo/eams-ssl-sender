using Microsoft.EntityFrameworkCore;
using EmailSslSender.Modules.Identity.Storage;
using EmailSslSender.Modules.Identity.Users;
using EmailSslSender.Shared.Errors;
using Microsoft.Extensions.DependencyInjection;

namespace EmailSslSender.Modules.Identity.Tests;

/// <summary>
/// Постраничный обход списка людей.
/// </summary>
/// <remarks>
/// ⚠ До 2026-08-22 курсора не было: поле <c>next_cursor</c> в ответе стояло и всегда было
/// пустым, а обрезание по потолку происходило молча — клиент не мог отличить «это всё» от
/// «дальше есть, но мы не покажем».
/// </remarks>
[Collection(IdentityDatabaseCollection.Name)]
public sealed class PagingTests(IdentityDatabase database)
{
    [Fact]
    public async Task Страница_отдаёт_курсор_и_он_продолжает_обход()
    {
        var prefix = await ThreeAsync();

        var first = await PageAsync(prefix, after: null, limit: 2);

        first.Items.Count.ShouldBe(2);
        first.NextCursor.ShouldNotBeNull("дальше есть, и об этом надо сказать");

        var second = await PageAsync(prefix, UserCursor.Decode(first.NextCursor), limit: 2);

        second.Items.Count.ShouldBe(1);
        second.NextCursor.ShouldBeNull("это была последняя страница");

        // Никто не повторился и никто не пропал.
        var seen = first.Items.Concat(second.Items).Select(user => user.Email).ToList();

        seen.Distinct().Count().ShouldBe(3);
    }

    [Fact]
    public async Task Ровно_поместившаяся_страница_курсора_не_даёт()
    {
        // ⚠ Граница, на которой ломается наивная реализация «набрали потолок — значит есть
        // ещё»: она отдала бы курсор на пустую страницу. Поэтому берётся на строку больше.
        var prefix = await ThreeAsync();

        var page = await PageAsync(prefix, after: null, limit: 3);

        page.Items.Count.ShouldBe(3);
        page.NextCursor.ShouldBeNull();
    }

    [Fact]
    public async Task Заведённый_во_время_обхода_не_ломает_страницу()
    {
        // Ради этого свойства обход идёт по ключу, а не по смещению: страница по смещению
        // съезжает, и человек либо видит кого-то дважды, либо не видит вовсе.
        var prefix = await ThreeAsync();

        var first = await PageAsync(prefix, after: null, limit: 2);

        // Заводим того, кто встанет в самое начало: при обходе по смещению он сдвинул бы всё.
        await CreateAsync($"{prefix}-000@example.com");

        var second = await PageAsync(prefix, UserCursor.Decode(first.NextCursor), limit: 2);

        second.Items.Select(user => user.Email)
            .ShouldNotContain(first.Items[0].Email, "первая строка не должна прийти дважды");
    }

    [Fact]
    public async Task Битый_курсор_отвергается_своим_кодом()
    {
        // Отдельный код, а не общая ошибка проверки: по нему клиент сбрасывает пагинацию и
        // начинает сначала, вместо того чтобы показать человеку невнятный отказ.
        Should.Throw<AppException>(() => UserCursor.Decode("это-не-курсор!!"))
            .Code.ShouldBe("INVALID_CURSOR");
    }

    [Fact]
    public void Пустой_курсор_означает_начало_а_не_ошибку()
    {
        UserCursor.Decode(null).ShouldBeNull();
        UserCursor.Decode("").ShouldBeNull();
    }

    // --- обвязка -------------------------------------------------------------------------

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private const string Strong = "Очень-Хороший-Пароль-42!";

    /// <summary>Трое с общим началом адреса: таблица одна на прогон, и выбирать надо своих.</summary>
    private async Task<string> ThreeAsync()
    {
        var prefix = $"paging-{Guid.CreateVersion7():N}";

        foreach (var index in new[] { 1, 2, 3 })
        {
            await CreateAsync($"{prefix}-{index}@example.com");
        }

        return prefix;
    }

    private Task CreateAsync(string email)
    {
        database.Actor.AsSuperuser();

        return database.InScopeAsync(users => users.CreateAsync(
            new UserDraft(email, "Постраничный", Strong, false), Ct));
    }

    /// <summary>
    /// Запрос списка людей НЕ читает хеш пароля.
    /// </summary>
    /// <remarks>
    /// ⚠ ⚠ <b>Сторож вскрытия 27.7, и смотрит он на текст запроса, а не на ответ.</b> По
    /// ответу этого не проверить: <c>UserOut</c> хеша не содержит и не содержал никогда —
    /// наружу он и раньше не уходил. Уходил он <b>из базы в память приложения</b>, на каждом
    /// чтении списка администраторов, и увидеть это можно только в том, что EF попросит у
    /// базы.
    ///
    /// ⚠ Довод — правило 14, а не скорость: замер дал 19.4 КБ на предельной странице, то есть
    /// доли миллисекунды (<c>docs/records/2026-09-02-чтение-людей-замер.md</c>). Хеш argon2 —
    /// материал для офлайнового подбора, и место ему не в выдаче списка.
    ///
    /// мутация 2026-09-02: добавил в проекцию обращение к <c>PasswordHash</c> (условие,
    /// которое ничего не меняет в ответе) → покраснел этот факт: колонка появилась в тексте
    /// запроса. То есть сторож смотрит на то, что РЕАЛЬНО читается из базы, а не на список
    /// полей проекции — вернуть хеш можно и не меняя формы ответа.
    /// </remarks>
    [Fact]
    public async Task Запрос_списка_не_читает_хеш_пароля()
    {
        await using var scope = database.Scope();

        var запрос = scope.ServiceProvider
            .GetRequiredService<UserRepository>()
            .PageQuery(query: null, after: null)
            .ToQueryString();

        // Сторож сторожа: разбор смотрит на настоящий запрос, а не на пустую строку.
        запрос.ShouldContain("users", Case.Insensitive);
        запрос.ShouldContain("email", Case.Insensitive);

        запрос.ShouldNotContain(
            "password_hash",
            Case.Insensitive,
            "хеш пароля читается из базы там, где ни одно поле ответа его не просит");

        await Task.CompletedTask;
    }

    private async Task<(List<Storage.UserRow> Items, string? NextCursor)> PageAsync(
        string query, string? after, int limit)
    {
        await using var scope = database.Scope();

        return await scope.ServiceProvider
            .GetRequiredService<UserRepository>()
            .PageAsync(query, after, limit, Ct);
    }
}
