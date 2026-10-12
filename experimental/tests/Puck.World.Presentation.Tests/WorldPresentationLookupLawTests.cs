using System.Numerics;
using System.Text.Json;

using Puck.Abstractions.Counting;
using Puck.Commands;
using Puck.SdfVm;
using Puck.SdfVm.Views;
using Puck.World.Client;

using Xunit;

namespace Puck.World.Presentation.Tests;

/// <summary>
/// Laws for reading the presentation manifest's registered slots (<see cref="WorldStateMirror.SlotOf(in StateBinding, WorldStateConversion)"/>):
/// a lookup registers, reads and allocates nothing, so after an install every consumer's first frame over the
/// flagship world reads no cell and adds no slot; a seat's own reads — its binding contexts, its radial wheel's label
/// and icon cells, its binding bar's icon, layout and model cells — are compiled from its composition
/// (<see cref="WorldPresentationManifest.SeatBindings"/>) and registered on the mirror its route reads through, and
/// withdrawn from a mirror it leaves; and a document swap retires every slot only the previous manifest registered and
/// no holder reads, keeps the index of every slot both manifests register, and never answers a lookup with a retired
/// slot.
/// </summary>
public sealed class WorldPresentationLookupLawTests {
    private static long Reads(WorldStateMirror mirror) {
        Assert.True(condition: ((IWorkCounterSource)mirror).TryRead(
            kind: WorldStateMirror.Reads,
            value: out var value
        ));

        return value;
    }
    // The first frame every document-level consumer draws after an install: the theme, the environment, every
    // camera program the document carries, and every bindable the manifest records read through Scalar and Color.
    private static void FirstFrame(WorldDefinition definition, WorldStateMirror mirror) {
        _ = new WorldThemeResolve(domains: new WorldValueDomainGuard()).Resolve(
            definition: definition,
            mirror: mirror,
            revision: 1
        );
        _ = new WorldEnvironmentResolve(domains: new WorldValueDomainGuard()).Resolve(
            definition: definition,
            mirror: mirror,
            revision: 1
        );

        var anchor = new SdfAnchor(
            Orientation: Quaternion.Identity,
            Position: Vector3.Zero
        );
        var clock = new SdfCameraClock(
            AuthoritativeTick: 0UL,
            PresentationSeconds: 0f
        );

        foreach (var camera in definition.Cameras) {
            _ = WorldCameraRigCompiler.Compile(domains: new WorldValueDomainGuard(),
                definition: definition,
                mirror: mirror,
                program: camera.Rig
            ).Resolve(
                anchor: in anchor,
                clock: in clock
            );
        }

        foreach (ref readonly var entry in mirror.Manifest.Bindings) {
            var slot = mirror.SlotOf(
                binding: entry.Binding,
                conversion: entry.Conversion
            );

            Assert.True(condition: (slot >= 0));
            _ = mirror.TryValue(
                slot: slot,
                value: out _
            );
            _ = mirror.TryColor(
                slot: slot,
                value: out _
            );
        }
    }
    [Fact]
    public void EveryConsumersFirstFrameOverTheFlagshipReadsNoCellAndAddsNoSlot_AndALookupOfAnUnrecordedBindingRegistersNothing() {
        var definition = AuthoredGameFixtures.Nexus;
        var mirror = new WorldStateMirror(view: new WorldDocumentStateView(definition: () => definition));

        mirror.Install(
            engineTick: 0UL,
            tick: 0UL
        );

        var slots = mirror.SlotCount;
        var reads = Reads(mirror: mirror);

        Assert.Equal(
            actual: slots,
            expected: mirror.Manifest.Bindings.Length
        );

        FirstFrame(
            definition: definition,
            mirror: mirror
        );
        Assert.Equal(
            actual: mirror.SlotOf(
                conversion: WorldStateConversion.Number,
                token: "state.nothingBindsThis"
            ),
            expected: -1
        );
        Assert.Equal(
            actual: (mirror.SlotCount, (Reads(mirror: mirror) - reads)),
            expected: (slots, 0L)
        );
    }
}
