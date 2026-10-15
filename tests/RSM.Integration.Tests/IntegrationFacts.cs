// The live demos measure RSM's own load on the server and create blocking on purpose: run test classes one by one.
[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace RSM.Integration.Tests;

/// <summary>A fact that is skipped unless the named environment variable is set.</summary>
public sealed class EnvFactAttribute : FactAttribute
{
    public EnvFactAttribute(string variable)
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(variable)))
        {
            Skip = $"set {variable} to run this integration test";
        }
    }
}
