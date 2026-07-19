using System.Diagnostics;
using System.DirectoryServices.Protocols;
using Aspire.OpenLdap;
using Xunit;

namespace Aspire.Hosting.OpenLdap.Tests;

/// <summary>
/// Pure unit tests for the telemetry mapping helpers — no LDAP server required. These guard the
/// operation-name derivation, result-code classification, and (critically) that no PII is recorded.
/// </summary>
public class OpenLdapInstrumentationTests
{
    [Fact]
    public void OperationName_MapsKnownRequestTypes()
    {
        Assert.Equal("search", OpenLdapInstrumentation.OperationName(new SearchRequest()));
        Assert.Equal("add", OpenLdapInstrumentation.OperationName(new AddRequest()));
        Assert.Equal("modify", OpenLdapInstrumentation.OperationName(new ModifyRequest()));
        Assert.Equal("delete", OpenLdapInstrumentation.OperationName(new DeleteRequest()));
        Assert.Equal("modifydn", OpenLdapInstrumentation.OperationName(new ModifyDNRequest()));
        Assert.Equal("compare", OpenLdapInstrumentation.OperationName(new CompareRequest()));
        Assert.Equal("extended", OpenLdapInstrumentation.OperationName(new ExtendedRequest()));
    }

    [Theory]
    // Every explicitly non-error code gets its own row (dropping one from the switch must
    // fail here); a single representative covers the default error branch.
    [InlineData(ResultCode.Success, false)]
    [InlineData(ResultCode.CompareTrue, false)]
    [InlineData(ResultCode.CompareFalse, false)]
    [InlineData(ResultCode.Referral, false)]
    [InlineData(ResultCode.ReferralV2, false)]
    [InlineData(ResultCode.NoSuchObject, true)]
    public void IsErrorCode_ClassifiesResultCodes(ResultCode code, bool expectedError)
        => Assert.Equal(expectedError, OpenLdapInstrumentation.IsErrorCode(code));

    [Fact]
    public void IsErrorCode_NullIsNotError() => Assert.False(OpenLdapInstrumentation.IsErrorCode(null));

    [Fact]
    public void ControlOids_NullWhenNoControls()
        => Assert.Null(OpenLdapInstrumentation.ControlOids(new SearchRequest().Controls));

    [Fact]
    public void ControlOids_ReturnsPagingOid()
    {
        var request = new SearchRequest();
        request.Controls.Add(new PageResultRequestControl(500));

        // 1.2.840.113556.1.4.319 is the paged-results control OID.
        Assert.Equal("1.2.840.113556.1.4.319", OpenLdapInstrumentation.ControlOids(request.Controls));
    }

    [Fact]
    public void SetRequestTags_RecordsMetadata_ButNeverPii()
    {
        using var activity = new Activity("test");
        activity.Start();

        // Deliberately put PII in the filter and base DN.
        var request = new SearchRequest(
            "ou=people,dc=example,dc=org", "(uid=secret-user)", SearchScope.Subtree, attributeList: null);
        request.Controls.Add(new PageResultRequestControl(500));

        OpenLdapInstrumentation.SetRequestTags(activity, request, "search", "ldap.example.com", 389);

        // EXACT request-tag key set: any new tag (a filter, a base DN, an attribute list)
        // fails here even before the value-level PII guard below.
        Assert.Equal(
            ["db.system.name", "db.operation.name", "server.address", "server.port", "db.ldap.scope", "db.ldap.controls"],
            activity.TagObjects.Select(t => t.Key).ToArray());

        Assert.Equal("openldap", activity.GetTagItem("db.system.name"));
        Assert.Equal("search", activity.GetTagItem("db.operation.name"));
        Assert.Equal("ldap.example.com", activity.GetTagItem("server.address"));
        Assert.Equal(389, Assert.IsType<int>(activity.GetTagItem("server.port")));
        Assert.Equal("subtree", activity.GetTagItem("db.ldap.scope"));
        Assert.Equal("1.2.840.113556.1.4.319", activity.GetTagItem("db.ldap.controls"));

        // PII guard: neither the search filter nor the base DN may appear in any tag.
        foreach (var tag in activity.TagObjects)
        {
            var value = tag.Value?.ToString() ?? string.Empty;
            Assert.DoesNotContain("secret-user", value);
            Assert.DoesNotContain("ou=people", value);
        }
    }

