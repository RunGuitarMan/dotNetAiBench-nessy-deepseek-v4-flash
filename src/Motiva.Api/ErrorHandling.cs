using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Motiva.Application;
using Npgsql;

namespace Motiva.Api;

/// <summary>Маппинг ошибок приложения на ProblemDetails (T04/D206): статус, machine-readable code, traceId.</summary>
public sealed class MotivaErrorHandling(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            await next(context);
        }
        catch (MotivaException ex)
        {
            await WriteProblemAsync(context, StatusFor(ex.Code), ex.MachineCode ?? ex.Code.ToString().ToLower(), ex.Message, ex.RetryAfter);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException pg && pg.SqlState == "23505")
        {
            await WriteProblemAsync(context, StatusCodes.Status409Conflict, "conflict", "Конфликт уникальности: повтор номера или состояния");
        }
        catch (OperationCanceledException)
        {
            await WriteProblemAsync(context, StatusCodes.Status503ServiceUnavailable, "service_unavailable", "Операция отменена или превышен лимит времени", TimeSpan.FromSeconds(2));
        }
        catch (Exception)
        {
            await WriteProblemAsync(context, StatusCodes.Status500InternalServerError, "internal", "Внутренняя ошибка сервиса");
        }
    }

    private static int StatusFor(ErrorCode code) => code switch
    {
        ErrorCode.ValidationError or ErrorCode.ResourceUnavailable => StatusCodes.Status400BadRequest,
        ErrorCode.NotFound => StatusCodes.Status404NotFound,
        ErrorCode.Forbidden => StatusCodes.Status403Forbidden,
        ErrorCode.Conflict => StatusCodes.Status409Conflict,
        ErrorCode.PreconditionFailed => StatusCodes.Status412PreconditionFailed,
        ErrorCode.PreconditionRequired => StatusCodes.Status428PreconditionRequired,
        ErrorCode.Gone => StatusCodes.Status410Gone,
        ErrorCode.ServiceUnavailable => StatusCodes.Status503ServiceUnavailable,
        _ => StatusCodes.Status500InternalServerError,
    };

    private static async Task WriteProblemAsync(HttpContext context, int status, string code, string detail, TimeSpan? retryAfter = null)
    {
        if (context.Response.HasStarted)
        {
            return;
        }

        context.Response.StatusCode = status;
        if (retryAfter is not null)
        {
            context.Response.Headers.RetryAfter = ((int)retryAfter.Value.TotalSeconds).ToString();
        }

        context.Response.ContentType = "application/problem+json";
        var problem = new
        {
            status,
            title = status.ToString(),
            detail,
            code,
            traceId = context.TraceIdentifier,
        };
        await context.Response.WriteAsync(JsonSerializer.Serialize(problem, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));
    }
}

public static class ResultExtensions
{
    /// <summary>Добавить strong ETag к ответу (T04/I201).</summary>
    public static IResult WithEtag(this IResult result, string etag)
    {
        return new EtagResult(result, etag);
    }

    private sealed class EtagResult(IResult inner, string etag) : IResult
    {
        public async Task ExecuteAsync(HttpContext httpContext)
        {
            httpContext.Response.Headers.ETag = etag;
            await inner.ExecuteAsync(httpContext);
        }
    }
}
