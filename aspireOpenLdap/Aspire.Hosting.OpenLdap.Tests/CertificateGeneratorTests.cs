using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Aspire.Hosting.ApplicationModel;
using Xunit;

namespace Aspire.Hosting.OpenLdap.Tests;

/// <summary>
/// The generated-certificate cache must only be reused when the full set (CA, server cert,
/// server key) is valid and mutually consistent — a corrupt or mismatched cached file used
/// to survive as "fresh" for up to two years as long as server.crt itself parsed.
/// </summary>
public class CertificateGeneratorTests : IDisposable
{
    private readonly string _appHostDir = Directory.CreateTempSubdirectory("aspire-openldap-certtest-").FullName;

    [Fact]
    public void Valid_Cached_Set_Is_Reused()
    {
        var first = OpenLdapCertificateGenerator.EnsureCertificates(_appHostDir, "ldap");
        var firstServerCert = File.ReadAllText(first.ServerCertPath);

        var second = OpenLdapCertificateGenerator.EnsureCertificates(_appHostDir, "ldap");

        Assert.Equal(first.ServerCertPath, second.ServerCertPath);
        Assert.Equal(firstServerCert, File.ReadAllText(second.ServerCertPath));
    }

    [Fact]
    public void Corrupt_Ca_Triggers_Regeneration()
    {
        var certs = OpenLdapCertificateGenerator.EnsureCertificates(_appHostDir, "ldap");
        File.WriteAllText(certs.CaCertPath, "not a certificate");

        var regenerated = OpenLdapCertificateGenerator.EnsureCertificates(_appHostDir, "ldap");

        Assert.NotEqual("not a certificate", File.ReadAllText(regenerated.CaCertPath));
        AssertConsistentSet(regenerated);
    }

    [Fact]
    public void Mismatched_Server_Key_Triggers_Regeneration()
    {
        var certs = OpenLdapCertificateGenerator.EnsureCertificates(_appHostDir, "ldap");
        var originalCert = File.ReadAllText(certs.ServerCertPath);

        using (var unrelatedKey = RSA.Create(2048))
        {
            File.WriteAllText(certs.ServerKeyPath, unrelatedKey.ExportRSAPrivateKeyPem());
        }

        var regenerated = OpenLdapCertificateGenerator.EnsureCertificates(_appHostDir, "ldap");

        Assert.NotEqual(originalCert, File.ReadAllText(regenerated.ServerCertPath));
        AssertConsistentSet(regenerated);
    }

    [Fact]
    public void Wrong_Root_Ca_Triggers_Regeneration()
    {
        var certs = OpenLdapCertificateGenerator.EnsureCertificates(_appHostDir, "ldap");
        var originalCert = File.ReadAllText(certs.ServerCertPath);

        // A parseable CA that did NOT sign the cached server certificate — the old
        // expiry-only check accepted this silently.
        using (var key = ECDsa.Create(ECCurve.NamedCurves.nistP256))
        {
            var request = new CertificateRequest("CN=Unrelated CA", key, HashAlgorithmName.SHA256);
            using var unrelatedCa = request.CreateSelfSigned(
                DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(2));
            File.WriteAllText(certs.CaCertPath, unrelatedCa.ExportCertificatePem());
        }

        var regenerated = OpenLdapCertificateGenerator.EnsureCertificates(_appHostDir, "ldap");

        Assert.NotEqual(originalCert, File.ReadAllText(regenerated.ServerCertPath));
        AssertConsistentSet(regenerated);
    }

    [Fact]
    public async Task Concurrent_EnsureCertificates_Yield_A_Consistent_Set()
    {
        var results = await Task.WhenAll(Enumerable.Range(0, 8)
            .Select(_ => Task.Run(() => OpenLdapCertificateGenerator.EnsureCertificates(_appHostDir, "ldap"))));

        Assert.All(results, AssertConsistentSet);
        Assert.Equal(results[0].CaCertPath, results[^1].CaCertPath);
    }

    [Fact]
    public async Task Concurrent_EnsureCertificates_Across_Processes_Yield_A_Consistent_Set()
    {
        // The in-process gate cannot serialize two real processes, so drive several worker
        // processes at the same directory, released together at a rendezvous, to exercise the
        // cross-process lock (AcquireLock) for real.
        const int workers = 4;
        var barrierDir = Path.Combine(_appHostDir, "barrier");
        Directory.CreateDirectory(barrierDir);
        var goPath = Path.Combine(barrierDir, "go");

        var tasks = Enumerable.Range(0, workers)
            .Select(i => Task.Run(() => RunWorker(Path.Combine(barrierDir, $"ready-{i}"), goPath)))
            .ToArray();

        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (Enumerable.Range(0, workers).Any(i => !File.Exists(Path.Combine(barrierDir, $"ready-{i}"))))
        {
            if (DateTime.UtcNow >= deadline)
            {
                Assert.Fail("workers did not reach the rendezvous");
            }

            await Task.Delay(20);
        }

        File.WriteAllText(goPath, string.Empty);
        await Task.WhenAll(tasks);

        // Validate the on-disk set directly — not via EnsureCertificates, which would
        // self-heal a mismatched set and mask the very race this test guards against.
        var certDir = Path.Combine(_appHostDir, "obj", "aspire-openldap-certs", "ldap");
        AssertConsistentSet(new OpenLdapCertificateGenerator.GeneratedCertificates(
            certDir,
            Path.Combine(certDir, "ca.crt"),
            Path.Combine(certDir, "server.crt"),
            Path.Combine(certDir, "server.key")));
    }

    private async Task RunWorker(string readyPath, string goPath)
    {
        var startInfo = new ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        startInfo.ArgumentList.Add("exec");
        startInfo.ArgumentList.Add(WorkerAssemblyPath);
        startInfo.ArgumentList.Add(_appHostDir);
        startInfo.ArgumentList.Add("ldap");
        startInfo.ArgumentList.Add(readyPath);
        startInfo.ArgumentList.Add(goPath);

        using var process = Process.Start(startInfo)!;
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();

        // The worker's own lock acquisition is bounded at 30 s; give it a generous ceiling so
        // a stuck process fails the test instead of hanging the run.
        if (!process.WaitForExit(TimeSpan.FromMinutes(2)))
        {
            process.Kill(entireProcessTree: true);
            Assert.Fail("worker process did not exit within 2 minutes");
        }

        var stdout = await stdoutTask;
        var stderr = await stderrTask;
        Assert.True(process.ExitCode == 0,
            $"worker failed (exit {process.ExitCode}):{Environment.NewLine}{stdout}{stderr}");
    }

    private static string WorkerAssemblyPath => Path.GetFullPath(
        typeof(CertificateGeneratorTests).Assembly
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .Single(a => a.Key == "WorkerAssemblyPath")
            .Value!);

    private static void AssertConsistentSet(OpenLdapCertificateGenerator.GeneratedCertificates certs)
    {
        // Pairing throws if the key doesn't match the certificate.
        using var serverCert = X509Certificate2.CreateFromPemFile(certs.ServerCertPath, certs.ServerKeyPath);
        using var caCert = Aspire.Hosting.OpenLdap.OpenLdapCertificateValidation.LoadPemCertificate(certs.CaCertPath);

        using var chain = new X509Chain();
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        chain.ChainPolicy.CustomTrustStore.Add(caCert);
        Assert.True(chain.Build(serverCert), "regenerated server certificate must chain to the regenerated CA");
    }

    public void Dispose() => Directory.Delete(_appHostDir, recursive: true);
}
