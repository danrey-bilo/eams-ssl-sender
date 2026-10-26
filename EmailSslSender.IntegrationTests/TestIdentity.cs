using Modules.Core.Contracts.Audit;
using Modules.Core.Contracts.Authz;
using Modules.Core.Contracts.Settings;

namespace EmailSslSender.IntegrationTests;

/// <summary>
/// Кто пришёл — задаёт тест.
/// </summary>
/// <remarks>
/// Подменяет личность запроса, пока <see cref="Overridden"/>: тест проверяет поведение
/// эндпоинта, а не весь путь входа. Путь входа целиком проверяют тесты, которые входят
/// по-настоящему (<c>AuthApi</c>, <c>AuthFoundationApi</c>), — там подмена снята.
/// </remarks>
public sealed class TestUser : ICurrentUser
{
    public Guid? Id { get; set; }

    /// <summary>
    /// Суперпользователь ли. По умолчанию да: ролей в проекте нет, и каждая учётная запись — суперпользователь.
    /// </summary>
    public bool IsSuperuser { get; set; } = true;

    /// <summary>Чем пришли. По умолчанию человек.</summary>
    public AuditActorType ActorType { get; set; } = AuditActorType.User;

    public bool MustChangePassword { get; set; }

    public bool MustEnrollTwoFactor { get; set; }

    public Guid? SessionId { get; set; }

    public DateTimeOffset? StepUpAt { get; set; }

    /// <summary>Действует ли подмена. Снятая — личность распознаётся по-настоящему.</summary>
    public bool Overridden { get; private set; }

    /// <summary>
    /// Завести учётную запись в базе под подставную личность.
    /// </summary>
    /// <remarks>
    /// ⚠ Движок прав проекта пускает только живую активную учётную запись: личность, которой нет в
    /// базе, не может ничего — и это правильно. Задаёт обвязка хоста.
    /// </remarks>
    public Action<Guid>? Завести { get; set; }

    /// <summary>Войти администратором со свежим вторым фактором.</summary>
    public Guid SignIn()
    {
        Id = Guid.CreateVersion7();
        Завести?.Invoke(Id.Value);
        ActorType = AuditActorType.User;
        MustChangePassword = false;
        MustEnrollTwoFactor = false;
        Overridden = true;
        StepUpAt = DateTimeOffset.UtcNow;

        return Id.Value;
    }

    /// <summary>Снять подмену: дальше личность распознаёт настоящий конвейер.</summary>
    public void SignOut()
    {
        Id = null;
        SessionId = null;
        StepUpAt = null;
        MustChangePassword = false;
        MustEnrollTwoFactor = false;
        Overridden = false;

        ActorType = AuditActorType.User;
    }
}

/// <summary>Настройки, которыми управляет тест.</summary>
/// <remarks>
/// Настоящий читатель поверх базы и конфигурации проверяется отдельно и своим тестом: здесь
/// нужна возможность менять значение между запросами, а конфигурация хоста задаётся один раз
/// при старте.
/// </remarks>
public sealed class TestSettings : ISettingsReader
{
    /// <summary>Заданные значения. Чего нет — берётся из объявления ключа.</summary>
    public Dictionary<string, object?> Values { get; } = new(StringComparer.Ordinal);

    /// <summary>
    /// Значения набора миграций проекта: их несёт каждая установка, и подделка обязана их знать.
    /// </summary>
    public static IReadOnlyDictionary<string, object?> ProjectValues { get; } =
        new Dictionary<string, object?>(StringComparer.Ordinal) { ["auth.step_up_minutes"] = 10 };

    public ValueTask<T> GetAsync<T>(SettingKey<T> key, CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(
            Values.TryGetValue(key.Name, out var value) ? (T)value!
            : ProjectValues.TryGetValue(key.Name, out var project) ? (T)project!
            : key.Default);
}
