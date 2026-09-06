using Aspire.LdapAdmin.Web;
using Aspire.LdapAdmin.Web.Components;

var builder = WebApplication.CreateBuilder(args);

// The hosting integration (WithLdapAdmin) injects the connection name and the matching
// ConnectionStrings entry; the dev AppHost mirrors the same contract. There is no login —
// every operation binds with the AppHost-provided admin credentials.
var connectionName = builder.Configuration["LdapAdmin:ConnectionName"]
    ?? throw new InvalidOperationException(
        "LdapAdmin:ConnectionName is not set. The LdapAdmin host is configured by WithLdapAdmin() " +
        "(or the dev AppHost), which provides the connection name and the corresponding " +
        "ConnectionStrings entry.");
builder.AddOpenLdapClient(connectionName);
builder.Services.AddLdapAdminCore();

// The topbar's server + bind chips. A malformed connection string fails here, at the host
// boundary, same as malformed settings below.
builder.Services.AddSingleton(ConsoleConnectionInfo.From(
    builder.Configuration.GetConnectionString(connectionName)
        ?? throw new InvalidOperationException(
            $"ConnectionStrings:{connectionName} is not set; WithLdapAdmin() (or the dev AppHost) provides it.")));

// Defaulted behavior: bound once at startup from the LdapAdmin__* env contract that
// WithLdapAdmin() emits. A malformed value (an unknown theme name, a non-numeric limit) fails
// here, at the host boundary, rather than as a broken page later.
var settings = builder.Configuration.GetSection(LdapAdminSettings.SectionName)
    .Get<LdapAdminSettings>() ?? new();
builder.Services.AddSingleton(settings);
builder.Services.AddProblemDetails();

// Add services to the container.
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();
// The console's toast slot — scoped per circuit, rendered by the shell page.
builder.Services.AddScoped<ConsoleToastService>();
// The one guarded copy-to-clipboard path — never throws into a handler.
builder.Services.AddScoped<ConsoleClipboard>();
builder.Services.AddScoped<ConsoleDownload>();

var app = builder.Build();

if (settings.EnableRestApi)
{
    // API exceptions are always problem responses, including in Development; never expose the
    // developer exception page's stack, headers, or query values on this opt-in transport.
    app.UseWhen(
        static context => context.Request.Path.StartsWithSegments(
            LdapAdminApi.Prefix, StringComparison.OrdinalIgnoreCase),
        static api =>
        {
            api.UseExceptionHandler();
            api.UseStatusCodePages();
        });
}

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
}
app.UseWhen(
    context => !settings.EnableRestApi || !context.Request.Path.StartsWithSegments(
        LdapAdminApi.Prefix, StringComparison.OrdinalIgnoreCase),
    static ui => ui.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true));
app.UseAntiforgery();

// Runs the openldap_{name} health check from AddOpenLdapClient — a real admin bind plus a
// root-DSE search — so /health answers "can this admin reach the directory", not just "is
// Kestrel up". WithLdapAdmin() points the resource health check here.
app.MapHealthChecks("/health");

LdapAdminApi.Map(app, settings);

app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();
