// SPIKE CODE — issue #53. Proves the EXISTING WithOverlay(...) API can express the syncprov
// overlay with no production change: builds the declaration through the public
// OpenLdapOverlay surface and prints the LDIF the hosting integration would mount at
// /overlays.ldif, for byte-comparison against the hand-written file the container probe used.
using System.Reflection;
using Aspire.Hosting.ApplicationModel;

var overlay = new OpenLdapOverlay
{
    Name = "syncprov",
    ModuleLoads = ["syncprov.so"],
    OverlayObjectClass = "olcSyncProvConfig",
    Attributes =
    [
        new("olcSpCheckpoint", "1 1"),
        new("olcSpSessionLog", "100"),
    ],
};
overlay.GetType().GetMethod("Validate", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)!
    .Invoke(overlay, null);
Console.WriteLine("Validate() passed on the public declaration.");

var ext = typeof(OpenLdapOverlay).Assembly.GetType("Aspire.Hosting.OpenLdapResourceBuilderExtensions")
    ?? typeof(OpenLdapOverlay).Assembly.GetTypes().First(t => t.Name == "OpenLdapResourceBuilderExtensions");
var gen = ext.GetMethod("GenerateOverlayLdif", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)!;
Console.WriteLine("---- generated /overlays.ldif ----");
Console.WriteLine((string)gen.Invoke(null, [new List<OpenLdapOverlay> { overlay }.AsReadOnly()])!);
