using System.Diagnostics;

namespace WotBTreader.Host.Web.Infrastructure;

internal sealed class CorrelationIdMiddleware(RequestDelegate next)
{
    internal const string HeaderName = "X-Correlation-ID";

    public async Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var supplied = context.Request.Headers[HeaderName].ToString();
        var correlationId = IsSafeCorrelationId(supplied)
            ? supplied
            : Guid.CreateVersion7().ToString("D");

        context.Items[HeaderName] = correlationId;
        context.Response.Headers[HeaderName] = correlationId;
        Activity.Current?.SetTag("wotbtreader.correlation_id", correlationId);

        await next(context);
    }

    private static bool IsSafeCorrelationId(string value) =>
        value.Length is > 0 and <= 64 &&
        value.All(character =>
            char.IsAsciiLetterOrDigit(character) ||
            character is '-' or '_' or '.');
}
