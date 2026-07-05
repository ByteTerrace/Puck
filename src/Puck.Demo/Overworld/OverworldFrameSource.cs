using System.Numerics;
using Puck.Compositing;
using Puck.HumbleGamingBrick;
using Puck.Maths;
using Puck.SdfVm;

namespace Puck.Demo.Overworld;

/// <summary>
/// Bridges the deterministic <see cref="OverworldWorld"/> to the SDF renderer. It builds the static room program (the
/// floor, four walls, the console stands with their diegetic screen surfaces, the cartridge shelf, and one box per
/// FIXED player/cartridge/control slot at a dynamic-transform slot) and, each frame, emits the slots' current
/// transforms (which the renderer uploads into the dynamic-transform buffer) plus the screen director's view list.
/// Everything is rendered relative to the room's SPAWN ANCHOR — the room is bounded (±8 world units), so a FIXED
/// anchor keeps every float small AND immovable; an anchor that followed the players (the unbounded-world pattern)
/// flipped across its snap grid at the room's center lines and made the smoothed camera race the jump. The program
/// rebuilds only when a console boots (its screen material lights up) — the instruction count is IDENTICAL across
/// rebuilds, so the renderer's once-sized program buffer stays valid. A player, cartridge, or control moves purely by
/// changing the per-frame dynamic-transform buffer (a free player slot's box rides a hidden position) and the
/// per-frame view regions.
/// </summary>
public sealed class OverworldFrameSource : ISdfFrameSource {
    // The default per-console accent palette, by console index: the DMG's grey-green shell, the CGB's berry purple,
    // the GBA's indigo — matching the default document's dmg/cgb/agb console order. Presentation only.
    private static readonly Vector3[] DefaultAccents = [
        new(0.72f, 0.73f, 0.66f),
        new(0.58f, 0.26f, 0.55f),
        new(0.33f, 0.30f, 0.71f),
    ];

    // The GB screen's native aspect (160×144 = 10:9) — the diegetic screen slab's visible face is sized close to it
    // so sampled pixels aren't grossly stretched.
    private const float ScreenAspect = (160f / 144f);
    // Per-console control-cluster piece count and their slot offsets within a console's 3-slot block (d-pad, A, B).
    private const int ControlsPerConsole = 3;
    private const int DPadControlOffset = 0;
    private const int AButtonControlOffset = 1;
    private const int BButtonControlOffset = 2;
    // How far a pressed control depresses into its pedestal (world units) — small enough to read as a press, not a
    // collapse.
    private const float ControlDepress = 0.03f;
    // The d-pad's tilt when a direction is held (radians) — cheap per-direction feedback without separate arms.
    private const float DPadTiltRadians = (12f * (MathF.PI / 180f));
    // The overworld MOOD: the room runs dim so each booted cabinet's diegetic screen glow becomes the dominant light —
    // the whole point of the CRT room. Presentation-only per-frame scales on the world path's ambient + sun terms.
    private const float OverworldAmbientScale = 0.42f;
    private const float OverworldSunScale = 0.5f;
    // CREATOR MODE (the in-engine SDF authoring surface): a reserved pool of dynamic-transform instances present from
    // frame 0 (hidden below the floor when unused) so the engine's once-sized program buffer reserves their capacity
    // up front — only WHICH primitive each slot draws ever changes on a rebuild, and every primitive is one SDF
    // instruction of identical word size, so the buffer never has to grow. One GHOST (the shape being placed) plus up
    // to CreatorShapeCapacity placed shapes.
    private const int CreatorShapeCapacity = 24;
    // A generous per-shape bound (each primitive's worst-case reach is well under this) — a fat bound only costs a rare
    // extra evaluation, and an unused slot's hidden instance culls to nothing.
    private const float CreatorInstanceRadius = 0.9f;
    // The series of primitives the player cycles through (order = cycle order); the ghost renders the selected one.
    private const int CreatorPrimitiveCount = 6;
    private enum CreatorPrimitive {
        Sphere,
        Box,
        Torus,
        Cylinder,
        Capsule,
        Ellipsoid,
    }

    private readonly OverworldWorld m_world;
    private readonly OverworldRoom m_room;
    private readonly ScreenDirector m_director;
    private readonly IReadOnlyList<Vector3> m_consoleAccents;
    // Reads the buttons currently applied to a console's machine this frame (GamingBrickChildNode.CurrentButtons) —
    // the SAME per-frame joypad state the machine consumes, never re-derived from raw input. Null (bare-room mode,
    // no consoles) means every control cluster stays in its neutral pose.
    private readonly Func<int, JoypadButtons>? m_controlsSource;
    // Reused render-relative position buffer for the screen director — Cleared+refilled each frame (no per-frame alloc).
    private readonly List<Vector3> m_activePositions = new(capacity: OverworldWorld.MaxPlayers);
    // The unified dynamic-transform layout: players first (fixed MaxPlayers slots, OverworldWorld's own numbering),
    // then one slot per unified cartridge index, then ControlsPerConsole slots per console stand. Computed once
    // (cartridge/console counts never change after construction) and reused every frame — no per-frame allocation.
    private readonly int m_controlSlotBase;
    // The creator pool's first dynamic-transform slot: the GHOST rides m_creatorSlotBase, and placed shape i rides
    // m_creatorSlotBase + 1 + i (see the creator-mode section below).
    private readonly int m_creatorSlotBase;
    private readonly int m_dynamicTransformCount;
    private readonly DynamicTransform[] m_dynamicTransforms;
    private SdfProgram? m_program;
    // The boot mask the current program's screen materials were chosen under; a boot rebuilds the program.
    private uint m_programBootedMask;
    private float m_time;
    // CREATOR-MODE presentation state (never hashed — the deterministic sim knows nothing of it): whether the mode is
    // active, the currently-selected primitive, the ghost's render-relative position, and the placed shapes. A change
    // that alters the program's COMPOSITION (which primitive a slot draws — a cycle or a place) sets the dirty flag so
    // CaptureFrame rebuilds; a MOVE is just a per-frame dynamic transform (no rebuild).
    private bool m_creatorActive;
    private int m_creatorGhostType;
    private Vector3 m_creatorGhostPosition;
    private readonly List<(int Type, Vector3 Position)> m_creatorPlaced = new(capacity: CreatorShapeCapacity);
    private bool m_creatorProgramDirty;

