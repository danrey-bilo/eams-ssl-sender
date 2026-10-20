using EmailSslSender.Shared.Contracts.Audit;

namespace EmailSslSender.Modules.Identity.Contracts;

/// <summary>
/// События, которые пишет модуль личностей.
/// </summary>
/// <remarks>
/// Пишутся через <c>IAuditWriter</c> — в этой установке это строка структурного лога с
/// полем <c>audit_action</c> (<c>LogAuditWriter</c> у хоста). Ровно тот класс событий, ради
/// которого журнал заводят: заведение учётной записи, отключение, вход, смена пароля,
/// второй фактор.
/// </remarks>
public static class IdentityActions
{
    /// <summary>Заведена учётная запись.</summary>
    public static readonly AuditAction UserCreated = new("user.create", "Заведён пользователь");

    /// <summary>Изменён профиль.</summary>
    public static readonly AuditAction UserUpdated = new("user.update", "Изменён пользователь");

    /// <summary>Учётная запись отключена.</summary>
    public static readonly AuditAction UserDisabled = new("user.disable", "Отключён пользователь");

    /// <summary>Учётная запись включена обратно.</summary>
    public static readonly AuditAction UserEnabled = new("user.enable", "Включён пользователь");

    /// <summary>Администратор снял человеку второй фактор.</summary>
    /// <remarks>
    /// ⚠ Отдельно от «отключён второй фактор»: там человек снимает защиту сам и с паролем,
    /// здесь — <b>другой человек</b> (или консольная команда) снимает её с чужой учётной
    /// записи. Это единственный законный путь для того, кто потерял телефон, — и единственный
    /// же путь снять чужую защиту. Различать их в журнале обязательно.
    /// </remarks>
    public static readonly AuditAction TwoFactorReset = new("user.totp_reset", "Снят второй фактор");

    /// <summary>Снята блокировка входа после неудачных попыток.</summary>
    /// <remarks>
    /// Отдельно от включения: блокировка ставится системой за перебор, снимается человеком, и
    /// частые её снятия у одного и того же человека — сами по себе повод посмотреть, кто и
    /// зачем подбирает его пароль.
    /// </remarks>
    public static readonly AuditAction UserUnlocked = new("user.unlock", "Снята блокировка входа");

    /// <summary>Учётная запись удалена (мягко).</summary>
    public static readonly AuditAction UserDeleted = new("user.delete", "Удалён пользователь");

    /// <summary>Выслано приглашение.</summary>
    public static readonly AuditAction InvitationSent = new("user.invitation_sent", "Выслано приглашение");

    /// <summary>Человек активировал учётную запись по ссылке.</summary>
    /// <remarks>
    /// Пишется от его имени, хотя в этот момент он ещё не вошёл: действие совершил он, а
    /// не система. Иначе строка «кто активировал» отвечала бы «система», и вопрос «в какой
    /// момент этот адрес превратился в живую учётную запись» остался бы без ответа.
    /// </remarks>
    public static readonly AuditAction UserActivated = new("user.activate", "Активирована учётная запись");

    /// <summary>Попытка активации по недействительной ссылке.</summary>
    /// <remarks>
    /// Пишется <b>всегда</b>, потому что это единственный след перебора ссылок: снаружи все
    /// отказы выглядят одинаково и специально, а внутри их надо различать и считать.
    /// </remarks>
    public static readonly AuditAction ActivationRejected = new("user.activation_rejected", "Отказ в активации");

    /// <summary>Пароль сменён.</summary>
    /// <remarks>
    /// Одно событие на все способы: сам сменил, сменил по ссылке сброса. Чем именно —
    /// пишется в подробностях. Разные события заставили бы того, кто разбирает «когда у
    /// этого человека менялся пароль», знать все способы наперёд.
    /// </remarks>
    public static readonly AuditAction PasswordChanged = new("user.password_changed", "Сменён пароль");

    /// <summary>Администратор выдал ссылку сброса пароля.</summary>
    /// <remarks>
    /// Отдельно от самой смены: выдача ссылки — действие администратора над чужим входом, и
    /// оно должно быть видно, даже если ссылкой не воспользовались.
    /// </remarks>
    public static readonly AuditAction PasswordResetIssued =
        new("user.password_reset_issued", "Выдана ссылка сброса пароля");

    /// <summary>Включён второй фактор.</summary>
    public static readonly AuditAction TwoFactorEnabled = new("user.totp_enabled", "Включён второй фактор");

    /// <summary>Отключён второй фактор.</summary>
    /// <remarks>
    /// Отдельно от включения и заметнее его: отключение фактора у чужой учётной записи —
    /// первое, что сделал бы захвативший её, и след об этом должен быть.
    /// </remarks>
    public static readonly AuditAction TwoFactorDisabled = new("user.totp_disabled", "Отключён второй фактор");

    /// <summary>Личность переподтверждена перед действием, которое этого требует.</summary>
    /// <remarks>
    /// ⚠ <b>Пишется и удача, и отказ.</b> Череда отказов подряд — это подбор кода на живой
    /// сессии, то есть уже захваченной вкладке не хватает одного шага до чужих паролей.
    /// Событие, которое видно только при успехе, показало бы ровно ту половину, что менее
    /// интересна.
    /// </remarks>
    public static readonly AuditAction StepUp = new("user.step_up", "Личность переподтверждена");

    // --- установка ----------------------------------------------------------------------

    /// <summary>Первый администратор заведён командой <c>create-superadmin</c>.</summary>
    /// <remarks>
    /// Отдельное событие, а не <see cref="UserCreated"/>: у него нет актора-человека, оно
    /// происходит один раз за жизнь установки и означает появление первого доступа в
    /// систему. Такое стоит уметь найти одним фильтром.
    /// </remarks>
    public static readonly AuditAction SuperuserBootstrapped =
        new("user.bootstrap", "Заведён первый администратор");

    // --- вход ---------------------------------------------------------------------------

    /// <summary>Попытка входа — удачная или нет.</summary>
    /// <remarks>
    /// Одно событие на оба исхода: отличает их поле результата, а причина отказа пишется
    /// настоящая. Наружу она при этом не уходит — снаружи все отказы одинаковы, иначе форма
    /// входа отвечает на вопрос «а есть ли такой человек».
    /// </remarks>
    public static readonly AuditAction LoggedIn = new("auth.login", "Вход");

    /// <summary>Уборка одноразовых ссылок и кодов.</summary>
    /// <remarks>
    /// Пишется системой и только когда что-то действительно убрано. Нужна затем, чтобы
    /// вопрос «куда делись старые приглашения» имел ответ, а неверно выставленный срок
    /// хранения было видно по счётчикам, а не по опустевшей таблице. Сессии убирает модуль
    /// токенов своим событием <c>auth.cleanup</c>.
    /// </remarks>
    public static readonly AuditAction LinksCleaned = new("identity.cleanup", "Уборка ссылок");
}

