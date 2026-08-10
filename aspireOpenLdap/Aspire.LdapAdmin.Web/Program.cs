using Aspire.LdapAdmin.Web;
using Aspire.LdapAdmin.Web.Components;

var builder = WebApplication.CreateBuilder(args);

// The hosting integration (WithLdapAdmin, #78) injects the connection name and the matching
// ConnectionStrings entry; the dev AppHost mirrors the same contract. There is no login by
// decision — every operation binds with the AppHost-provided admin credentials.
var connectionName = builder.Configuration["LdapAdmin:ConnectionName"]
    ?? throw new InvalidOperationException(
        "LdapAdmin:ConnectionName is not set. The LdapAdmin host is configured by WithLdapAdmin() " +
        "(or the dev AppHost), which provides the connection name and the corresponding " +
        "ConnectionStrings entry.");
builder.AddOpenLdapClient(connectionName);
builder.Services.AddLdapAdminCore();

// Defaulted behavior (#98): bound once at startup from the LdapAdmin__* env contract that
// WithLdapAdmin() emits. A malformed value (an unknown theme name, a non-numeric limit) fails
// here, at the host boundary, rather than as a broken page later.
var settings = builder.Configuration.GetSection(LdapAdminSettings.SectionName)
    .Get<LdapAdminSettings>() ?? new();
builder.Services.AddSingleton(settings);

// Add services to the container.
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();
// The console's toast slot — scoped per circuit, rendered by the shell page.
builder.Services.AddScoped<ConsoleToastService>();

var app = builder.Build();

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
}
app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
app.UseAntiforgery();

// Runs the openldap_{name} health check from AddOpenLdapClient — a real admin bind plus a
// root-DSE search — so /health answers "can this admin reach the directory", not just "is
// Kestrel up". WithLdapAdmin() points the resource health check here.
app.MapHealthChecks("/health");

app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();
