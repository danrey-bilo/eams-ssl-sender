using Modules.Core.Contracts.Audit;
using Modules.Core.Contracts.Authz;
using Modules.Core.Contracts.Settings;

namespace EmailSslSender.IntegrationTests;

/// <summary>Подставная личность запроса, пока <see cref="Overridden"/>.</summary>
public sealed class TestUser : ICurrentUser
{
    public Guid? Id { get; set; }

    /// <summary>Ролей нет — каждая учётная запись суперпользователь.</summary>
    public bool IsSuperuser { get; set; } = true;

    public AuditActorType ActorType { get; set; } = AuditActorType.User;

    public bool MustChangePassword { get; set; }

    public bool MustEnrollTwoFactor { get; set; }

    public Guid? SessionId { get; set; }

    public DateTimeOffset? StepUpAt { get; set; }

    /// <summary>Снятая подмена — личность распознаётся по-настоящему.</summary>
    public bool Overridden { get; private set; }

    /// <summary>⚠ Завести учётную запись в базе: движок прав не знает несуществующих.</summary>
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

    /// <summary>Снять подмену.</summary>
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

/// <summary>Настройки, которые тест меняет между запросами.</summary>
public sealed class TestSettings : ISettingsReader
{
    /// <summary>Чего нет — из набора проекта или умолчания ключа.</summary>
    public Dictionary<string, object?> Values { get; } = new(StringComparer.Ordinal);

    /// <summary>Значения набора <c>sender</c>, как в каждой установке.</summary>
    public static IReadOnlyDictionary<string, object?> ProjectValues { get; } =
        new Dictionary<string, object?>(StringComparer.Ordinal) { ["auth.step_up_minutes"] = 10 };

    public ValueTask<T> GetAsync<T>(SettingKey<T> key, CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(
            Values.TryGetValue(key.Name, out var value) ? (T)value!
            : ProjectValues.TryGetValue(key.Name, out var project) ? (T)project!
            : key.Default);
}