    /// <summary>Initializes the frame source over a world, its room, and the screen director that lays out the views.</summary>
    /// <param name="world">The deterministic simulation to present.</param>
    /// <param name="room">The authored room (the same instance the world's collision derived from).</param>
    /// <param name="director">The screen director that lays out the room view + console panes.</param>
    /// <param name="consoleAccents">An optional per-console accent color (albedo), by console index — the host passes
    /// the document consoles' costume colors; null falls back to the built-in dmg/cgb/agb palette.</param>
    /// <param name="controlsSource">An optional per-console reader of the joypad buttons currently applied to that
    /// console's machine this frame (see <see cref="Demo.GamingBrickChildNode.CurrentButtons"/>) — drives the
    /// control-cluster press animation. Null keeps every cluster in its neutral pose (the bare-room path).</param>
    public OverworldFrameSource(OverworldWorld world, OverworldRoom room, ScreenDirector director, IReadOnlyList<Vector3>? consoleAccents = null, Func<int, JoypadButtons>? controlsSource = null) {
        ArgumentNullException.ThrowIfNull(world);
        ArgumentNullException.ThrowIfNull(room);
        ArgumentNullException.ThrowIfNull(director);

        m_world = world;
        m_room = room;
        m_director = director;
        m_consoleAccents = (consoleAccents ?? DefaultAccents);
        m_controlsSource = controlsSource;

        // No physical cartridge instances any more (the cart choice lives at the cabinet); the control clusters follow
        // the player slots directly.
        m_controlSlotBase = OverworldWorld.MaxPlayers;
        // The creator pool sits after the control slots: one ghost slot, then CreatorShapeCapacity placed-shape slots.
        m_creatorSlotBase = (m_controlSlotBase + (m_room.Consoles.Count * ControlsPerConsole));
        m_dynamicTransformCount = (m_creatorSlotBase + 1 + CreatorShapeCapacity);
        m_dynamicTransforms = new DynamicTransform[m_dynamicTransformCount];
    }

    /// <summary>The ROOM view's normalized region as of the most recent <see cref="CaptureFrame"/> — the rect the
    /// screen director gave view 0 this frame (fullscreen before any console boots, shrinking through the staged
    /// layouts as panes light). The binding-bar overlay confines its cluster to it, so the controls hug the room
    /// view rather than painting across the console panes.</summary>
    public NormalizedRect LastRoomRegion { get; private set; } = new(X: 0f, Y: 0f, Width: 1f, Height: 1f);

    /// <summary>An optional per-console CLOSENESS for the pane cameras (0 = the wide room framing, 1 = right up on the
    /// cabinet's diegetic screen so the brick fills the pane). The overworld sets it per mode — immersed panes tight,
    /// break-out panes a medium shot, hidden panes 0. Combined with each cabinet's screen center to drive the director's
    /// per-pane cameras. Presentation-only.</summary>
    public Func<int, float>? PaneCloseness { get; set; }

    /// <summary>An optional PRESENTATION-ONLY position override per player slot: when it returns a world position for a
    /// slot, that player's avatar renders there instead of at its simulation body. The switchboard's brick→world
    /// coupling uses it to make a driving player's avatar FOLLOW their brick sprite around the room — machine state
    /// moving a presentation transform, deliberately OUT of the hashed simulation. Null (or a null result) = the sim
    /// body, as always.</summary>
    public Func<int, WorldCoord3?>? PresentationOverride { get; set; }

