using System.Numerics;
using System.Runtime.InteropServices;
using Puck.SignedDistance;

namespace Puck.SdfVm;

public sealed partial class SdfWorldTables {
    // Packs the rows the frame's moved set owes into the dynamic-transform region — 3 float4 per slot: position.xyz
    // (+ shadow participation), the orientation quaternion (xyzw), then the lanes — the table
    // SDF_OP_TRANSFORM_DYNAMIC indexes by slot; the region owes the words of each packed row that changed. The packed
    // rows are the ones the producer's SdfMovedTransforms recorded since the last frame this engine consumed from it; a
    // frame with no moved set declares its table static and packs nothing past the first frame that carries that
    // table. Every row is packed on this engine's first frame, for a frame from another producer or another static
    // table, and when the last consumed frame has left the producer's history; no unpacked row is compared. An empty
    // list is only valid for a program with no dynamic slots (PrepareFrame throws otherwise); it still packs the one
    // always-present slot as identity so the binding stays valid. Clamped to the slot capacity the construction
    // options grew the table to. Returns whether any row was packed.
    private bool PackDynamicTransforms(SdfFrame frame) {
        var transforms = frame.DynamicTransforms;
        var moved = frame.MovedTransforms;
        var count = Math.Min(
            val1: transforms.Count,
            val2: m_dynamicTransformCapacity
        );
        var everything = (
            !m_dynamicTransformsPacked ||
            !ReferenceEquals(
            objA: moved,
            objB: m_movedTransformsSource
        ) ||
            ((moved is null)
                ? !ReferenceEquals(
                    objA: transforms,
                    objB: m_movedTransformsTable
                )
                : !moved.TryCollect(
                    into: m_owedTransforms,
                    since: m_movedTransformsSerial
                ))
        );

        if (moved is null) {
            m_owedTransforms.Clear();
        }

        m_movedTransformsSource = moved;
        m_movedTransformsSerial = (moved?.Serial ?? 0L);
        m_movedTransformsTable = transforms;

        Span<float> floats = stackalloc float[DynamicTransformWordCount];

        if (everything) {
            if (count == 0) {
                floats.Clear();
                floats[7] = 1f; // identity quaternion
                WriteDynamicTransform(
                    floats: floats,
                    slot: 0
                );
            }

            for (var index = 0; (index < count); index++) {
                PackDynamicTransform(
                    floats: floats,
                    transform: transforms[index]
                );
                WriteDynamicTransform(
                    floats: floats,
                    slot: index
                );
            }

            m_dynamicTransformsPacked = true;
            m_dynamicTransformRevision++;

            return true;
        }

        var packed = false;

        for (var run = 0; (run < m_owedTransforms.Count); run++) {
            var start = m_owedTransforms.Start(index: run);
            var end = Math.Min(
                val1: (start + m_owedTransforms.Length(index: run)),
                val2: count
            );

            for (var index = start; (index < end); index++) {
                PackDynamicTransform(
                    floats: floats,
                    transform: transforms[index]
                );
                WriteDynamicTransform(
                    floats: floats,
                    slot: index
                );
                packed = true;
            }
        }

        if (packed) {
            m_dynamicTransformRevision++;
        }

        return packed;
    }
    // Writes one packed slot into the dynamic-transform region, which owes the words of it that changed.
    private void WriteDynamicTransform(ReadOnlySpan<float> floats, int slot) =>
        _ = m_dynamicTransformRegion.Write(
            bytes: MemoryMarshal.AsBytes(span: floats),
            offset: (slot * DynamicTransformByteLength)
        );
    // position.w encodes per-instance soft-shadow participation: 0 = casts, 1 = shadow-suppressed (skipped by the
    // soft-shadow march only), read by field/sdf-layout.hlsli's sdfShadowParticipationActive skip. The lanes row is what an op
    // evaluating under this slot (SDF_OP_LANE_ERODE, shade-volumes.hlsli's selected intensity lane) reads through
    // sdfDynamicTransforms[(3*slot)+2]; a shape under no slot reads zero.
    private static void PackDynamicTransform(Span<float> floats, in DynamicTransform transform) {
        floats[0] = transform.Position.X; floats[1] = transform.Position.Y; floats[2] = transform.Position.Z; floats[3] = (transform.CastsSoftShadow
            ? 0f
            : 1f
        );
        floats[4] = transform.Orientation.X; floats[5] = transform.Orientation.Y; floats[6] = transform.Orientation.Z; floats[7] = transform.Orientation.W;
        floats[8] = transform.Lanes.X; floats[9] = transform.Lanes.Y; floats[10] = transform.Lanes.Z; floats[11] = transform.Lanes.W;
    }
    // Packs the screen-light table: each screen slot's emitted color (the framebuffer average set through SetScreenLight)
    // with the room-glow intensity gain in w. KEEP IN SYNC with frame/sdf-environment.hlsli's sdfScreenLights.
    private void PackScreenLights() {
        var floats = MemoryMarshal.Cast<byte, float>(span: m_screenLightScratch.AsSpan());

        for (var index = 0; (index < MaxScreenSurfaces); index++) {
            var color = m_screenLightColors[index];
            var b = (index * 4);

            floats[(b + 0)] = color.X; floats[(b + 1)] = color.Y; floats[(b + 2)] = color.Z; floats[(b + 3)] = ScreenLightIntensity;
        }
    }
    // Eleven float4 rows, paired with shade-volumes.hlsli. Unused trailing slots carry zero bounds.
    private void PackVolumes(SdfFrame frame) {
        Array.Clear(array: m_volumeScratch);

        var floats = MemoryMarshal.Cast<byte, float>(span: m_volumeScratch.AsSpan());
        var volumes = frame.Volumes;

        if (volumes.Count > MaxVolumes) {
            throw new ArgumentOutOfRangeException(
                nameof(frame),
                "Too many bounded volumes."
            );
        }
        var count = volumes.Count;

        for (var index = 0; (index < count); index++) {
            var volume = volumes[index];

            volume.Validate(dynamicTransformCount: frame.DynamicTransforms.Count);
            var b = ((index * SdfVolume.VectorsPerEntry) * 4);
            var rotation = Quaternion.Normalize(value: volume.Rotation);

            floats[(b + 0)] = volume.Position.X; floats[(b + 1)] = volume.Position.Y; floats[(b + 2)] = volume.Position.Z; floats[(b + 3)] = volume.DynamicSlot;
            floats[(b + 4)] = rotation.X; floats[(b + 5)] = rotation.Y; floats[(b + 6)] = rotation.Z; floats[(b + 7)] = rotation.W;
            floats[(b + 8)] = volume.HalfExtent.X; floats[(b + 9)] = volume.HalfExtent.Y; floats[(b + 10)] = volume.HalfExtent.Z; floats[(b + 11)] = volume.Axis;
            var motion = SdfVolumeMotion.At(
                clock: frame.Clock,
                volume: volume
            );

            floats[(b + 12)] = volume.Width; floats[(b + 13)] = motion.Advection;
            floats[(b + 14)] = BitConverter.UInt32BitsToSingle(value: volume.Seed); floats[(b + 15)] = volume.Steps;
            floats[(b + 16)] = volume.Intensity; floats[(b + 17)] = volume.Extinction;
            floats[(b + 18)] = motion.Pulse;
            floats[(b + 20)] = (volume.IntensityLane ?? -1); floats[(b + 21)] = volume.Ramp.Count;
            floats[(b + 22)] = ((float)volume.Kind); floats[(b + 23)] = motion.AdvectionZ;
            floats[(b + 40)] = volume.Coverage; floats[(b + 41)] = volume.Softness;
            for (var stop = 0; (stop < volume.Ramp.Count); stop++) {
                var row = ((b + 24) + (stop * 4));
                var value = volume.Ramp[stop];

                floats[row] = value.Color.X; floats[(row + 1)] = value.Color.Y;
                floats[(row + 2)] = value.Color.Z; floats[(row + 3)] = value.Density;
            }
        }
    }

