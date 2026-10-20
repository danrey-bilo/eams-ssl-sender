namespace EmailSslSender.Shared.Errors;

/// <summary>
/// Машинные коды ошибок — один реестр на всю систему.
/// </summary>
/// <remarks>
/// Код нужен не человеку, а клиенту: по нему десктоп и веб решают, что показать и что
/// предложить сделать. Текст сообщения для этого не годится — он меняется при первой же
/// правке формулировки, и клиент, разбирающий текст, ломается молча.
///
/// Реестр здесь, а не в модулях: два модуля, вернувшие разные коды на одну и ту же беду,
/// заставят клиент обрабатывать оба, и однажды он обработает только один.
///
/// Константы, а не перечисление: код уезжает в JSON строкой, и <c>enum</c> потребовал бы
/// конвертера, который однажды напишет "PermissionDenied" вместо "PERMISSION_DENIED".
/// </remarks>
public static class ErrorCodes
{
    // --- общие ---------------------------------------------------------------------

    /// <summary>Непредвиденный сбой. Подробности только в логе — наружу уходит номер обращения.</summary>
    public const string Internal = "INTERNAL_ERROR";

    /// <summary>Тело или параметры запроса не прошли проверку.</summary>
    public const string Validation = "VALIDATION_ERROR";

    /// <summary>Нет удостоверения или оно недействительно.</summary>
    public const string Unauthenticated = "UNAUTHENTICATED";

    /// <summary>Удостоверение есть, прав не хватает.</summary>
    public const string PermissionDenied = "PERMISSION_DENIED";

    /// <summary>Объект не найден — или найден, но показывать его этому человеку нельзя.</summary>
    /// <remarks>
    /// Различать «нет» и «не положено» нельзя: разница сама по себе является утечкой —
    /// по ней перебором выясняется, какие объекты существуют.
    /// </remarks>
    public const string NotFound = "NOT_FOUND";

    /// <summary>Зависимости недоступны: база, кэш, партиции журнала.</summary>
    public const string NotReady = "NOT_READY";

    /// <summary>Слишком часто. Ставится ограничителем частоты на входе.</summary>
    public const string RateLimited = "RATE_LIMITED";

    /// <summary>Тело запроса больше предела этого адреса (413).</summary>
    /// <remarks>
    /// Решение 0095: прежде такой отказ сервера уходил пятисоткой <c>INTERNAL_ERROR</c> — клиент
    /// не мог отличить «файл великоват» от сбоя и предлагал сообщить администратору.
    /// </remarks>
    public const string PayloadTooLarge = "PAYLOAD_TOO_LARGE";

    /// <summary>Адрес есть, а этого метода у него нет (405).</summary>
    public const string MethodNotAllowed = "METHOD_NOT_ALLOWED";

    // --- личности ------------------------------------------------------------------
    //
    // ⚠ Здесь, а не в модуле, и это не формальность. Коды разбирают веб и десктоп; модуль,
    // объявивший код у себя, вынуждает клиента знать, в каком модуле искать. Хуже другое:
    // два модуля независимо назвали бы одну и ту же беду по-разному, и клиент однажды
    // обработал бы только один из вариантов. Найдено разбором 2026-08-22 — десять кодов
    // модуля личностей жили строковыми литералами.

    /// <summary>Неверный адрес или пароль. Один на все причины отказа при входе.</summary>
    /// <remarks>
    /// Различать «нет такого адреса» и «пароль не тот» нельзя: разница превращает форму
    /// входа в справочник существующих учётных записей.
    /// </remarks>
    public const string InvalidCredentials = "INVALID_CREDENTIALS";

    public const string SessionExpired = "SESSION_EXPIRED";

    /// <summary>Сессия закрыта: вышли, отозвали, обнаружили кражу токена.</summary>
    /// <remarks>Отдельно от истечения: клиент показывает причину, а не «войдите заново».</remarks>
    public const string SessionRevoked = "SESSION_REVOKED";

    /// <summary>Учётная запись отключена или удалена.</summary>
    public const string UserInactive = "USER_INACTIVE";

    /// <summary>Такой адрес уже занят.</summary>
    public const string UserAlreadyExists = "USER_ALREADY_EXISTS";

    /// <summary>Учётная запись уже активирована — приглашение ей больше не нужно.</summary>
    public const string UserAlreadyActivated = "USER_ALREADY_ACTIVATED";

    /// <summary>Ссылка активации или сброса недействительна: истекла, погашена, не та.</summary>
    public const string ActivationTokenInvalid = "ACTIVATION_TOKEN_INVALID";

    /// <summary>Пароль не удовлетворяет политике. В подробностях — что именно не так.</summary>
    public const string PasswordPolicyViolation = "PASSWORD_POLICY_VIOLATION";

    /// <summary>Последнюю активную учётную запись нельзя отключить или удалить: каждая — администратор.</summary>
    public const string LastSuperadmin = "LAST_SUPERADMIN";

    /// <summary>Пароль требуется сменить: до этого доступно только это действие.</summary>
    /// <remarks>
    /// Отдельный код, а не общий отказ: по нему клиент ведёт человека на форму смены
    /// пароля, вместо того чтобы показать «нет прав» на каждом экране.
    /// </remarks>
    public const string PasswordChangeRequired = "PASSWORD_CHANGE_REQUIRED";

    /// <summary>Нужен второй фактор: пароль принят, вход не завершён.</summary>
    /// <remarks>
    /// Отдельный код, а не отказ: по нему клиент показывает поле для кода, а не экран
    /// «неверный пароль». В подробностях приезжает расписка, с которой надо вернуться.
    /// </remarks>
    public const string TwoFactorRequired = "TWO_FACTOR_REQUIRED";