    /// <inheritdoc/>
    public SdfFrame CaptureFrame(uint width, uint height, float deltaSeconds, float interpolationAlpha) {
        m_time += deltaSeconds;

        // The world anchor is the room's spawn anchor, ALWAYS: the room is bounded, so every render-relative float
        // stays within a few units (precise no matter which cell the room sits in — subtracted in FIXED POINT before
        // the float cast), and the anchor NEVER moves mid-session (a moving anchor made the smoothed camera chase the
        // jump every time the players crossed its snap grid).
        var renderOrigin = m_world.SpawnAnchor;

        // Rebuild ONLY when a console boots (its screen material lights). The instruction count is unchanged, so the
        // renderer's once-sized program buffer stays valid.
        var bootedMask = m_world.BootedMask;
        // A boot lights a screen material; a creator cycle/place changes which primitive a creator slot draws — both
        // rebuild the (constant-length) program. A creator MOVE never rebuilds (it rides the dynamic-transform buffer).
        var programChanged = ((m_program is null) || (bootedMask != m_programBootedMask) || m_creatorProgramDirty);

        if (programChanged) {
            m_program = BuildProgram(bootedMask: bootedMask);
            m_programBootedMask = bootedMask;
            m_creatorProgramDirty = false;
        }

        // Render-relative, alpha-interpolated player positions for the camera director (reused list — no per-frame alloc).
        // A presentation override (a player driving their brick, whose avatar follows the game sprite) wins over the sim
        // body, so the camera frames the followed avatar.
        m_activePositions.Clear();

        for (var slot = 0; (slot < m_world.Slots.Count); slot++) {
            if (m_world.Slots[slot] is not { } player) {
                continue;
            }

            m_activePositions.Add(item: ((PresentationOverride?.Invoke(slot) is { } overridden)
                ? overridden.ToRenderRelative(origin: renderOrigin)
                : player.Body.RenderRelativePositionAt(renderOrigin: renderOrigin, alpha: interpolationAlpha)));
        }

        // Drive each pane camera from its cabinet's diegetic-screen center + the overworld's per-console closeness. The
        // screen center is a fixed room-local position; render-relative equals local here (the render origin is the
        // spawn anchor at local zero), so it lives in the same space as the active player positions the camera frames.
        m_director.PaneCameraSource = paneIndex => ((paneIndex >= 0) && (paneIndex < m_room.Consoles.Count)
            ? (ScreenCenterLocal(consoleIndex: paneIndex), (PaneCloseness?.Invoke(paneIndex) ?? 0f), ScreenHalfHeightLocal(consoleIndex: paneIndex))
            : ((Vector3, float, float)?)null);

        var views = m_director.Compose(activePositions: m_activePositions, bootOrder: m_world.BootOrder, imageWidth: width, imageHeight: height, deltaSeconds: deltaSeconds);

        // View 0 is always the room; stash its live rect for the binding-bar overlay (this runs INSIDE the producer's
        // ProduceFrame, so the overlay — which wraps the producer — always reads the region of the frame it draws over).
        LastRoomRegion = views[0].Region;

        PackDynamicTransforms(renderOrigin: renderOrigin, alpha: interpolationAlpha);

        // While immersed the room is UNLIT (RoomLightFactor 0), so the letterbox margins around a contained screen
        // render BLACK — a native handheld/emulator look — easing up to the arcade mood as the reveal lights the room.
        var roomLight = m_director.RoomLightFactor;

        return new SdfFrame(
            Program: m_program!, // non-null: programChanged is true whenever m_program was null, so it was just built
            ProgramChanged: programChanged,
            Views: views,
            Time: m_time,
            WarpAmount: 0f
        ) {
            AmbientScale = (OverworldAmbientScale * roomLight),
            DynamicTransforms = m_dynamicTransforms,
            SunScale = (OverworldSunScale * roomLight),
        };
    }

    // The room-local center of a console's diegetic screen — the SAME placement BuildProgram gives the screen slab (top
    // of the pedestal, sized to the GB aspect). Render-relative equals this here (the render origin is the spawn anchor
    // at local zero), so the camera can push in toward it.
    private Vector3 ScreenCenterLocal(int consoleIndex) {
        var stand = m_room.Consoles[consoleIndex];
        var screenHalfHeight = ((stand.HalfExtents.X * 0.8f) / ScreenAspect);

        return new Vector3(stand.Center.X, (m_room.FloorY + (2f * stand.HalfExtents.Y) + screenHalfHeight), stand.Center.Y);
    }

    // A console's diegetic-screen half-height (the SAME value BuildProgram/ScreenCenterLocal derive) — the pane camera
    // uses it to sit at exactly the distance that makes the screen fill the viewport height (the native-panel look).
    private float ScreenHalfHeightLocal(int consoleIndex) {
        var stand = m_room.Consoles[consoleIndex];

        return ((stand.HalfExtents.X * 0.8f) / ScreenAspect);
    }

    // Fills the unified dynamic-transform buffer for this frame: players (OverworldWorld's own slots, unchanged),
    // cartridges (shelf-resting / carried-above-head / seated-in-stand, by unified index), and per-console control
    // clusters (neutral pose, or depressed/tilted by the console's current joypad state). REUSED buffer, no
    // per-frame allocation.
    private void PackDynamicTransforms(WorldCoord3 renderOrigin, float alpha) {
        var players = m_world.DynamicTransforms(renderOrigin: renderOrigin, alpha: alpha);

        for (var slot = 0; (slot < OverworldWorld.MaxPlayers); slot++) {
            // A driving player's avatar follows their brick sprite (presentation override); everyone else renders at
            // their simulation body.
            m_dynamicTransforms[slot] = ((PresentationOverride?.Invoke(slot) is { } overridden)
                ? (players[slot] with { Position = overridden.ToRenderRelative(origin: renderOrigin) })
                : players[slot]);
        }

        PackControlTransforms();
        PackCreatorTransforms();
    }

    // The creator pool's per-frame transforms: the ghost rides its live position while the mode is active (hidden
    // below the floor otherwise), and each placed shape sits at its placement position; unused placed slots hide. The
    // GEOMETRY each slot draws is baked into the program (rebuilt on a cycle/place) — here only the positions move.
    private void PackCreatorTransforms() {
        var hidden = HiddenPosition();

        m_dynamicTransforms[m_creatorSlotBase] = new DynamicTransform(
            Orientation: Quaternion.Identity,
            Position: (m_creatorActive ? m_creatorGhostPosition : hidden)
        );

        for (var index = 0; (index < CreatorShapeCapacity); index++) {
            m_dynamicTransforms[m_creatorSlotBase + 1 + index] = new DynamicTransform(
                Orientation: Quaternion.Identity,
                Position: ((index < m_creatorPlaced.Count) ? m_creatorPlaced[index].Position : hidden)
            );
        }
    }

