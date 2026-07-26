using Microsoft.AspNetCore.Antiforgery;

namespace WotBTreader.Host.Web.Infrastructure;

/// <summary>
/// Protects every present and future unsafe API method with both an unguessable
/// short-lived local capability and ASP.NET antiforgery validation.
/// </summary>
internal sealed class MutationProtectionMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(
        HttpContext context,
        LocalMutationSecurity security,
        IAntiforgery antiforgery)
    {
        if (!context.Request.Path.StartsWithSegments("/api/v1") ||
            HttpMethods.IsGet(context.Request.Method) ||
            HttpMethods.IsHead(context.Request.Method) ||
            HttpMethods.IsOptions(context.Request.Method))
        {
            await next(context);
            return;
        }

        if (!security.Validate(
                context.Request.Headers[
                    LocalMutationSecurity.CapabilityHeaderName].ToString()))
        {
            await ApiProblemWriter.WriteAsync(
                context,
                StatusCodes.Status401Unauthorized,
                "web.local_capability_required",
                "The short-lived local capability is absent or expired.",
                retryable: true);
            return;
        }

        try
        {
            await antiforgery.ValidateRequestAsync(context);
        }
        catch (AntiforgeryValidationException)
        {
            await ApiProblemWriter.WriteAsync(
                context,
                StatusCodes.Status403Forbidden,
                "web.antiforgery_validation_failed",
                "Antiforgery validation failed.",
                retryable: true);
            return;
        }

        await next(context);
    }
}
