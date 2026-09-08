using Xunit;

namespace Puck.World.Schema.Tests;

/// <summary>The one file-name suffix a minting site appends to a <see cref="SafeName"/> fits the suffix ceiling
/// the name's own length bound reserves, so a name that validates can always be written to disk.</summary>
public sealed class WorldSafeNameSuffixLawTests {
    [Fact]
    public void TheOwnedWorldSuffixFitsTheReservedCeiling() {
        Assert.True(WorldOwnedWorldFileName.Suffix.Length <= SafeName.MaxSuffixLength);
        Assert.Equal(255 - SafeName.MaxSuffixLength, SafeName.MaxLength);
    }
}
