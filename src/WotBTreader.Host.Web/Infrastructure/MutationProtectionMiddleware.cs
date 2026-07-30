using Microsoft.AspNetCore.Antiforgery;

namespace WotBTreader.Host.Web.Infrastructure;

/// <summary>
/// Protects every unsafe API method including SignalR connection negotiation.
/// Native overlay/CLI clients authenticate with the rendezvous capability header.
/// Browser clients authenticate with a same-origin capability cookie plus antiforgery.
/// </summary>
internal sealed class MutationProtectionMiddleware(RequestDelegate next)
{
    private const string CapabilityCookieName = "WotBTreader.Capability";

    public async Task InvokeAsync(
        HttpContext context,
        LocalMutationSecurity security,
        IAntiforgery antiforgery)
    {
        // Only protect mutation routes under /api/v1. GET/HEAD/OPTIONS pass through.
        if (!context.Request.Path.StartsWithSegments("/api/v1") ||
            HttpMethods.IsGet(context.Request.Method) ||
            HttpMethods.IsHead(context.Request.Method) ||
            HttpMethods.IsOptions(context.Request.Method))
        {
            await next(context);
            return;
        }

        // Native client path: capability header from rendezvous record.
        string? headerValue = context.Request.Headers[
            LocalMutationSecurity.CapabilityHeaderName].ToString();
        if (!string.IsNullOrEmpty(headerValue) && security.Validate(headerValue))
        {
            // Native client authenticated — no antiforgery required.
            await next(context);
            return;
        }

        // Browser path: capability cookie + antiforgery.
        string? cookieValue = context.Request.Cookies[CapabilityCookieName];
        if (string.IsNullOrEmpty(cookieValue) || !security.Validate(cookieValue))
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
