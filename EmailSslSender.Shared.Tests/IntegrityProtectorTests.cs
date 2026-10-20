using System.Security.Cryptography;
using EmailSslSender.Shared.Crypto;

namespace EmailSslSender.Shared.Tests;

/// <summary>
/// Подпись значений недоверенных хранилищ (правило 23, решение 0006).
/// </summary>
/// <remarks>
/// <b>Проверяется не «работает ли HMAC» — он работает.</b> Проверяется то, что легко
/// сделать неправильно и невозможно заметить по работающей системе: покрывает ли подпись
/// ключ записи, различаются ли назначения, что происходит после ротации мастер-ключа.
/// </remarks>
public sealed class IntegrityProtectorTests
{
    private const string Кэш = "authz-cache";

    [Fact]
    public void Своё_значение_читается_обратно()
    {
        var сторож = Сторож(1);
        var конверт = сторож.Protect(Кэш, "authz:profile:v1:ключ", "{\"permissions\":[]}");

        сторож.TryUnprotect(Кэш, "authz:profile:v1:ключ", конверт, out var значение)
            .ShouldBe(IntegrityVerdict.Ok);

        значение.ShouldBe("{\"permissions\":[]}");
    }

    /// <summary>
    /// Годную подпись нельзя переставить на чужую запись.
    /// </summary>
    /// <remarks>
    /// ⚠ <b>Ради этого свойства ключ записи и входит в подпись.</b> Без него нападение
    /// выглядит так: взять профиль администратора (законный, законно подписанный) и
    /// положить его под ключ обычного человека. Подпись сходится, значение настоящее,
    /// права — администраторские. Ни одна проверка «подпись верна» такого не увидит.
    /// </remarks>
    [Fact]
    public void Подпись_не_переставляется_на_чужую_запись()
    {
        var сторож = Сторож(1);
        var конверт = сторож.Protect(Кэш, "authz:profile:v1:начальник", "{\"permissions\":[\"*\"]}");

        сторож.TryUnprotect(Кэш, "authz:profile:v1:подчинённый", конверт, out var значение)
            .ShouldBe(IntegrityVerdict.Tampered);

        значение.ShouldBeNull();
    }

    /// <summary>
    /// Ключ записи входит в подпись СОДЕРЖИМЫМ, а не длиной.
    /// </summary>
    /// <remarks>
    /// ⚠ <b>Тест дописан 2026-08-25 после мутации, которую сьют не поймал.</b> Мутация
    /// выбрасывала ключ записи из подписываемых байтов, оставляя на его месте нули, — и все
    /// пятнадцать проверок остались зелёными. Причина: буфер по-прежнему рассчитывался по
    /// длине ключа, а все ключи в тестах были <b>разной</b> длины, то есть подписи
    /// расходились по размеру, а не по содержимому. Здесь ключи одной длины, и подмена
    /// одного другим обязана быть замечена.
    /// </remarks>
    [Fact]
    public void Ключи_одной_длины_не_взаимозаменяемы()
    {
        var сторож = Сторож(1);
        var конверт = сторож.Protect(Кэш, "authz:profile:v1:ааа", "{\"permissions\":[\"*\"]}");

        сторож.TryUnprotect(Кэш, "authz:profile:v1:ббб", конверт, out var значение)
            .ShouldBe(IntegrityVerdict.Tampered);

        значение.ShouldBeNull();
    }

    /// <summary>
    /// Граница между ключом и значением однозначна.
    /// </summary>
    /// <remarks>
    /// ⚠ Без разделителя пара («ключ <c>ab</c>», «значение <c>c</c>») и пара («ключ
    /// <c>a</c>», «значение <c>bc</c>») дают одну подписываемую строку — то есть подпись
    /// переставляется между записями подбором границы. Тест ловит ровно это.
    /// </remarks>
    [Fact]
    public void Граница_ключа_и_значения_не_сдвигается()
    {
        var сторож = Сторож(1);
        var конверт = сторож.Protect(Кэш, "ab", "c");

        сторож.TryUnprotect(Кэш, "a", конверт.Replace(".c", ".bc", StringComparison.Ordinal), out _)
            .ShouldBe(IntegrityVerdict.Tampered);
    }

    [Fact]
    public void Изменённое_значение_не_проходит()
    {
        var сторож = Сторож(1);
        var конверт = сторож.Protect(Кэш, "ключ", "{\"permissions\":[]}");
        var подделка = конверт.Replace("[]", "[\"credential.reveal\"]", StringComparison.Ordinal);

        сторож.TryUnprotect(Кэш, "ключ", подделка, out _).ShouldBe(IntegrityVerdict.Tampered);
    }

