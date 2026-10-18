using Puck.Abstractions.Gpu;
using Xunit;

namespace Puck.World.Tests;

/// <summary>Laws for the World's GPU diagnostic flags (<see cref="WorldDiagnosticFlags"/>): a bare
/// <c>--debug-layers</c> is the basic level every verb passes, <c>full</c> the hunting level, and any other value is
/// refused by name.</summary>
public sealed class WorldDiagnosticFlagsLawTests {
    [InlineData(null, GpuValidation.Basic)]
    [InlineData("basic", GpuValidation.Basic)]
    [InlineData("full", GpuValidation.Full)]
    [Theory]
    public void EachLevelParsesToItself(string? token, GpuValidation expected) {
        var errors = new List<string>();

        Assert.Equal(actual: WorldDiagnosticFlags.ParseValidation(error: errors.Add, token: token), expected: expected);
        Assert.Empty(collection: errors);
    }
    [Fact]
    public void AnUnknownLevelIsRefusedByName() {
        var errors = new List<string>();

        Assert.Equal(actual: WorldDiagnosticFlags.ParseValidation(error: errors.Add, token: "gpu"), expected: GpuValidation.Off);
        Assert.Contains(expectedSubstring: "'gpu'", actualString: Assert.Single(collection: errors));
    }
    [Fact]
    public void AMissingSinkIsRefused() =>
        Assert.Throws<ArgumentNullException>(testCode: () => WorldDiagnosticFlags.ParseValidation(error: null!, token: null));
}
