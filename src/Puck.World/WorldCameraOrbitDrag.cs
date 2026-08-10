using System.Numerics;
using Puck.World.Client;

namespace Puck.World;

/// <summary>
/// The pointer consumer that turns a drag into <see cref="WorldCameraOrbit"/> nudges, according to THAT SEAT's
/// merged control-feel policy (<see cref="WorldSeatCameraResolver.ResolveSeatLook"/> — rig structure, the pitch
/// clamp, from whichever world currently frames the seat's live route (see <see cref="ResolveStructure"/>); input
/// preferences, sensitivity/inversion/arming, from <see cref="WorldSeatFeel"/>, the seat's own profile's
/// <c>playerDefaults.seatLook</c> or the world's when it carries no profile): its <see cref="WorldSeatLook.Arming"/>
/// selects which button (if any) arms the drag, or disables orbiting entirely
/// (<see cref="WorldSeatLookArming.None"/>); its sensitivities, invert flags, and pitch clamp shape the resulting
/// nudge. The cursor is free (and drives the console/overlay/editor as usual) whenever the drag is not armed.
/// </summary>
/// <remarks>Reads the arming half of its answer from <see cref="WorldPointer"/> — that seat's live held-button
/// state, and the drag distance is that seat's drained motion — so it tracks no held state of its own and observes
/// no raw window events. It is one of many consumers behind the single <see cref="WorldPointerSink"/>, which
/// resolves the seat the mouse rides (see its remarks) and drives this on every pointer event. The pitch clamp it
/// merges in follows the seat across a crossing: <see cref="ResolveStructure"/> reads
/// <see cref="WorldInstanceHost.ResolveRoutedDefinition"/>, the same routed-definition lookup
/// <c>WorldViewCommandModule.DescribeOrbit</c>'s <c>world.view.orbit</c> echo reads — one source, never re-derived.
/// A carried orbit additionally RECLAMPS the instant the route itself changes (<see cref="WorldSeatInstanceRouter.LocationChanged"/>,
/// subscribed in the constructor), so a pitch dragged out of range against the old structure snaps legal at the
/// crossing rather than persisting into a destination whose renderer already frames through the new one
/// (<c>AwaySeatSceneEmitter.ResolveChaseCamera</c>).</remarks>
internal sealed class WorldCameraOrbitDrag : IWorldPointerConsumer {
    private readonly WorldInstanceHost m_instances;
    private readonly WorldCameraOrbit m_orbit;
    private readonly WorldPointer m_pointer;
    private readonly WorldSeatFeel m_seatFeel;

    /// <summary>Initializes a new instance of the <see cref="WorldCameraOrbitDrag"/> class.</summary>
    /// <param name="instances">The running-instance registry — resolves the seat's CURRENT route to the world
    /// document actually framing it (see <see cref="ResolveStructure"/>), boot document included (
    /// <see cref="WorldInstanceHost.ResolveRoutedDefinition"/> falls back to it itself, so this type carries no
    /// second reference to the boot document of its own).</param>
    /// <param name="orbit">The shared orbit state this consumer nudges.</param>
    /// <param name="seatFeel">The per-seat control feel — the preference half of the merged policy.</param>
    /// <param name="pointer">The live pointer store this consumer reads arming and motion from.</param>
    /// <param name="seatRouter">The traveler-follow router — its <see cref="WorldSeatInstanceRouter.LocationChanged"/>
    /// event drives the crossing reclamp; this type never reads it beyond subscribing (structure resolution goes
    /// through <paramref name="instances"/>, which already routes through it internally).</param>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    public WorldCameraOrbitDrag(WorldInstanceHost instances, WorldCameraOrbit orbit, WorldSeatFeel seatFeel, WorldPointer pointer, WorldSeatInstanceRouter seatRouter) {
        ArgumentNullException.ThrowIfNull(argument: instances);
        ArgumentNullException.ThrowIfNull(argument: orbit);
        ArgumentNullException.ThrowIfNull(argument: seatFeel);
        ArgumentNullException.ThrowIfNull(argument: pointer);
        ArgumentNullException.ThrowIfNull(argument: seatRouter);

