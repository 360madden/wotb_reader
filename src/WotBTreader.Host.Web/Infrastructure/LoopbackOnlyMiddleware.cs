using System.Net;

namespace WotBTreader.Host.Web.Infrastructure;

/// <summary>
/// Rejects requests that did not originate locally or attempt to pivot through a
/// hostile Host/Origin header. Kestrel still binds only to IPv4 loopback; this is
/// a second boundary for DNS rebinding and future hosting configuration mistakes.
/// </summary>
internal sealed class LoopbackOnlyMiddleware(RequestDelegate next)
{
    internal const string ErrorCode = "web.local_request_required";

    public async Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (!IsAllowed(context.Request))
        {
            await ApiProblemWriter.WriteAsync(
                context,
                StatusCodes.Status403Forbidden,
                ErrorCode,
                "Only requests from this computer are accepted.",
                retryable: false);
            return;
        }

        await next(context);
    }

    internal static bool IsAllowed(HttpRequest request)
    {
        var remoteAddress = request.HttpContext.Connection.RemoteIpAddress;
        if (remoteAddress is null || !IPAddress.IsLoopback(remoteAddress))
        {
            return false;
        }

        if (!IsLoopbackHost(request.Host.Host))
        {
            return false;
        }

        var origin = request.Headers.Origin.ToString();
        if (string.IsNullOrWhiteSpace(origin))
        {
            return true;
        }

        if (!Uri.TryCreate(origin, UriKind.Absolute, out var originUri) ||
            !IsLoopbackHost(originUri.Host) ||
            (originUri.Scheme != Uri.UriSchemeHttp &&
             originUri.Scheme != Uri.UriSchemeHttps))
        {
            return false;
        }

        // The browser origin must name the same listener. This blocks another
        // local web site from driving mutation endpoints with ambient cookies.
        var requestPort = request.Host.Port ??
            (request.IsHttps ? 443 : 80);
        return originUri.Port == requestPort;
    }

    internal static bool IsLoopbackHost(string host)
    {
        if (string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return IPAddress.TryParse(host, out var address) &&
            IPAddress.IsLoopback(address);
    }
}
