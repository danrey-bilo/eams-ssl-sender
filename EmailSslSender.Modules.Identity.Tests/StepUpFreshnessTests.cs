using System.Net;
using EmailSslSender.Modules.Identity.Settings;
using EmailSslSender.Modules.Identity.TwoFactor;
using EmailSslSender.Shared.Errors;

namespace EmailSslSender.Modules.Identity.Tests;

/// <summary>
/// Свежесть второго фактора: отметка ТОКЕНА плюс окно <c>auth.step_up_minutes</c>.
/// </summary>
/// <remarks>
/// ⚠ ⚠ Решение владельца 2026-10-20: опасные действия требуют кода, подтверждённого на этой
/// сессии не раньше окна (по умолчанию десять минут). Здесь — сама арифметика окна и форма
/// отказа, без базы: путь отметки через вход и <c>POST /api/me/step-up</c> проверяют
/// <c>StepUpTests</c> и интеграционные тесты.
/// </remarks>
public sealed class StepUpFreshnessTests
{
    private static readonly DateTimeOffset Сейчас = new(2026, 10, 20, 12, 0, 0, TimeSpan.Zero);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Код_внутри_окна_пропускает()
    {
        var (свежесть, _) = Собрать(отметка: Сейчас.AddMinutes(-9));

        await Should.NotThrowAsync(() => свежесть.RequireAsync(Ct));
    }

    /// <summary>Давний код и отсутствие кода отказывают одинаково — 403 с размером окна.</summary>
    // мутация 2026-10-20: условие пропуска заменено на «всегда истина» → покраснели все три случая.
    [Theory]
    [InlineData(11)]
    [InlineData(600)]
    [InlineData(null)]
    public async Task Давний_или_отсутствующий_код_отвергается_с_размером_окна(int? минутНазад)
    {
        var (свежесть, _) = Собрать(отметка: минутНазад is { } м ? Сейчас.AddMinutes(-м) : null);

        var отказ = await Should.ThrowAsync<AppException>(() => свежесть.RequireAsync(Ct));

        отказ.Code.ShouldBe(ErrorCodes.SecondFactorRequired);
        отказ.Status.ShouldBe(HttpStatusCode.Forbidden);
        отказ.Message.ShouldBe("Подтвердите действие кодом из приложения.");
        отказ.Details!["window_minutes"].ShouldBe(10);
    }

    [Fact]
    public async Task Граница_окна_включительна()
    {
        var (свежесть, часы) = Собрать(отметка: Сейчас.AddMinutes(-10));

        await Should.NotThrowAsync(() => свежесть.RequireAsync(Ct));

        часы.Вперёд(TimeSpan.FromTicks(1));

        (await Should.ThrowAsync<AppException>(() => свежесть.RequireAsync(Ct))).Code
            .ShouldBe(ErrorCodes.SecondFactorRequired);
    }

    [Fact]
    public async Task Окно_задаётся_настройкой_и_называется_в_отказе()
    {
        var (свежесть, часы) = Собрать(отметка: Сейчас.AddMinutes(-20), окно: 30);

        await Should.NotThrowAsync(() => свежесть.RequireAsync(Ct));

        часы.Вперёд(TimeSpan.FromMinutes(11));

        (await Should.ThrowAsync<AppException>(() => свежесть.RequireAsync(Ct))).Details!["window_minutes"]
            .ShouldBe(30);
    }

    [Fact]
    public async Task Окно_не_бывает_короче_минуты()
    {
        // Негодная строка или подставной читатель не должны превращать окно в «никогда».
        var (свежесть, _) = Собрать(отметка: Сейчас.AddSeconds(-30), окно: 0);

        await Should.NotThrowAsync(() => свежесть.RequireAsync(Ct));
    }

    [Fact]
    public async Task Срок_для_экрана_совпадает_с_окном_отказа()
    {
        var отметка = Сейчас.AddMinutes(-4);
        var (свежесть, часы) = Собрать(отметка);

        (await свежесть.UntilAsync(отметка, Ct)).ShouldBe(отметка.AddMinutes(10));
        (await свежесть.UntilAsync(null, Ct)).ShouldBeNull();

        // Окно прошло — экран не обещает того, чего сервер уже не даёт.
        часы.Вперёд(TimeSpan.FromMinutes(7));

        (await свежесть.UntilAsync(отметка, Ct)).ShouldBeNull();
        await Should.ThrowAsync<AppException>(() => свежесть.RequireAsync(Ct));
    }

    [Fact]
    public void Настройка_окна_объявлена_как_настройка_защиты()
    {
        var объявление = new IdentitySettingDeclarations().Declarations
            .Single(о => о.Name == "auth.step_up_minutes");

        объявление.ShouldBeSameAs(IdentitySettings.StepUpMinutes);
        объявление.IsSecurity.ShouldBeTrue();
        объявление.Category.ShouldBe("auth");
        объявление.DefaultValue.ShouldBe(10);
        объявление.Minimum.ShouldBe(1);
        объявление.Maximum.ShouldBe(120);
        объявление.Title.ShouldNotBeNullOrWhiteSpace();
        объявление.Description.ShouldNotBeNullOrWhiteSpace();
    }

    private static (StepUpFreshness Свежесть, ПодвижныеЧасы Часы) Собрать(DateTimeOffset? отметка, int? окно = null)
    {
        var часы = new ПодвижныеЧасы(Сейчас);
        var настройки = new TestSettings();

        if (окно is { } минут)
        {
            настройки.Values[IdentitySettings.StepUpMinutes.Name] = минут;
        }

        var кто = new TestActor { StepUpAt = отметка };

        return (new StepUpFreshness(кто, настройки, часы), часы);
    }
}
