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
