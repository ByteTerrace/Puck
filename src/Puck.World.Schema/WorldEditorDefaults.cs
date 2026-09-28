using System.Numerics;
using System.Text.Json.Serialization;
using Puck.Abstractions.Documents;
using Puck.Assets.Documents;

namespace Puck.World;

/// <summary>Where a builder's world grid draws.</summary>
[JsonConverter(typeof(StrictEnumConverter<WorldEditorGridMode>))]
public enum WorldEditorGridMode {
    /// <summary>On every surface the view hits, projected along the surface normal: floors at any height, walls and
    /// slopes alike.</summary>
    Surface,
    /// <summary>On a horizontal working plane at the height of the surface under the seat's pointer, moving with it.</summary>
    Follow,
    /// <summary>On a horizontal working plane at a fixed height (<see cref="WorldEditorGrid.PlaneY"/>).</summary>
    Plane,
}
/// <summary>The editor grid a seat in build mode starts from.</summary>
/// <param name="Visible">Whether the grid draws.</param>
/// <param name="Mode">Where the world grid draws.</param>
/// <param name="Pitch">The world lattice's pitch on X, Y and Z, in world units; every component positive.</param>
/// <param name="PlaneY">The working plane's height, in world units, when the mode is <see cref="WorldEditorGridMode.Plane"/>.</param>
/// <param name="LineWidth">The drawn line's width, in pixels; positive.</param>
public sealed record WorldEditorGrid(
    bool Visible = false,
    WorldEditorGridMode Mode = WorldEditorGridMode.Surface,
    DocumentVector3? Pitch = null,
    float PlaneY = 0f,
    float LineWidth = 1.5f
) {
    /// <summary>The pitch a grid takes when <see cref="Pitch"/> is absent, in world units.</summary>
    public const float DefaultPitch = 0.5f;

    /// <summary>Gets the lattice pitch, <see cref="DefaultPitch"/> on each axis when absent.</summary>
    [JsonIgnore]
    public Vector3 ResolvedPitch => (Pitch?.Value ?? new Vector3(value: DefaultPitch));
}
/// <summary>How a seat in build mode snaps what it places, moves and turns.</summary>
/// <param name="Enabled">Whether positions snap to the grid.</param>
/// <param name="AngleStepDegrees">The angle a turn steps and a rotation snaps to, in degrees; in (0, 180].</param>
/// <param name="Surface">Whether a placement rests on the surface under the pointer.</param>
/// <param name="ObjectPatchRadius">How far a captured reference's own grid reaches around it, in multiples of its
/// largest half extent; positive.</param>
public sealed record WorldEditorSnap(
    bool Enabled = false,
    float AngleStepDegrees = 15f,
    bool Surface = true,
    float ObjectPatchRadius = 2.5f
);
/// <summary>The editor camera's feel, which the editor camera reads.</summary>
/// <param name="OrbitRate">The orbit rate at full input, in radians per second; positive.</param>
/// <param name="PanRate">The pan rate at full input, in world units per second; positive.</param>
/// <param name="ZoomRate">The exponential dolly rate at full input, per second; positive.</param>
/// <param name="FlyRate">The fly rate at full input, in world units per second; positive.</param>
/// <param name="MinPitch">The lowest orbit pitch, in radians; above -π/2 and below <paramref name="MaxPitch"/>.</param>
/// <param name="MaxPitch">The highest orbit pitch, in radians; below π/2.</param>
/// <param name="MinDistance">The nearest orbit distance, in world units; positive and below
/// <paramref name="MaxDistance"/>.</param>
/// <param name="MaxDistance">The farthest orbit distance, in world units.</param>
public sealed record WorldEditorCamera(
    float OrbitRate = 2.4f,
    float PanRate = 3f,
    float ZoomRate = 1.2f,
    float FlyRate = 6f,
    float MinPitch = 0.05f,
    float MaxPitch = 1.35f,
    float MinDistance = 0.5f,
    float MaxDistance = 14f
);
/// <summary>The <c>editor</c> section: what a seat in build mode starts from. The grid starts hidden and snapping off,
/// so a world that authors nothing plays exactly as it does without one. A live <c>world.grid</c> or
/// <c>world.snap</c> overrides a value for its seat, and <c>world.save</c> folds the primary seat's overrides back into
/// the section.</summary>
/// <param name="Grid">The grid; absent takes every grid default.</param>
/// <param name="Snap">The snapping; absent takes every snapping default.</param>
/// <param name="Camera">The editor camera's feel; absent takes every camera default.</param>
public sealed record WorldEditorDefaults(
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] WorldEditorGrid? Grid = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] WorldEditorSnap? Snap = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] WorldEditorCamera? Camera = null
) {
    /// <summary>Gets the defaults a world that authors no <c>editor</c> section builds with.</summary>
    public static WorldEditorDefaults Default { get; } = new();

    /// <summary>Gets the grid, its defaults when absent.</summary>
    [JsonIgnore]
    public WorldEditorGrid ResolvedGrid => (Grid ?? DefaultGrid);
    /// <summary>Gets the snapping, its defaults when absent.</summary>
    [JsonIgnore]
    public WorldEditorSnap ResolvedSnap => (Snap ?? DefaultSnap);
    /// <summary>Gets the editor camera's feel, its defaults when absent.</summary>
    [JsonIgnore]
    public WorldEditorCamera ResolvedCamera => (Camera ?? DefaultCamera);

    private static readonly WorldEditorGrid DefaultGrid = new();
    private static readonly WorldEditorSnap DefaultSnap = new();
    private static readonly WorldEditorCamera DefaultCamera = new();
}
