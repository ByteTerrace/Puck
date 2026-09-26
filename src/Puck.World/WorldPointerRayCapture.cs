using Puck.Commands;
using Puck.Maths;
using Puck.World.Client;

namespace Puck.World;

/// <summary>
/// Produces the pointer ray a <c>Simulation</c> screen reads: once per host frame, before the frame's ticks are
/// snapshotted, it casts the OS pointer through the camera of the seat whose mouse moved it
/// (<see cref="SourceRay.Through"/> over the view <see cref="WorldSeatViewports"/> published for the frame on screen)
/// and sustains the ray on that seat's lane as the <see cref="SourcePointerCommands.Origin"/> and
/// <see cref="SourcePointerCommands.Direction"/> commands (<see cref="InputRouter.Sustain"/>), so every tick the frame
/// runs carries it, a catch-up burst included. Only a seat that still holds the mouse the position is attributed to
/// points: the ray ends when that mouse leaves the seat or the position names no mouse (a platform that reports only
/// the aggregate cursor, or a gamepad-only seat), when the pointer leaves the seat's view or the window
/// (<see cref="WorldPointer.ForgetPosition"/>), or when the world declares no <c>Simulation</c> screen; the server then
/// reads <c>on</c> 0.
/// </summary>
/// <remarks>The camera is presentation float state; the ray enters the command plane as the float values the tick's
/// snapshot records and is quantized once into simulation at the seat verb, so a replay reproduces it exactly.
/// Single-threaded: the host loop services every <see cref="ISnapshotInputCapture"/> on the window-pump thread, where
/// the frame source also publishes the seat views.</remarks>
public sealed class WorldPointerRayCapture : ISnapshotInputCapture {
    private readonly Func<WorldDefinition> m_definition;
    private readonly WorldPointer m_pointer;
    private readonly PlayerRoster m_roster;
    private readonly InputRouter m_router;
    private readonly WorldSeatViewports m_viewports;

    // The seat the ray is sustained on, or -1 while none is.
    private int m_sustainedSlot = -1;

    /// <summary>Initializes a new instance of the <see cref="WorldPointerRayCapture"/> class.</summary>
    /// <param name="pointer">The live pointer store, read non-destructively.</param>
    /// <param name="viewports">The per-seat view publication of the frame on screen.</param>
    /// <param name="roster">The roster that says which seat holds the mouse the position is attributed to.</param>
    /// <param name="definition">Reads the delivered definition whose screens the ray may map through.</param>
    /// <param name="router">The router whose seat lanes carry the ray.</param>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    public WorldPointerRayCapture(WorldPointer pointer, WorldSeatViewports viewports, PlayerRoster roster, Func<WorldDefinition> definition, InputRouter router) {
        ArgumentNullException.ThrowIfNull(argument: pointer);
        ArgumentNullException.ThrowIfNull(argument: viewports);
        ArgumentNullException.ThrowIfNull(argument: roster);
        ArgumentNullException.ThrowIfNull(argument: definition);
        ArgumentNullException.ThrowIfNull(argument: router);

        m_definition = definition;
        m_pointer = pointer;
        m_roster = roster;
        m_router = router;
        m_viewports = viewports;
    }

    private bool DeclaresSimulationScreen() {
        foreach (var screen in m_definition().Screens) {
            if (screen.Route.Input == SourceDestination.Simulation) {
                return true;
            }
        }

        return false;
    }
    private void End() {
        if (m_sustainedSlot < 0) {
            return;
        }

        _ = m_router.EndSustain(
            command: SourcePointerCommands.Origin,
            slot: m_sustainedSlot
        );
        _ = m_router.EndSustain(
            command: SourcePointerCommands.Direction,
            slot: m_sustainedSlot
        );
        m_sustainedSlot = -1;
    }

    /// <inheritdoc/>
    public void CaptureFrame(ulong frameKey) {
        // The seat and its device in one read, so they always name the same report.
        if (
            (m_pointer.Positioned is not (var slot, var device)) ||
            (m_roster.DeviceSlot(device: device) != slot) ||
            (m_roster.KindOf(device: device) != InputDeviceKind.Mouse) ||
            !DeclaresSimulationScreen()
        ) {
            End();

            return;
        }

        var view = m_viewports.Seat(slot: slot);

        if (m_viewports.Locate(
            framePosition: out _,
            local: out var local,
            position: m_pointer.Position(slot: slot),
            view: in view
        ) != WorldSeatPointerPlace.Inside) {
            End();

            return;
        }
        if (slot != m_sustainedSlot) {
            End();
        }

        var ray = SourceRay.Through(
            camera: view.Camera,
            image: new FixedVector2(
                X: FixedQ4816.FromDouble(value: local.X),
                Y: FixedQ4816.FromDouble(value: local.Y)
            )
        );

        _ = m_router.Sustain(
            command: SourcePointerCommands.Origin,
            slot: slot,
            value: CommandValue.Axis(value: ray.Origin.ToVector3())
        );
        _ = m_router.Sustain(
            command: SourcePointerCommands.Direction,
            slot: slot,
            value: CommandValue.Axis(value: ray.Direction.ToVector3())
        );
        m_sustainedSlot = slot;
    }
}
