using Puck.SdfVm;
using Puck.SignedDistance;

namespace Puck.World.Client;

public sealed partial class WorldFramePresenter {
    /// <summary>Gets the screen-surface transform of every engine screen slot: a carried screen's
    /// (<see cref="WorldScreen.Carry"/>) answers the frame its carrier holds it at this frame, or its authored frame
    /// while no body carries it, so its picture rides the carrier's hands and returns to the device when it is put down;
    /// every other slot answers <see langword="null"/> and keeps the frame its program declares.</summary>
    public IReadOnlyDictionary<int, Func<SdfScreenSurfaceTransform?>>? ScreenSurfaceTransforms => m_screenTransforms;

    private static Dictionary<int, Func<SdfScreenSurfaceTransform?>> NewScreenTransforms(WorldFramePresenter presenter) {
        var providers = new Dictionary<int, Func<SdfScreenSurfaceTransform?>>(capacity: SdfProgramBuilder.MaxScreenSurfaces);

        for (var index = 0; (index < SdfProgramBuilder.MaxScreenSurfaces); index++) {
            var screen = index;

            providers[index] = () => presenter.ScreenTransform(index: screen);
        }

        return providers;
    }
    // The frame a carried screen's surface takes this frame; null for a screen no body can carry.
    private SdfScreenSurfaceTransform? ScreenTransform(int index) {
        foreach (var screen in m_client.Definition.Screens) {
            if ((screen.Index != index) || (screen.Carry is null)) {
                continue;
            }

            var frame = Carried(screen: screen);

            return new SdfScreenSurfaceTransform(
                HalfHeight: frame.HalfHeight,
                HalfWidth: frame.HalfWidth,
                Origin: frame.Origin,
                Right: System.Numerics.Vector3.Normalize(value: frame.Right),
                Up: System.Numerics.Vector3.Normalize(value: frame.Up)
            );
        }

        return null;
    }
    // A screen row where its picture is this frame: at its carrier's box while a body carries it, else as authored.
    private WorldScreen Carried(WorldScreen screen) => (m_animator.TryCarriedScreen(
        carried: out var held,
        client: m_client,
        screen: screen
    )
        ? held
        : screen);
}
