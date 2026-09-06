using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using LdifDotNet;
using Xunit;

namespace Aspire.LdapAdmin.Tests;

[Collection(LdapAdminAppHostCollection.Name)]
[Trait("Category", "RestIntegration")]
public sealed class RestApiIntegrationTests(LdapAdminAppHostFixture fixture)
{
    [Fact]
    public async Task Read_search_schema_and_ldif_surfaces_use_stable_json_shapes()
    {
        using var cts = TestCancellation.Source();

        using var metadata = await GetJsonAsync("/api/v1/directory", cts.Token);
        Assert.Equal(fixture.BaseDn, metadata.RootElement.GetProperty("baseDn").GetString());

        var directoryDn = fixture.DnUnder("ou=directory");
        using var children = await GetJsonAsync(
            $"/api/v1/directory/children?dn={Uri.EscapeDataString(directoryDn)}&limit=1", cts.Token);
        Assert.True(children.RootElement.GetProperty("truncated").GetBoolean());
        Assert.Single(children.RootElement.GetProperty("children").EnumerateArray());

        using var searchResponse = await fixture.Api.PostAsJsonAsync("/api/v1/directory/search", new
        {
            baseDn = directoryDn,
            filter = "(objectClass=inetOrgPerson)",
            scope = "oneLevel",
            limit = 1,
            attributes = new[] { "cn" },
        }, cts.Token);
        Assert.Equal(HttpStatusCode.OK, searchResponse.StatusCode);
        using var search = JsonDocument.Parse(await searchResponse.Content.ReadAsStringAsync(cts.Token));
        Assert.True(search.RootElement.GetProperty("truncated").GetBoolean());
        Assert.Equal("schema", search.RootElement.GetProperty("entries")[0]
            .GetProperty("attributes")[0].GetProperty("classification").GetString());

        var aliceDn = fixture.DnUnder("uid=alice", "ou=people");
        using var entry = await GetJsonAsync(
            $"/api/v1/entries?dn={Uri.EscapeDataString(aliceDn)}&attributes=cn", cts.Token);
        Assert.False(entry.RootElement.GetProperty("truncated").GetBoolean());
        Assert.Equal(aliceDn, entry.RootElement.GetProperty("entry").GetProperty("dn").GetString());

        using var schema = await GetJsonAsync("/api/v1/schema", cts.Token);
        Assert.False(schema.RootElement.GetProperty("truncated").GetBoolean());
        Assert.NotEmpty(schema.RootElement.GetProperty("objectClasses").EnumerateArray());
        Assert.All(schema.RootElement.GetProperty("objectClasses").EnumerateArray(), item =>
            Assert.Contains(item.GetProperty("kind").GetString(), new[] { "abstract", "auxiliary", "structural" }));

        using var planResponse = await fixture.Api.PostAsJsonAsync("/api/v1/ldif/plan", new
        {
            ldif = $"dn: uid=planned,ou=people,{fixture.BaseDn}\nchangetype: delete\n",
        }, cts.Token);
        Assert.Equal(HttpStatusCode.OK, planResponse.StatusCode);
        using var plan = JsonDocument.Parse(await planResponse.Content.ReadAsStringAsync(cts.Token));
        Assert.False(plan.RootElement.GetProperty("truncated").GetBoolean());
        Assert.Equal("delete", plan.RootElement.GetProperty("items")[0].GetProperty("changeType").GetString());

        using var invalidPlan = await fixture.Api.PostAsJsonAsync("/api/v1/ldif/plan", new
        {
            ldif = "userPassword: submitted-ldif-secret",
        }, cts.Token);
        Assert.Equal(HttpStatusCode.BadRequest, invalidPlan.StatusCode);
        Assert.DoesNotContain(
            "submitted-ldif-secret",
            await invalidPlan.Content.ReadAsStringAsync(cts.Token),
            StringComparison.Ordinal);

        using var export = await GetJsonAsync(
            $"/api/v1/ldif/export?baseDn={Uri.EscapeDataString(aliceDn)}", cts.Token);
        Assert.False(export.RootElement.GetProperty("truncated").GetBoolean());
        Assert.Equal(1, export.RootElement.GetProperty("entryCount").GetInt32());
        Assert.Contains($"dn: {aliceDn}", export.RootElement.GetProperty("ldif").GetString(), StringComparison.Ordinal);

        using var wrongMethod = await fixture.Api.PutAsJsonAsync("/api/v1/directory", new { }, cts.Token);
        Assert.Equal(HttpStatusCode.MethodNotAllowed, wrongMethod.StatusCode);
        Assert.Equal("application/problem+json", wrongMethod.Content.Headers.ContentType?.MediaType);

        using var protectedPassword = await fixture.Api.PostAsJsonAsync("/api/v1/entries/password", new
        {
            dn = fixture.Settings.BindDn,
            newPassword = "submitted-password-secret",
        }, cts.Token);
        Assert.Equal(HttpStatusCode.BadRequest, protectedPassword.StatusCode);
        Assert.DoesNotContain(
            "submitted-password-secret",
            await protectedPassword.Content.ReadAsStringAsync(cts.Token),
            StringComparison.Ordinal);

        using var nullAttribute = await fixture.Api.PostAsJsonAsync("/api/v1/entries", new
        {
            dn = fixture.DnUnder("uid=never-created", "ou=people"),
            attributes = new object?[] { null },
        }, cts.Token);
        Assert.Equal(HttpStatusCode.BadRequest, nullAttribute.StatusCode);

        using var nullChange = await fixture.Api.PatchAsJsonAsync("/api/v1/entries", new
        {
            dn = aliceDn,
            changes = new object?[] { null },
        }, cts.Token);
        Assert.Equal(HttpStatusCode.BadRequest, nullChange.StatusCode);

        using var invalidFilter = await fixture.Api.PostAsJsonAsync("/api/v1/directory/search", new
        {
            filter = "(&(objectClass=*)",
            scope = "subtree",
            limit = 1,
        }, cts.Token);
        Assert.Equal(HttpStatusCode.BadRequest, invalidFilter.StatusCode);
        Assert.Equal("application/problem+json", invalidFilter.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task Entry_write_routes_round_trip_without_putting_the_dn_in_the_path()
    {
        using var cts = TestCancellation.Source();
        var originalDn = fixture.DnUnder(Dn.Rdn("uid", "api-roundtrip"), "ou=people");
        var renamedDn = fixture.DnUnder(Dn.Rdn("uid", "api-renamed"), "ou=people");

        try
        {
            using var added = await fixture.Api.PostAsJsonAsync("/api/v1/entries", new
            {
                dn = originalDn,
                attributes = new object[]
                {
                    new { name = "objectClass", values = new[] { "inetOrgPerson" } },
                    new { name = "uid", values = new[] { "api-roundtrip" } },
                    new { name = "cn", values = new[] { "API Round Trip" } },
                    new { name = "sn", values = new[] { "Trip" } },
                },
            }, cts.Token);
            Assert.Equal(HttpStatusCode.Created, added.StatusCode);
            using (var body = JsonDocument.Parse(await added.Content.ReadAsStringAsync(cts.Token)))
            {
                Assert.Equal("success", body.RootElement.GetProperty("operationStatus").GetString());
                Assert.Equal("success", body.RootElement.GetProperty("resultCode").GetString());
            }

            using var modified = await fixture.Api.PatchAsJsonAsync("/api/v1/entries", new
            {
                dn = originalDn,
                changes = new[] { new { operation = "replace", name = "cn", values = new[] { "API Modified" } } },
            }, cts.Token);
            Assert.Equal(HttpStatusCode.OK, modified.StatusCode);

            using var renamed = await fixture.Api.PostAsJsonAsync("/api/v1/entries/rename", new
            {
                dn = originalDn,
                newRdn = "uid=api-renamed",
            }, cts.Token);
            Assert.Equal(HttpStatusCode.OK, renamed.StatusCode);

            using var password = await fixture.Api.PostAsJsonAsync("/api/v1/entries/password", new
            {
                dn = renamedDn,
                newPassword = "new-api-password",
            }, cts.Token);
            Assert.Equal(HttpStatusCode.OK, password.StatusCode);

            using var read = await GetJsonAsync(
                $"/api/v1/entries?dn={Uri.EscapeDataString(renamedDn)}&attributes=cn", cts.Token);
            Assert.Equal("API Modified", read.RootElement.GetProperty("entry")
                .GetProperty("attributes")[0].GetProperty("values")[0].GetString());

            using var deleted = await fixture.Api.DeleteAsync(
                $"/api/v1/entries?dn={Uri.EscapeDataString(renamedDn)}", cts.Token);
            Assert.Equal(HttpStatusCode.OK, deleted.StatusCode);
        }
        finally
        {
            await fixture.Directory.DeleteEntryAsync(originalDn, cts.Token);
            await fixture.Directory.DeleteEntryAsync(renamedDn, cts.Token);
        }
    }

    [Fact]
    public async Task Import_failure_preserves_progress_without_exposing_service_diagnostics()
    {
        using var cts = TestCancellation.Source();
        var first = fixture.DnUnder(Dn.Rdn("uid", "api-import-first"), "ou=people");
        var missing = fixture.DnUnder(Dn.Rdn("uid", "api-import-missing"), "ou=people");
        var last = fixture.DnUnder(Dn.Rdn("uid", "api-import-last"), "ou=people");
        var ldif =
            $"dn: {first}\nobjectClass: inetOrgPerson\nuid: api-import-first\ncn: First\nsn: First\n\n" +
            $"dn: {missing}\nchangetype: delete\n\n" +
            $"dn: {last}\nobjectClass: inetOrgPerson\nuid: api-import-last\ncn: Last\nsn: Last\n";

        try
        {
            using var response = await fixture.Api.PostAsJsonAsync("/api/v1/ldif/import", new { ldif }, cts.Token);
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
            Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
            var body = await response.Content.ReadAsStringAsync(cts.Token);
            using var problem = JsonDocument.Parse(body);
            Assert.Equal(1, problem.RootElement.GetProperty("applied").GetInt32());
            Assert.Equal(3, problem.RootElement.GetProperty("total").GetInt32());
            Assert.Equal(missing, problem.RootElement.GetProperty("failedDn").GetString());
            Assert.Equal("notFound", problem.RootElement.GetProperty("operationStatus").GetString());
            Assert.DoesNotContain("BindPassword", body, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            await fixture.Directory.DeleteEntryAsync(first, cts.Token);
            await fixture.Directory.DeleteEntryAsync(last, cts.Token);
        }
    }

    private async Task<JsonDocument> GetJsonAsync(string path, CancellationToken cancellationToken)
    {
        using var response = await fixture.Api.GetAsync(path, cancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
    }
}
