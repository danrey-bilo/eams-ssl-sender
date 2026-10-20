using EmailSslSender.Shared.Contracts.Settings;

namespace EmailSslSender.Modules.Settings.Catalog;

/// <summary>
/// Всё, что объявили модули: имя → объявление.
/// </summary>
/// <remarks>
/// ⚠ <b>Справочник — раньше хранения, и это не порядок работ, а необходимость.</b> Слою базы
/// нужно знать, что такое ключ, чтобы отличить «строки нет, действует умолчание» от «такого
/// ключа никто не объявлял». Без справочника оба случая выглядят одинаково — пустой выборкой,
/// — и опечатка в имени тихо превращается в «настройка не задана».
///
/// ⚠ <b>Спор двух модулей за одно имя роняет СТАРТ.</b> Разница по последствиям большая:
/// отказ при старте останавливает выкатку и виден одной строкой в логе; отказ при первом
/// обращении выглядит так — приложение поднялось, проверка живости зелёная, оркестратор
/// доволен, а экран настроек отвечает пятисоткой. Разбор того же приёма у соседа —
/// <c>EmailSslSender.Modules.Audit/Catalog/AuditCatalogService.cs</c>.
/// </remarks>
internal sealed class SettingsCatalog
{
    private readonly Dictionary<string, ISettingDeclaration> _byName;

    public SettingsCatalog(IEnumerable<ISettingDeclarationProvider> providers)
    {
        ArgumentNullException.ThrowIfNull(providers);

        _byName = new Dictionary<string, ISettingDeclaration>(StringComparer.Ordinal);
        var duplicates = new List<string>();

        foreach (var declaration in providers.SelectMany(provider => provider.Declarations))
        {
            if (_byName.TryGetValue(declaration.Name, out var existing))
            {
                // ⚠ Повтор ОДИНАКОВОГО объявления законен и не считается спором. Четыре ключа
                // `auth.*` объявляют и `Identity`, и `Tokens`: сессия принадлежит второму,
                // а проверяет её срок первый, и заставить один модуль ссылаться на другой
                // ради текста подписи значило бы нарушить правило 1 ради косметики.
                //
                // Спор — это одно имя с РАЗНЫМ содержимым: тогда экран покажет одни границы,
                // а проверка значения возьмёт другие, и настройку станет можно задать и нельзя
                // прочитать обратно.
                // мутация 2026-08-29: обезвредил условие (`false &&`) → покраснели
                // CatalogTests.Спор_двух_модулей_за_имя_роняет_справочник и
                // .Спор_меряется_содержимым_а_не_текстом (правило 9).
                if (!Совпадают(existing, declaration))
                {
                    duplicates.Add(declaration.Name);
                }

                continue;
            }

            _byName.Add(declaration.Name, declaration);
        }

        if (duplicates.Count > 0)
        {
            throw new InvalidOperationException(
                "Два модуля объявили настройку с одним именем и разным содержимым: "
                + string.Join(", ", duplicates.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal))
                + ". Имя настройки лежит в базе и в запросах клиента; два объявления одного "
                + "имени означают, что экран показывает одни границы, а проверка берёт другие.");
        }

        // Порядок — раздел, потом имя: экран группирует по разделам, и стабильный порядок
        // избавляет его от сортировки, а тесты — от зависимости от порядка регистрации
        // модулей в хосте.
        All =
        [
            .. _byName.Values
                .OrderBy(declaration => declaration.Category, StringComparer.Ordinal)
                .ThenBy(declaration => declaration.Name, StringComparer.Ordinal),
        ];
    }

    /// <summary>Все объявления, по разделам и именам.</summary>
    public IReadOnlyList<ISettingDeclaration> All { get; }

    /// <summary>Объявление по имени или <c>null</c>, если такого ключа никто не объявлял.</summary>
    public ISettingDeclaration? Find(string name) =>
        _byName.GetValueOrDefault(name);

    /// <summary>
    /// Два объявления одного имени описывают одно и то же.
    /// </summary>
    /// <remarks>
    /// ⚠ Сравнивается всё, что видит клиент и проверка значения, — <b>кроме</b> подписей.
    /// Расхождение в тексте пояснения ничего не ломает: экран покажет то, что пришло первым,
    /// и оба текста верны. Расхождение в границе, умолчании или типе — ломает.
    /// </remarks>
    private static bool Совпадают(ISettingDeclaration a, ISettingDeclaration b) =>
        a.ValueType == b.ValueType
        && Equals(a.DefaultValue, b.DefaultValue)
        && a.Minimum == b.Minimum
        && a.Maximum == b.Maximum
        && a.IsNullable == b.IsNullable
        && a.IsSecurity == b.IsSecurity
        && a.Choices.SequenceEqual(b.Choices, StringComparer.Ordinal);
}
