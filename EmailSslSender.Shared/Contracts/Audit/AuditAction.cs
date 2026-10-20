namespace EmailSslSender.Shared.Contracts.Audit;

/// <summary>
/// Имя события журнала: <c>auth.login</c>, <c>user.create</c>.
/// </summary>
/// <remarks>
/// Тип, а не голая строка: писать событие, которого нет среди констант модуля, должно
/// требовать усилия.
///
/// ⚠ <b>Имена событий не переименовываются.</b> По ним ищут в логах; переименование делает
/// прошлые строки ненаходимыми ровно тогда, когда их ищут. Ошиблись — заводится новое имя.
/// </remarks>
/// <param name="Name">Имя события.</param>
/// <param name="Title">Как это событие называется для человека.</param>
public readonly record struct AuditAction(string Name, string? Title = null)
{
    public override string ToString() => Name;
}
