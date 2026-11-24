using Puck.Abstractions.Gpu;

namespace Puck.SdfVm;

/// <summary>
/// The bottom-level acceleration structures of every probe client on one device, one a trace geometry
/// (<see cref="SdfMeshIrradianceScene.Geometry"/>), which scenes of one world share: a second residency of a world names
/// the meshes the first does (the mesh region store places them once), so its scene holds the first's geometries and its
/// client claims the first's bottoms instead of building and compacting them again. Each client builds the bottoms it
/// finds wanted and skips those another client is building or compacting; every state change is made under
/// <see cref="Gate"/>, which a client takes inside its own gate and which never calls a client.
/// </summary>
/// <remarks>Invariant: a bottom's structure is disposed only when no client claims it, and a client gives a claim back
/// only through its own retirement (once every job it admitted before it stopped naming the bottom has landed) or once its
/// service has drained. So a structure every claimant's tops and queries may read outlives them all. A ready bottom's
/// structure never changes; a compaction replaces a built one, which no top names, through the compacting client's
/// retirement.</remarks>
internal sealed class SdfMeshTraceBottoms {
    private readonly Dictionary<SdfMeshIrradianceScene.Geometry, Bottom> m_bottoms = new(ReferenceEqualityComparer.Instance);

    /// <summary>Gets the lock every bottom's state is read and changed under.</summary>
    public Lock Gate { get; } = new();

    /// <summary>Returns a geometry's bottom with one more claim on it, wanted if no client had claimed it. Under
    /// <see cref="Gate"/>.</summary>
    /// <param name="geometry">The trace geometry.</param>
    /// <returns>The bottom.</returns>
    public Bottom Claim(SdfMeshIrradianceScene.Geometry geometry) {
        if (!m_bottoms.TryGetValue(key: geometry, value: out var bottom)) {
            bottom = new Bottom(geometry: geometry);
            m_bottoms.Add(key: geometry, value: bottom);
        }
        bottom.Claims++;
        return bottom;
    }
    /// <summary>Gives a claim back, and returns the structure to dispose when it was the last.</summary>
    /// <param name="bottom">The claimed bottom.</param>
    /// <returns>The structure no client claims any more, or <see langword="null"/>.</returns>
    public IGpuAccelerationStructure? Release(Bottom bottom) {
        lock (Gate) {
            if (--bottom.Claims != 0) { return null; }
            _ = m_bottoms.Remove(key: bottom.Geometry);
            var structure = bottom.Structure;

            bottom.Structure = null;
            return structure;
        }
    }

    /// <summary>A bottom's state: wanted, being built, built with its compacted size read, being compacted, or
    /// ready.</summary>
    public enum State {
        /// <summary>No structure is built or being built.</summary>
        Wanted,
        /// <summary>A client's job builds it.</summary>
        Building,
        /// <summary>Built, its compacted size read, waiting for its compaction.</summary>
        Built,
        /// <summary>A client's job compacts it.</summary>
        Compacting,
        /// <summary>The structure a top level names.</summary>
        Ready,
    }

    /// <summary>One geometry's bottom level and the claims the clients hold on it. Read and written under
    /// <see cref="Gate"/>.</summary>
    /// <param name="geometry">The trace geometry.</param>
    internal sealed class Bottom(SdfMeshIrradianceScene.Geometry geometry) {
        /// <summary>Gets the trace geometry.</summary>
        public SdfMeshIrradianceScene.Geometry Geometry { get; } = geometry;
        /// <summary>Gets or sets the bottom's state.</summary>
        public State State { get; set; }
        /// <summary>Gets or sets the structure: the built one until its compaction lands, then the compacted one.</summary>
        public IGpuAccelerationStructure? Structure { get; set; }
        /// <summary>Gets or sets the compacted size the driver answered, zero for none.</summary>
        public ulong CompactedBytes { get; set; }
        /// <summary>Gets or sets the clients that claim the bottom.</summary>
        public int Claims { get; set; }
    }
}
