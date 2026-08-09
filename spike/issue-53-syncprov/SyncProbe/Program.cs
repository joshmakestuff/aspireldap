// SPIKE CODE — issue #53. Throwaway quality on purpose. Not shipped, not referenced by the solution.
//
// Question under probe: does System.DirectoryServices.Protocols expose enough to drive
// RFC 4533 refreshAndPersist (sync request control + persist-stage notifications) from .NET,
// or does it need hand-rolled control encoding / a different client stack?
//
// Usage: dotnet run -- <host> <port> <bindDn> <password> <baseDn>

using System.Diagnostics;
using System.DirectoryServices.Protocols;
using System.Formats.Asn1;
using System.Net;
using System.Text;

namespace Spike53;

internal static class Program
{
    private const string SyncRequestOid = "1.3.6.1.4.1.4203.1.9.1.1";
    private const string SyncStateOid = "1.3.6.1.4.1.4203.1.9.1.2";
    private const string SyncDoneOid = "1.3.6.1.4.1.4203.1.9.1.3";
    private const string SyncInfoOid = "1.3.6.1.4.1.4203.1.9.1.4";

    private static int Main(string[] args)
    {
        var host = args.ElementAtOrDefault(0) ?? "localhost";
        var port = int.Parse(args.ElementAtOrDefault(1) ?? "13389");
        var bindDn = args.ElementAtOrDefault(2) ?? "cn=admin,dc=spike,dc=test";
        var password = args.ElementAtOrDefault(3) ?? "adminpass";
        var baseDn = args.ElementAtOrDefault(4) ?? "dc=spike,dc=test";

        // Unique per run: a stale value left in the directory by an earlier run must not be
        // mistaken for this run's persist-stage notification.
        var marker = $"SPIKE53-PERSIST-{Guid.NewGuid():N}";

        Log($"probe start  host={host}:{port} base={baseDn} runtime={Environment.Version} os={Environment.OSVersion}");

        using var conn = new LdapConnection(new LdapDirectoryIdentifier(host, port))
        {
            AuthType = AuthType.Basic,
            Credential = new NetworkCredential(bindDn, password),
        };
        conn.SessionOptions.ProtocolVersion = 3;
        conn.Timeout = TimeSpan.FromMinutes(5);
        conn.Bind();
        Log("bind ok");

        // ---- STEP 1: hand-encode syncRequestValue --------------------------------------
        // RFC 4533 2.2:
        //   syncRequestValue ::= SEQUENCE {
        //       mode        ENUMERATED { refreshOnly (1), refreshAndPersist (3) },
        //       cookie      syncCookie OPTIONAL,   -- OCTET STRING
        //       reloadHint  BOOLEAN DEFAULT FALSE }
        // There is NO built-in DirectoryControl subclass for this in SDP, so the value is
        // written by hand with System.Formats.Asn1.
        var w = new AsnWriter(AsnEncodingRules.BER);
        using (w.PushSequence())
        {
            w.WriteEnumeratedValue(SyncRequestMode.RefreshAndPersist);
        }
        var syncRequestValue = w.Encode();
        Log($"syncRequestValue BER = {Convert.ToHexString(syncRequestValue)}");

        var syncControl = new DirectoryControl(SyncRequestOid, syncRequestValue, isCritical: true, serverSide: true);

        var request = new SearchRequest(baseDn, "(objectClass=*)", SearchScope.Subtree, "cn", "description", "entryUUID");
        request.Controls.Add(syncControl);
        request.TimeLimit = TimeSpan.Zero;

        // ---- STEP 2: can SDP keep the search open and hand back partial results? --------
        var entries = 0;
        var stateControls = 0;
        var mutationSeen = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var sw = Stopwatch.StartNew();

        void OnPartial(PartialResultsCollection partial)
        {
            foreach (var obj in partial)
            {
                switch (obj)
                {
                    case SearchResultEntry e:
                        entries++;
                        var ctlNames = string.Join(",", e.Controls.Select(c => c.Type));
                        var state = e.Controls.FirstOrDefault(c =>
                            string.Equals(c.Type, SyncStateOid, StringComparison.Ordinal));
                        if (state is not null)
                        {
                            stateControls++;
                        }
                        var desc = e.Attributes.Contains("description")
                            ? e.Attributes["description"][0]?.ToString()
                            : null;
                        Log($"[{sw.Elapsed:mm\\:ss\\.ff}] ENTRY dn={e.DistinguishedName} attrs={e.Attributes.Count} controls=[{ctlNames}] " +
                            $"syncState={(state is null ? "<none>" : DecodeSyncState(state.GetValue()))} description={desc}");
                        if (string.Equals(desc, marker, StringComparison.Ordinal))
                        {
                            mutationSeen.TrySetResult(desc);
                        }
                        break;
                    case SearchResultReference r:
                        Log($"[{sw.Elapsed:mm\\:ss\\.ff}] REFERENCE {string.Join(",", r.Reference)}");
                        break;
                    default:
                        // If SDP ever surfaces an intermediate response (SyncInfoMessage) it would
                        // land here. Recording the concrete type is the whole point.
                        Log($"[{sw.Elapsed:mm\\:ss\\.ff}] OTHER partial object type={obj.GetType().FullName} value={obj}");
                        break;
                }
            }
        }

        var callbackHits = 0;
        var async = conn.BeginSendRequest(
            request,
            PartialResultProcessing.ReturnPartialResultsAndNotifyCallback,
            ar =>
            {
                callbackHits++;
                try
                {
                    var partial = conn.GetPartialResults(ar);
                    OnPartial(partial);
                }
                catch (Exception ex)
                {
                    Log($"callback threw: {ex.GetType().Name}: {ex.Message}");
                }
            },
            state: null);

        Log("search issued with partial-results callback; waiting for refresh stage to settle...");
        Thread.Sleep(TimeSpan.FromSeconds(5));
        Log($"after refresh window: entries={entries} syncStateControls={stateControls} callbackHits={callbackHits} " +
            $"searchCompleted={async.IsCompleted}");

        // ---- STEP 3: mutate an entry on a SEPARATE connection, watch for persist notice ----
        var target = $"cn=user01,ou=users,{baseDn}";
        using (var mutator = new LdapConnection(new LdapDirectoryIdentifier(host, port))
        {
            AuthType = AuthType.Basic,
            Credential = new NetworkCredential(bindDn, password),
        })
        {
            mutator.SessionOptions.ProtocolVersion = 3;
            mutator.Bind();
            var mod = new ModifyRequest(target, DirectoryAttributeOperation.Replace, "description", marker);
            var resp = (ModifyResponse)mutator.SendRequest(mod);
            Log($"MUTATE {target} description={marker} -> {resp.ResultCode}");
        }

        var observed = mutationSeen.Task.Wait(TimeSpan.FromSeconds(20));
        Log($"persist-stage notification observed = {observed}" +
            (observed ? $" (payload='{mutationSeen.Task.Result}')" : ""));
        Log($"totals: entries={entries} syncStateControls={stateControls} callbackHits={callbackHits} " +
            $"searchCompleted={async.IsCompleted}");

        // ---- STEP 3b: ADD then DELETE — can a subscriber tell a delete apart from an add? ----
        var addDn = $"cn=spike53tmp,ou=users,{baseDn}";
        using (var mutator = new LdapConnection(new LdapDirectoryIdentifier(host, port))
        {
            AuthType = AuthType.Basic,
            Credential = new NetworkCredential(bindDn, password),
        })
        {
            mutator.SessionOptions.ProtocolVersion = 3;
            mutator.Bind();
            var add = new AddRequest(addDn,
                new DirectoryAttribute("objectClass", "inetOrgPerson"),
                new DirectoryAttribute("cn", "spike53tmp"),
                new DirectoryAttribute("sn", "tmp"),
                new DirectoryAttribute("description", "SPIKE53-ADD"));
            Log($"ADD {addDn} -> {((AddResponse)mutator.SendRequest(add)).ResultCode}");
            Thread.Sleep(TimeSpan.FromSeconds(3));
            Log($"DELETE {addDn} -> {((DeleteResponse)mutator.SendRequest(new DeleteRequest(addDn))).ResultCode}");
        }
        Thread.Sleep(TimeSpan.FromSeconds(4));
        Log($"totals after add/delete: entries={entries} syncStateControls={stateControls} callbackHits={callbackHits}");

        // ---- STEP 4: refreshOnly comparison — does the sync DONE control come back? -------
        RefreshOnlyProbe(host, port, bindDn, password, baseDn);

        try
        {
            conn.Abort(async);
        }
        catch (Exception ex)
        {
            Log($"abort threw: {ex.GetType().Name}: {ex.Message}");
        }

        Log($"probe end: PERSIST_OBSERVED={observed}");
        return observed ? 0 : 1;
    }

