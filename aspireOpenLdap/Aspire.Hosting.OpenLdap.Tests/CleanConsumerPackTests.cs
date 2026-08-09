using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Xunit;
using Xunit.Abstractions;

namespace Aspire.Hosting.OpenLdap.Tests;

/// <summary>
/// The clean-consumer test (#82): packs JoshMakeStuff.Aspire.Hosting.OpenLdap, restores the pack
/// into a minimal consumer AppHost scaffolded in an isolated temp workspace (no project
/// references, no source-checkout paths), and runs <c>AddOpenLdap(...).WithLdapAdmin()</c> end to
/// end — proving the packed artifact alone delivers the OpenLDAP build context, the admin
/// container payload (#78), and a working admin→LDAP path over required TLS.
/// </summary>
/// <remarks>
/// <para>
/// The consumer program does its own verification (resource health, the admin's <c>/health</c>
/// LDAP round trip, the home page) and reports through <c>CLEANCONSUMER</c> output markers plus
/// its exit code; the test asserts on those. The checkout is used only to run <c>dotnet pack</c>
/// — every path the consumer resolves comes from the restored package.
/// </para>
/// <para>
/// Category=CleanConsumer (not Integration): CI runs it as its own publish-gating job, and both
/// suite filters plus the stryker configs exclude it. It joins <see cref="AppHostCollection"/>
/// and holds <see cref="DockerHostGate"/> for the consumer's full run — the run boots a DCP host
/// and builds two images, exactly the overlap the gate exists to prevent (#54).
/// </para>
/// </remarks>
[Collection(AppHostCollection.Name)]
[Trait("Category", "CleanConsumer")]
public sealed class CleanConsumerPackTests(ITestOutputHelper output)
{
    [Fact]
    public async Task Packed_Artifact_Delivers_WithLdapAdmin_To_A_Clean_Consumer()
    {
        // Cold runs build the OpenLDAP and admin images from scratch on top of a full restore.
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(25));
        var cancellationToken = cts.Token;

