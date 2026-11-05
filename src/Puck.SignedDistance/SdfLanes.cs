namespace Puck.SignedDistance;

/// <summary>
/// The render lanes a shading-time consumer reads (a weathering's amount, an emission's switch, a flow's velocity and
/// coverage, a volume's intensity): eight anonymous numbers in two scopes. Lanes 0 through 3 are the surface's own, the
/// <see cref="DynamicTransform.Lanes"/> of the frame slot it rides (an inhabited body's look lanes; zero on a surface
/// with no slot or a slot that carries none). Lanes 4 through 7 are the world's, the frame's
/// <c>SdfFrame.WorldLanes</c> resolved from <c>render.lanes</c> against the presented state, the same for every surface,
/// so a static copy sharing one baked mesh reads them with no slot and no per-instance record. The kernels resolve a lane
/// through one function, <c>sdfLane</c> (<c>frame/sdf-lanes.hlsli</c>), and the world lanes enter them through one pass
/// value, <c>SdfWorldPackage.WorldLanes</c>. A bake-time consumer (a shape's erosion, a morph) reads the riding slot's
/// lanes alone (<see cref="IsSlotLane"/>): the world's are presentation and never key a mesh.
/// </summary>
public static class SdfLanes {
    /// <summary>The lanes a frame slot carries, 0 through 3.</summary>
    public const int SlotCount = 4;
    /// <summary>The lanes the world carries, numbered from <see cref="FirstWorld"/>.</summary>
    public const int WorldCount = 4;
    /// <summary>The first world lane's index (<c>SDF_FIRST_WORLD_LANE</c>).</summary>
    public const int FirstWorld = SlotCount;
    /// <summary>Every lane a shading-time consumer may name.</summary>
    public const int Count = (SlotCount + WorldCount);

    /// <summary>Returns whether a shading-time lane is absent or names one of the eight lanes.</summary>
    /// <param name="lane">The lane, or <see langword="null"/> for none.</param>
    /// <returns><see langword="true"/> when <paramref name="lane"/> is null or in [0, <see cref="Count"/>).</returns>
    public static bool IsLane(int? lane) => ((lane is not { } index) || (((uint)index) < Count));
    /// <summary>Returns whether a bake-time lane names one of the riding slot's four lanes.</summary>
    /// <param name="lane">The lane.</param>
    /// <returns><see langword="true"/> when <paramref name="lane"/> is in [0, <see cref="SlotCount"/>).</returns>
    public static bool IsSlotLane(int lane) => (((uint)lane) < SlotCount);
}