    /// <summary>Код второго фактора не подошёл.</summary>
    public const string TwoFactorInvalid = "TWO_FACTOR_INVALID";

    /// <summary>Второй фактор уже включён.</summary>
    public const string TwoFactorAlreadyEnabled = "TWO_FACTOR_ALREADY_ENABLED";

    /// <summary>Второй фактор не заведён или не подтверждён.</summary>
    public const string TwoFactorNotEnrolled = "TWO_FACTOR_NOT_ENROLLED";

    /// <summary>Настроенный второй фактор временно недоступен; пароль не открывает повторную настройку.</summary>
    public const string TwoFactorUnavailable = "TWO_FACTOR_UNAVAILABLE";

    /// <summary>Система требует включить второй фактор: до этого доступно только его включение.</summary>
    /// <remarks>
    /// Родной брат <see cref="PasswordChangeRequired"/>: тот же приём, другое требование.
    /// Клиент по нему ведёт человека на экран включения фактора.
    /// </remarks>
    public const string TwoFactorEnrollmentRequired = "TWO_FACTOR_ENROLLMENT_REQUIRED";

    // --- выдачи с курсором ----------------------------------------------------------

    /// <summary>Курсор пагинации испорчен или не от этой выдачи.</summary>
    /// <remarks>
    /// Отдельный код, а не общая ошибка проверки: клиент по нему сбрасывает пагинацию и
    /// начинает сначала, вместо того чтобы показать человеку невнятный отказ.
    /// </remarks>
    public const string InvalidCursor = "INVALID_CURSOR";

    // --- сессии ----------------------------------------------------------------------

    /// <summary>Токен предъявлен, но его срок вышел.</summary>
    /// <remarks>
    /// ⚠ Отдельно от <see cref="SessionRevoked"/> намеренно. Истечение — штатное событие, и
    /// клиент отвечает на него обновлением; отзыв — событие, после которого обновлять
    /// нечего и надо входить заново. Один код на оба заставил бы клиент угадывать, что
    /// делать, а угадывает он в пользу повтора — то есть входит в цикл.
    /// </remarks>
    public const string TokenExpired = "TOKEN_EXPIRED";

    // --- настройки ---------------------------------------------------------------------

    /// <summary>Настройки с таким именем не объявлял ни один модуль.</summary>
    /// <remarks>
    /// ⚠ <b>Отдельный код, а не общий отказ проверки.</b> «Такой настройки нет» и
    /// «значение не годится» требуют от человека разного: первое — проверить имя (обычно
    /// опечатка или настройка выведена из обращения), второе — исправить значение.
    /// </remarks>
    public const string UnknownSetting = "UNKNOWN_SETTING";

    /// <summary>Значение не подходит по типу, границам или списку допустимых.</summary>
    public const string InvalidSettingValue = "INVALID_SETTING_VALUE";

    // --- почта -------------------------------------------------------------------------

    /// <summary>Почта не настроена: слать некуда.</summary>
    /// <remarks>
    /// ⚠ Отвечается <c>409</c>, а не <c>503</c>: это не «сервис недоступен», а «настройка
    /// не заполнена», и чинится она человеком за минуту.
    /// </remarks>
    public const string MailNotConfigured = "MAIL_NOT_CONFIGURED";

    /// <summary>Чужой почтовый сервер отказал.</summary>
    /// <remarks>
    /// ⚠ <b>Тоже <c>409</c>, и это не небрежность.</b> Семантически верен <c>502</c>, но
    /// Cloudflare подменяет тело любого <c>5xx</c> от origin своей страницей — и конверт с
    /// текстом ответа чужого сервера до браузера не доезжает. Администратор увидел бы «письмо
    /// не отправлено» вместо <c>5.7.1 Authentication Required</c>. Найдено живой проверкой на
    /// стенде 2026-08-18.
    /// </remarks>
    public const string MailSendFailed = "MAIL_SEND_FAILED";

    /// <summary>Настройку правят не на том уровне: личную — как системную или наоборот.</summary>
    /// <remarks>
    /// ⚠ <b>Не «нет прав».</b> Отказ по правам говорит «вам нельзя»; этот говорит «не
    /// туда»: системную настройку не задать себе лично, а личное предпочтение не навязать
    /// установке. Слить их в `PERMISSION_DENIED` значило бы отправить человека выпрашивать
    /// право, которое ничего не изменит.
    /// </remarks>
    public const string SettingLevelForbidden = "SETTING_LEVEL_FORBIDDEN";

    // --- Key Manager Plus --------------------------------------------------------------

    /// <summary>Не заданы адрес Key Manager Plus или ключ API.</summary>
    /// <remarks>⚠ <c>409</c> по той же причине, что <see cref="MailNotConfigured"/>.</remarks>
    public const string KeyManagerNotConfigured = "KEYMANAGER_NOT_CONFIGURED";

    /// <summary>Key Manager Plus не ответил или ответил отказом: <c>details.reason</c>.</summary>
    /// <remarks>⚠ <c>409</c>, а не <c>502</c> — по той же причине, что <see cref="MailSendFailed"/>.</remarks>
    public const string KeyManagerUnavailable = "KEYMANAGER_UNAVAILABLE";

    /// <summary>Сертификата с таким Common Name в Key Manager Plus нет.</summary>
    public const string KeyManagerCertificateNotFound = "KEYMANAGER_CERTIFICATE_NOT_FOUND";

    /// <summary>Эту подписку прямо сейчас отправляет другой запрос или фоновый опрос.</summary>
    public const string DeliveryInProgress = "DELIVERY_IN_PROGRESS";
}
