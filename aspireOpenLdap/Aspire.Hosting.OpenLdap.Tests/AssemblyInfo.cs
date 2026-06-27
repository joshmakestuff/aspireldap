using Xunit;

// Each test spins up its own Aspire AppHost (DCP host, dashboard, OpenLDAP container).
// Running test classes in parallel starts multiple AppHosts in one process at once, which
// contends on orchestration host ports and hangs. Run tests sequentially instead.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
