using System.Numerics;
using Puck.Assets.Documents;

namespace Puck.World.Client;

/// <summary>What the pointer of one seat rests on: the point of the surface under it, in world space.</summary>
/// <param name="Point">The hit point, in world space.</param>
/// <param name="Normal">The surface's unit normal at the hit, or zero when the query gives none.</param>
public readonly record struct WorldEditorPointerHit(Vector3 Point, Vector3 Normal);
/// <summary>The ray one seat aims along, in world space.</summary>
/// <param name="Origin">The ray's origin, on the seat camera's near plane.</param>
/// <param name="Direction">The ray's unit direction.</param>
public readonly record struct WorldEditorRay(Vector3 Origin, Vector3 Direction);
/// <summary>Each seat's editor state beyond the document: the grid and snapping values a live <c>world.grid</c> or
/// <c>world.snap</c> has moved for that seat, the captured snap reference, and the placement the seat last put down or
/// moved. A value no verb has moved reads through to the document's <c>editor</c> section, so a reload that changes the
/// section reaches every seat that never overrode it, and a seat's own overrides survive the reload. Presentation state:
/// nothing here reaches the simulation.</summary>
public sealed class WorldEditorSeats {
    private readonly Seat[] m_seats;

    /// <summary>Initializes a new instance of the <see cref="WorldEditorSeats"/> class with no seat's value moved.</summary>
    public WorldEditorSeats() {
        m_seats = new Seat[PlayerRoster.MaxSlots];

        for (var slot = 0; (slot < m_seats.Length); slot++) {
            m_seats[slot] = new Seat();
        }
    }

    /// <summary>Gets or sets the probe the presentation asks for the ray a seat aims along, by slot: through its pointer
    /// when the pointer is over its view, otherwise through the middle of its view; <see langword="null"/> when the seat
    /// presents no view.</summary>
    public Func<int, WorldEditorRay?>? AimProbe { get; set; }
    /// <summary>Gets or sets the probe the presentation asks where a ray first meets the solid surfaces of the world a
    /// seat is presented in, by slot, ray (in that world's coordinates) and distance in world units; it answers
    /// <see langword="null"/> when the ray meets none, or the presentation has no queryable field for that world.</summary>
    public Func<int, WorldEditorRay, float, WorldEditorPointerHit?>? SurfaceProbe { get; set; }
    /// <summary>Gets or sets the probe that answers whether a seat's principal may edit placements, by slot;
    /// <see langword="null"/> answers that every seat may.</summary>
    public Func<int, bool>? EditProbe { get; set; }
    /// <summary>Gets or sets the probe the presentation asks for the surface under a seat's pointer, by slot; it answers
    /// <see langword="null"/> when the seat has no pointer over its view or the ray meets nothing.</summary>
    public Func<int, WorldEditorPointerHit?>? PointerProbe { get; set; }
    /// <summary>Gets a revision that moves whenever any seat's editor state moves.</summary>
    public long Revision { get; private set; }

    private Seat At(int slot) {
        ArgumentOutOfRangeException.ThrowIfNegative(value: slot);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(other: m_seats.Length, value: slot);

        return m_seats[slot];
    }
    private void Moved() => Revision++;

