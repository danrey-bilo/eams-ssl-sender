using EmailSslSender.Modules.Settings.Domain;
using EmailSslSender.Shared.Contracts.Data;
using Microsoft.EntityFrameworkCore;

namespace EmailSslSender.Modules.Settings.Storage;

/// <summary>
/// Таблица системных настроек в общей модели.
/// </summary>
/// <remarks>
/// ⚠ <b>Личных предпочтений здесь нет</b>, хотя в EAMS они есть (вторая таблица): все
/// настройки этой установки — одни на всех.
///
/// ⚠ <b>Внешнего ключа на реестр ключей нет и быть не может.</b> Реестр живёт в коде;
/// зеркалить его таблицей значило бы завести второй источник правды, который разойдётся при
/// первом же откате выкатки. Строка с неизвестным ключом законна в базе и отбрасывается при
/// чтении.
///
/// ⚠ <b><c>value jsonb NOT NULL</c> — это не запрет на <c>null</c>.</b> JSON-значение
/// <c>null</c> хранится как <c>'null'::jsonb</c> и остаётся законным значением настройки
/// («адрес не задан»); «не задано» выражается <b>отсутствием строки</b>.
/// </remarks>
internal sealed class SettingsEntities : IEntityConfigurationProvider
{
    /// <summary>Таблица системных значений.</summary>
    internal const string SystemTable = "system_settings";


    /// <summary>
    /// Наибольшая длина имени настройки.
    /// </summary>
    /// <remarks>
    /// Длина имени одна на систему: разная в коде и в базе означала бы, что настройку можно
    /// объявить так, как записать её нельзя.
    /// </remarks>
    internal const int KeyLength = 128;

    public void Configure(ModelBuilder model)
    {
        ArgumentNullException.ThrowIfNull(model);

        model.Entity<SystemSetting>(строка =>
        {
            строка.ToTable(SystemTable);

            строка.HasKey(запись => запись.Key).HasName($"pk_{SystemTable}");

            строка.Property(запись => запись.Key)
                .HasColumnName("key").HasMaxLength(KeyLength).IsRequired();

            строка.Property(запись => запись.ValueJson)
                .HasColumnName("value").HasColumnType("jsonb").IsRequired();

            // ⚠ Без внешнего ключа: таблица пользователей принадлежит чужому модулю.
            строка.Property(запись => запись.UpdatedBy).HasColumnName("updated_by");

            // ⚠ Умолчание базы ЕСТЬ, хотя приложение всегда задаёт время само. Оно не для
            // приложения: настройку правят и напрямую в psql — это единственная таблица
            // системы, куда оператор реально лезет руками, — и строка без времени правки
            // делает бесполезным ответ на вопрос «когда это поменяли».
            строка.Property(запись => запись.UpdatedAt)
                .HasColumnName("updated_at").HasColumnType("timestamptz").IsRequired()
                .HasDefaultValueSql("now()");
        });

    }
}