    // ---- Creator mode (the in-engine SDF authoring surface) ------------------------------------------------------
    // The host (OverworldRenderNode) drives these from the creating player's controller; all state here is presentation
    // only — the deterministic world/hash never sees a creator shape.

    /// <summary>Whether creator mode is active (the mode's ghost is visible and the player edits shapes).</summary>
    public bool CreatorActive => m_creatorActive;
    /// <summary>The selected primitive's name (for the console/HUD readout).</summary>
    public string CreatorShapeName => ((CreatorPrimitive)m_creatorGhostType).ToString();
    /// <summary>The selected primitive's index in the cycle (0-based).</summary>
    public int CreatorShapeIndex => m_creatorGhostType;
    /// <summary>How many shapes have been placed so far.</summary>
    public int CreatorPlacedCount => m_creatorPlaced.Count;
    /// <summary>The maximum number of shapes that can be placed.</summary>
    public static int CreatorCapacity => CreatorShapeCapacity;

    /// <summary>Enters or leaves creator mode. Entering re-seats the ghost at the room center; leaving keeps every
    /// placed shape. Toggling never rebuilds the program (the ghost geometry is present regardless — only its
    /// transform hides it), so it is a cheap per-frame decision.</summary>
    /// <param name="active">The desired state.</param>
    public void SetCreatorActive(bool active) {
        if (m_creatorActive == active) {
            return;
        }

        m_creatorActive = active;

        if (active) {
            // Spawn the ghost at the room center, floating a little above the floor so it reads immediately.
            m_creatorGhostPosition = new Vector3(
                (0.5f * (m_room.BoundsMin.X + m_room.BoundsMax.X)),
                (m_room.FloorY + 0.7f),
                (0.5f * (m_room.BoundsMin.Y + m_room.BoundsMax.Y))
            );
        }
    }

    /// <summary>Cycles the selected primitive (wraps both directions). Rebuilds the program (the ghost slot draws a
    /// different primitive).</summary>
    /// <param name="direction">+1 for the next primitive, -1 for the previous.</param>
    public void CreatorCycleShape(int direction) {
        if (!m_creatorActive) {
            return;
        }

        m_creatorGhostType = ((((m_creatorGhostType + direction) % CreatorPrimitiveCount) + CreatorPrimitiveCount) % CreatorPrimitiveCount);
        m_creatorProgramDirty = true;
    }

    /// <summary>Moves the ghost through the room this frame — planar on the floor plane, plus a vertical nudge —
    /// clamped inside the room bounds. A pure transform update (no rebuild).</summary>
    /// <param name="planar">The X/Z move (already in render space: +Y of the vector is +Z).</param>
    /// <param name="vertical">The up/down nudge (+ up).</param>
    /// <param name="deltaSeconds">The frame delta.</param>
    public void CreatorMove(Vector2 planar, float vertical, float deltaSeconds) {
        if (!m_creatorActive) {
            return;
        }

        const float moveSpeed = 3.2f;
        var next = (m_creatorGhostPosition + (new Vector3(planar.X, vertical, planar.Y) * (moveSpeed * deltaSeconds)));

        m_creatorGhostPosition = new Vector3(
            Math.Clamp(value: next.X, max: (m_room.BoundsMax.X - 0.3f), min: (m_room.BoundsMin.X + 0.3f)),
            Math.Clamp(value: next.Y, max: (m_room.FloorY + 3.0f), min: (m_room.FloorY + 0.35f)),
            Math.Clamp(value: next.Z, max: (m_room.BoundsMax.Y - 0.3f), min: (m_room.BoundsMin.Y + 0.3f))
        );
    }

    /// <summary>Places the ghost's current primitive at its current position (a no-op when the pool is full).
    /// Rebuilds the program (a placed slot now draws the placed primitive).</summary>
    public void CreatorPlace() {
        if (!m_creatorActive || (m_creatorPlaced.Count >= CreatorShapeCapacity)) {
            return;
        }

        m_creatorPlaced.Add(item: (m_creatorGhostType, m_creatorGhostPosition));
        m_creatorProgramDirty = true;
    }

    /// <summary>Removes the most recently placed shape (a no-op when nothing is placed). Rebuilds the program.</summary>
    public void CreatorUndo() {
        if (!m_creatorActive || (m_creatorPlaced.Count == 0)) {
            return;
        }

        m_creatorPlaced.RemoveAt(index: (m_creatorPlaced.Count - 1));
        m_creatorProgramDirty = true;
    }

