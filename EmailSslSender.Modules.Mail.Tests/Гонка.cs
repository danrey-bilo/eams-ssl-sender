using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Npgsql;

namespace EmailSslSender.Modules.Mail.Tests;

/// <summary>
/// Вклинивается между чтением порции и записью — ровно в то окно, которое закрывает
/// условное обновление.
/// </summary>
/// <remarks>
/// ⚠ ⚠ <b>Без него условное обновление НЕ ПРОВЕРЯЕТСЯ ничем.</b> Мутация «убрать условие»
/// пережила первую редакцию тестов: сценарий «перезадали до ротации» до условия просто не
/// доходит — строка уже под действующей версией, и цикл пропускает её раньше. Настоящая гонка
/// живёт внутри одной транзакции, между <c>SELECT</c> порции и <c>UPDATE</c>, и снаружи её не
/// устроить.
///
/// ⚠ <b>Правит ЧУЖИМ подключением</b>, а не тем же контекстом: правка через контекст попала бы
/// в ту же транзакцию, и никакой гонки бы не было — вышло бы обычное последовательное
/// изменение.
///
/// ⚠ <b>Срабатывает РОВНО ОДИН РАЗ.</b> Перехватчик висит на контексте всю обвязку; без
/// одноразовости он вмешивался бы и в чтения соседних тестов, и падали бы они.
/// </remarks>
public sealed class Гонка(string строкаПодключения) : DbCommandInterceptor
{
    private int _осталось;
    private string? _ключ;

    /// <summary>Вмешаться один раз в ближайшее чтение порции секретов.</summary>
    /// <param name="ключ">Какую строку править.</param>
    public void Взвести(string ключ)
    {
        _ключ = ключ;
        Interlocked.Exchange(ref _осталось, 1);
    }

    /// <summary>Снять взвод — между тестами.</summary>
    public void Снять() => Interlocked.Exchange(ref _осталось, 0);

    /// <summary>
    /// Правит строку ПОСЛЕ чтения порции, а не до.
    /// </summary>
    /// <remarks>
    /// ⚠ ⚠ <b>Именно <c>Executed</c>, а не <c>Executing</c>.</b> Первая редакция вклинивалась
    /// до выполнения — и порция читалась уже испорченной, то есть ротация падала на разборе
    /// четырёхбайтового ключа вместо того, чтобы дойти до условного обновления. Гонка, которую
    /// надо воспроизвести, устроена наоборот: порция прочитана СТАРОЙ, а в базе уже новое.
    ///
    /// Снимок оператора в PostgreSQL берётся на его старте (READ COMMITTED), поэтому уже
    /// прочитанные строки чужой правки не видят — ровно то, что нужно.
    /// </remarks>
    public override async ValueTask<DbDataReader> ReaderExecutedAsync(
        DbCommand command,
        CommandExecutedEventData eventData,
        DbDataReader result,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        // Ждём именно чтение порции секретов: на прочих запросах вмешиваться не во что.
        if (command.CommandText.Contains("system_secrets", StringComparison.Ordinal)
            && command.CommandText.StartsWith("SELECT", StringComparison.Ordinal)
            && Interlocked.Exchange(ref _осталось, 0) == 1)
        {
            await ПодменитьAsync(cancellationToken);
        }

        return await base.ReaderExecutedAsync(command, eventData, result, cancellationToken);
    }

    /// <summary>Заменить ключ данных строки — как если бы пароль задали заново.</summary>
    private async Task ПодменитьAsync(CancellationToken cancellationToken)
    {
        await using var подключение = new NpgsqlConnection(строкаПодключения);
        await подключение.OpenAsync(cancellationToken);

        await using var команда = подключение.CreateCommand();
        команда.CommandText =
            "UPDATE system_secrets SET data_key = decode('00ff00ff', 'hex') WHERE key = @key";
        команда.Parameters.AddWithValue("key", _ключ!);

        await команда.ExecuteNonQueryAsync(cancellationToken);
    }
}
