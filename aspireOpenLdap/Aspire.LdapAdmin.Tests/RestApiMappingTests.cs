using System.DirectoryServices.Protocols;
using System.Text.Json;
using Aspire.LdapAdmin.Core;
using Aspire.LdapAdmin.Web;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Aspire.LdapAdmin.Tests;

public sealed class RestApiMappingTests
{
    [Fact]
    public async Task Api_is_not_mapped_until_the_setting_is_enabled()
    {
        await using var disabled = BuildApp();
        Assert.False(LdapAdminApi.Map(disabled, new LdapAdminSettings()));
        Assert.Empty(ApiEndpoints(disabled));

        await using var enabled = BuildApp();
        Assert.True(LdapAdminApi.Map(enabled, new LdapAdminSettings { EnableRestApi = true }));

        Assert.Equal(
            [
                "DELETE /api/v1/entries",
                "GET /api/v1/directory",
                "GET /api/v1/directory/children",
                "GET /api/v1/entries",
                "GET /api/v1/ldif/export",
                "GET /api/v1/schema",
                "PATCH /api/v1/entries",
                "POST /api/v1/directory/search",
                "POST /api/v1/entries",
                "POST /api/v1/entries/password",
                "POST /api/v1/entries/rename",
                "POST /api/v1/ldif/import",
                "POST /api/v1/ldif/plan",
            ],
            ApiEndpoints(enabled).Order(StringComparer.Ordinal));
    }

    [Theory]
    [InlineData(LdapOperationStatus.NotFound, 404, "not-found")]
    [InlineData(LdapOperationStatus.AlreadyExists, 409, "already-exists")]
    [InlineData(LdapOperationStatus.AccessDenied, 403, "access-denied")]
    [InlineData(LdapOperationStatus.NotAllowedOnNonLeaf, 409, "non-leaf")]
    [InlineData(LdapOperationStatus.SchemaViolation, 422, "schema-violation")]
    [InlineData(LdapOperationStatus.ConstraintViolation, 409, "constraint-violation")]
    [InlineData(LdapOperationStatus.InvalidRequest, 400, "invalid-request")]
    [InlineData(LdapOperationStatus.Refused, 422, "refused")]
    [InlineData(LdapOperationStatus.Cancelled, 499, "cancelled")]
    [InlineData(LdapOperationStatus.Failed, 502, "failed")]
    public async Task Every_failure_status_has_a_stable_problem_mapping(
        LdapOperationStatus status,
        int expectedHttpStatus,
        string expectedTypeSuffix)
    {
        var result = new LdapOperationResult(
            status,
            ResultCode.Busy,
            "BindPassword=do-not-return; submitted-password; System.Exception: stack",
            DeletedCount: 7);

        var response = await ExecuteAsync(LdapAdminApi.ToProblem(result));
        using var document = JsonDocument.Parse(response.Body);
        var root = document.RootElement;

        Assert.Equal(expectedHttpStatus, response.StatusCode);
        Assert.Equal($"urn:aspireldap:problem:ldap:{expectedTypeSuffix}", root.GetProperty("type").GetString());
        Assert.Equal(ToWireName(status), root.GetProperty("operationStatus").GetString());
        Assert.Equal("busy", root.GetProperty("resultCode").GetString());
        Assert.Equal(7, root.GetProperty("deletedCount").GetInt32());
        Assert.DoesNotContain("do-not-return", response.Body, StringComparison.Ordinal);
        Assert.DoesNotContain("submitted-password", response.Body, StringComparison.Ordinal);
        Assert.DoesNotContain("System.Exception", response.Body, StringComparison.Ordinal);
    }

    private static WebApplication BuildApp()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Services.AddProblemDetails();
        builder.Services.AddSingleton<LdapDirectoryService>(static _ => null!);
        builder.Services.AddSingleton<LdapSchemaService>(static _ => null!);
        builder.Services.AddSingleton<LdapLdifService>(static _ => null!);
        return builder.Build();
    }

    private static IEnumerable<string> ApiEndpoints(WebApplication app) =>
        ((IEndpointRouteBuilder)app).DataSources
            .SelectMany(static source => source.Endpoints)
            .OfType<RouteEndpoint>()
            .Select(static endpoint =>
            {
                var method = Assert.Single(endpoint.Metadata.GetMetadata<HttpMethodMetadata>()!.HttpMethods);
                return $"{method} {endpoint.RoutePattern.RawText}";
            });

    private static async Task<(int StatusCode, string Body)> ExecuteAsync(IResult result)
    {
        var services = new ServiceCollection().AddLogging().AddProblemDetails().BuildServiceProvider();
        await using var body = new MemoryStream();
        var context = new DefaultHttpContext { RequestServices = services };
        context.Response.Body = body;

        await result.ExecuteAsync(context);
        body.Position = 0;
        using var reader = new StreamReader(body);
        return (context.Response.StatusCode, await reader.ReadToEndAsync());
    }

    private static string ToWireName(LdapOperationStatus status)
    {
        var name = status.ToString();
        return char.ToLowerInvariant(name[0]) + name[1..];
    }
}
