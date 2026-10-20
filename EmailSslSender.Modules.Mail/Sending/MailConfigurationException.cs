namespace EmailSslSender.Modules.Mail.Sending;

/// <summary>
/// Почта настроена так, что слать нельзя: отказ составлен здесь, до чужого сервера.
/// </summary>
/// <remarks>
/// ⚠ ⚠ <b>Текст составлен модулем и секретов не несёт</b> — ни пароля, ни адресов получателей.
/// Ради этого тип и заведён: его сообщение можно отдать администратору (проверочное письмо) и
/// положить в <c>last_error</c> письма, а не прятать за цепочкой типов <c>SafeError</c>, как текст
/// любого чужого исключения.
///
/// ⚠ Наследует <see cref="InvalidOperationException"/>: «операция недопустима при таких
/// настройках» — ровно его смысл, и прежние ловцы по этому типу продолжают работать.
/// </remarks>
internal sealed class MailConfigurationException(string message, Exception? inner = null)
    : InvalidOperationException(message, inner);
