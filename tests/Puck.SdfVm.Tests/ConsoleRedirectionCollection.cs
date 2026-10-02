using Xunit;

namespace Puck.SdfVm.Tests;

/// <summary>Serializes laws that replace the process-wide console writers against every other collection.</summary>
[CollectionDefinition(name: Name, DisableParallelization = true)]
public sealed class ConsoleRedirectionCollection {
    public const string Name = "console-redirection";
}
