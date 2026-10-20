using EmailSslSender.Modules.Identity.Domain;
using EmailSslSender.Shared.Contracts.Authz;
using EmailSslSender.Shared.Contracts.Tokens;
using EmailSslSender.Shared.Data;
using EmailSslSender.Shared.Errors;
using Microsoft.EntityFrameworkCore;

namespace EmailSslSender.Modules.Identity.Users;

/// <summary>
/// Распознаёт человека по предъявленному токену.
/// </summary>
/// <remarks>
/// ⚠ <b>Граница модулей проходит ЗДЕСЬ, и она проведена по вопросу, а не по файлу.</b>
/// «Жив ли секрет и чей он» отвечает модуль токенов
/// (<see cref="ITokenIntrospector"/>); «а этому человеку можно войти» — мы; собрать из двух
/// ответов личность — тоже мы, потому что ограничения личности наши.
///
/// Обратный раздел означал бы, что модуль токенов читает таблицу людей.
///
/// ⚠ <b>Цена названа честно: одно лишнее обращение к базе на каждом запросе с токеном.</b>
///
/// <b>Два разных «нет».</b> Заголовка нет, он не той схемы или значение не нашей формы —
/// <c>null</c>: «это не ко мне», пусть попробует следующий. А вот предъявленный токен нашей
/// формы, оказавшийся негодным, — исключение: человеку надо сказать, почему его сессия
/// кончилась. Разница между «вы вышли» и «обнаружена кража токена» — это разница между
/// «ладно» и «звоните в безопасность».
/// </remarks>
internal sealed class SessionIdentityResolver(
    ITokenIntrospector tokens,
    SenderDbContext db) : IIdentityResolver
{
    /// <summary>Схема, которой помечается токен доступа.</summary>
    private const string Scheme = "Bearer ";

    public async Task<ResolvedIdentity?> ResolveAsync(
        IIdentityRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var header = request.Header("Authorization");

        // ⚠ ⚠ Только заголовок, НЕ кука. Браузер шлёт куки сам и не спрашивая: прими мы сессию
        // из куки — личность предъявлялась бы на каждый запрос с любого сайта, то есть мы
        // завели бы себе CSRF там, где его не было.
        if (header is null || !header.StartsWith(Scheme, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var presented = header[Scheme.Length..].Trim();

        if (presented.Length == 0)
        {
            return null;
        }

        // ⚠ Дешёвая проверка формы ДО обращения к базе. Заголовок `Bearer` общий: появись
        // второй способ входа, модуль, забирающий любое значение, обрывал бы опрос цепочки
        // исключением «сессия кончилась».
        if (!tokens.Recognises(presented))
        {
            return null;
        }

        if (await tokens.IntrospectAsync(presented, cancellationToken) is not { } token)
        {
            return null;
        }

        // ⚠ Состояние человека читается из базы на КАЖДОМ запросе. Это и есть причина, по
        // которой токены непрозрачные: отключённый перестаёт проходить немедленно, а не
        // когда истечёт его подписанный срок.
        //
        // ⚠ Проекция на нужные поля, а не вся строка: argon2-хеш пароля незачем материализовать
        // на каждом запросе — секрету место там, где он нужен.
        var found = await (
            from user in db.Set<User>()
            where user.Id == token.UserId
            select new
            {
                user.Id,
                user.IsActive,
                user.DeletedAt,
                user.MustChangePassword,
            })
            .AsNoTracking()
            .FirstOrDefaultAsync(cancellationToken);

        if (found is null || !found.IsActive || found.DeletedAt is not null)
        {
            throw new AppException(
                ErrorCodes.UserInactive, "Учётная запись отключена.", System.Net.HttpStatusCode.Unauthorized);
        }

        // ⚠ ⚠ Сессия без отметки второго фактора — это СЕССИЯ НАСТРОЙКИ (`SignInService`: первый
        // вход того, кто фактор ещё не подтверждал). Ей открыто только подключение
        // аутентификатора; подтверждение кода ставит отметку на ЭТОТ токен и снимает
        // ограничение. Ни настройка установки, ни работающая почта его не снимают — только
        // доказательство в данной сессии.
        var enroll = token.StepUpAt is null;

        return new ResolvedIdentity(
            found.Id,
            // ⚠ Вид субъекта считает ВЛАДЕЛЕЦ ТОКЕНОВ, а не мы.
            token.ActorType,
            token.TokenId,
            found.MustChangePassword,
            enroll,

            // ⚠ Отметка приезжает С ТОКЕНОМ и не пересчитывается здесь: наше дело — донести
            // факт и время.
            token.StepUpAt);
    }
}