    /// <summary>Returns a seat's grid: the document's, with every value the seat has moved in its place.</summary>
    /// <param name="slot">The seat.</param>
    /// <param name="document">The document's <c>editor</c> section.</param>
    /// <returns>The seat's grid.</returns>
    public WorldEditorGrid GridOf(int slot, WorldEditorDefaults document) {
        ArgumentNullException.ThrowIfNull(argument: document);

        var seat = At(slot: slot);
        var grid = document.ResolvedGrid;

        return (seat.HasGridOverride
            ? new WorldEditorGrid(
                LineWidth: grid.LineWidth,
                Mode: (seat.GridMode ?? grid.Mode),
                Pitch: new DocumentVector3(value: (seat.GridPitch ?? grid.ResolvedPitch)),
                PlaneY: (seat.PlaneY ?? grid.PlaneY),
                Visible: (seat.GridVisible ?? grid.Visible)
            )
            : grid);
    }
    /// <summary>Returns a seat's snapping: the document's, with every value the seat has moved in its place.</summary>
    /// <param name="slot">The seat.</param>
    /// <param name="document">The document's <c>editor</c> section.</param>
    /// <returns>The seat's snapping.</returns>
    public WorldEditorSnap SnapOf(int slot, WorldEditorDefaults document) {
        ArgumentNullException.ThrowIfNull(argument: document);

        var seat = At(slot: slot);
        var snap = document.ResolvedSnap;

        return (seat.HasSnapOverride
            ? new WorldEditorSnap(
                AngleStepDegrees: (seat.AngleStepDegrees ?? snap.AngleStepDegrees),
                Enabled: (seat.SnapEnabled ?? snap.Enabled),
                ObjectPatchRadius: snap.ObjectPatchRadius,
                Surface: (seat.SurfaceSnap ?? snap.Surface)
            )
            : snap);
    }
    /// <summary>Returns the placement a seat's object grid and reference snapping align to in a world, or
    /// <see langword="null"/> for none: a reference captured in another world does not reach this one.</summary>
    /// <param name="slot">The seat, zero-based.</param>
    /// <param name="world">The world's instance name, as the seat's authority route names it.</param>
    /// <returns>The placement's id.</returns>
    public string? ReferenceOf(int slot, string world) => (At(slot: slot).InWorld(world: world)
        ? At(slot: slot).Reference
        : null);
    /// <summary>Returns the placement a seat last put down or moved in a world, which a bound nudge or turn acts on, or
    /// <see langword="null"/> for none: a selection made in another world does not reach this one.</summary>
    /// <param name="slot">The seat, zero-based.</param>
    /// <param name="world">The world's instance name, as the seat's authority route names it.</param>
    /// <returns>The placement's id.</returns>
    public string? CurrentOf(int slot, string world) => (At(slot: slot).InWorld(world: world)
        ? At(slot: slot).Current
        : null);
    /// <summary>Returns the height the seat's working plane last followed in a world, or <see langword="null"/> when
    /// it has followed nothing there yet.</summary>
    /// <param name="slot">The seat, zero-based.</param>
    /// <param name="world">The world's instance name, as the seat's authority route names it.</param>
    /// <returns>The height, in world units.</returns>
    public float? FollowedHeightOf(int slot, string world) => (At(slot: slot).InWorld(world: world)
        ? At(slot: slot).FollowedHeight
        : null);
    /// <summary>Returns whether a seat's principal may not edit placements, which its build bar badges.</summary>
    /// <param name="slot">The seat, zero-based.</param>
    /// <returns><see langword="true"/> when <see cref="EditProbe"/> refuses the seat.</returns>
    public bool IsReadOnly(int slot) => ((EditProbe is { } probe) && !probe(arg: slot));
    /// <summary>Sets whether a seat's grid draws.</summary>
    /// <param name="slot">The seat, zero-based.</param>
    /// <param name="visible">Whether the grid draws.</param>
    public void SetGridVisible(int slot, bool visible) {
        At(slot: slot).GridVisible = visible;
        Moved();
    }
    /// <summary>Sets where a seat's grid draws; <see cref="WorldEditorGridMode.Plane"/> with a height also sets the
    /// plane's height.</summary>
    /// <param name="slot">The seat, zero-based.</param>
    /// <param name="mode">Where the grid draws.</param>
    /// <param name="planeY">The working plane's height, in world units, or <see langword="null"/> to keep it.</param>
    public void SetGridMode(int slot, WorldEditorGridMode mode, float? planeY = null) {
        var seat = At(slot: slot);

        seat.GridMode = mode;

        if (planeY is { } height) {
            seat.PlaneY = height;
        }

        Moved();
    }
    /// <summary>Sets a seat's grid pitch.</summary>
    /// <param name="slot">The seat, zero-based.</param>
    /// <param name="pitch">The pitch on each axis, in world units.</param>
    /// <exception cref="ArgumentOutOfRangeException">A component of <paramref name="pitch"/> is not finite and
    /// positive.</exception>
    public void SetGridPitch(int slot, Vector3 pitch) {
        if (!(float.IsFinite(f: pitch.X) && float.IsFinite(f: pitch.Y) && float.IsFinite(f: pitch.Z) && (pitch.X > 0f) && (pitch.Y > 0f) && (pitch.Z > 0f))) {
            throw new ArgumentOutOfRangeException(
                actualValue: pitch,
                message: "A grid pitch is finite and positive on every axis.",
                paramName: nameof(pitch)
            );
        }

        At(slot: slot).GridPitch = pitch;
        Moved();
    }
    /// <summary>Sets whether a seat's positions snap.</summary>
    /// <param name="slot">The seat, zero-based.</param>
    /// <param name="enabled">Whether positions snap.</param>
    public void SetSnapEnabled(int slot, bool enabled) {
        At(slot: slot).SnapEnabled = enabled;
        Moved();
    }
    /// <summary>Sets a seat's angle step.</summary>
    /// <param name="slot">The seat, zero-based.</param>
    /// <param name="degrees">The step, in degrees.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="degrees"/> is outside (0, 180].</exception>
    public void SetAngleStep(int slot, float degrees) {
        if (!((degrees > 0f) && (degrees <= 180f))) {
            throw new ArgumentOutOfRangeException(
                actualValue: degrees,
                message: "An angle step is within (0, 180] degrees.",
                paramName: nameof(degrees)
            );
        }

        At(slot: slot).AngleStepDegrees = degrees;
        Moved();
    }
    /// <summary>Sets whether a seat's placements rest on the surface they are put down on.</summary>
    /// <param name="slot">The seat, zero-based.</param>
    /// <param name="surface">Whether placing rests on the surface.</param>
    public void SetSurfaceSnap(int slot, bool surface) {
        At(slot: slot).SurfaceSnap = surface;
        Moved();
    }
    /// <summary>Sets or clears the placement a seat's object grid and reference snapping align to in a world, forgetting
    /// the seat's selection in any other world.</summary>
    /// <param name="slot">The seat, zero-based.</param>
    /// <param name="world">The world's instance name, as the seat's authority route names it.</param>
    /// <param name="placement">The placement's id, or <see langword="null"/> to clear it.</param>
    public void SetReference(int slot, string world, string? placement) {
        At(slot: slot).Enter(world: world).Reference = placement;
        Moved();
    }
    /// <summary>Sets the placement a seat last put down or moved in a world, forgetting the seat's selection in any
    /// other world.</summary>
    /// <param name="slot">The seat, zero-based.</param>
    /// <param name="world">The world's instance name, as the seat's authority route names it.</param>
    /// <param name="placement">The placement's id, or <see langword="null"/> for none.</param>
    public void SetCurrent(int slot, string world, string? placement) {
        At(slot: slot).Enter(world: world).Current = placement;
        Moved();
    }
    /// <summary>Retains each seat's selection and snap reference only while the rebuilt world still declares that id.</summary>
    /// <param name="world">The rebuilt world's instance name.</param>
    /// <param name="placements">The rebuilt world's placement rows.</param>
    /// <remarks>Grid, snap, camera and build-mode state are not reset by a document reload.</remarks>
    public void Reconcile(string world, IReadOnlyList<WorldPlacement> placements) {
        ArgumentNullException.ThrowIfNull(placements);
        foreach (var seat in m_seats) {
            if (!seat.InWorld(world: world)) { continue; }
            if ((seat.Current is { } current) && !placements.Any(predicate: row => (row.Id == current))) {
                seat.Current = null;
                Moved();
            }
            if ((seat.Reference is { } reference) && !placements.Any(predicate: row => (row.Id == reference))) {
                seat.Reference = null;
                Moved();
            }
        }
    }
    /// <summary>Records the height a seat's following working plane rests at in a world.</summary>
    /// <param name="slot">The seat, zero-based.</param>
    /// <param name="world">The world's instance name, as the seat's authority route names it.</param>
    /// <param name="height">The height, in world units.</param>
    public void Follow(int slot, string world, float height) {
        var seat = At(slot: slot).Enter(world: world);

        if (seat.FollowedHeight != height) {
            seat.FollowedHeight = height;
            Moved();
        }
    }
    /// <summary>Forgets everything one seat moved, as a seat leaving does.</summary>
    /// <param name="slot">The seat, zero-based.</param>
    public void Reset(int slot) {
        m_seats[slot] = new Seat();
        Moved();
    }
    /// <summary>Folds the primary seat's moved grid and snapping values into the document's section: the authored
    /// section unchanged when the seat moved nothing, otherwise the authored section, or the defaults when absent,
    /// with the seat's values in place.</summary>
    /// <param name="authored">The section the document authors, or <see langword="null"/> when it authors none.</param>
    /// <returns>The section to save.</returns>
    public WorldEditorDefaults? Fold(WorldEditorDefaults? authored) {
        var seat = m_seats[0];

        if (!(seat.HasGridOverride || seat.HasSnapOverride)) {
            return authored;
        }

        var basis = (authored ?? WorldEditorDefaults.Default);

        return (basis with {
            Grid = (seat.HasGridOverride ? GridOf(document: basis, slot: 0) : basis.Grid),
            Snap = (seat.HasSnapOverride ? SnapOf(document: basis, slot: 0) : basis.Snap),
        });
    }

    private sealed class Seat {
        public float? AngleStepDegrees { get; set; }
        public string? Current { get; set; }
        public float? FollowedHeight { get; set; }
        public WorldEditorGridMode? GridMode { get; set; }
        public Vector3? GridPitch { get; set; }
        public bool? GridVisible { get; set; }
        public bool HasGridOverride => ((GridVisible is not null) || (GridMode is not null) || (GridPitch is not null) || (PlaneY is not null));
        public bool HasSnapOverride => ((SnapEnabled is not null) || (AngleStepDegrees is not null) || (SurfaceSnap is not null));
        public float? PlaneY { get; set; }
        public string? Reference { get; set; }
        public bool? SnapEnabled { get; set; }
        public bool? SurfaceSnap { get; set; }
        // The world the selection (current placement, reference, followed height) was made in.
        public string? World { get; private set; }

        // Makes `world` the selection's world, forgetting a selection made in another.
        public Seat Enter(string world) {
            if (!InWorld(world: world)) {
                Current = null;
                FollowedHeight = null;
                Reference = null;
                World = world;
            }

            return this;
        }
        public bool InWorld(string world) => string.Equals(
            a: World,
            b: world,
            comparisonType: StringComparison.Ordinal
        );
    }
}
