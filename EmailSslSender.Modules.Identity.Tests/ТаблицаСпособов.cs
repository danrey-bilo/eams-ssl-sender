using EmailSslSender.Modules.Identity.Domain;
using EmailSslSender.Modules.Identity.Settings;
using EmailSslSender.Modules.Identity.TwoFactor;
using EmailSslSender.Modules.Identity.Users;
using EmailSslSender.Shared.Errors;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace EmailSslSender.Modules.Identity.Tests;

/// <summary>
/// Таблица способов второго фактора — по тесту на строку.
/// </summary>
/// <remarks>
/// ⚠ ⚠ <b>Файл существует потому, что прежняя формулировка условия завершения была
/// непроверяемой.</b> План этапа 17.7 требовал «ни одна комбинация не оставляет человека
/// снаружи»; перечня комбинаций при этом не было, значит «да» по такому условию говорят не
/// считая. Комбинаций двенадцать — выбор человека (три) × заведён ли аутентификатор × настроена
/// ли почта, — и сворачиваются они в девять строк таблицы в
/// <c>docs/roadmap/EmailSslSender.Modules.Ops.md</c>. Здесь проверяются все двенадцать.
///
/// ⚠ ⚠ <b>Две строки таблицы говорят «почта не важна», и обе взяты ОБОИМИ значениями.</b>
/// Соблазн был взять по одному, и он ведёт к тесту, который зеленеет по неверной причине: при
/// <b>ненастроенной</b> почте «<c>totp</c> · аутентификатор заведён» даёт <c>totp</c> и с
/// выброшенным правилом «выбранный и заведённый аутентификатор решает» — потому что тот же
/// ответ приходит последней строкой разрешения, откатом «почты нет, но аутентификатор есть».
/// Мутацию ловит только настроенная почта. Раз выбирать между двумя значениями приходится
/// рассуждением, надёжнее не выбирать: <b>двенадцать наборов — двенадцать фактов</b>, ровно
/// столько, сколько их бывает, и девять строк таблицы разложены по ним комментариями.
///
/// ⚠ <b>Ни одна строка не даёт «заперт».</b> Это и есть проверяемая форма прежнего утверждения:
/// последний столбец таблицы здесь — отдельное <c>ShouldNotBe</c> на каждой строке, а не общее
/// рассуждение под ней.
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
    /// ⚠ ⚠ <b>Обязательно, и это ВТОРОЙ случай той же заразы в одном этапе.</b> Обвязка одна на
    /// коллекцию: оставленная настроенной почта делает второй фактор письмом у всех, и
    /// восемнадцать тестов, входящих по паролю, получают «нужен второй фактор» — при том что про
    /// почту ни один из них ничего не знает. Первый случай (`MailNoticeTests`) стоил трёх красных
    /// тестов в чужом файле; этот — восемнадцати, и разбирать их пришлось бы по одному.
    ///
    /// ⚠ Признак заразы узнаётся не по числу, а по месту: краснеют файлы, которых правка не
    /// касалась. Такое всегда обвязка, а не код.
    /// </remarks>
    public ValueTask DisposeAsync()
    {
        database.Почта.Письма.Clear();
        database.Почта.Настроена = false;
        database.Settings.Values.Remove(IdentitySettings.SecondFactor.Name);

        return ValueTask.CompletedTask;
    }

    // Строка 1: totp · аутентификатор есть · почта НЕ ВАЖНА → аутентификатор. Оба значения.
    [Theory]
    [InlineData("totp", true, true, "totp")]
    [InlineData("totp", true, false, "totp")]
    // Строка 2: totp · аутентификатора нет · почта есть → письмо (подмена).
    [InlineData("totp", false, true, "email")]
    // Строка 3: totp · аутентификатора нет · почты нет → фактор не спрашивается.
    [InlineData("totp", false, false, "none")]
    // Строка 4: email · аутентификатор есть · почта есть → письмо. ⚠ Строка, на которой
    // разошлись таблица и код: выбор человека не отменяется тем, что у него есть что-то
    // надёжнее. Потерявший телефон обязан уметь переключиться на письмо.
    [InlineData("email", true, true, "email")]
    // Строка 5: email · аутентификатор есть · почты нет → аутентификатор (регрессия эталона).
    [InlineData("email", true, false, "totp")]
    // Строка 6: email · аутентификатора нет · почта есть → письмо.
    [InlineData("email", false, true, "email")]
    // Строка 7: email · аутентификатора нет · почты нет → не спрашивается (принятый риск Р6).
    [InlineData("email", false, false, "none")]
    // Строка 8: выбора нет · аутентификатор есть · почта НЕ ВАЖНА → аутентификатор. Оба значения.
    [InlineData(null, true, true, "totp")]
    [InlineData(null, true, false, "totp")]
    // Строка 9: выбора нет · аутентификатора нет → как задано настройкой, то есть строки
    // выше при `email`: почта есть — письмо, почты нет — не спрашивается.
    [InlineData(null, false, true, "email")]
    [InlineData(null, false, false, "none")]
    public async Task Строка_таблицы(string? выбран, bool аутентификатор, bool почта, string ожидается)
    {
        var id = await ЧеловекAsync(выбран, аутентификатор);

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

    /// <summary>
    /// Системная настройка не действует на того, у кого аутентификатор заведён.
    /// </summary>
    /// <remarks>
    /// ⚠ Девятая строка таблицы («как задано настройкой») читается вместе с восьмой: настройка
    /// решает только там, где нет ни выбора человека, ни аутентификатора. Здесь проверяется
    /// именно граница — переключение настройки на <c>none</c> у того, кто аутентификатор завёл.
    /// </remarks>
    [Fact]
    public async Task Настройка_не_снимает_заведённый_аутентификатор()
    {
        var id = await ЧеловекAsync(выбран: null, аутентификатор: true);

        database.Почта.Настроена = false;
        database.Settings.Values[IdentitySettings.SecondFactor.Name] = "none";

        try
        {
            (await РешитьAsync(id)).ShouldBe("totp");
        }
        finally
        {
            database.Settings.Values.Remove(IdentitySettings.SecondFactor.Name);
        }
    }

    /// <summary>
    /// Незнакомое значение настройки откатывается на письмо, а не на отсутствие фактора.
    /// </summary>
    /// <remarks>
    /// ⚠ ⚠ Способ хранится строкой (см. <c>SecondFactorMethods</c>), а настройка правится из
    /// админки — значит однажды там окажется опечатка или значение из будущей версии. Откат
    /// обязан идти в <b>безопасную</b> сторону: письмо всё-таки спрашивает фактор, <c>none</c> —
    /// снимает его молча. Ровно об этом предупреждает примечание над <c>SecondFactorMethods</c>:
    /// разбор с откатом на умолчание — то самое место, где теряют фактор.
    /// </remarks>
    [Fact]
    public async Task Незнакомое_значение_настройки_откатывается_на_письмо()
    {
        var id = await ЧеловекAsync(выбран: null, аутентификатор: false);

        database.Почта.Настроена = true;
        database.Settings.Values[IdentitySettings.SecondFactor.Name] = "sms";

        try
        {
            (await РешитьAsync(id)).ShouldBe("email");
        }
        finally
        {
            database.Settings.Values.Remove(IdentitySettings.SecondFactor.Name);
        }
    }

    /// <summary>
    /// «Нет фактора» не выбирается человеком: отказ приходит из списка, а не после нажатия.
    /// </summary>
    /// <remarks>
    /// ⚠ Условие завершения этапа 17.7 названо дословно: «<c>NONE</c> отсутствует в списке того,
    /// что человек может себе выбрать». Здесь проверяется и отказ, и то, ЧТО он перечисляет:
    /// экран строит переключатель по <c>details.allowed</c>, и <c>none</c> в нём быть не должно.
    /// </remarks>
    [Fact]
    public async Task Отказаться_от_фактора_нельзя()
    {
        var id = await ЧеловекAsync(выбран: null, аутентификатор: false);

        database.Actor.Become(id);

        var error = await Should.ThrowAsync<AppException>(() =>
            database.InScopeAsync(users => users.ChooseSecondFactorAsync("none", Ct)));

        error.Details!["allowed"].ShouldBe(SecondFactorMethods.Choosable);
        ((IEnumerable<string>)error.Details["allowed"]!).ShouldNotContain("none");
    }

    /// <summary>Завести человека с нужным выбором и, если просят, аутентификатором.</summary>
    private async Task<Guid> ЧеловекAsync(string? выбран, bool аутентификатор)
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

        // ⚠ Выбор кладётся прямо в колонку, а не через API: проверяется правило разрешения, а
        // не путь, которым значение туда попало. Путь проверяет свой тест.
        await database.ReadAsync(db => db.Set<User>()
            .Where(row => row.Id == user.Id)
            .ExecuteUpdateAsync(set => set.SetProperty(row => row.SecondFactorMethod, выбран), Ct));

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
