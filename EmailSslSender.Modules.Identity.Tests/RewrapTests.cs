using System.Security.Cryptography;
using EmailSslSender.Modules.Identity.Domain;
using EmailSslSender.Modules.Identity.TwoFactor;
using EmailSslSender.Modules.Identity.Users;
using EmailSslSender.Shared.Crypto;
using EmailSslSender.Shared.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace EmailSslSender.Modules.Identity.Tests;

/// <summary>
/// Перекладывание обёрток при смене мастер-ключа.
/// </summary>
/// <remarks>
/// ⚠ <b>Ротация без этого — половина дела, и худшая.</b> Дописать в кольцо новую версию можно
/// было и раньше: свежие секреты запечатывались ею, старые открывались прежней. Но
/// <b>выбросить прежнюю было нельзя</b> — а ради этого ротацию и делают: ключ, который не
/// вывести из обращения, после утечки остаётся полезным навсегда. Документация при этом
/// описывала ротацию как готовую. Найдено разбором готовности 2026-08-24.
///
/// Кольцо здесь строится своё — из ключа обвязки (версия 1) плюс новый (версия 2). Иначе
/// развернуть то, что запечатала обвязка, было бы нечем.
/// </remarks>
[Collection(IdentityDatabaseCollection.Name)]
public sealed class RewrapTests(IdentityDatabase database)
{
    /// <summary>Обёртка переезжает на новую версию, а секрет остаётся читаемым.</summary>
    /// <remarks>
    /// ⚠ Вторая половина важнее первой: переложить обёртку так, что секрет перестанет
    /// открываться, — это молчаливая потеря второго фактора у всех сразу, и обнаружится она
    /// на входе людей, а не здесь.
    /// </remarks>
    [Fact]
    public async Task Обёртка_переезжает_на_новую_версию_а_секрет_читается()
    {
        var (id, секрет) = await СФакторомAsync();

        var новое = Кольцо();
        var было = await КлючAsync(id);

        новое.NeedsRewrap(new WrappedDataKey(было)).ShouldBeTrue("обёртка и так под новой версией");

        var порция = await ПереложитьAsync(новое);

        порция.Rewrapped.ShouldBeGreaterThanOrEqualTo(1);

        var стало = await КлючAsync(id);

        стало.ShouldNotBe(было, "обёртка не изменилась");
        новое.NeedsRewrap(new WrappedDataKey(стало)).ShouldBeFalse("обёртка осталась под старой версией");

        // И главное: секрет по-прежнему разворачивается — тем же контекстом, что и раньше.
        // Сравниваются БАЙТЫ seed'а: в базе лежит он, а `setup.Secret` — его же запись в
        // base32, показанная человеку один раз.
        var запечатанный = await СекретAsync(id);

        var развёрнутый = новое.Open(
            new WrappedDataKey(стало), new SealedSecret(запечатанный), $"user:{id}:totp_seed");

        развёрнутый.ShouldBe(
            FromBase32(секрет), "после перекладывания секрет перестал открываться");
    }

    /// <summary>Повторный проход ничего не трогает.</summary>
    /// <remarks>
    /// ⚠ Перекладывание — это перезапись столбца с ключом, единственного места, где сбой
    /// стоит секрета. Проход, перекладывающий уже переложенное, делает эту перезапись на
    /// ровном месте и при каждом запуске.
    /// </remarks>
    [Fact]
    public async Task Повторный_проход_ничего_не_перекладывает()
    {
        await СФакторомAsync();

        var новое = Кольцо();

        (await ПереложитьAsync(новое)).Rewrapped.ShouldBeGreaterThanOrEqualTo(1);
        (await ПереложитьAsync(новое)).Rewrapped.ShouldBe(0, "второй проход переложил заново");
    }

