using System.Net;
using WotBTreader.Bootstrap.Configuration;
using WotBTreader.Bootstrap.DependencyInjection;
using WotBTreader.Host.Web.Components;
using WotBTreader.Host.Web.Endpoints;
using WotBTreader.Host.Web.Hubs;
using WotBTreader.Host.Web.Infrastructure;

var builder = WebApplication.CreateBuilder(args);

// Always resolve ContentRootPath and WebRootPath from the assembly location
// so wwwroot and static assets are found regardless of the process working directory.
var baseDir = AppContext.BaseDirectory;
builder.Environment.ContentRootPath = baseDir;
builder.Environment.WebRootPath = Path.Combine(baseDir, "wwwroot");

// Binding is deliberately configured in code so an inherited ASPNETCORE_URLS value
// cannot accidentally expose replay data on a LAN interface.
var configuredPort = builder.Configuration.GetValue<int?>("Web:Port") ?? 9182;
if (configuredPort is < 0 or > 65535)
{
    throw new InvalidOperationException("Web:Port must be between 0 and 65535.");
}

builder.WebHost.ConfigureKestrel(options =>
{
    options.AddServerHeader = false;
    options.Listen(IPAddress.Loopback, configuredPort);
});

builder.Services.AddWotBTreaderFoundation(new TreaderBootstrapOptions(
    builder.Configuration["Paths:ApplicationDataRoot"],
    builder.Configuration["Game:Root"],
    builder.Configuration["Game:UserDataRoot"]));

builder.Services
    .AddRazorComponents()
    .AddInteractiveServerComponents();
builder.Services.AddSignalR(options =>
{
    options.EnableDetailedErrors = builder.Environment.IsDevelopment();
    options.MaximumReceiveMessageSize = 32 * 1024;
});
builder.Services.AddProblemDetails();
builder.Services.AddAntiforgery(options =>
{
    options.HeaderName = LocalMutationSecurity.AntiforgeryHeaderName;
    options.Cookie.Name = "WotBTreader.Antiforgery";
    options.Cookie.HttpOnly = true;
    options.Cookie.SameSite = SameSiteMode.Strict;
    options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
});
builder.Services.AddHttpContextAccessor();
builder.Services.AddWebSurface(builder.Configuration);

var app = builder.Build();

app.UseMiddleware<CorrelationIdMiddleware>();
app.UseMiddleware<LoopbackOnlyMiddleware>();
app.UseExceptionHandler();
app.UseStatusCodePages();
app.UseAntiforgery();
app.UseMiddleware<MutationProtectionMiddleware>();
app.UseStaticFiles();

app.MapReadApi();
app.MapGameApi();
app.MapHub<TelemetryHub>("/api/v1/stream");
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

try
{
    await app.RunAsync();
}
catch (IOException) when (app.Environment.IsProduction())
{
    Console.Error.WriteLine(
        $"Port {configuredPort} is already in use. Stop the other instance first.");
    Environment.Exit(1);
}
