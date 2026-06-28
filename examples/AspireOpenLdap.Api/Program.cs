using System.DirectoryServices.Protocols;
using Aspire.OpenLdap;

var builder = WebApplication.CreateBuilder(args);

// OpenTelemetry, health checks, etc. — exports to the Aspire dashboard via OTLP.
builder.AddServiceDefaults();

// Registers the instrumented OpenLdapClient against the "openldap" connection string.
builder.AddOpenLdapClient("openldap");

var app = builder.Build();

app.MapDefaultEndpoints();

// GET /users — searches the directory through OpenLdapClient. Each call produces an
// "LDAP search" span (a child of the incoming HTTP request span) and a
// db.client.operation.duration metric on the "Aspire.OpenLdap" source/meter, both visible
// in the Aspire dashboard's Traces and Metrics views.
app.MapGet("/users", (OpenLdapClient ldap) =>
{
    var response = (SearchResponse)ldap.Send(new SearchRequest(
        distinguishedName: "dc=example,dc=org",
        ldapFilter: "(objectClass=inetOrgPerson)",
        searchScope: SearchScope.Subtree,
        "uid", "cn", "mail"));

    var users = response.Entries.Cast<SearchResultEntry>().Select(entry => new
    {
        dn = entry.DistinguishedName,
        uid = FirstValue(entry, "uid"),
        cn = FirstValue(entry, "cn"),
        mail = FirstValue(entry, "mail"),
    });

    return Results.Ok(users);
});

app.Run();

static string? FirstValue(SearchResultEntry entry, string attribute) =>
    entry.Attributes.Contains(attribute)
        ? entry.Attributes[attribute].GetValues(typeof(string)).FirstOrDefault() as string
        : null;