    /// <summary>Проход идёт порциями и доходит до конца.</summary>
    /// <remarks>
    /// ⚠ Отобрать «нуждающиеся» запросом нельзя — версия лежит внутри байтов обёртки.
    /// Поэтому проход идёт по всем строкам подряд с курсором, и порция размером в одну строку
    /// проверяет, что курсор не перескакивает и не зацикливается.
    /// </remarks>
    [Fact]
    public async Task Порциями_по_одной_строке_доходит_до_конца()
    {
        await СФакторомAsync();
        await СФакторомAsync();
        await СФакторомAsync();

        var новое = Кольцо();
        int переложено = 0, шагов = 0;
        string? курсор = null;

        do
        {
            var порция = await ПереложитьAsync(новое, batch: 1, after: курсор);

            переложено += порция.Rewrapped;
            курсор = порция.Next;
            шагов++;

            шагов.ShouldBeLessThan(500, "проход не сходится: курсор зациклился");
        }
        while (курсор is not null);

        переложено.ShouldBeGreaterThanOrEqualTo(3, "порциями переложено меньше, чем строк");
    }

    /// <summary>Перезаведённый в это же время фактор не затирается перекладыванием.</summary>
    /// <remarks>
    /// ⚠ <b>Окно между чтением порции и записью — не микросекунды.</b> При порции в двести
    /// строк между <c>SELECT</c> и <c>UPDATE</c> проходит целая пачка работы, и человек успевает
    /// перезавести себе фактор: <c>SetupAsync</c> заменяет и ключ, и секрет. Запись
    /// присваиванием положила бы обёртку от <b>старого</b> ключа поверх нового — а секрет рядом
    /// уже новый, и открыть его стало бы нечем. Это не «строка потерялась»: это молча
    /// испорченный второй фактор у конкретного человека, и обнаружит он это на входе.
    ///
    /// Окно здесь открывается настоящее, а не описывается словами: перезавод делается изнутри
    /// <c>Rewrap</c>, то есть ровно между чтением порции и записью, и другим соединением.
    /// Найдено разбором готовности 2026-08-24.
    /// </remarks>
    [Fact]
    public async Task Перезаведённый_в_это_же_время_фактор_не_затирается()
    {
        var (id, _) = await СФакторомAsync();

        var новое = Кольцо();
        var было = await КлючAsync(id);

        var подменыш = new ВОкне(новое, было, () => ПерезавестиAsync(id, новое));

        await ПереложитьAsync(подменыш);

        подменыш.Подложенное.ShouldNotBeNull("вклиниться не удалось: окно не открылось");

        var стало = await КлючAsync(id);

        стало.ShouldBe(
            подменыш.Подложенное, "перекладывание затёрло только что перезаведённый фактор");
    }

    // --- обвязка -------------------------------------------------------------------------

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private const string Strong = "Очень-Хороший-Пароль-42!";

    /// <summary>Кольцо из ключа обвязки и нового: действующая версия — вторая.</summary>
    private SecretEnvelope Кольцо() => new(MasterKeyRing.FromKeys(new Dictionary<int, byte[]>
    {
        [1] = database.MasterKey,
        [2] = RandomNumberGenerator.GetBytes(32),
    }));

    /// <summary>Завести человека с включённым фактором. Возвращает его и открытый секрет.</summary>
    private async Task<(Guid Id, string Secret)> СФакторомAsync()
    {
        database.Actor.AsSuperuser();

        var email = $"rewrap-{Guid.CreateVersion7():N}@example.com";
        var (user, _) = await database.InScopeAsync(users => users.CreateAsync(
            new UserDraft(email, "Ротация", Strong, MustChangePassword: false), Ct));

        database.Actor.Become(user.Id);

        await using var scope = database.Scope();

        var factors = scope.ServiceProvider.GetRequiredService<TwoFactorService>();
        var setup = await factors.SetupAsync(Ct);

        await factors.ConfirmAsync(Totp.Code(FromBase32(setup.Secret), Totp.StepAt(DateTimeOffset.UtcNow)), Ct);

        return (user.Id, setup.Secret);
    }

    private Task<RewrapBatch> ПереложитьAsync(ISecretEnvelope envelope, int batch = 100, string? after = null)
    {
        return Run();

        async Task<RewrapBatch> Run()
        {
            await using var scope = database.Scope();

            var target = new TwoFactorRewrap(
                scope.ServiceProvider.GetRequiredService<SenderDbContext>(), envelope);

            return await target.RewrapAsync(batch, after, Ct);
        }
    }

