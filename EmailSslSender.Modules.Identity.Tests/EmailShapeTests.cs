using EmailSslSender.Modules.Identity.Contracts;
using EmailSslSender.Modules.Identity.Users;
using EmailSslSender.Shared.Errors;

namespace EmailSslSender.Modules.Identity.Tests;

/// <summary>
/// Адресом учётной записи может стать только адрес — на обоих путях заведения.
/// </summary>
/// <remarks>
/// ⚠ ⚠ <b>Сьют заведён по живой находке владельца</b> (вскрытие 43, 2026-09-02): команда
/// <c>create-superadmin</c> приняла строку <c>windowsdanigame.com</c> — без <c>@</c> — и
/// отчиталась успехом. Вышла учётная запись, в которую <b>нельзя войти через веб</b> (у поля
/// формы стоит <c>type="email"</c>) и до которой не доедет ни приглашение, ни сброс, ни код
/// второго фактора.
///
/// ⚠ <b>Почему этого не ловил ни один прежний тест.</b> Все они подставляют правдоподобные
/// адреса: проверка «а что если адреса нет вовсе» не пишется сама — её пишут после того, как
/// кто-то ввёл не адрес. Проверку при этом держал <b>браузер</b>, и в вебе дефект не
/// воспроизводился никогда — ровно тот случай, о котором свод говорит «UI-проверка — это UX,
/// а не защита».
///
/// ⚠ <b>Путей ДВА, и проверяются оба.</b> Одного мало по существу: команда идёт мимо объявления
/// ввода <c>UserIn</c> прямо в службу, а API — через него. Проверь только один — второй
/// разойдётся с ним ровно так же, как разошёлся браузер с сервером.
/// </remarks>
[Collection(IdentityDatabaseCollection.Name)]
public sealed class EmailShapeTests(IdentityDatabase database)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private const string Strong = "Очень-Хороший-Пароль-42!";

    /// <summary>Строки, адресами не являющиеся. Первая — та самая, живая.</summary>
    public static TheoryData<string> НеАдреса =>
    [
        "windowsdanigame.com",   // ⚠ ровно то, что ввёл владелец: домен без `@`
        "просто-строка",
        "@нет-имени.ru",
        "нет-домена@",
        "два@собаки@ru",
    ];

    [Theory]
    [MemberData(nameof(НеАдреса))]
    public async Task Команда_не_заводит_суперпользователя_на_не_адресе(string строка)
    {
        database.Actor.AsSuperuser();

        var error = await Should.ThrowAsync<AppException>(() =>
            database.InScopeAsync(users =>
                users.BootstrapSuperuserAsync(строка, "Проба", Strong, Ct)));

        error.Code.ShouldBe(ErrorCodes.Validation);
    }

    [Theory]
    [MemberData(nameof(НеАдреса))]
    public async Task Служба_не_заводит_учётную_запись_на_не_адресе(string строка)
    {
        database.Actor.AsSuperuser();

        var error = await Should.ThrowAsync<AppException>(() =>
            database.InScopeAsync(users => users.CreateAsync(
                new UserDraft(строка, "Проба", Strong, MustChangePassword: false), Ct)));

        error.Code.ShouldBe(ErrorCodes.Validation);
    }

    [Fact]
    public async Task Настоящий_адрес_по_прежнему_заводится()
    {
        // ⚠ Положительная сверка обязательна, и без неё сьют доказывал бы не то. Проверка,
        // отвергающая ВСЁ, красит те же два факта и выглядит работающей — именно так
        // «строгая валидация» однажды запирает снаружи всех разом.
        database.Actor.AsSuperuser();

        var адрес = $"shape-{Guid.CreateVersion7():N}@example.com";

        var (user, _) = await database.InScopeAsync(users => users.CreateAsync(
            new UserDraft(адрес, "Проба", Strong, MustChangePassword: false), Ct));

        user.Email.ShouldBe(адрес);
    }

    [Fact]
    public async Task Проверка_НЕ_строгая_по_RFC_и_это_названо()
    {
        // ⚠ ⚠ Граница проверки названа фактом, а не подразумевается. `EmailAddressAttribute`
        // спрашивает «одна собака, обе половины непусты» — и пропускает, например, пробел в
        // левой половине. Это НЕ недосмотр: цель — отвергнуть «это вообще не адрес», а не
        // быть почтовым сервером. Строгая регулярка ловила бы такие строки и заодно запирала
        // снаружи живых людей с необычными, но законными адресами; чинить дефект «нельзя
        // войти» вторым дефектом «нельзя завестись» — плохая мена.
        //
        // Факт стоит здесь затем, чтобы следующий, кто решит ужесточить проверку, увидел, что
        // мягкость выбрана, а не досталась по недосмотру.
        database.Actor.AsSuperuser();

        var странный = $"с пробелом-{Guid.CreateVersion7():N}@example.com";

        var (user, _) = await database.InScopeAsync(users => users.CreateAsync(
            new UserDraft(странный, "Проба", Strong, MustChangePassword: false), Ct));

        user.Email.ShouldBe(странный, "проверка формы отвергает отсутствие адреса, а не его необычность");
    }

    [Fact]
    public async Task Края_адреса_по_прежнему_обрезаются()
    {
        // Пробелы по краям — не порча адреса, а частый способ его вставить. Проверка формы
        // не должна отменять давнее поведение `Normalize`.
        database.Actor.AsSuperuser();

        var адрес = $"trim-{Guid.CreateVersion7():N}@example.com";

        var (user, _) = await database.InScopeAsync(users => users.CreateAsync(
            new UserDraft($"  {адрес}\t", "Проба", Strong, MustChangePassword: false), Ct));

        user.Email.ShouldBe(адрес);
    }
}