    /// <summary>
    /// Назначения не пересекаются.
    /// </summary>
    /// <remarks>
    /// Значение, годное в кэше прав, не должно оказаться годным в кэше настроек: форматы
    /// разные, и что выйдет из подстановки одного вместо другого, предсказать нельзя.
    /// </remarks>
    [Fact]
    public void Чужое_назначение_не_проходит()
    {
        var сторож = Сторож(1);
        var конверт = сторож.Protect(Кэш, "ключ", "значение");

        сторож.TryUnprotect("settings-cache", "ключ", конверт, out _)
            .ShouldBe(IntegrityVerdict.Tampered);
    }

    [Theory]
    [InlineData("")]
    [InlineData("голое значение без конверта")]
    [InlineData("1.подпись-не-base64.значение")]
    [InlineData("1.значение")]
    [InlineData("ноль.AAAA.значение")]
    [InlineData("0.AAAA.значение")]
    [InlineData("-1.AAAA.значение")]
    public void Мусор_под_нашим_ключом_считается_подделкой(string мусор)
    {
        // ⚠ Именно подделкой, а не промахом. Форму записи различает номер в самом ключе
        // хранилища: записи прежней формы не находятся вовсе, и неразбираемое значение под
        // нашим ключом означает, что туда писал кто-то, кроме нас.
        Сторож(1).TryUnprotect(Кэш, "ключ", мусор, out var значение).ShouldBe(IntegrityVerdict.Tampered);

        значение.ShouldBeNull();
    }

    /// <summary>
    /// Ротация мастер-ключа — не атака.
    /// </summary>
    /// <remarks>
    /// ⚠ <b>Главный тест файла.</b> Ключ подписи выводится из мастер-ключа, а тот
    /// ротируется — значит после ротации все прежние записи перестают проверяться. Если
    /// назвать это подделкой, каждая ротация поднимет тревогу на весь кэш, и тревогу
    /// научатся игнорировать ровно к тому дню, когда она окажется настоящей.
    /// </remarks>
    [Fact]
    public void Запись_пережившая_ротацию_это_промах_а_не_тревога()
    {
        var старый = Ключ();
        var конверт = new IntegrityProtector(MasterKeyRing.FromKeys(new Dictionary<int, byte[]> { [1] = старый }))
            .Protect(Кэш, "ключ", "значение");

        // Ротация прошла целиком: прежней версии в ринге больше нет.
        var после = new IntegrityProtector(
            MasterKeyRing.FromKeys(new Dictionary<int, byte[]> { [2] = Ключ() }));

        после.TryUnprotect(Кэш, "ключ", конверт, out var значение).ShouldBe(IntegrityVerdict.Foreign);

        значение.ShouldBeNull();
    }

    /// <summary>
    /// Пока ротация идёт, читаются обе версии.
    /// </summary>
    /// <remarks>
    /// Обратная сторона предыдущего: ринг держит обе версии как раз затем, чтобы система
    /// работала во время ротации. Читать при этом надо и то, что подписано прежней.
    /// </remarks>
    [Fact]
    public void Во_время_ротации_читается_и_прежняя_версия()
    {
        var первый = Ключ();
        var второй = Ключ();

        var до = new IntegrityProtector(
            MasterKeyRing.FromKeys(new Dictionary<int, byte[]> { [1] = первый }));

        var конверт = до.Protect(Кэш, "ключ", "значение");

        var во_время = new IntegrityProtector(
            MasterKeyRing.FromKeys(new Dictionary<int, byte[]> { [1] = первый, [2] = второй }));

        во_время.TryUnprotect(Кэш, "ключ", конверт, out var значение).ShouldBe(IntegrityVerdict.Ok);
        значение.ShouldBe("значение");

        // И новое подписывается уже действующей версией, а не прежней.
        во_время.Protect(Кэш, "ключ", "значение").ShouldStartWith("2.");
    }

    /// <summary>Значение с точками внутри не ломает разбор конверта.</summary>
    /// <remarks>
    /// Разделитель — первые две точки, дальше значение идёт как есть. JSON точек не
    /// содержит, а вот число с плавающей запятой или версия — вполне.
    /// </remarks>
    [Fact]
    public void Точки_внутри_значения_безобидны()
    {
        var сторож = Сторож(1);
        var конверт = сторож.Protect(Кэш, "ключ", "1.2.3.4");

        сторож.TryUnprotect(Кэш, "ключ", конверт, out var значение).ShouldBe(IntegrityVerdict.Ok);
        значение.ShouldBe("1.2.3.4");
    }

    private static IntegrityProtector Сторож(int version) =>
        new(MasterKeyRing.FromKeys(new Dictionary<int, byte[]> { [version] = Ключ() }));

    private static byte[] Ключ() => RandomNumberGenerator.GetBytes(MasterKeyRing.KeyBytes);
}