    // Emits ONE creator primitive on a dynamic-transform slot. Every branch is ResetPoint + TransformDynamic + one
    // shape instruction, so the instruction/word count is IDENTICAL for every primitive — a rebuild that swaps a
    // slot's primitive never changes the program's size, so the engine's once-sized buffers stay valid.
    private static void EmitCreatorPrimitive(SdfProgramBuilder builder, int slot, int type, int material) {
        var chain = builder.ResetPoint().TransformDynamic(slot: slot);

        switch ((CreatorPrimitive)type) {
            case CreatorPrimitive.Box:
                _ = chain.Box(halfExtents: new Vector3(0.34f, 0.34f, 0.34f), round: 0.04f, material: material);

                break;
            case CreatorPrimitive.Torus:
                _ = chain.Torus(majorRadius: 0.30f, minorRadius: 0.12f, material: material);

                break;
            case CreatorPrimitive.Cylinder:
                _ = chain.Cylinder(radius: 0.30f, halfHeight: 0.36f, material: material);

                break;
            case CreatorPrimitive.Capsule:
                _ = chain.Capsule(endpoint: new Vector3(0f, 0.55f, 0f), radius: 0.20f, material: material);

                break;
            case CreatorPrimitive.Ellipsoid:
                _ = chain.Ellipsoid(radii: new Vector3(0.42f, 0.28f, 0.34f), material: material);

                break;
            default:
                _ = chain.Sphere(radius: 0.38f, material: material);

                break;
        }
    }

    // Each console's control cluster: a d-pad cross (tilts toward the held direction) and two round buttons (A/B,
    // depress a few centimeters when held). Reads the SAME per-frame joypad state the console's machine consumes —
    // never re-derived from raw input — so an unbooted/unassigned console (whose provider is absent from
    // m_controlsSource's backing dictionary, or simply never pressed) stays in its neutral pose.
    private void PackControlTransforms() {
        for (var consoleIndex = 0; (consoleIndex < m_room.Consoles.Count); consoleIndex++) {
            var stand = m_room.Consoles[consoleIndex];
            var buttons = (m_controlsSource?.Invoke(consoleIndex) ?? JoypadButtons.None);
            var anchor = ControlClusterAnchor(stand: stand);
            var dPadPressed = (buttons & (JoypadButtons.Left | JoypadButtons.Right | JoypadButtons.Up | JoypadButtons.Down));
            // Composed per-axis (not a single switch on the exact combo) so a diagonal press (Left+Up, a real GB
            // joypad state) tilts both axes at once instead of falling through to a neutral pose.
            var tiltZ = (((dPadPressed & JoypadButtons.Right) != 0) ? DPadTiltRadians : (((dPadPressed & JoypadButtons.Left) != 0) ? -DPadTiltRadians : 0f));
            var tiltX = (((dPadPressed & JoypadButtons.Up) != 0) ? DPadTiltRadians : (((dPadPressed & JoypadButtons.Down) != 0) ? -DPadTiltRadians : 0f));
            var dPadTilt = (Quaternion.CreateFromAxisAngle(axis: Vector3.UnitX, angle: tiltX) * Quaternion.CreateFromAxisAngle(axis: Vector3.UnitZ, angle: tiltZ));

            var baseSlot = (m_controlSlotBase + (consoleIndex * ControlsPerConsole));

            m_dynamicTransforms[baseSlot + DPadControlOffset] = new DynamicTransform(
                Position: (dPadPressed != JoypadButtons.None ? DepressedInward(position: anchor.DPad) : anchor.DPad),
                Orientation: dPadTilt
            );
            m_dynamicTransforms[baseSlot + AButtonControlOffset] = new DynamicTransform(
                Position: (((buttons & JoypadButtons.A) != 0) ? DepressedInward(position: anchor.A) : anchor.A),
                Orientation: Quaternion.Identity
            );
            m_dynamicTransforms[baseSlot + BButtonControlOffset] = new DynamicTransform(
                Position: (((buttons & JoypadButtons.B) != 0) ? DepressedInward(position: anchor.B) : anchor.B),
                Orientation: Quaternion.Identity
            );
        }
    }

    // A pressed control moves a few centimeters INTO the pedestal (−Z, against the front-face normal) — a uniform
    // depress, the cheap fallback the mission allows when a per-shape tilt isn't warranted (the buttons are round,
    // not directional).
    private static Vector3 DepressedInward(Vector3 position) =>
        (position - new Vector3(0f, 0f, ControlDepress));

    private (Vector3 DPad, Vector3 A, Vector3 B) ControlClusterAnchor(ConsoleStand stand) {
        // Centered-low on the pedestal's front face (+Z, the same face the screen and the stand's cartridge slot
        // share); the d-pad sits left of center, A/B sit right of center — a minimal cluster sized to read at room
        // scale without crowding the cartridge slot (offset to the LEFT of stand center; the cluster favors the
        // right half).
        var faceZ = (stand.Center.Y + stand.HalfExtents.Z);
        var faceY = (m_room.FloorY + (stand.HalfExtents.Y * 0.65f));
        var dPad = new Vector3((stand.Center.X - (stand.HalfExtents.X * 0.15f)), faceY, faceZ);
        var buttonA = new Vector3((stand.Center.X + (stand.HalfExtents.X * 0.45f)), (faceY + 0.06f), faceZ);
        var buttonB = new Vector3((stand.Center.X + (stand.HalfExtents.X * 0.25f)), (faceY - 0.06f), faceZ);

        return (dPad, buttonA, buttonB);
    }

    private Vector3 HiddenPosition() =>
        new(0f, (m_room.FloorY - 1000f), 0f);

    // The carried cartridge's hover offset above a player's interpolated render position — a small, fixed lift (no
    // wall-clock bob, so the render is a pure function of the already-interpolated player transform).
    private static readonly Vector3 CarryHoverOffset = new(0f, 0.9f, 0f);
    // The GB-cartridge-proportioned box: small, flat, and tall (a real brick cartridge's silhouette).
    private static readonly Vector3 CartridgeHalfExtents = new(0.14f, 0.18f, 0.02f);

