using System.Reflection;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Controllers;

namespace EmailSslSender.Modules.KeyManager.Api;

/// <summary>
/// Находит <c>internal</c>-контроллеры этой сборки — и только этой.
/// </summary>
/// <remarks>
/// ⚠ Копия провайдера почты, а не общий тип в <c>Shared</c>: провайдер смотрит на СВОЮ сборку, и
/// общий экземпляр делал бы маршрутизируемым любой непубличный класс на «Controller» во всём
/// приложении (разбор — у провайдера модуля почты).
/// </remarks>
internal sealed class InternalControllerFeatureProvider : ControllerFeatureProvider
{
    private static readonly Assembly Own = typeof(InternalControllerFeatureProvider).Assembly;

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
