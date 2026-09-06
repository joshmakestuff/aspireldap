using System.Diagnostics;
using System.IO.Compression;
using Xunit;
using Xunit.Abstractions;

namespace Aspire.Hosting.OpenLdap.Tests;

/// <summary>Package production must restore its private payload without Docker or a prior solution build.</summary>
[Trait("Category", "Packaging")]
public sealed class ColdHostingPackTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData("Debug")]
    [InlineData("Release")]
    public async Task Hosting_Only_Pack_Restores_Cold_Admin_Graph(string configuration)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(10));
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "AspireOpenLdap.slnx")))
        {
            root = root.Parent;
        }
        Assert.NotNull(root?.Parent);
        var repository = root.Parent.FullName;
        var workspace = Directory.CreateTempSubdirectory("aspireldap-coldpack-");
        try
        {
            var source = Path.Combine(workspace.FullName, "source");
            // Copy current files, including unstaged and untracked source. No ignored bin/obj
            // or workspace output can warm this build. SourceLink is disabled for this snapshot.
            var files = await RunAsync("git", repository, ["ls-files", "--cached", "--others", "--exclude-standard", "-z"], cts.Token);
            foreach (var relative in files.Split('\0', StringSplitOptions.RemoveEmptyEntries).Distinct(StringComparer.Ordinal))
            {
                var original = Path.Combine(repository, relative);
                if (!File.Exists(original))
                {
                    continue;
                }
                var destination = Path.Combine(source, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                File.Copy(original, destination);
            }
            Assert.Empty(Directory.EnumerateDirectories(source, "obj", SearchOption.AllDirectories));
            Assert.Empty(Directory.EnumerateDirectories(source, "bin", SearchOption.AllDirectories));
            const string version = "0.0.1-coldpack";
            var feed = Path.Combine(workspace.FullName, "feed");
            await RunAsync("dotnet", source,
                ["pack", "aspireOpenLdap/Aspire.Hosting.OpenLdap/Aspire.Hosting.OpenLdap.csproj",
                 "-c", configuration, $"-p:Version={version}", "-p:ContinuousIntegrationBuild=true",
                 "-p:EnableSourceControlManagerQueries=false", "-p:EnableSourceLink=false", "-warnaserror", "-o", feed], cts.Token);

            foreach (var project in new[] { "Aspire.LdapAdmin.Web", "Aspire.LdapAdmin.Core", "Aspire.OpenLdap" })
            {
                Assert.True(File.Exists(Path.Combine(source, "aspireOpenLdap", project, "obj", "project.assets.json")), project);
                var assembly = Path.Combine(source, "aspireOpenLdap", project, "bin", configuration, "net10.0", project + ".dll");
                Assert.True(File.Exists(assembly), assembly);
                Assert.StartsWith(version, FileVersionInfo.GetVersionInfo(assembly).ProductVersion, StringComparison.Ordinal);
            }
            using var package = ZipFile.OpenRead(Assert.Single(Directory.GetFiles(feed, "*.nupkg")));
            foreach (var payload in new[]
            {
                "Dockerfile", "app/Aspire.LdapAdmin.Web.dll", "app/Aspire.LdapAdmin.Core.dll",
                "app/Aspire.OpenLdap.dll", "app/Aspire.LdapAdmin.Web.deps.json",
                "app/Aspire.LdapAdmin.Web.runtimeconfig.json", "app/wwwroot/css/console.css",
            })
            {
                var entry = package.GetEntry("contentFiles/any/any/ldapadmin/" + payload);
                Assert.NotNull(entry);
                Assert.True(entry.Length > 0, payload);
            }
            Assert.DoesNotContain(package.Entries, entry => entry.FullName.StartsWith("lib/", StringComparison.Ordinal)
                && entry.FullName.Contains("LdapAdmin", StringComparison.Ordinal));
        }
        finally
        {
            workspace.Delete(recursive: true);
        }
    }

    private async Task<string> RunAsync(string executable, string directory, string[] arguments, CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo(executable)
        {
            WorkingDirectory = directory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }
        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderr = process.StandardError.ReadToEndAsync(cancellationToken);
        try
        {
            await process.WaitForExitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
            await process.WaitForExitAsync(CancellationToken.None);
            throw;
        }
        var result = await stdout;
        var errors = await stderr;
        if (executable == "dotnet")
        {
            output.WriteLine(result + errors);
        }
        Assert.True(process.ExitCode == 0, $"{executable} exited {process.ExitCode}:\n{result}\n{errors}");
        return result;
    }
}