    // Instance bounding-sphere radii (world units), each the worst-case reach of its wrapped geometry from the chosen
    // center plus a generous rounding/margin — a fat bound only costs a rare extra evaluation, a tight one clips
    // geometry at the tile boundary (the Post world-instanced stage's proven caller contract).
    // Stand: pedestal (half-extents length ~0.95) < screen slab (the farthest member, center offset + half-extents
    // length ~1.84) < housing/control cluster (~1.0) — 2.2 covers the worst member with ~20% headroom.
    private const float StandInstanceRadius = 2.2f;
    // Shelf bracket: half-extents length ~0.46 (anchor half-extents (0.3, 0.175, 0.3)) + round + margin.
    private const float ShelfInstanceRadius = 0.55f;
    // Cartridge: half-extents length ~0.23 (CartridgeHalfExtents) + round + carry-bob margin.
    private const float CartridgeInstanceRadius = 0.3f;
    // Player: half-extents length ~0.70 (PlayerHalfExtents) + round + margin.
    private const float PlayerInstanceRadius = 0.85f;

    private SdfProgram BuildProgram(uint bootedMask) {
        // The render anchor IS the spawn anchor, so the room is authored directly in the spawn cell's local frame
        // (origin delta identically zero); the per-slot player boxes ride the dynamic-transform buffer, which the
        // frame source already feeds render-relative to the same anchor.
        var origin = Vector3.Zero;
        var builder = new SdfProgramBuilder();
        var floorMaterial = builder.AddMaterial(material: new SdfMaterial(Albedo: new Vector3(0.34f, 0.36f, 0.42f)));
        var wallMaterial = builder.AddMaterial(material: new SdfMaterial(Albedo: new Vector3(0.50f, 0.46f, 0.58f)));
        var playerMaterial = builder.AddMaterial(material: new SdfMaterial(Albedo: new Vector3(0.93f, 0.52f, 0.18f)));
        var screenOffMaterial = builder.AddMaterial(material: new SdfMaterial(Albedo: new Vector3(0.08f, 0.09f, 0.10f)));
        var shelfMaterial = builder.AddMaterial(material: new SdfMaterial(Albedo: new Vector3(0.40f, 0.38f, 0.35f)));
        var controlMaterial = builder.AddMaterial(material: new SdfMaterial(Albedo: new Vector3(0.15f, 0.15f, 0.17f)));

        // Floor: a plane whose surface sits at world y = FloorY. In render-relative space (p = world − origin) the plane
        // equation dot(p, n) + offset must still vanish there, so offset = origin.Y − FloorY (= −FloorY when origin = 0).
        _ = builder.ResetPoint().Plane(normal: Vector3.UnitY, offset: (origin.Y - m_room.FloorY), material: floorMaterial);

        // Four perimeter walls as thin tall boxes. The half-thickness is the room's single source of truth, so these
        // visual walls and the collision clamp planes (FixedRoom.From) stay locked — a body rests flush against the
        // inner face drawn here.
        var wallHeight = 1.5f;
        var wallThickness = m_room.WallThickness;
        var minX = m_room.BoundsMin.X;
        var maxX = m_room.BoundsMax.X;
        var minZ = m_room.BoundsMin.Y;
        var maxZ = m_room.BoundsMax.Y;
        var midX = (0.5f * (minX + maxX));
        var midZ = (0.5f * (minZ + maxZ));
        var halfSpanX = (0.5f * (maxX - minX));
        var halfSpanZ = (0.5f * (maxZ - minZ));
        var wallCenterY = (m_room.FloorY + wallHeight);

        // Wall centers are rebased into render-relative space (− origin); the half-extents are differences, hence anchor-invariant.
        AddWall(builder: builder, center: (new Vector3(maxX, wallCenterY, midZ) - origin), halfExtents: new Vector3(wallThickness, wallHeight, halfSpanZ), material: wallMaterial);
        AddWall(builder: builder, center: (new Vector3(minX, wallCenterY, midZ) - origin), halfExtents: new Vector3(wallThickness, wallHeight, halfSpanZ), material: wallMaterial);
        AddWall(builder: builder, center: (new Vector3(midX, wallCenterY, maxZ) - origin), halfExtents: new Vector3(halfSpanX, wallHeight, wallThickness), material: wallMaterial);
        AddWall(builder: builder, center: (new Vector3(midX, wallCenterY, minZ) - origin), halfExtents: new Vector3(halfSpanX, wallHeight, wallThickness), material: wallMaterial);

        // The console stands: each is a pedestal (its accent color, boot target + obstacle) with a screen slab
        // sitting on top facing the room (+Z, toward the player area — the stands sit near the −Z wall). An UNBOOTED
        // stand's screen is the powered-off dark box; a boot swaps that one instruction for a diegetic screen-surface
        // slab (identical instruction count, constant material table) whose world frame MATCHES the geometry:
        // worldOrigin sits on the slab's front face (center + halfExtents.Z along +Z, the face normal),
        // worldRight/worldUp are the slab's local +X/+Y in world space (no rotation is applied to the point before
        // the Box, so they are simply +X/+Y). The screen-surface table therefore changes ONLY on boot rebuilds —
        // exactly when the program re-uploads anyway.
        //
        // Each stand is ONE static per-object instance: pedestal, screen, cartridge-slot patch, control housing, and
        // the stand's 3 control-cluster pieces (which ride their own dynamic-transform slots but only travel a few
        // centimeters around the stand) all fold into a single bound centered on the pedestal — StandInstanceRadius
        // is sized generously past the farthest member (the screen slab) so the beam prepass's tile cull never clips
        // any of them. The control pieces are emitted HERE (not in the trailing loop the flat layout used) so the
        // whole stand is one CONTIGUOUS instruction range — BeginInstance/EndInstance cannot straddle a gap.
        for (var index = 0; (index < m_room.Consoles.Count); index++) {
            var stand = m_room.Consoles[index];
            var accent = m_consoleAccents[index % m_consoleAccents.Count];
            var bodyMaterial = builder.AddMaterial(material: new SdfMaterial(Albedo: accent));
            // The lit material is ALWAYS added (constant material table across rebuilds — the program buffer is sized
            // once); the boot bit only selects which index the screen references.
            var screenLitMaterial = builder.AddMaterial(material: new SdfMaterial(Albedo: ((accent * 0.35f) + new Vector3(0.55f, 0.62f, 0.45f))));
            var screenMaterial = ((0u != (bootedMask & (1u << index))) ? screenLitMaterial : screenOffMaterial);
            var pedestalCenter = new Vector3(stand.Center.X, (m_room.FloorY + stand.HalfExtents.Y), stand.Center.Y);
            // Sized close to the GB's 10:9 aspect (width : height) so a bound screen source isn't grossly stretched.
            var screenHalfWidth = (stand.HalfExtents.X * 0.8f);
            var screenHalfExtents = new Vector3(screenHalfWidth, (screenHalfWidth / ScreenAspect), 0.08f);
            var screenCenter = new Vector3(stand.Center.X, (m_room.FloorY + (2f * stand.HalfExtents.Y) + screenHalfExtents.Y), stand.Center.Y);
            var screenFaceOrigin = (screenCenter + new Vector3(0f, 0f, screenHalfExtents.Z) - origin);
            var buttonMaterial = builder.AddMaterial(material: new SdfMaterial(Albedo: new Vector3(0.85f, 0.15f, 0.20f)));

            _ = builder.BeginInstance(boundCenter: pedestalCenter, boundRadius: StandInstanceRadius);
            _ = builder.ResetPoint().Translate(offset: (pedestalCenter - origin)).Box(halfExtents: stand.HalfExtents, round: 0.05f, material: bodyMaterial);

            if ((index < SdfProgramBuilder.MaxScreenSurfaces) && (0u != (bootedMask & (1u << index)))) {
                _ = builder.ResetPoint().Translate(offset: (screenCenter - origin)).ScreenSlab(
                    halfExtents: screenHalfExtents,
                    round: 0.03f,
                    worldOrigin: screenFaceOrigin,
                    worldRight: Vector3.UnitX,
                    worldUp: Vector3.UnitY,
                    screenIndex: index
                );
            } else {
                // Unbooted (or past the engine's 4-surface cap — MaxConsoles is exactly that cap, a full house, so
                // the index guard is defense in depth): the powered-off dark screen, exactly the pre-diegetic look —
                // the boot rebuild swaps this one instruction for the screen-surface slab above (identical
                // instruction count either way).
                _ = builder.ResetPoint().Translate(offset: (screenCenter - origin)).Box(halfExtents: screenHalfExtents, round: 0.03f, material: screenMaterial);
            }

            // The stand's cartridge slot: a shallow notch-colored patch on the pedestal's front face (a visual seat,
            // not an obstacle — cartridges resolve against the stand box itself), so an inserted-but-unbooted
            // cartridge has a place to visibly sit even before the screen lights.
            var slotCenter = new Vector3((stand.Center.X - (stand.HalfExtents.X * 0.55f)), (m_room.FloorY + (stand.HalfExtents.Y * 1.5f)), (stand.Center.Y + stand.HalfExtents.Z - 0.02f));

            _ = builder.ResetPoint().Translate(offset: (slotCenter - origin)).Box(halfExtents: new Vector3(0.16f, 0.20f, 0.02f), round: 0.01f, material: bodyMaterial);

            // The control cluster: a static housing bump behind the animated pieces (the d-pad/button shapes ride
            // their own dynamic-transform slots below), so the cluster reads as recessed into the pedestal.
            var anchor = ControlClusterAnchor(stand: stand);
            var housingCenter = new Vector3(((anchor.DPad.X + anchor.A.X) * 0.5f), anchor.DPad.Y, (stand.Center.Y + stand.HalfExtents.Z - 0.01f));

            _ = builder.ResetPoint().Translate(offset: (housingCenter - origin)).Box(halfExtents: new Vector3((stand.HalfExtents.X * 0.7f), 0.12f, 0.01f), round: 0.02f, material: controlMaterial);

            // The stand's animated control cluster: a d-pad cross (two crossed boxes) and two round buttons (A/B),
            // one dynamic-transform slot per piece — folded into THIS stand's instance (they ride a dynamic slot but
            // only travel a few centimeters, well inside StandInstanceRadius's margin). PackControlTransforms
            // depresses/tilts pressed pieces every frame; the instruction count here never changes.
            var baseSlot = (m_controlSlotBase + (index * ControlsPerConsole));

            _ = builder.ResetPoint().TransformDynamic(slot: (baseSlot + DPadControlOffset)).Box(halfExtents: new Vector3(0.09f, 0.03f, 0.015f), round: 0.005f, material: controlMaterial);
            _ = builder.ResetPoint().TransformDynamic(slot: (baseSlot + DPadControlOffset)).Box(halfExtents: new Vector3(0.03f, 0.09f, 0.015f), round: 0.005f, material: controlMaterial);
            _ = builder.ResetPoint().TransformDynamic(slot: (baseSlot + AButtonControlOffset)).Sphere(radius: 0.035f, material: buttonMaterial);
            _ = builder.ResetPoint().TransformDynamic(slot: (baseSlot + BButtonControlOffset)).Sphere(radius: 0.035f, material: buttonMaterial);
            _ = builder.EndInstance();
        }

        // The cartridge shelf: one static wall-mounted bracket per shelf slot (a simple slab, always present) plus
        // one cartridge box per shelf slot rides its OWN dynamic-transform slot below (so a picked-up cartridge can
        // leave the shelf without a program rebuild). One static instance PER SLOT (not one for the whole strip):
        // a full 8-slot shelf spans ~15 world units, so a single enclosing sphere would cover most of the room and
        // defeat the tile cull; per-slot bounds stay tight (ShelfInstanceRadius).
        for (var index = 0; (index < m_room.Shelf.Count); index++) {
            var anchor = m_room.Shelf[index];
            var bracketCenter = new Vector3(anchor.Center.X, (m_room.FloorY + (anchor.HalfExtents.Y * 0.5f)), anchor.Center.Y);

            _ = builder.BeginInstance(boundCenter: bracketCenter, boundRadius: ShelfInstanceRadius);
            _ = builder.ResetPoint().Translate(offset: (bracketCenter - origin)).Box(halfExtents: new Vector3(anchor.HalfExtents.X, (anchor.HalfExtents.Y * 0.5f), anchor.HalfExtents.Z), round: 0.03f, material: shelfMaterial);
            _ = builder.EndInstance();
        }

        // One player box per FIXED slot, placed by its per-frame dynamic transform (active slots at the player, free
        // slots hidden below the floor). Built once — never rebuilt as players join or leave. One dynamic instance
        // per slot: the bound tracks the slot's own position (boundOffset zero — the box is centered on the slot),
        // so a free/hidden slot's instance simply culls to nothing productive rather than needing special-casing.
        for (var slot = 0; (slot < OverworldWorld.MaxPlayers); slot++) {
            _ = builder.BeginInstanceDynamic(slot: slot, boundOffset: Vector3.Zero, boundRadius: PlayerInstanceRadius);
            _ = builder.ResetPoint().TransformDynamic(slot: slot).Box(halfExtents: m_room.PlayerHalfExtents, round: 0.06f, material: playerMaterial);
            _ = builder.EndInstance();
        }

        // The CREATOR pool: the ghost (the shape being placed, a bright accent so it reads as a preview) plus one
        // instance per placed-shape slot. Emitted every rebuild with a CONSTANT material and instruction count — an
        // unused placed slot draws a default sphere hidden below the floor (PackCreatorTransforms), so the engine's
        // buffers are sized for the full pool from frame 0 and a cycle/place never has to grow them.
        var ghostMaterial = builder.AddMaterial(material: new SdfMaterial(Albedo: new Vector3(0.35f, 0.92f, 1.0f)));

        _ = builder.BeginInstanceDynamic(slot: m_creatorSlotBase, boundOffset: Vector3.Zero, boundRadius: CreatorInstanceRadius);
        EmitCreatorPrimitive(builder: builder, slot: m_creatorSlotBase, type: m_creatorGhostType, material: ghostMaterial);
        _ = builder.EndInstance();

        for (var index = 0; (index < CreatorShapeCapacity); index++) {
            var slot = (m_creatorSlotBase + 1 + index);
            var type = ((index < m_creatorPlaced.Count) ? m_creatorPlaced[index].Type : (int)CreatorPrimitive.Sphere);
            var material = builder.AddMaterial(material: new SdfMaterial(Albedo: CartridgeHue(index: index)));

            _ = builder.BeginInstanceDynamic(slot: slot, boundOffset: Vector3.Zero, boundRadius: CreatorInstanceRadius);
            EmitCreatorPrimitive(builder: builder, slot: slot, type: type, material: material);
            _ = builder.EndInstance();
        }

        return builder.Build();
    }
    private static void AddWall(SdfProgramBuilder builder, Vector3 center, Vector3 halfExtents, int material) {
        _ = builder.ResetPoint().Translate(offset: center).Box(halfExtents: halfExtents, round: 0f, material: material);
    }
    // A per-index accent hue so the shelf's cartridges (and a stand's inserted one) read as distinct titles — cheap,
    // deterministic HSV-ish sweep (no allocation, no randomness).
    private static Vector3 CartridgeHue(int index) {
        var hue = ((index * 0.61803399f) % 1f); // golden-ratio sweep — well-separated hues for small index counts
        var (r, g, b) = HueToRgb(hue: hue);

        return new Vector3((0.35f + (0.5f * r)), (0.35f + (0.5f * g)), (0.35f + (0.5f * b)));
    }
    private static (float R, float G, float B) HueToRgb(float hue) {
        var h6 = (hue * 6f);
        var x = (1f - MathF.Abs(((h6 % 2f) - 1f)));

        return ((int)h6 switch {
            0 => (1f, x, 0f),
            1 => (x, 1f, 0f),
            2 => (0f, 1f, x),
            3 => (0f, x, 1f),
            4 => (x, 0f, 1f),
            _ => (1f, 0f, x),
        });
    }
}