    // The latest packed frame's environment rows, SdfEnvironment's lanes with the host bakes.
    private readonly float[] m_environment = new float[SdfEnvironment.LaneCount];

    /// <summary>Gets or sets the SDF debug view mode every pass block carries; 0 renders the final lit image.</summary>
    public int DebugMode { get; set; }
    /// <summary>Gets what every pass block of the latest packed frame takes from the tables: the bound screens, the
    /// instance-mask width, the mesh draws, the debug view mode, and the environment rows with the host bakes applied
    /// (<see cref="SdfFrameBlock.BakeEnvironment"/>).</summary>
    public SdfPassValues PassValues => new(
        DebugMode: DebugMode,
        Environment: m_environment,
        InstanceMaskWordCount: InstanceMaskWordCount,
        MeshDraws: MeshDrawCount,
        ScreenCount: BoundScreenCount()
    );
    /// <summary>Gets the live program's per-tile instance-mask width, which every pass block carries.</summary>
    public uint InstanceMaskWordCount => ((uint)m_liveInstanceMaskWordCount);

    /// <summary>Packs a frame into the tables host-side: validates it, writes the dynamic transforms it moved, rebuilds the
    /// frame instance grid when a binnable instance moved, and packs the screen lights, volumes and mesh draws. Each region
    /// owes only the words that changed; the frame's first pass sends them (<see cref="SubmitUpload"/>). The frame's values
    /// reach the passes through each pass block (<see cref="SdfFrameBlock"/>), not the tables.</summary>
    /// <param name="frame">The frame.</param>
    /// <exception cref="ArgumentNullException"><paramref name="frame"/> is <see langword="null"/>.</exception>
    /// <exception cref="ObjectDisposedException">The tables are disposed.</exception>
    /// <exception cref="ArgumentException">The frame has no view, supplies fewer dynamic transforms than the program
    /// requires, or a far distance that is not finite and positive.</exception>
    public void Pack(SdfFrame frame) {
        ArgumentNullException.ThrowIfNull(frame);
        ObjectDisposedException.ThrowIf(
            condition: m_disposed,
            instance: this
        );

        if (frame.Views.Count == 0) {
            throw new ArgumentException(
                message: "A frame renders at least one view.",
                paramName: nameof(frame)
            );
        }

        if (frame.DynamicTransforms.Count < m_requiredDynamicTransformCapacity) {
            throw new ArgumentException(
                message: $"The uploaded SDF program requires {m_requiredDynamicTransformCapacity} dynamic-transform slots; the frame supplies {frame.DynamicTransforms.Count}.",
                paramName: nameof(frame)
            );
        }

        // The far distance is read by every marching kernel as the depth each march ends at; a non-finite or
        // non-positive value would make every cone proof and far exit meaningless, so it is refused here rather than
        // guarded per kernel (the world validator refuses the authored value by name long before it reaches a frame).
        if (
            !float.IsFinite(f: frame.FarDistance) ||
            (frame.FarDistance <= 0f)
        ) {
            throw new ArgumentException(
                message: $"The frame's far distance must be finite and positive; got {frame.FarDistance}.",
                paramName: nameof(frame)
            );
        }

        var transformsChanged = PackDynamicTransforms(frame: frame);

        // Re-bin only when an active maskable dynamic instance can move a grid entry, and only on a frame whose
        // transforms moved (or after a program upload): the grid is a pure function of the program and the transforms.
        // Invariant programs staged their grid once at UploadProgram.
        if (
            m_rebuildInstanceGridPerFrame &&
            (transformsChanged || m_instanceGridRebuildOwed)
        ) {
            var frameGrid = m_liveProgram.BuildFrameInstanceGrid(
                transforms: frame.DynamicTransforms,
                inputScratch: m_instanceGridInputScratch,
                workspace: m_instanceGridWorkspace
            );

            ValidateInstanceGridCapacity(words: frameGrid);
            StageInstanceGrid(words: frameGrid);
            m_instanceGridRebuildOwed = false;
        }

        // The screen-light and volume tables are packed every frame; UploadProgram seeds the screen-surface table and
        // SetScreenSurface patches it, and SetScreenDecal/ClearScreenDecal patch the decal table.
        PackScreenLights();
        SdfFrameBlock.BakeEnvironment(
            frame: frame,
            rows: m_environment
        );
        _ = m_screenLightRegion.Write(
            bytes: m_screenLightScratch,
            offset: 0
        );
        PackVolumes(frame: frame);
        _ = m_volumeRegion.Write(
            bytes: m_volumeScratch,
            offset: 0
        );
        StageMeshRegion(
            draws: frame.MeshDraws,
            revision: frame.MeshDrawsRevision
        );
    }

    private void ValidateInstanceGridCapacity(ReadOnlySpan<uint> words) {
        if (words.Length > m_instanceGridWordCapacity) {
            throw new InvalidOperationException(message: $"The frame instance grid packed {words.Length} words into a {m_instanceGridWordCapacity}-word construction envelope.");
        }
    }
}
