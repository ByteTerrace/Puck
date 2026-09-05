using Xunit;

namespace Puck.World.Schema.Tests;

/// <summary>The one file-name suffix a minting site appends to a <see cref="WorldSafeName"/> fits the suffix ceiling
/// the name's own length bound reserves, so a name that validates can always be written to disk.</summary>
public sealed class WorldSafeNameSuffixLawTests {
    [Fact]
    public void TheOwnedWorldSuffixFitsTheReservedCeiling() {
        Assert.True(WorldOwnedWorldFileName.Suffix.Length <= WorldSafeName.MaxSuffixLength);
        Assert.Equal(255 - WorldSafeName.MaxSuffixLength, WorldSafeName.MaxLength);
    }
}
