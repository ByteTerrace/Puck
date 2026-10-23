using Puck.Abstractions.Presentation;
using Puck.Assets;
using Puck.Testing;
using Puck.World.Client;
using Xunit;

namespace Puck.World.Tests;

/// <summary><c>world.screenshot &lt;path.png&gt; slots</c> cuts a served still at each slot of the layout it was armed
/// under, origin top-left, one PNG per slot named by what the slot shows.</summary>
public sealed class WorldSlotStillsLawTests {
    [Fact]
    public void EachSlotIsCutFromItsOwnRegionAndNamedByItsCamera() {
        using var directory = new TemporaryDirectory(prefix: "puck-slot-stills-");
        var path = directory.PathOf(name: "frame.png");
        // A 4x2 frame whose four 2x1 quadrants carry their own red value: top-left 10, top-right 20, bottom-left 30,
        // bottom-right 40.
        var rgba = new byte[(4 * 2) * 4];

        for (var y = 0; (y < 2); y++) {
            for (var x = 0; (x < 4); x++) {
                rgba[((((y * 4) + x) * 4) + 0)] = ((byte)((((y * 2) + (x / 2)) + 1) * 10));
                rgba[((((y * 4) + x) * 4) + 3)] = 255;
            }
        }

        PngEncoder.Write(height: 2, path: path, rgba: rgba, width: 4);

        var stills = WorldSlotStills.Of(slots: [
            new WorldComposedSlot(Camera: "motion-profile", Region: new NormalizedRect(Height: 0.5f, Width: 0.5f, X: 0f, Y: 0f), SeatOrder: 0),
            new WorldComposedSlot(Camera: "$operator", Region: new NormalizedRect(Height: 0.5f, Width: 0.5f, X: 0.5f, Y: 0.5f), SeatOrder: 0),
            new WorldComposedSlot(Camera: null, Region: new NormalizedRect(Height: 0.5f, Width: 0.5f, X: 0.5f, Y: 0f), SeatOrder: 2),
            new WorldComposedSlot(Camera: null, Region: new NormalizedRect(Height: 0.5f, Width: 0.5f, X: 0f, Y: 0.5f), SeatOrder: 2),
            new WorldComposedSlot(Camera: "hidden", Region: NormalizedRect.Hidden, SeatOrder: 0),
        ]);

        Assert.Equal(expected: ["motion-profile", "-operator", "seat2-2", "seat2-3"], actual: stills.Select(selector: static still => still.Name));

        WorldSlotStills.Write(result: new FrameCaptureResult(Error: null, Path: path), slots: stills);

        Assert.Equal(expected: 10, actual: Red(path: directory.PathOf(name: "frame-motion-profile.png")));
        Assert.Equal(expected: 40, actual: Red(path: directory.PathOf(name: "frame--operator.png")));
        Assert.Equal(expected: 20, actual: Red(path: directory.PathOf(name: "frame-seat2-2.png")));
        Assert.Equal(expected: 30, actual: Red(path: directory.PathOf(name: "frame-seat2-3.png")));

        // A failed capture cuts nothing.
        File.Delete(path: directory.PathOf(name: "frame-seat2-3.png"));
        WorldSlotStills.Write(result: new FrameCaptureResult(Error: new InvalidOperationException(), Path: path), slots: stills);
        Assert.False(condition: File.Exists(path: directory.PathOf(name: "frame-seat2-3.png")));
    }

    // The red value every pixel of a 2x1 still carries.
    private static int Red(string path) {
        var image = PngDecoder.Decode(pngBytes: File.ReadAllBytes(path: path));

        Assert.Equal(expected: (2, 1), actual: (image.Width, image.Height));
        Assert.Equal(expected: image.RgbaPixels[0], actual: image.RgbaPixels[4]);

        return image.RgbaPixels[0];
    }
}
