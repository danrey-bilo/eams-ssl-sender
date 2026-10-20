using System.Security.Cryptography;
using EmailSslSender.Shared.Crypto;

namespace EmailSslSender.Shared.Tests;

/// <summary>
/// Разбор источника мастер-ключа: формат, отказы и то, что отказы не проговариваются.
/// </summary>
public sealed class MasterKeyRingTests
{
    [Fact]
    public void Одна_строка_base64_это_версия_один()
    {
        // ⚠ Ровно то, что пишет scripts/gen-secrets.sh Python-версии, и это не совпадение:
        // второй формат означал бы второй генератор и второй способ ошибиться.
        var ring = MasterKeyRing.Parse(Convert.ToBase64String(Key()));

        ring.CurrentVersion.ShouldBe(1);
        ring.Count.ShouldBe(1);
    }

    [Fact]
    public void Действующая_версия_наибольшая()
    {
        // Так выглядит файл во время ротации: старый ключ ещё нужен, чтобы разворачивать
        // прежние обёртки, новым уже оборачивают.
        var ring = MasterKeyRing.Parse($"1:{Convert.ToBase64String(Key())}\n2:{Convert.ToBase64String(Key())}");

        ring.CurrentVersion.ShouldBe(2);
        ring.Count.ShouldBe(2);
    }

    [Fact]
    public void Пустые_строки_и_пояснения_пропускаются()
    {
        // Рядом с ключом должно быть можно написать, когда и зачем он появился: файл
        // секрета живёт годами, и «почему тут две версии» через полгода не вспомнит никто.
        var ring = MasterKeyRing.Parse(
            $"# заведён при выкатке 2026-08-22\n\n1:{Convert.ToBase64String(Key())}\n\n");

        ring.CurrentVersion.ShouldBe(1);
        ring.Count.ShouldBe(1);
    }

    [Fact]
    public void Одна_версия_дважды_это_отказ()
    {
        // Угадывать, какой из двух действующий, нельзя: ошибка выбора означает, что часть
        // обёрток перестанет разворачиваться.
        var text = $"1:{Convert.ToBase64String(Key())}\n1:{Convert.ToBase64String(Key())}";

        Should.Throw<InvalidOperationException>(() => MasterKeyRing.Parse(text))
            .Message.ShouldContain("дважды");
    }

    [Fact]
    public void Ключ_не_той_длины_это_отказ()
    {
        // ⚠ Ключ короче 32 байт не «чуть слабее»: он молча превращает AES-256 в AES-128, и
        // по работающей системе этого не видно.
        var short16 = Convert.ToBase64String(RandomNumberGenerator.GetBytes(16));

        Should.Throw<InvalidOperationException>(() => MasterKeyRing.Parse(short16))
            .Message.ShouldContain("16 байт");
    }

    [Fact]
    public void Пустой_источник_это_отказ_с_подсказкой()
    {
        // Самый частый случай в жизни: файл секрета забыли создать. Ответ должен говорить,
        // чем его создают, а не «ключ не найден».
        Should.Throw<InvalidOperationException>(() => MasterKeyRing.Parse("\n\n# только пояснение\n"))
            .Message.ShouldContain("gen-secrets.sh");
    }

    [Fact]
    public void Отказ_не_печатает_ключ()
    {
        // ⚠ Правило 14. Разбор ломается на плохом формате, а плохой формат — это чаще всего
        // почти правильный ключ. Печатать содержимое строки в такой момент значит записать
        // мастер-ключ установки в лог запуска.
        var key = Key();
        var encoded = Convert.ToBase64String(key);

        // Валидный base64, но не тех размеров — сообщение обязано назвать длину и номер
        // строки, а не то, что было в строке.
        var error = Should.Throw<InvalidOperationException>(
            () => MasterKeyRing.Parse($"1:{Convert.ToBase64String(key.AsSpan(0, 24).ToArray())}"));

        error.Message.ShouldNotContain(encoded);
        error.Message.ShouldNotContain(Convert.ToBase64String(key.AsSpan(0, 24).ToArray()));
        error.Message.ShouldContain("строка 1");

        // И то же самое для нечитаемой строки.
        var broken = Should.Throw<InvalidOperationException>(() => MasterKeyRing.Parse("1:не-base64!!"));

        broken.Message.ShouldNotContain("не-base64");
    }

    [Fact]
    public void Мусор_вместо_номера_версии_это_отказ()
    {
        Should.Throw<InvalidOperationException>(() => MasterKeyRing.Parse($"ноль:{Convert.ToBase64String(Key())}"));
        Should.Throw<InvalidOperationException>(() => MasterKeyRing.Parse($"0:{Convert.ToBase64String(Key())}"));
        Should.Throw<InvalidOperationException>(() => MasterKeyRing.Parse($"-1:{Convert.ToBase64String(Key())}"));
    }

    [Fact]
    public void Ринг_копирует_ключи_а_не_ссылается()
    {
        // Иначе вызывающий затрёт ключ у нас под ногами — например, добросовестно вызвав
        // ZeroMemory на своём буфере, — и обнаружится это расшифровкой, переставшей работать.
        var key = Key();
        var ring = MasterKeyRing.FromKeys(new Dictionary<int, byte[]> { [1] = key });
        var envelope = new SecretEnvelope(ring);

        var wrapped = envelope.CreateDataKey();

        CryptographicOperations.ZeroMemory(key);

        Should.NotThrow(() => envelope.Rewrap(wrapped));
    }

    private static byte[] Key() => RandomNumberGenerator.GetBytes(MasterKeyRing.KeyBytes);
}
