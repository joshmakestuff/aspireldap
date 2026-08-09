using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting.OpenLdap.Tests;

/// <summary>
/// Resolves a resource's environment the way the orchestrator does at container start: run every
/// callback in registration order, then materialize deferred values (parameters, reference
/// expressions) to strings. Shared by the model tests for every sidecar/env contract.
/// </summary>
internal static class EnvironmentEvaluation
{
    public static async Task<Dictionary<string, string>> EvaluateEnvironmentAsync(IResource resource)
    {
        var context = new EnvironmentCallbackContext(
            new DistributedApplicationExecutionContext(DistributedApplicationOperation.Run),
            resource);
        foreach (var annotation in resource.Annotations.OfType<EnvironmentCallbackAnnotation>())
        {
            await annotation.Callback(context);
        }

        var result = new Dictionary<string, string>();
        foreach (var (name, value) in context.EnvironmentVariables)
        {
            result[name] = value switch
            {
                string s => s,
                IValueProvider provider => await provider.GetValueAsync() ?? string.Empty,
                _ => value?.ToString() ?? string.Empty,
            };
        }
        return result;
    }
}