    private Task<byte[]> КлючAsync(Guid id) =>
        database.ReadAsync(db => db.Set<UserTwoFactor>()
            .Where(row => row.UserId == id)
            .Select(row => row.DataKey)
            .FirstAsync(Ct));

    private Task<byte[]> СекретAsync(Guid id) =>
        database.ReadAsync(db => db.Set<UserTwoFactor>()
            .Where(row => row.UserId == id)
            .Select(row => row.Secret)
            .FirstAsync(Ct));

    /// <summary>Перезавести фактор человеку — другим соединением, как это и бывает.</summary>
    /// <remarks>Возвращает подложенную обёртку: именно она должна остаться в базе.</remarks>
    private async Task<byte[]> ПерезавестиAsync(Guid id, ISecretEnvelope envelope)
    {
        var ключ = envelope.CreateDataKey();
        var секрет = envelope.Seal(
            ключ, RandomNumberGenerator.GetBytes(20), $"user:{id}:totp_seed").Bytes;

        await using var scope = database.Scope();

        var db = scope.ServiceProvider.GetRequiredService<SenderDbContext>();

        await db.Set<UserTwoFactor>()
            .Where(row => row.UserId == id)
            .ExecuteUpdateAsync(
                set => set
                    .SetProperty(row => row.DataKey, ключ.Bytes)
                    .SetProperty(row => row.Secret, секрет),
                Ct);

        return ключ.Bytes;
    }

    /// <summary>Конверт, дающий кому-то ещё влезть в строку в самый неподходящий момент.</summary>
    /// <remarks>
    /// Вклинивается по байтам обёртки, а не по счётчику вызовов: в общей тестовой базе лежат
    /// строки от соседних тестов, и «первое перекладывание» пришлось бы на чужую.
    /// </remarks>
    private sealed class ВОкне(ISecretEnvelope inner, byte[] когда, Func<Task<byte[]>> вклиниться)
        : ISecretEnvelope
    {
        /// <summary>Что подложил вклинившийся. <c>null</c> — значит окно так и не открылось.</summary>
        public byte[]? Подложенное { get; private set; }

        public WrappedDataKey Rewrap(WrappedDataKey key)
        {
            var результат = inner.Rewrap(key);

            if (key.Bytes.AsSpan().SequenceEqual(когда))
            {
                // Синхронно: `Rewrap` синхронен, и переписывать контракт ради теста — хвост,
                // виляющий собакой. `Task.Run` уводит с текущего контекста, чтобы не встать.
                Подложенное = Task.Run(вклиниться).GetAwaiter().GetResult();
            }

            return результат;
        }

        public WrappedDataKey CreateDataKey(int generation = 1) => inner.CreateDataKey(generation);

        public SealedSecret Seal(WrappedDataKey key, ReadOnlySpan<byte> plaintext, string context) =>
            inner.Seal(key, plaintext, context);

        public byte[] Open(WrappedDataKey key, SealedSecret secret, string context) =>
            inner.Open(key, secret, context);

        public SealedSecret SealText(WrappedDataKey key, string plaintext, string context) =>
            inner.SealText(key, plaintext, context);

        public string OpenText(WrappedDataKey key, SealedSecret secret, string context) =>
            inner.OpenText(key, secret, context);

        public bool NeedsRewrap(WrappedDataKey key) => inner.NeedsRewrap(key);
    }

    /// <summary>Base32 обратно в байты — тем же разбором, что и у приложения.</summary>
    private static byte[] FromBase32(string value)
    {
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
        var bits = 0;
        var value2 = 0;
        var bytes = new List<byte>();

        foreach (var symbol in value.TrimEnd('='))
        {
            value2 = (value2 << 5) | alphabet.IndexOf(symbol, StringComparison.Ordinal);
            bits += 5;

            if (bits < 8)
            {
                continue;
            }

            bytes.Add((byte)(value2 >> (bits - 8)));
            bits -= 8;
        }

        return [.. bytes];
    }
}