        var solutionDir = FindSolutionDirectory();
        var workspace = Directory.CreateTempSubdirectory("aspireldap-cleanconsumer-");
        try
        {
            // Unique prerelease version per run: the global NuGet cache can never satisfy the
            // consumer's pin with a stale payload from an earlier run.
            var version = $"0.0.1-clean{DateTime.UtcNow:yyyyMMddHHmmssfff}";
            var feedDir = Path.Combine(workspace.FullName, "feed");
            var consumerDir = Path.Combine(workspace.FullName, "consumer");
            Directory.CreateDirectory(feedDir);
            Directory.CreateDirectory(consumerDir);

            var pack = await RunDotnetAsync(
                solutionDir,
                [
                    "pack",
                    Path.Combine(solutionDir, "Aspire.Hosting.OpenLdap", "Aspire.Hosting.OpenLdap.csproj"),
                    "-c", "Release",
                    $"-p:Version={version}",
                    "-o", feedDir,
                ],
                cancellationToken);
            Assert.True(pack.ExitCode == 0, $"dotnet pack failed:{Environment.NewLine}{pack.Output}");

            ScaffoldConsumer(consumerDir, feedDir, version, ReadAppHostSdkVersion(solutionDir));

            // The consumer's AppHost run boots a DCP host and builds the bundled OpenLDAP and
            // admin images — hold the docker gate for the whole run, like every other AppHost.
            DockerResult run;
            using (await DockerHostGate.AcquireAsync(cancellationToken))
            {
                run = await RunDotnetAsync(consumerDir, ["run", "-c", "Release"], cancellationToken);
            }

            output.WriteLine(run.Output);
            Assert.True(run.ExitCode == 0, $"clean consumer exited {run.ExitCode}:{Environment.NewLine}{run.Output}");
            Assert.Contains("CLEANCONSUMER resource ldap=Healthy", run.Output, StringComparison.Ordinal);
            Assert.Contains("CLEANCONSUMER resource ldap-ldapadmin=Healthy", run.Output, StringComparison.Ordinal);
            Assert.Contains("CLEANCONSUMER health=200:Healthy", run.Output, StringComparison.Ordinal);
            Assert.Contains("CLEANCONSUMER home=200", run.Output, StringComparison.Ordinal);
            Assert.Contains("CLEANCONSUMER OK", run.Output, StringComparison.Ordinal);
        }
        finally
        {
            TryDelete(workspace);
        }
    }

    /// <summary>
    /// The consumer workspace: a nuget.config whose only sources are the just-packed local feed
    /// (for this package) and nuget.org (for everything else), the same Aspire.AppHost.Sdk pin
    /// the repo builds with, and an AppHost whose program verifies the running resources itself.
    /// Nothing in it references the checkout.
    /// </summary>
    private static void ScaffoldConsumer(string consumerDir, string feedDir, string version, string sdkVersion)
    {
        File.WriteAllText(Path.Combine(consumerDir, "nuget.config"), $"""
            <?xml version="1.0" encoding="utf-8"?>
            <configuration>
              <packageSources>
                <clear />
                <add key="local-pack" value="{feedDir}" />
                <add key="nuget.org" value="https://api.nuget.org/v3/index.json" protocolVersion="3" />
              </packageSources>
              <packageSourceMapping>
                <packageSource key="local-pack">
                  <package pattern="JoshMakeStuff.Aspire.Hosting.OpenLdap" />
                </packageSource>
                <packageSource key="nuget.org">
                  <package pattern="*" />
                </packageSource>
              </packageSourceMapping>
            </configuration>
            """);

        File.WriteAllText(Path.Combine(consumerDir, "global.json"), $$"""
            {
              "msbuild-sdks": {
                "Aspire.AppHost.Sdk": "{{sdkVersion}}"
              }
            }
            """);

        File.WriteAllText(Path.Combine(consumerDir, "CleanConsumer.csproj"), $"""
            <Project Sdk="Aspire.AppHost.Sdk">

              <PropertyGroup>
                <OutputType>Exe</OutputType>
                <TargetFramework>net10.0</TargetFramework>
                <ImplicitUsings>enable</ImplicitUsings>
                <Nullable>enable</Nullable>
              </PropertyGroup>

              <ItemGroup>
                <PackageReference Include="JoshMakeStuff.Aspire.Hosting.OpenLdap" Version="{version}" />
              </ItemGroup>

            </Project>
            """);

        File.WriteAllText(Path.Combine(consumerDir, "Program.cs"), """
            using Aspire.Hosting;
            using Aspire.Hosting.ApplicationModel;
            using Microsoft.Extensions.DependencyInjection;

            var builder = DistributedApplication.CreateBuilder(new DistributedApplicationOptions
            {
                Args = args,
                DisableDashboard = true,
            });

            // Required TLS is the strictest arm the admin supports: the directory refuses plain
            // LDAP, so the admin must reach it over LDAPS (encrypted, unverified in-container —
            // see WithLdapAdmin's remarks).
            builder.AddOpenLdap("ldap")
                .WithOrganizationalUnit("people")
                .WithUser("alice", "alice-password", ou: "people")
                .WithTls()
                .WithRequiredTls()
                .WithLdapAdmin();

            var app = builder.Build();

            using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(20));
            var ct = cts.Token;
            try
            {
                await app.StartAsync(ct);
                await app.ResourceNotifications.WaitForResourceHealthyAsync("ldap", ct);
                Console.WriteLine("CLEANCONSUMER resource ldap=Healthy");
                // Healthy admin = its /health answered 200, i.e. an admin bind + root-DSE search
                // against the directory succeeded from inside the admin container.
                await app.ResourceNotifications.WaitForResourceHealthyAsync("ldap-ldapadmin", ct);
                Console.WriteLine("CLEANCONSUMER resource ldap-ldapadmin=Healthy");

                var admin = app.Services.GetRequiredService<DistributedApplicationModel>()
                    .Resources.OfType<LdapAdminResource>().Single();
                var baseUrl = new Uri(new EndpointReference(admin, "http").Url);
                using var http = new HttpClient();

                // Witness the same LDAP round trip directly, plus the rendered home page.
                var health = await http.GetAsync(new Uri(baseUrl, "/health"), ct);
                var healthBody = await health.Content.ReadAsStringAsync(ct);
                Console.WriteLine($"CLEANCONSUMER health={(int)health.StatusCode}:{healthBody}");
                var home = await http.GetAsync(baseUrl, ct);
                Console.WriteLine($"CLEANCONSUMER home={(int)home.StatusCode}");

                var ok = (int)health.StatusCode == 200
                    && string.Equals(healthBody, "Healthy", StringComparison.Ordinal)
                    && (int)home.StatusCode == 200;
                Console.WriteLine(ok ? "CLEANCONSUMER OK" : "CLEANCONSUMER FAIL");
                await app.StopAsync(ct);
                return ok ? 0 : 1;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"CLEANCONSUMER FAIL {ex}");
                return 1;
            }
            """);
    }

    /// <summary>
    /// Walks up from the test output directory to the aspireOpenLdap solution directory. Used
    /// only to invoke pack and to read the SDK pin — never to feed paths into the consumer.
    /// </summary>
    private static string FindSolutionDirectory()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "AspireOpenLdap.slnx")))
            {
                return dir.FullName;
            }
            dir = dir.Parent;
        }
        throw new InvalidOperationException("AspireOpenLdap.slnx not found above the test output directory.");
    }

    /// <summary>The Aspire.AppHost.Sdk version the repo pins — reused so the consumer cannot drift.</summary>
    private static string ReadAppHostSdkVersion(string solutionDir)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(solutionDir, "global.json")));
        return doc.RootElement.GetProperty("msbuild-sdks").GetProperty("Aspire.AppHost.Sdk").GetString()
            ?? throw new InvalidOperationException("Aspire.AppHost.Sdk version missing from global.json.");
    }

    private static async Task<DockerResult> RunDotnetAsync(
        string workingDirectory, string[] args, CancellationToken cancellationToken)
    {
        var psi = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var arg in args)
        {
            psi.ArgumentList.Add(arg);
        }

        using var process = Process.Start(psi)!;
        var stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderr = process.StandardError.ReadToEndAsync(cancellationToken);
        try
        {
            await process.WaitForExitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            // A timed-out consumer run must not leave the AppHost (and its containers) running.
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }
            }
            catch (Exception)
            {
                // The process may have exited between the check and the kill.
            }
            throw;
        }

        var buffer = new StringBuilder();
        buffer.AppendLine(await stdout);
        buffer.AppendLine(await stderr);
        return new DockerResult(process.ExitCode, buffer.ToString());
    }

    private static void TryDelete(DirectoryInfo directory)
    {
        try
        {
            directory.Delete(recursive: true);
        }
        catch (Exception)
        {
            // Best effort: a straggling file lock must not fail a passing test.
        }
    }
}