    // refreshOnly (mode 1) terminates, so it can go through the ordinary synchronous path.
    // This isolates "does SDP send/receive the control at all" from "does SDP support a
    // never-completing search".
    private static void RefreshOnlyProbe(string host, int port, string bindDn, string password, string baseDn)
    {
        Log("--- refreshOnly (mode 1) synchronous probe ---");
        try
        {
            using var conn = new LdapConnection(new LdapDirectoryIdentifier(host, port))
            {
                AuthType = AuthType.Basic,
                Credential = new NetworkCredential(bindDn, password),
            };
            conn.SessionOptions.ProtocolVersion = 3;
            conn.Bind();

            var w = new AsnWriter(AsnEncodingRules.BER);
            using (w.PushSequence())
            {
                w.WriteEnumeratedValue(SyncRequestMode.RefreshOnly);
            }
            var req = new SearchRequest(baseDn, "(objectClass=*)", SearchScope.Subtree, "cn");
            req.Controls.Add(new DirectoryControl(SyncRequestOid, w.Encode(), isCritical: true, serverSide: true));

            var resp = (SearchResponse)conn.SendRequest(req);
            Log($"refreshOnly result={resp.ResultCode} entries={resp.Entries.Count} " +
                $"responseControls=[{string.Join(",", resp.Controls.Select(c => c.Type))}]");
            foreach (DirectoryControl c in resp.Controls)
            {
                if (c.Type == SyncDoneOid)
                {
                    Log($"  syncDoneValue BER = {Convert.ToHexString(c.GetValue())} -> {DecodeSyncDone(c.GetValue())}");
                }
            }
            var withState = resp.Entries.Cast<SearchResultEntry>()
                .Count(e => e.Controls.Any(c => c.Type == SyncStateOid));
            Log($"refreshOnly entries carrying a SyncState control: {withState}/{resp.Entries.Count}");
        }
        catch (Exception ex)
        {
            Log($"refreshOnly FAILED: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private enum SyncRequestMode
    {
        RefreshOnly = 1,
        RefreshAndPersist = 3,
    }

    // syncStateValue ::= SEQUENCE { state ENUMERATED {present(0),add(1),modify(2),delete(3)},
    //                               entryUUID syncUUID, cookie syncCookie OPTIONAL }
    private static string DecodeSyncState(byte[] value)
    {
        try
        {
            var r = new AsnReader(value, AsnEncodingRules.BER);
            var seq = r.ReadSequence();
            var state = (int)seq.ReadEnumeratedValue<SyncStateKind>();
            var uuid = seq.ReadOctetString();
            string? cookie = null;
            if (seq.HasData)
            {
                cookie = Encoding.UTF8.GetString(seq.ReadOctetString());
            }
            return $"state={(SyncStateKind)state} uuid={new Guid(uuid):D} cookie={cookie ?? "<none>"}";
        }
        catch (Exception ex)
        {
            return $"<undecodable: {ex.Message}> raw={Convert.ToHexString(value)}";
        }
    }

    private static string DecodeSyncDone(byte[] value)
    {
        try
        {
            var seq = new AsnReader(value, AsnEncodingRules.BER).ReadSequence();
            var cookie = seq.HasData ? Encoding.UTF8.GetString(seq.ReadOctetString()) : "<none>";
            return $"cookie={cookie}";
        }
        catch (Exception ex)
        {
            return $"<undecodable: {ex.Message}>";
        }
    }

    private enum SyncStateKind
    {
        Present = 0,
        Add = 1,
        Modify = 2,
        Delete = 3,
    }

    private static void Log(string message) =>
        Console.WriteLine($"{DateTime.Now:HH:mm:ss.fff}  {message}");
}
