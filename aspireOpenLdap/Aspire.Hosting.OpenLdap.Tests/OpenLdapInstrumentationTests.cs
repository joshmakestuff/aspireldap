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
    [InlineData(ResultCode.Success, false)]
    [InlineData(ResultCode.CompareTrue, false)]
    [InlineData(ResultCode.CompareFalse, false)]
    [InlineData(ResultCode.Referral, false)]
    [InlineData(ResultCode.NoSuchObject, true)]
    [InlineData(ResultCode.OperationsError, true)]
    [InlineData(ResultCode.ProtocolError, true)]
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
    public void BuildMetricTags_AreLowCardinality()
    {
        var tags = OpenLdapInstrumentation.BuildMetricTags("search", "ldap.example.com", 389, ResultCode.Success, failure: null);
        var keys = tags.Select(t => t.Key).ToList();

        Assert.Contains("db.system.name", keys);
        Assert.Contains("db.operation.name", keys);
        Assert.Contains("server.address", keys);
        Assert.Contains("server.port", keys);
        Assert.Contains("db.response.status_code", keys);

        // High-cardinality / PII keys must never be metric tags.
        Assert.DoesNotContain("db.ldap.entries_returned", keys);
        Assert.DoesNotContain("db.ldap.controls", keys);
    }
}
