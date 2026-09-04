namespace RoslynMcpServer.Tests;

internal static class TestEnvironmentLocks
{
    internal static readonly object DotNetRoot = new();

    /// <summary>
    /// Serializes tests that mutate <see cref="Environment.CurrentDirectory"/> (process-global).
    /// Hold it around the change + every call that may read cwd (including
    /// <c>Host.CreateApplicationBuilder()</c>, which defaults the content root to the process cwd).
    /// </summary>
    internal static readonly object Cwd = new();
}
