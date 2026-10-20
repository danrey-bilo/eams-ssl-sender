using System.Reflection;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Controllers;

namespace EmailSslSender.Modules.Identity.Api;

/// <summary>
/// Разрешает MVC находить контроллеры, объявленные <c>internal</c>, — только свои.
/// </summary>
/// <remarks>
/// Копия того же механизма из модуля журнала, и копия намеренная: общий базовый класс жил
/// бы в <c>Shared</c>, то есть общая библиотека потянула бы за собой веб-фреймворк ради
/// двадцати строк. Ограничение своей сборкой обязательно — провайдер попадает в общий
/// список и его спрашивают про каждый тип каждой подключённой части.
/// </remarks>
internal sealed class InternalControllers : ControllerFeatureProvider
{
    private static readonly Assembly Own = typeof(InternalControllers).Assembly;

    protected override bool IsController(TypeInfo typeInfo)
    {
        if (base.IsController(typeInfo))
        {
            return true;
        }

        if (!typeInfo.Assembly.Equals(Own))
        {
            return false;
        }

        return typeInfo is { IsClass: true, IsAbstract: false, ContainsGenericParameters: false }
            && !typeInfo.IsDefined(typeof(NonControllerAttribute))
            && (typeInfo.Name.EndsWith("Controller", StringComparison.OrdinalIgnoreCase)
                || typeInfo.IsDefined(typeof(ControllerAttribute)));
    }
}
