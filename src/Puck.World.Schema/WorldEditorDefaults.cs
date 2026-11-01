using System.Text.Json.Serialization;
using Puck.Abstractions.Documents;
using Puck.Assets.Documents;
using Puck.Commands;

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
public sealed record WorldEditorGrid(bool Visible, WorldEditorGridMode Mode, DocumentVector3 Pitch, float PlaneY, float LineWidth);
/// <summary>How a seat in build mode snaps what it places, moves and turns.</summary>
/// <param name="Enabled">Whether positions snap to the grid.</param>
/// <param name="AngleStepDegrees">The angle a turn steps and a rotation snaps to, in degrees; in (0, 180].</param>
/// <param name="Surface">Whether a placement rests on the surface under the pointer.</param>
/// <param name="ObjectPatchRadius">How far a captured reference's own grid reaches around it, in multiples of its
/// largest half extent; positive.</param>
public sealed record WorldEditorSnap(bool Enabled, float AngleStepDegrees, bool Surface, float ObjectPatchRadius);
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
public sealed record WorldEditorCamera(float OrbitRate, float PanRate, float ZoomRate, float FlyRate, float MinPitch, float MaxPitch,
    float MinDistance, float MaxDistance);
/// <summary>The <c>editor</c> section: the design a seat in build mode works with. A world that authors none has no
/// build controls, grid or snapping: <c>player.build</c> still switches the seat's mode, and the editor verbs refuse
/// by name. A live <c>world.grid</c> or <c>world.snap</c> overrides a value for its seat, and <c>world.save</c> folds
/// the primary seat's overrides back into the section.</summary>
/// <param name="Grid">The grid a building seat starts from.</param>
/// <param name="Snap">The snapping a building seat starts from.</param>
/// <param name="Camera">The editor camera's feel.</param>
/// <param name="Toggle">The binding entry that flips a seat between playing and building, composed beneath the resting
/// page of every group the world's binding layers declare, so a seat reaches build mode from wherever it rests. The
/// world's own layers compose over it, so a page that binds the entry's id or one of its sources keeps its own
/// meaning.</param>
/// <param name="Bar">The binding bar a building seat shows when its own bar places no bank for a page this bar
/// shows.</param>
/// <param name="ReadOnlyLabel">The page label a building seat's bar shows in place of its page's own when the seat's
/// principal may not edit placements.</param>
public sealed record WorldEditorDefaults(WorldEditorGrid Grid, WorldEditorSnap Snap, WorldEditorCamera Camera,
    BindingPageEntryDefinition Toggle, WorldBindingBarAuthoring Bar, string ReadOnlyLabel);
