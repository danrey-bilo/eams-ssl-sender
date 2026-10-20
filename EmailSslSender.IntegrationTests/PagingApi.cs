using System.Net;
using System.Text.Json;
using EmailSslSender.Shared.Contracts.Audit;

namespace EmailSslSender.IntegrationTests;

/// <summary>
/// Размер страницы: одно поведение на все выдачи с курсором.
/// </summary>
/// <remarks>
/// ⚠ ⚠ <b>Заведено 2026-08-28 по следу настоящего расхождения.</b> Четыре выдачи решали вопрос
/// «а если попросили тысячу» по-разному: три <b>молча подрезали</b> до потолка, четвёртая
/// <b>отвечала отказом</b>. Одна ошибка клиента — два исхода на соседних адресах одного API.
///
/// ⚠ <b>Арх-проверка (<c>PagingLimits</c>) держит МЕХАНИЗМ, эта — ОТВЕТ.</b> Первая запрещает
/// подрезать где бы то ни было; вторая проверяет, что клиент действительно получает 422, а не
/// что код выглядит правильно. Без второй достаточно позвать общий помощник и проглотить его
/// исключение.
///
/// ⚠ <b>Проверяются все адреса разом, а не по одному в своём сьюте.</b> Расхождение возникло
/// именно потому, что каждый адрес проверялся отдельно и своим модулем: сравнить их было негде.
/// </remarks>
[Collection(SenderHost.Collection)]
public sealed class PagingApi(SenderHost host)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>Выдачи с курсором и права, которыми они закрыты.</summary>
    /// <remarks>
    /// ⚠ Список закрытый и пополняется вместе с новой постраничной выдачей. Забыть строку можно
    /// — и это осознанная граница: сторож формы (<c>PagingLimits</c>) машинный и полный, а этот
    /// список про ответ, и полным его не сделать, не подняв каждую выдачу с её данными.
    /// </remarks>
    public static TheoryData<string, string[]> Выдачи() => new()
    {
        { "/api/users", [] },
    };

    [Theory]
    [MemberData(nameof(Выдачи))]
    public async Task Предел_вне_допустимого_отвергается_везде_одинаково(string адрес, string[] права)
    {
        Войти(права);

        foreach (var предел in new[] { 0, -1, 100_000 })
        {
            var ответ = await host.CreateClient().GetAsync($"{адрес}?limit={предел}", Ct);

            ответ.StatusCode.ShouldBe(
                HttpStatusCode.UnprocessableContent,
                $"{адрес}?limit={предел} — предел вне допустимого обязан отвергаться, а не "
                + "подрезаться молча");

            var тело = JsonDocument.Parse(await ответ.Content.ReadAsStringAsync(Ct));

            // ⚠ Код тот же самый у всех: клиент, разбирающий ответ машинно, обязан узнавать
            // эту ошибку по коду, а не по тексту.
            тело.RootElement.GetProperty("code").GetString().ShouldBe("VALIDATION_ERROR", адрес);

            // Сообщение обязано называть допустимый диапазон — иначе клиенту нечего исправлять.
            тело.RootElement.GetProperty("message").GetString()
                .ShouldNotBeNull(адрес)
                .ShouldContain("до", Case.Sensitive, адрес);
        }
    }

    [Theory]
    [MemberData(nameof(Выдачи))]
    public async Task Предел_внутри_допустимого_и_его_отсутствие_работают(string адрес, string[] права)
    {
        // Зеркало: без него «всё отвергается» зеленело бы у выдачи, отвергающей вообще всё.
        Войти(права);

        foreach (var запрос in new[] { string.Empty, "?limit=1", "?limit=20" })
        {
            var ответ = await host.CreateClient().GetAsync($"{адрес}{запрос}", Ct);

            ответ.StatusCode.ShouldBe(HttpStatusCode.OK, $"{адрес}{запрос}");
        }
    }

    private void Войти(string[] права)
    {
        host.User.SignIn();

        // Журналу нужен ещё и вид актора: он пишет строку о самом чтении.
        host.User.ActorType = AuditActorType.User;
    }
}
