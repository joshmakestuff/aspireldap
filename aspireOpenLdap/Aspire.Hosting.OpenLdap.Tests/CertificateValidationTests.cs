using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Xunit;
using CertificateValidation = Aspire.OpenLdap.OpenLdapCertificateValidation;

namespace Aspire.Hosting.OpenLdap.Tests;

public class CertificateValidationTests
{
    private static X509Certificate2 CreateCa(string cn = "CN=Test CA")
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest(cn, key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        return request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddDays(30));
    }

    private static X509Certificate2 CreateLeaf(
        X509Certificate2 ca,
        string[] dnsNames,
        IPAddress[]? ipAddresses = null,
        string subjectCn = "CN=leaf")
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest(subjectCn, key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, false));

        if (dnsNames.Length > 0 || ipAddresses is { Length: > 0 })
        {
            var san = new SubjectAlternativeNameBuilder();
            foreach (var dns in dnsNames)
            {
                san.AddDnsName(dns);
            }
            foreach (var ip in ipAddresses ?? [])
            {
                san.AddIpAddress(ip);
            }
            request.CertificateExtensions.Add(san.Build());
        }

        var serial = new byte[8];
        RandomNumberGenerator.Fill(serial);
        return request.Create(ca, DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddDays(20), serial);
    }

    [Fact]
    public void Correct_Chain_And_Host_Is_Accepted()
    {
        using var ca = CreateCa();
        using var leaf = CreateLeaf(ca, ["ldap.example.org"]);

        Assert.True(CertificateValidation.ValidateAgainstCustomRoot(leaf, ca, "ldap.example.org"));
    }

    [Fact]
    public void Correct_Chain_But_Wrong_Host_Is_Rejected()
    {
        using var ca = CreateCa();
        using var leaf = CreateLeaf(ca, ["ldap.example.org"]);

        Assert.False(CertificateValidation.ValidateAgainstCustomRoot(leaf, ca, "other.example.org"));
    }

    [Fact]
    public void Wrong_Chain_With_Correct_Host_Is_Rejected()
    {
        using var ca = CreateCa();
        using var otherCa = CreateCa("CN=Other CA");
        using var leaf = CreateLeaf(otherCa, ["ldap.example.org"]);

        Assert.False(CertificateValidation.ValidateAgainstCustomRoot(leaf, ca, "ldap.example.org"));
    }

    [Fact]
    public void Explicit_Null_Host_Skips_Hostname_Validation_Only()
    {
        using var ca = CreateCa();
        using var otherCa = CreateCa("CN=Other CA");
        using var leaf = CreateLeaf(ca, ["ldap.example.org"]);
        using var wrongChainLeaf = CreateLeaf(otherCa, ["ldap.example.org"]);

        Assert.True(CertificateValidation.ValidateAgainstCustomRoot(leaf, ca, expectedHost: null));
        Assert.False(CertificateValidation.ValidateAgainstCustomRoot(wrongChainLeaf, ca, expectedHost: null));
    }

    [Fact]
    public void Ip_Address_Sans_Match_Ip_Hosts()
    {
        using var ca = CreateCa();
        using var leaf = CreateLeaf(ca, [], [IPAddress.Loopback, IPAddress.IPv6Loopback]);

        Assert.True(CertificateValidation.MatchesHost(leaf, "127.0.0.1"));
        Assert.True(CertificateValidation.MatchesHost(leaf, "::1"));
        Assert.True(CertificateValidation.MatchesHost(leaf, "[::1]"));
        Assert.False(CertificateValidation.MatchesHost(leaf, "10.0.0.1"));
        Assert.False(CertificateValidation.MatchesHost(leaf, "localhost"));
    }

    [Theory]
    [InlineData("a.example.org", true)]
    [InlineData("A.EXAMPLE.ORG", true)]
    [InlineData("example.org", false)]
    [InlineData("a.b.example.org", false)]
    public void Wildcard_Sans_Match_Exactly_One_Label(string host, bool expected)
    {
        using var ca = CreateCa();
        using var leaf = CreateLeaf(ca, ["*.example.org"]);

        Assert.Equal(expected, CertificateValidation.MatchesHost(leaf, host));
    }

    [Fact]
    public void Cn_Fallback_Applies_Only_Without_San()
    {
        using var ca = CreateCa();
        using var noSanLeaf = CreateLeaf(ca, [], subjectCn: "CN=ldap.example.org");
        using var sanLeaf = CreateLeaf(ca, ["other.example.org"], subjectCn: "CN=ldap.example.org");

        Assert.True(CertificateValidation.MatchesHost(noSanLeaf, "ldap.example.org"));
        // When a SAN exists the CN must be ignored (RFC 6125).
        Assert.False(CertificateValidation.MatchesHost(sanLeaf, "ldap.example.org"));
    }
}