        m_instances = instances;
        m_orbit = orbit;
        m_pointer = pointer;
        m_seatFeel = seatFeel;

        // The crossing-reclamp edge: fires only when Publish actually changes the seat's presenting INSTANCE (see
        // WorldSeatInstanceRouter.LocationChanged's own remarks), never on an ordinary drag. Lives for the process
        // (this consumer and the router are both container singletons), so no unsubscribe — the same lifetime
        // WorldPointerSink's own DeviceSlotChanging subscription relies on.
        seatRouter.LocationChanged += OnLocationChanged;
    }

    // Reclamps slot's carried orbit pitch against the NEW structure the instant its route changes, so a pitch
    // dragged out of range against the world it just left never persists into the world it just entered — the
    // renderer (WorldSeatCameraResolver.ResolveChase, fed this same accumulator) would otherwise render an
    // out-of-authored-range orbit for every frame between the crossing and this seat's next drag. Nudge with a
    // zero delta is exactly a reclamp: yaw is unaffected (a full-turn wrap of an already-wrapped value is a
    // no-op), pitch re-clamps to the new min/max.
    private void OnLocationChanged(int slot) {
        var structure = ResolveStructure(slot: slot);

        m_orbit.Reclamp(slot: slot, seatLook: structure);
    }

    // The rig-structure half of the merged seat-look policy (WorldSeatCameraResolver.ResolveSeatLook's own split):
    // whichever world the seat's LIVE route currently frames it from, not always the boot world — the same routed
    // lookup WorldViewCommandModule.DescribeOrbit's world.view.orbit echo reads, so a drag's clamp and its own
    // read-back never disagree about which document is in force.
    private WorldSeatLook ResolveStructure(int slot) => m_instances.ResolveRoutedDefinition(slot: slot).PlayerDefaults.SeatLook;

    /// <inheritdoc/>
    public void OnPointer(int slot) {
        var seatLook = WorldSeatCameraResolver.ResolveSeatLook(structure: ResolveStructure(slot: slot), preference: m_seatFeel.Look(slot: slot));

        if (seatLook.Arming == WorldSeatLookArming.None) {
            // None fully disables orbiting. Drain anyway: motion accumulated while disabled must not be banked and
            // then applied in one jump the moment a live playerDefaults.seatLook edit re-arms the drag.
            _ = m_pointer.TakeMotion(slot: slot);

            return;
        }

        if ((ArmingButtonIndex(arming: seatLook.Arming) is { } armingButton) && !m_pointer.IsButtonDown(slot: slot, button: armingButton)) {
            // Armed by a button that is not down: same rule as above — the free cursor's motion is browsing, and
            // banking it would make the next press jump.
            _ = m_pointer.TakeMotion(slot: slot);

            return;
        }

        var motion = m_pointer.TakeMotion(slot: slot);

        if (motion == Vector2.Zero) {
            return;
        }

        // Dragging right swings the camera to show the player's right side; dragging down raises the camera to look
        // down at the player (WoW default). WorldCameraOrbit.Nudge is the one inversion/clamp door shared with the
        // stick path; keeping the original motion and sensitivity operands here leaves pointer feel unchanged.
        m_orbit.Nudge(
            slot: slot,
            input: motion,
            yawScale: seatLook.YawSensitivity,
            pitchScale: seatLook.PitchSensitivity,
            seatLook: seatLook
        );
    }

    // Maps an authored button-arming mode to the pointer button index the store keys held state by (0=left,
    // 1=right, 2=middle), or null for a mode with no arming button (Always — which orbits continuously — and,
    // already returned above, None). Shared with WorldCursorFeed's visibility rule (the cursor hides exactly while
    // this consumer would eat the motion), so the two read one mapping and can never disagree on which button arms.
    internal static int? ArmingButtonIndex(WorldSeatLookArming arming) => arming switch {
        WorldSeatLookArming.LeftButton => 0,
        WorldSeatLookArming.RightButton => 1,
        WorldSeatLookArming.MiddleButton => 2,
        _ => null,
    };
}