    [Fact]
    public void SetResponseTags_SetsErrorStatus_ForFailureCode()
    {
        using var activity = new Activity("test");
        activity.Start();

        OpenLdapInstrumentation.SetResponseTags(activity, response: null, ResultCode.NoSuchObject, failure: null);

        Assert.Equal(ActivityStatusCode.Error, activity.Status);
        Assert.Equal((int)ResultCode.NoSuchObject, activity.GetTagItem("db.response.status_code"));
        Assert.Equal("NoSuchObject", activity.GetTagItem("db.ldap.result_code"));
    }

    [Fact]
    public void SetResponseTags_RecordsExceptionType_ButNeverItsMessage()
    {
        using var activity = new Activity("test");
        activity.Start();

        // Server-supplied diagnostics routinely embed DNs; none of this text may be exported.
        var failure = new DirectoryOperationException(
            "No such object: cn=sentinel-user,ou=people,dc=example,dc=org");

        OpenLdapInstrumentation.SetResponseTags(activity, response: null, code: null, failure);

        Assert.Equal(ActivityStatusCode.Error, activity.Status);
        Assert.Equal(typeof(DirectoryOperationException).FullName, activity.GetTagItem("error.type"));

        // EXACT response-tag key set for the exception path: the type tag and nothing else.
        Assert.Equal(["error.type"], activity.TagObjects.Select(t => t.Key).ToArray());

        Assert.Empty(activity.Events);
        foreach (var tag in activity.TagObjects)
        {
            Assert.DoesNotContain("sentinel-user", tag.Value?.ToString() ?? string.Empty);
        }
    }

    [Fact]
    public void BuildMetricTags_AreLowCardinality()
    {
        // EXACT allowlists, not per-key Contains: adding any tag — a DN, a filter, a
        // control value, an exception message — changes the key set and fails this test.
        var success = OpenLdapInstrumentation.BuildMetricTags(
            "search", "ldap.example.com", 389, ResultCode.Success, failure: null);
        Assert.Equal(
            ["db.system.name", "db.operation.name", "server.address", "server.port", "db.response.status_code"],
            success.Select(t => t.Key).ToArray());
        Assert.Equal("openldap", success.Single(t => t.Key == "db.system.name").Value);
        Assert.Equal("search", success.Single(t => t.Key == "db.operation.name").Value);
        Assert.Equal("ldap.example.com", success.Single(t => t.Key == "server.address").Value);
        Assert.Equal(389, success.Single(t => t.Key == "server.port").Value);
        Assert.Equal((int)ResultCode.Success, success.Single(t => t.Key == "db.response.status_code").Value);

        // Failure path: the exception contributes its TYPE as error.type and nothing else —
        // the sentinel DN in its message must not reach any tag value.
        const string sentinel = "cn=sentinel-user,ou=people,dc=example,dc=org";
        var failure = OpenLdapInstrumentation.BuildMetricTags(
            "search", "ldap.example.com", 389, code: null,
            new System.DirectoryServices.Protocols.DirectoryOperationException($"No such object: {sentinel}"));
        Assert.Equal(
            ["db.system.name", "db.operation.name", "server.address", "server.port", "error.type"],
            failure.Select(t => t.Key).ToArray());
        Assert.Equal(typeof(DirectoryOperationException).FullName, failure.Single(t => t.Key == "error.type").Value);
        Assert.All(failure, t => Assert.DoesNotContain(sentinel, t.Value?.ToString() ?? string.Empty));
    }
}
