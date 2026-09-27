using Puck.Abstractions.Presentation;
using Xunit;

namespace Puck.World.Tests;

/// <summary>Laws for the host settings' display values: the resolved color space is the authored one, SDR by
/// default, and the paper white is the authored level or the SDR white level.</summary>
public sealed class WorldHostDisplaySettingsLawTests {
    private static WorldHostSettings Resolve(WorldHostDefaults defaults) => WorldHostSettings.Resolve(
        backendOverride: null,
        defaults: defaults,
        directXAvailable: false,
        exitAfterSecondsOverride: null,
        heightOverride: null,
        presentModeOverride: null,
        widthOverride: null
    );

    [Fact]
    public void TheResolvedDisplayIsTheAuthoredOneAndSdrAtSdrWhiteByDefault() {
        var absent = Resolve(defaults: WorldHostDefaults.Absent);
        var hdr = Resolve(defaults: (WorldHostDefaults.Absent with {
            ColorSpace = DisplayColorSpace.ScRgb,
            PaperWhiteNits = 250.0,
        }));

        Assert.Equal(
            actual: (absent.ColorSpace, absent.PaperWhiteNits),
            expected: (DisplayColorSpace.Srgb, DisplayOutput.SdrWhiteNits)
        );
        Assert.Equal(
            actual: (hdr.ColorSpace, hdr.PaperWhiteNits),
            expected: (DisplayColorSpace.ScRgb, 250.0)
        );
    }
}
