using EmailSslSender.Shared.Errors;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace EmailSslSender.Api.Infrastructure;

/// <summary>
/// Вносит конверт ошибки в контракт: он общий на всю систему и обязан быть описан.
/// </summary>
/// <remarks>
/// ⚠ <b>Найдено сборкой контракта (этап 11): конверта ошибки в документе не было ВОВСЕ.</b>
/// В нём описывались только успешные ответы — то есть машиночитаемый контракт умалчивал о
/// единственном, на что клиенты опираются, принимая решения. Правило 17 делает конверт
/// центральным, оба клиента ветвятся по полю <c>code</c>, а сгенерированные типы про него не
/// знали ничего.
///
/// ⚠ <b>Транформер документа, а не <c>[ProducesResponseType]</c> на каждом действии.</b>
/// Второе означало бы четыре пометки на каждый из четырёх десятков эндпоинтов — и первый же
/// новый эндпоинт был бы заведён без них, молча. Конверт общий, значит и вносится он в одном
/// месте.
///
/// ⚠ <b>Ответ <c>default</c>, а не перечень кодов.</b> Коды у эндпоинтов разные (401, 403,
/// 404, 409, 429…), и перечислять их здесь значило бы обещать те, которых у конкретного
/// действия не бывает. <c>default</c> в OpenAPI означает ровно нужное: «всё остальное
/// выглядит так».
/// </remarks>
internal sealed class ErrorEnvelopeDocument : IOpenApiDocumentTransformer
{
    /// <summary>Имя схемы. Совпадает с именем типа: по нему клиенты и ищут.</summary>
    private const string Схема = nameof(ErrorResponse);

    public async Task TransformAsync(
        OpenApiDocument document,
        OpenApiDocumentTransformerContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(context);

        document.Components ??= new OpenApiComponents();
        document.Components.Schemas ??= new Dictionary<string, IOpenApiSchema>(StringComparer.Ordinal);

        // ⚠ Схема берётся у генератора, а не пишется руками: руками написанная разошлась бы с
        // типом при первой же его правке, и разошлась бы молча.
        var схема = await context.GetOrCreateSchemaAsync(typeof(ErrorResponse), cancellationToken: cancellationToken);

        document.Components.Schemas[Схема] = схема;

        foreach (var путь in document.Paths ?? [])
        {
            foreach (var (_, операция) in путь.Value.Operations ?? [])
            {
                Дописать(операция);
            }
        }
    }

    /// <summary>
    /// Дописать операции ответ «всё остальное».
    /// </summary>
    /// <remarks>
    /// ⚠ Существующий <c>default</c> не перезаписывается: если действие однажды опишет свой
    /// особый отказ, общий конверт не должен его затирать. Сегодня таких нет — правило заведено
    /// на будущее и стоит одной строки.
    /// </remarks>
    private static void Дописать(OpenApiOperation операция)
    {
        операция.Responses ??= new OpenApiResponses();

        if (операция.Responses.ContainsKey("default"))
        {
            return;
        }

        операция.Responses["default"] = new OpenApiResponse
        {
            Description = "Отказ. Единый конверт: машинный код, сообщение, подробности, номер обращения.",
            Content = new Dictionary<string, OpenApiMediaType>(StringComparer.Ordinal)
            {
                ["application/json"] = new()
                {
                    Schema = new OpenApiSchemaReference(Схема),
                },
            },
        };
    }
}
