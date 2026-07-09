using System.Numerics;
using Puck.Cameras;
using Puck.Compositing;

namespace Puck.SdfVm;

public readonly record struct SdfViewSnapshot(CameraSnapshot Camera, NormalizedRect Region);

/// <summary>One moving entity's rigid transform for a frame: a world position and an orientation. The renderer uploads
/// these into the per-frame dynamic-transform buffer the <c>SdfOp.TransformDynamic</c> opcode indexes by slot, so an
/// entity moves without rebuilding the scene program. The slot is the entity's index in <see cref="SdfFrame.DynamicTransforms"/>.</summary>
public readonly record struct DynamicTransform(Vector3 Position, Quaternion Orientation);
public sealed record SdfFrame(
    SdfProgram Program,
    bool ProgramChanged,
    IReadOnlyList<SdfViewSnapshot> Views,
    float Time,
    float WarpAmount
) {
    /// <summary>Per-frame transforms for the scene's moving entities, indexed by dynamic-transform slot. Must supply
    /// at least the program's <see cref="SdfProgram.RequiredDynamicTransformCapacity"/> entries (the render frame
    /// throws otherwise — a dynamic slot silently rendering at identity is a bug, not a default); empty is therefore
    /// valid only for a program with no dynamic slots (the renderer then binds a single identity slot the program
    /// never references). Updating this list is how entities move — the program (binding 1) is uploaded once and left
    /// untouched.</summary>
    public IReadOnlyList<DynamicTransform> DynamicTransforms { get; init; } = [];
    /// <summary>A per-frame scale on the world path's AMBIENT term (default 1 = unchanged). Below 1 dims the room so
    /// the diegetic screen glow dominates — the overworld sets it low for mood; other scenes leave the default.</summary>
    public float AmbientScale { get; init; } = 1f;
    /// <summary>A per-frame scale on the world path's SUN (directional) term (default 1 = unchanged). Pairs with
    /// <see cref="AmbientScale"/> to darken the room for the overworld mood.</summary>
    public float SunScale { get; init; } = 1f;
    /// <summary>The SLICE debug view's plane selector: 0 (the default) = camera-locked (the plane through the world
    /// origin with normal = camera forward), 1/2/3 = a world-axis-aligned plane (X/Y/Z normal) at
    /// <see cref="DebugSliceOffset"/> along that axis. Rides the screen-light buffer's environment entry's two spare
    /// lanes (KEEP IN SYNC with sdf-world.hlsli's <c>sdfScreenLights</c> env decode and
    /// <c>SdfWorldEngine.PackScreenLights</c>) — no new upload plumbing. Read only by debug view mode 7 (slice);
    /// every other mode ignores it, so the default demo is byte-unchanged.</summary>
    public float DebugSliceAxis { get; init; }
    /// <summary>The axis-aligned slice plane's signed offset along the <see cref="DebugSliceAxis"/> axis (world
    /// units). Ignored while <see cref="DebugSliceAxis"/> is 0 (camera-locked).</summary>
    public float DebugSliceOffset { get; init; }
    /// <summary>The grid-lock overlay flags (bit0 = draw the world floor grid, bit1 = draw the object grid). Rides
    /// the screen-light buffer's grid rows 9..12 (KEEP IN SYNC with <c>SdfWorldEngine.PackScreenLights</c> and
    /// sdf-world.hlsli's <c>SdfGridWorld..SdfGridObjParams</c> decode). Default 0 = no overlay, so a frame that never
    /// sets it uploads the same zeros as before.</summary>
    public uint GridFlags { get; init; }
    /// <summary>The world floor grid's per-axis lattice pitch on X/Z (world units); 0 disables the grid on that axis.</summary>
    public Vector2 GridWorldPitch { get; init; }
    /// <summary>The floor plane height the world grid draws on (the overlay gates on the surface being near this Y).</summary>
    public float GridFloorY { get; init; }
    /// <summary>The object grid's reference frame origin (world space).</summary>
    public Vector3 GridObjectOrigin { get; init; }
    /// <summary>The object grid's reference frame orientation (the lattice renders in this frame's coordinates).</summary>
    public Quaternion GridObjectFrame { get; init; } = Quaternion.Identity;
    /// <summary>The object grid's per-axis in-plane pitch (reference-local X/Z).</summary>
    public Vector2 GridObjectPitch { get; init; }
    /// <summary>The object grid's finite-patch radius (reference-local units); 0 disables the object grid.</summary>
    public float GridObjectPatchRadius { get; init; }
}
