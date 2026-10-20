using System.Buffers.Text;
using System.Text;
using EmailSslSender.Shared.Errors;

namespace EmailSslSender.Modules.Identity.Storage;

/// <summary>
/// Курсор постраничного обхода списка людей: адрес последней показанной строки.
/// </summary>
/// <remarks>
/// <b>Ключ — адрес, а не смещение.</b> Правило 20 требует keyset, и не ради скорости на
/// сотне строк: страница по смещению съезжает, когда во время обхода кого-то заводят или
/// удаляют, и человек либо видит одного дважды, либо не видит вовсе. Порядок по адресу
/// устойчив — он уникален среди живых.
///
/// <b>Непрозрачен намеренно.</b> Клиент получает строку и возвращает её как есть. Иначе
/// смена ключа сортировки стала бы ломающим изменением API.
///
/// Колонка <c>citext</c>: сравнение и порядок нечувствительны к регистру, поэтому курсор
/// не разъедется с сортировкой из-за заглавной буквы в чьём-то адресе.
/// </remarks>
internal static class UserCursor
{
    /// <summary>Собрать курсор по последней строке страницы.</summary>
    public static string Encode(string email) => Base64Url.EncodeToString(Encoding.UTF8.GetBytes(email));

    /// <summary>
    /// Разобрать курсор.
    /// </summary>
    /// <remarks>
    /// Испорченный курсор — отдельный код ошибки, а не общая ошибка проверки: по нему клиент
    /// сбрасывает пагинацию и начинает сначала, вместо того чтобы показать невнятный отказ.
    /// </remarks>
    public static string? Decode(string? raw)
    {
        if (string.IsNullOrEmpty(raw))
        {
            return null;
        }

        try
        {
            var email = Encoding.UTF8.GetString(Base64Url.DecodeFromChars(raw));

            return email.Length == 0 ? throw AppException.InvalidCursor() : email;
        }
        catch (Exception error) when (error is FormatException or ArgumentException)
        {
            throw AppException.InvalidCursor();
        }
    }
}
