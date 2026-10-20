using EmailSslSender.Shared.Contracts.Audit;
using EmailSslSender.Shared.Contracts.Authz;

namespace EmailSslSender.Api.Infrastructure;

/// <summary>
/// Личность запроса: заполняется один раз, читается всеми.
/// </summary>
/// <remarks>
/// Изменяемая реализация <see cref="ICurrentUser"/> с областью запроса. Заполняет её ровно
/// одно место — <see cref="CurrentUserMiddleware"/>; все остальные только читают.
/// </remarks>
internal sealed class RequestCurrentUser : ICurrentUser
{
    public Guid? Id { get; private set; }


    public Guid? SessionId { get; private set; }


    public bool MustChangePassword { get; private set; }

    public bool MustEnrollTwoFactor { get; private set; }

    public DateTimeOffset? StepUpAt { get; private set; }

    /// <summary>
    /// Чем пришли. До привязки — человек.
    /// </summary>
    /// <remarks>
    /// ⚠ Умолчание здесь безопасно ровно потому, что до привязки <see cref="Id"/> равен
    /// <c>null</c>: неаутентифицированный запрос не проходит ни одной проверки прав, и
    /// вопрос «а ты человек?» к нему не задаётся вовсе.
    /// </remarks>
    public AuditActorType ActorType { get; private set; } = AuditActorType.User;

    /// <summary>Запомнить, кто пришёл.</summary>
    /// <exception cref="InvalidOperationException">
    /// Личность назвалась системой. Такого не бывает: «система» — это фоновая задача, а она
    /// по HTTP не приходит.
    /// </exception>
    public void Bind(ResolvedIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(identity);

        // ⚠ Отказ, а не молчаливое приведение к человеку. Личность с видом «система»
        // означает, что распознаватель ошибся видом, и ошибка эта разрешающая: события
        // такого запроса ушли бы в журнал подписанными системой, то есть неотличимо от
        // действий уборщика — а разбирают журнал именно по этому полю.
        if (identity.ActorType == AuditActorType.System)
        {
            throw new InvalidOperationException(
                "Личность запроса назвалась системой. «Система» — это фоновая задача, и по "
                + "HTTP она не приходит; распознаватель обязан назвать человека.");
        }

        Id = identity.UserId;
        SessionId = identity.SessionId;
        MustChangePassword = identity.MustChangePassword;
        MustEnrollTwoFactor = identity.MustEnrollTwoFactor;
        StepUpAt = identity.StepUpAt;
        ActorType = identity.ActorType;
    }
}

/// <summary>
/// Спрашивает распознаватели, кто пришёл, и запоминает ответ.
/// </summary>
/// <remarks>
/// <b>Цепочка, а не один способ.</b> Распознаватели опрашиваются по очереди, первый
/// ответивший и определяет личность. Сегодня это сессионный токен, следующим этапом рядом
/// встанет ключ API, когда-нибудь — единый вход. Добавление способа не трогает ни один
/// существующий модуль.
///
/// <b>Два разных «нет» — и это главное здесь.</b> Распознаватель, вернувший <c>null</c>,
/// говорит «это не ко мне»: спрашиваем следующего. Распознаватель, бросивший исключение,
/// говорит «ко мне, но не годится» — и тогда опрос прекращается, а человек узнаёт причину:
/// вышел сам, отозвали, обнаружена кража. Проглоти мы это исключение ради «попробуем
/// следующего» — и любая кончившаяся сессия выглядела бы просто как отсутствие входа.
///
/// ⚠ Стоит <b>после</b> контекста журнала и <b>до</b> контроллеров. Личность дописывается в
/// уже собранный контекст (адрес и агент известны с первой строки конвейера), а проверка
/// прав спрашивает <see cref="ICurrentUser"/> уже заполненным.
///
/// Реализовано как <c>IMiddleware</c>, а не по соглашению: тогда службы с областью запроса
/// приходят конструктором законно. Обычному middleware их пришлось бы принимать параметром
/// метода — конструктор заставил бы область вести себя как синглтон, и это не совет, а
/// исключение во время выполнения.
/// </remarks>
internal sealed class CurrentUserMiddleware(
    RequestCurrentUser current,
    IEnumerable<IIdentityResolver> resolvers) : IMiddleware
{
    public async Task InvokeAsync(HttpContext context, RequestDelegate next)
    {
        // ⚠ На публичных путях личность не распознаётся вовсе, и это не оптимизация.
        // Распознаватель, увидевший негодный токен, бросает исключение — и оно доезжает до
        // обработчика ошибок раньше контроллера. То есть вход с ещё не снятым заголовком от
        // прошлой, уже мёртвой сессии отвечал бы «сессия кончилась» вместо того, чтобы пустить;
        // а обновление токена — «войдите заново» вместо новой пары, то есть не работало бы
        // никогда. Клиент чистит заголовок не раньше, чем получит ответ, — и получить его
        // не мог бы.
        if (context.GetEndpoint()?.Metadata.GetMetadata<PublicEndpointAttribute>() is not null)
        {
            await next(context);

            return;
        }

        var request = new HttpIdentityRequest(context);

        foreach (var resolver in resolvers)
        {
            if (await resolver.ResolveAsync(request, context.RequestAborted) is not { } identity)
            {
                continue;
            }

            current.Bind(identity);


            break;
        }

        await next(context);
    }
}

/// <summary>То немногое из запроса, что видит распознаватель.</summary>
/// <remarks>
/// Заголовки и куки — и ничего больше. Ни тела, ни маршрута, ни ответа: распознаватель
/// отвечает на вопрос «кто это», и всё остальное ему для этого не нужно.
/// </remarks>
internal sealed class HttpIdentityRequest(HttpContext context) : IIdentityRequest
{
    public string? Header(string name) =>
        context.Request.Headers.TryGetValue(name, out var values) ? values.FirstOrDefault() : null;

    public string? Cookie(string name) =>
        context.Request.Cookies.TryGetValue(name, out var value) ? value : null;
}
