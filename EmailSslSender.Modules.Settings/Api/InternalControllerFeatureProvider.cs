using System.Reflection;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Controllers;

namespace EmailSslSender.Modules.Settings.Api;

/// <summary>
/// Разрешает MVC находить контроллеры, объявленные <c>internal</c>.
/// </summary>
/// <remarks>
/// По умолчанию <c>ControllerFeatureProvider</c> берёт только <b>публичные</b> типы, и
/// внутренний контроллер он просто не увидит — молча, дав 404 без единой ошибки при старте.
///
/// Альтернатива — сделать контроллер публичным, но тогда публичными обязаны стать и типы
/// его зависимостей: правило C# не даёт публичному конструктору принимать внутренние
/// параметры. То есть ради одного HTTP-шва наружу вылезли бы чтение, справочник и
/// разрешение имён — всё нутро модуля.
///
/// Проверять, что маршруты действительно поднялись, надо тестом: без него следующая правка
/// сборочных свойств снова даст тихий 404.
/// </remarks>
internal sealed class InternalControllerFeatureProvider : ControllerFeatureProvider
{
    /// <summary>Сборка, чьи внутренние контроллеры этот провайдер соглашается видеть.</summary>
    private static readonly Assembly Own = typeof(InternalControllerFeatureProvider).Assembly;

    protected override bool IsController(TypeInfo typeInfo)
    {
        if (base.IsController(typeInfo))
        {
            return true;
        }

        // ⚠ Только своя сборка. Провайдер попадает в общий список ApplicationPartManager,
        // то есть его спрашивают про КАЖДЫЙ тип КАЖДОЙ подключённой части — хоста, соседних
        // модулей, тестовых сборок. Без этой строки он делал бы маршрутизируемым любой
        // непубличный класс с именем на «Controller» во всём приложении: вспомогательный
        // класс соседа, написанный без объявления прав, стал бы живым эндпоинтом, и не
        // заметил бы этого никто.
        if (!typeInfo.Assembly.Equals(Own))
        {
            return false;
        }

        // Та же проверка, что у базовой, минус требование быть публичным.
        return typeInfo is { IsClass: true, IsAbstract: false, ContainsGenericParameters: false }
            && !typeInfo.IsDefined(typeof(NonControllerAttribute))
            && (typeInfo.Name.EndsWith("Controller", StringComparison.OrdinalIgnoreCase)
                || typeInfo.IsDefined(typeof(ControllerAttribute)));
    }
}
