using Microsoft.AspNetCore.Mvc;

namespace WotBTreader.Host.Web.Infrastructure;

internal static class ApiProblemWriter
{
    public static async Task WriteAsync(
        HttpContext context,
        int statusCode,
        string code,
        string detail,
        bool retryable,
        CancellationToken cancellationToken = default)
    {
        var correlationId =
            context.Items[CorrelationIdMiddleware.HeaderName]?.ToString() ??
            Guid.CreateVersion7().ToString("D");

        var problem = new ProblemDetails
        {
            Status = statusCode,
            Title = TitleFor(statusCode),
            Detail = detail,
            Type = $"urn:wotbtreader:problem:{code}",
            Instance = context.Request.Path,
        };
        problem.Extensions["code"] = code;
        problem.Extensions["correlationId"] = correlationId;
        problem.Extensions["retryable"] = retryable;

        context.Response.StatusCode = statusCode;
        context.Response.ContentType = "application/problem+json";
        await context.Response.WriteAsJsonAsync(
            problem,
            cancellationToken: cancellationToken);
    }

    private static string TitleFor(int statusCode) => statusCode switch
    {
        StatusCodes.Status400BadRequest => "Invalid request",
        StatusCodes.Status401Unauthorized => "Local capability required",
        StatusCodes.Status403Forbidden => "Request denied",
        StatusCodes.Status404NotFound => "Not found",
        StatusCodes.Status409Conflict => "Conflict",
        StatusCodes.Status503ServiceUnavailable => "Temporarily unavailable",
        _ => "Request failed",
    };
}
