// SPIKE CODE — issue #53. Throwaway quality on purpose.
//
// Counterpart to SyncProbe: same RFC 4533 refreshAndPersist scenario, but over the pure-managed
// Novell.Directory.Ldap.NETStandard stack instead of System.DirectoryServices.Protocols.
// Purpose: establish whether the SDP limitations measured on Linux (search terminates after the
// refresh stage) and on both platforms (per-entry SyncState controls dropped) are inherent to
// LDAP or specific to SDP.
//
// Usage: dotnet run -- <host> <port> <bindDn> <password> <baseDn>

using System.Formats.Asn1;
using System.Text;
using Novell.Directory.Ldap;

const string SyncRequestOid = "1.3.6.1.4.1.4203.1.9.1.1";
const string SyncStateOid = "1.3.6.1.4.1.4203.1.9.1.2";
const string SyncInfoOid = "1.3.6.1.4.1.4203.1.9.1.4";

var host = args.ElementAtOrDefault(0) ?? "localhost";
var port = int.Parse(args.ElementAtOrDefault(1) ?? "13389");
var bindDn = args.ElementAtOrDefault(2) ?? "cn=admin,dc=spike,dc=test";
var password = args.ElementAtOrDefault(3) ?? "adminpass";
var baseDn = args.ElementAtOrDefault(4) ?? "dc=spike,dc=test";
var marker = $"SPIKE53-NOVELL-{Guid.NewGuid():N}";

void Log(string m) => Console.WriteLine($"{DateTime.Now:HH:mm:ss.fff}  {m}");

Log($"novell probe start host={host}:{port} os={Environment.OSVersion}");

var conn = new LdapConnection();
conn.Connect(host, port);
conn.Bind(bindDn, password);
Log("bind ok");

var w = new AsnWriter(AsnEncodingRules.BER);
using (w.PushSequence())
{
    w.WriteEnumeratedValue((SyncMode)3); // refreshAndPersist
}
var syncValue = w.Encode();
Log($"syncRequestValue BER = {Convert.ToHexString(syncValue)}");

var cons = conn.SearchConstraints;
cons.BatchSize = 1;                 // deliver each message as it arrives
cons.TimeLimit = 0;
cons.SetControls(new LdapControl(SyncRequestOid, true, syncValue));

var queue = conn.Search(baseDn, LdapConnection.ScopeSub, "(objectClass=*)",
    ["cn", "description"], false, null, cons);

var entries = 0;
var stateControls = 0;
var infoMessages = 0;
var seen = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
var start = DateTime.Now;

var pump = new Thread(() =>
{
    try
    {
        while (true)
        {
            var msg = queue.GetResponse();
            if (msg is null)
            {
                Log("queue returned null (stream ended)");
                return;
            }
            var el = (DateTime.Now - start).ToString(@"mm\:ss\.ff");
            var ctls = msg.Controls;
            var ctlNames = ctls is null ? "" : string.Join(",", ctls.Select(c => c.Id));
            switch (msg)
            {
                case LdapSearchResult sr:
                {
                    entries++;
                    var e = sr.Entry;
                    string desc = null;
                    try { desc = e.GetAttribute("description")?.StringValue; }
                    catch (KeyNotFoundException) { /* attribute absent */ }
                    var state = ctls?.FirstOrDefault(c => c.Id == SyncStateOid);
                    if (state is not null)
                    {
                        stateControls++;
                    }
                    Log($"[{el}] ENTRY dn={e.Dn} controls=[{ctlNames}] " +
                        $"syncState={(state is null ? "<none>" : Decode(state.GetValue()))} description={desc}");
                    if (desc == marker)
                    {
                        seen.TrySetResult(desc);
                    }
                    break;
                }
                case LdapIntermediateResponse ir:
                    infoMessages++;
                    Log($"[{el}] INTERMEDIATE oid={ir.GetType().Name} name={TryName(ir)} controls=[{ctlNames}]");
                    break;
                case LdapResponse r:
                    Log($"[{el}] RESPONSE result={r.ResultCode} controls=[{ctlNames}]");
                    break;
                default:
                    Log($"[{el}] OTHER {msg.GetType().FullName}");
                    break;
            }
        }
    }
    catch (Exception ex)
    {
        Log($"pump threw: {ex.GetType().Name}: {ex.Message}");
    }
})
{ IsBackground = true };
pump.Start();

Thread.Sleep(TimeSpan.FromSeconds(5));
Log($"after refresh window: entries={entries} syncStateControls={stateControls} intermediates={infoMessages}");

var mutator = new LdapConnection();
mutator.Connect(host, port);
mutator.Bind(bindDn, password);
var target = $"cn=user01,ou=users,{baseDn}";
mutator.Modify(target, new LdapModification(LdapModification.Replace,
    new LdapAttribute("description", marker)));
Log($"MUTATE {target} description={marker}");

var observed = seen.Task.Wait(TimeSpan.FromSeconds(20));

// ADD then DELETE: does the SyncState control let a subscriber tell a delete from an add?
var addDn = $"cn=spike53tmp,ou=users,{baseDn}";
try { mutator.Delete(addDn); } catch (LdapException) { /* not present */ }
mutator.Add(new LdapEntry(addDn, new LdapAttributeSet
{
    new LdapAttribute("objectClass", ["inetOrgPerson"]),
    new LdapAttribute("cn", "spike53tmp"),
    new LdapAttribute("sn", "tmp"),
    new LdapAttribute("description", "SPIKE53-ADD"),
}));
Log($"ADD {addDn}");
Thread.Sleep(TimeSpan.FromSeconds(3));
mutator.Delete(addDn);
Log($"DELETE {addDn}");
Thread.Sleep(TimeSpan.FromSeconds(4));

Log($"persist-stage notification observed = {observed}");
Log($"totals: entries={entries} syncStateControls={stateControls} intermediates={infoMessages}");
Log($"novell probe end: PERSIST_OBSERVED={observed}");
return observed ? 0 : 1;

static string TryName(LdapIntermediateResponse ir)
{
    try { return ir.GetType().GetProperty("Name")?.GetValue(ir)?.ToString() ?? "<null>"; }
    catch { return "<err>"; }
}

static string Decode(byte[] value)
{
    try
    {
        var seq = new AsnReader(value, AsnEncodingRules.BER).ReadSequence();
        var state = seq.ReadEnumeratedValue<SyncState>();
        var uuid = seq.ReadOctetString();
        var cookie = seq.HasData ? Encoding.UTF8.GetString(seq.ReadOctetString()) : "<none>";
        return $"state={state} uuid={new Guid(uuid):D} cookie={cookie}";
    }
    catch (Exception ex)
    {
        return $"<undecodable: {ex.Message}> raw={Convert.ToHexString(value)}";
    }
}

internal enum SyncMode { RefreshOnly = 1, RefreshAndPersist = 3 }

internal enum SyncState { Present = 0, Add = 1, Modify = 2, Delete = 3 }
