using Aspire.Hosting.ApplicationModel;

// Cross-process worker for OpenLdapCertificateGenerator concurrency tests. Rendezvous on
// <readyPath>/<goPath> so every worker reaches EnsureCertificates at the same instant,
// forcing real cross-process contention on AcquireLock.
if (args.Length != 4)
{
    Console.Error.WriteLine("usage: worker <appHostDir> <resourceName> <readyPath> <goPath>");
    return 1;
}

File.WriteAllText(args[2], string.Empty);

var deadline = DateTime.UtcNow.AddSeconds(30);
while (!File.Exists(args[3]))
{
    if (DateTime.UtcNow >= deadline)
    {
        Console.Error.WriteLine("timed out waiting for the go signal");
        return 2;
    }

    Thread.Sleep(20);
}

OpenLdapCertificateGenerator.EnsureCertificates(args[0], args[1]);
return 0;
