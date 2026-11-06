using System.Globalization;
using System.Numerics;
using System.Text.Json;
using System.Text.Json.Serialization;
using Puck.Maths;
using Puck.SdfVm;
using Puck.SignedDistance;
using Puck.World.Protocol;

namespace Puck.World.Client;

/// <summary>A finite observation of public presentation inputs, independent of authority replay and renderer caches.
/// Its nullable observer is absent outside an armed window; ordinary play performs no hashing or sample allocation.</summary>
public sealed class WorldPresentationProbe(TimeProvider? time = null) {
    /// <summary>The largest finite tick window this diagnostic retains.</summary>
    public const int MaximumSamples = 65536;
    private readonly TimeProvider m_time = time ?? TimeProvider.System;
    private readonly Lock m_gate = new();
    private Recording? m_recording;
    private Recording? m_last;

    /// <summary>Gets the armed observer, or null when presentation should record nothing.</summary>
    public WorldPresentationProbe? Observer => Volatile.Read(ref m_recording) is null ? null : this;

    /// <summary>Arms an inclusive tick window, replacing the previous observation.</summary>
    /// <param name="firstTick">The first tick to observe.</param>
    /// <param name="lastTick">The last tick to observe.</param>
    /// <param name="measureFirstTick">The first measured tick after any warmup; absent means the first tick.</param>
    public void Begin(ulong firstTick, ulong lastTick, ulong? measureFirstTick = null) {
        if ((lastTick < firstTick) || ((lastTick - firstTick) >= MaximumSamples)) { throw new ArgumentOutOfRangeException(nameof(lastTick), $"An observation needs 1..{MaximumSamples} ticks in order."); }
        var measured = measureFirstTick ?? firstTick;
        if ((measured < firstTick) || (measured > lastTick)) { throw new ArgumentOutOfRangeException(nameof(measureFirstTick), "The measured interval must lie inside the observed window."); }
        var recording = new Recording(firstTick, lastTick, measured);
        lock (m_gate) { m_last = recording; Volatile.Write(ref m_recording, recording); }
    }

    /// <summary>Stops observation while retaining its snapshot for read-back.</summary>
    public void End() { lock (m_gate) { Volatile.Write(ref m_recording, null); } }

    /// <summary>Observes the final composed frame. Signatures fold public program, camera/framing and transform inputs,
    /// including rigid-copy roots; they are diagnostics, never simulation state or an image-equivalence proof.</summary>
    /// <param name="tick">The authority tick whose delivered state this frame presents.</param>
    /// <param name="frame">The actual composed frame.</param>
    /// <param name="deltaSeconds">The effective presentation advancement after clock policy.</param>
    /// <param name="interpolationAlpha">The effective interpolation fraction after clock policy.</param>
    /// <param name="scope">The logical authority presenting the frame.</param>
    /// <param name="documentId">The authored document identity, not its process activation token.</param>
    /// <param name="width">The actual composed render width.</param>
    /// <param name="height">The actual composed render height.</param>
    public void Record(ulong tick, SdfFrame frame, float deltaSeconds, float interpolationAlpha, string scope, string? documentId, uint width, uint height) {
        if (Volatile.Read(ref m_recording) is null) { return; }
        lock (m_gate) {
            if (m_recording is not { } recording || tick < recording.First || tick > recording.Last) { return; }
            var timestamp = m_time.GetTimestamp();
            ref var entry = ref recording.Entries[checked((int)(tick - recording.First))];
            entry.OutOfOrder |= recording.HasTick && tick < recording.HighestTick;
            recording.HighestTick = Math.Max(recording.HighestTick, tick);
            recording.HasTick = true;
            if (entry.Count < 2) { entry.Count++; }
            if (entry.Count != 1) { return; }
            recording.Count++;
            if (!ReferenceEquals(recording.Program, frame.Program)) {
                recording.ProgramHash = ProgramHash(frame.Program);
                recording.Program = frame.Program;
            }
            entry.Timestamp = timestamp;
            entry.ClockWhole = frame.Clock.Whole;
            entry.ClockFraction = unchecked((ulong)BitConverter.DoubleToInt64Bits(frame.Clock.Fraction));
            entry.Time = BitConverter.SingleToUInt32Bits(frame.Time);
            entry.Delta = BitConverter.SingleToUInt32Bits(deltaSeconds);
            entry.Alpha = BitConverter.SingleToUInt32Bits(interpolationAlpha);
            entry.Scope = scope;
            entry.DocumentId = documentId;
            entry.Program = recording.ProgramHash;
            entry.Views = ViewsHash(frame);
            entry.Transforms = TransformsHash(frame, recording.UnsupportedIdentityTypes, out entry.UnsupportedIdentityCount);
            entry.ViewCount = frame.Views.Count;
            entry.TransformCount = frame.DynamicTransforms.Count;
            entry.Width = width;
            entry.Height = height;
            entry.MeshInstanceCount = frame.MeshInstances.Count;
            entry.MeshAssetSlotCount = frame.MeshAssetSlots.Count;
            entry.HostDrawCount = frame.MeshDraws.Count;
            entry.TickLocked = frame.TickLocked;
            entry.ConvergesLighting = frame.ConvergesLighting;
        }
    }

    /// <summary>Copies a coherent snapshot, or returns null before any observation began. Coverage is complete only
    /// when every requested tick appeared once and in order; duplicate samples never overwrite the first sample.</summary>
    /// <returns>The immutable read-back of the latest observation.</returns>
    public WorldPresentationTrace? Read() {
        lock (m_gate) {
            if (m_last is not { } recording) { return null; }
            var samples = new List<WorldPresentationSample>(recording.Count);
            var missing = new List<ulong>();
            var duplicates = new List<ulong>();
            var outOfOrder = new List<ulong>();
            for (var index = 0; index < recording.Entries.Length; index++) {
                var tick = recording.First + (ulong)index;
                var entry = recording.Entries[index];
                if (entry.Count == 0) { missing.Add(tick); continue; }
                if (entry.Count > 1) { duplicates.Add(tick); }
                if (entry.OutOfOrder) { outOfOrder.Add(tick); }
                samples.Add(new WorldPresentationSample(tick, entry.ClockWhole, Hex(entry.ClockFraction), entry.Time, entry.Delta, entry.Alpha,
                    entry.Scope!, entry.DocumentId, Hex(entry.Program), Hex(entry.Views), Hex(entry.Transforms), entry.ViewCount, entry.TransformCount,
                    entry.TickLocked, entry.ConvergesLighting, entry.Width, entry.Height, entry.MeshInstanceCount, entry.MeshAssetSlotCount, entry.HostDrawCount, entry.UnsupportedIdentityCount));
            }
            var start = recording.Entries[checked((int)(recording.MeasureFirst - recording.First))];
            var end = recording.Entries[^1];
            double? elapsed = ((start.Count > 0 && end.Count > 0 && end.Timestamp >= start.Timestamp)
                ? m_time.GetElapsedTime(start.Timestamp, end.Timestamp).TotalMilliseconds : null);
            return new WorldPresentationTrace(recording.First, recording.Last, recording.MeasureFirst, recording.Count,
                missing.Count == 0 && duplicates.Count == 0 && outOfOrder.Count == 0, missing.ToArray(), duplicates.ToArray(), outOfOrder.ToArray(), elapsed, samples.ToArray(), recording.UnsupportedIdentityTypes.Order(StringComparer.Ordinal).ToArray());
        }
    }

    /// <summary>Serializes the current snapshot through the generated diagnostic contract.</summary>
    /// <returns>The structured read-back, or JSON null before any observation began.</returns>
    public string ReadJson() => JsonSerializer.Serialize(Read(), WorldPresentationTraceJsonContext.Default.WorldPresentationTrace);

    private static string Hex(ulong value) => value.ToString("x16", CultureInfo.InvariantCulture);
    private static ulong ProgramHash(SdfProgram program) {
        var hash = Fnv1aHash.Create();
        hash.Add((uint)program.Words.Length);
        foreach (var word in program.Words) { hash.Add(word); }
        hash.Add((uint)program.Appearance.ScreenSurfaceWords.Length);
        foreach (var word in program.Appearance.ScreenSurfaceWords) { hash.Add(word); }
        return hash.Value;
    }

    private static ulong ViewsHash(SdfFrame frame) {
        var hash = Fnv1aHash.Create();
        hash.Add((uint)frame.Views.Count);
        foreach (var view in frame.Views) {
            var camera = view.Camera;
            Add(ref hash, camera.Position); Add(ref hash, camera.Right); Add(ref hash, camera.Up); Add(ref hash, camera.Forward);
            Add(ref hash, camera.TanHalfFieldOfView); Add(ref hash, camera.AspectRatio); Add(ref hash, camera.Near); Add(ref hash, camera.FrustumOffset.X); Add(ref hash, camera.FrustumOffset.Y);
            Add(ref hash, view.Region.X); Add(ref hash, view.Region.Y); Add(ref hash, view.Region.Width); Add(ref hash, view.Region.Height);
            Add(ref hash, view.RenderScale); Add(ref hash, view.ResolvedRenderScale); Add(ref hash, view.UpscaleSharpness); Add(ref hash, view.ScreenFidelity);
            hash.Add(view.DemandHeight); hash.Add(view.CutRevision); hash.Add((byte)(view.Elsewhere ? 1 : 0)); hash.Add((byte)(view.Unexposed ? 1 : 0));
            Add(ref hash, view.Quality.SkyFieldScale); hash.Add((uint)view.Quality.ShadowResolution);
            hash.Add((byte)(view.Quality.GeometryOnly ? 1 : 0)); hash.Add((byte)(view.Quality.DisableAmbientOcclusion ? 1 : 0)); hash.Add((byte)(view.Quality.DisableSoftShadows ? 1 : 0)); hash.Add((byte)(view.Quality.Temporal ? 1 : 0));
            hash.Add((uint)view.StampFade.FirstSlot); hash.Add((uint)view.StampFade.SlotCount); Add(ref hash, view.StampFade.Opacity);
        }
        return hash.Value;
    }

    private static ulong TransformsHash(SdfFrame frame, HashSet<string> unsupportedTypes, out int unsupportedCount) {
        var hash = Fnv1aHash.Create();
        unsupportedCount = 0;
        hash.Add((uint)frame.DynamicTransforms.Count);
        foreach (var transform in frame.DynamicTransforms) { Add(ref hash, transform); }
        hash.Add((uint)frame.MeshInstances.Count);
        foreach (var copy in frame.MeshInstances) {
            AddIdentity(ref hash, copy.Identity, unsupportedTypes, ref unsupportedCount);
            AddIdentity(ref hash, copy.MotionIdentity ?? copy.Identity, unsupportedTypes, ref unsupportedCount);
            hash.Add(unchecked((uint)copy.AssetSlot)); hash.Add(unchecked((uint)copy.FrameSlot)); Add(ref hash, copy.Transform); Add(ref hash, copy.Scale);
            hash.Add((byte)(copy.IsDynamic ? 1 : 0)); hash.Add((byte)(copy.UsesRootMaterialFrame ? 1 : 0)); hash.Add((uint)copy.Material);
            hash.Add((byte)(copy.TemplateRange.HasValue ? 1 : 0));
            if (copy.TemplateRange is { } range) { hash.Add((uint)range.FirstInstance); hash.Add((uint)range.InstanceCount); }
            hash.Add((uint)copy.OtherAssetSlots.Count); foreach (var slot in copy.OtherAssetSlots) { hash.Add(unchecked((uint)slot)); }
            AddMesh(ref hash, copy.Mesh);
            hash.Add((byte)(copy.Clip.HasValue ? 1 : 0));
            if (copy.Clip is { } clip) { Add(ref hash, clip.Plane); Add(ref hash, clip.CopyRotation); Add(ref hash, clip.CopyTranslation); Add(ref hash, clip.CopyPlane); Add(ref hash, clip.CopyScale); }
        }
        hash.Add((uint)frame.MeshAssetSlots.Count);
        foreach (var slot in frame.MeshAssetSlots) { hash.Add(unchecked((uint)slot)); }
        hash.Add((uint)frame.MeshDraws.Count);
        foreach (var draw in frame.MeshDraws) {
            AddIdentity(ref hash, draw.Identity, unsupportedTypes, ref unsupportedCount);
            AddIdentity(ref hash, draw.MotionIdentity ?? draw.Identity, unsupportedTypes, ref unsupportedCount);
            AddMesh(ref hash, draw.Mesh);
            Add(ref hash, draw.ObjectToWorld); hash.Add(unchecked((uint)(draw.FrameSlot ?? -1))); hash.Add((uint)draw.Material);
            Add(ref hash, draw.CarryRotation); Add(ref hash, draw.CarryTranslation); Add(ref hash, draw.CarryScale); Add(ref hash, draw.ClipPlane);
            hash.Add((byte)(draw.IsDynamic ? 1 : 0)); hash.Add((byte)(draw.Glass ? 1 : 0)); hash.Add((byte)(draw.UsesRootMaterialFrame ? 1 : 0));
        }
        hash.Add((uint)frame.SlotClips.Count);
        foreach (var clip in frame.SlotClips) {
            hash.Add((uint)clip.FirstSlot); hash.Add((uint)clip.SlotCount); Add(ref hash, clip.Plane); Add(ref hash, clip.CopyRotation);
            Add(ref hash, clip.CopyTranslation); Add(ref hash, clip.CopyPlane); Add(ref hash, clip.CopyScale);
        }
        return hash.Value;
    }

    private static void AddMesh(ref Fnv1aHash hash, SdfMesh? mesh) {
        hash.Add((byte)(mesh is null ? 0 : 1));
        if (mesh is null) { return; }
        // Mesh memories are borrowed, unlike SdfProgram's private immutable words. Read live contents rather than
        // caching a signature by object reference and missing an in-place authoring change.
        hash.Add((uint)mesh.Positions.Length); foreach (var value in mesh.Positions.Span) { Add(ref hash, value); }
        hash.Add((uint)mesh.Indices.Length); foreach (var value in mesh.Indices.Span) { hash.Add(value); }
        hash.Add((uint)mesh.Normals.Length); foreach (var value in mesh.Normals.Span) { Add(ref hash, value); }
        hash.Add((uint)mesh.TriangleMaterials.Length); foreach (var value in mesh.TriangleMaterials.Span) { hash.Add(value); }
        hash.Add((uint)mesh.Members.Length); foreach (var member in mesh.Members.Span) { hash.Add((long)member.SlotOffset); Add(ref hash, member.Position); Add(ref hash, member.Orientation); }
        hash.Add((uint)mesh.VertexMembers.Length); hash.Add(mesh.VertexMembers.Span);
        Add(ref hash, mesh.BakeCell); hash.Add((byte)(mesh.Encloses ? 1 : 0));
        hash.Add((uint)mesh.Levels.Count);
        foreach (var level in mesh.Levels) {
            Add(ref hash, level.Error); hash.Add((uint)level.Indices.Length); foreach (var value in level.Indices.Span) { hash.Add(value); }
            hash.Add((uint)level.TriangleMaterials.Length); foreach (var value in level.TriangleMaterials.Span) { hash.Add(value); }
        }
        hash.Add((uint)mesh.Chunks.Count); foreach (var chunk in mesh.Chunks) { AddMesh(ref hash, chunk); }
    }

    private static void Add(ref Fnv1aHash hash, float value) => hash.Add(BitConverter.SingleToUInt32Bits(value));
    private static void AddIdentity(ref Fnv1aHash hash, object identity, HashSet<string> unsupportedTypes, ref int unsupportedCount) {
        // Logical authored names are comparable across fresh processes; opaque host identities are not.
        if (identity is WorldPickTarget target) { hash.Add("world"); hash.Add(target.Placement); hash.Add((long)(target.BodyIndex ?? -1)); hash.Add(target.Prototype); hash.Add((long)(target.Occurrence ?? -1)); hash.Add((long)target.CreationSlots); }
        else if (identity is ValueTuple<WorldPickTarget, WorldEntityAddress, int> motion) {
            hash.Add("body-motion"); AddIdentity(ref hash, motion.Item1, unsupportedTypes, ref unsupportedCount);
            hash.Add(motion.Item2.Authority); hash.Add((long)motion.Item2.Index); hash.Add((long)motion.Item2.Generation); hash.Add((byte)(motion.Item2.Titled ? 1 : 0)); hash.Add((long)motion.Item3);
        }
        else if (identity is ValueTuple<string, string, int> occurrence) { hash.Add("placement-motion"); hash.Add(occurrence.Item1); hash.Add(occurrence.Item2); hash.Add((long)occurrence.Item3); }
        else if (identity is WorldPlacementStamper.StaticMeshIdentity mesh) { hash.Add("placement-mesh"); hash.Add(mesh.Scope); hash.Add(mesh.Placement); hash.Add((long)mesh.Ordinal); }
        else if (identity is string name) { hash.Add("name"); hash.Add(name); }
        else if (identity is int ordinal) { hash.Add("ordinal"); hash.Add((long)ordinal); }
        else { hash.Add("unsupported"); var typeName = identity.GetType().FullName ?? identity.GetType().Name; hash.Add(typeName); unsupportedTypes.Add(typeName); unsupportedCount++; }
    }
    private static void Add(ref Fnv1aHash hash, Vector3 value) { Add(ref hash, value.X); Add(ref hash, value.Y); Add(ref hash, value.Z); }
    private static void Add(ref Fnv1aHash hash, Vector4 value) { Add(ref hash, value.X); Add(ref hash, value.Y); Add(ref hash, value.Z); Add(ref hash, value.W); }
    private static void Add(ref Fnv1aHash hash, Quaternion value) { Add(ref hash, value.X); Add(ref hash, value.Y); Add(ref hash, value.Z); Add(ref hash, value.W); }
    private static void Add(ref Fnv1aHash hash, DynamicTransform value) { Add(ref hash, value.Position); Add(ref hash, value.Orientation); Add(ref hash, value.Lanes); Add(ref hash, value.Scale); Add(ref hash, value.MaterialScale); }
    private static void Add(ref Fnv1aHash hash, Matrix4x4 value) {
        Add(ref hash, value.M11); Add(ref hash, value.M12); Add(ref hash, value.M13); Add(ref hash, value.M14);
        Add(ref hash, value.M21); Add(ref hash, value.M22); Add(ref hash, value.M23); Add(ref hash, value.M24);
        Add(ref hash, value.M31); Add(ref hash, value.M32); Add(ref hash, value.M33); Add(ref hash, value.M34);
        Add(ref hash, value.M41); Add(ref hash, value.M42); Add(ref hash, value.M43); Add(ref hash, value.M44);
    }

    private sealed class Recording(ulong first, ulong last, ulong measureFirst) {
        public readonly ulong First = first, Last = last, MeasureFirst = measureFirst;
        public readonly Entry[] Entries = new Entry[checked((int)(last - first + 1))];
        public int Count;
        public bool HasTick;
        public ulong HighestTick, ProgramHash;
        public SdfProgram? Program;
        public readonly HashSet<string> UnsupportedIdentityTypes = new(StringComparer.Ordinal);
    }
    private struct Entry {
        public int Count, ViewCount, TransformCount, MeshInstanceCount, MeshAssetSlotCount, HostDrawCount, UnsupportedIdentityCount;
        public bool OutOfOrder, TickLocked, ConvergesLighting;
        public long Timestamp;
        public ulong ClockWhole, ClockFraction, Program, Views, Transforms;
        public uint Time, Delta, Alpha, Width, Height;
        public string? Scope, DocumentId;
    }
}

/// <summary>An immutable finite presentation observation, with timing separate from compared semantic samples.</summary>
/// <param name="FirstTick">The first requested tick.</param>
/// <param name="LastTick">The last requested tick.</param>
/// <param name="MeasureFirstTick">The first measured tick after warmup.</param>
/// <param name="SampleCount">The number of distinct ticks observed.</param>
/// <param name="Complete">Whether coverage is exact, unique and ordered.</param>
/// <param name="Missing">Requested ticks never observed.</param>
/// <param name="DuplicateTicks">Ticks observed more than once.</param>
/// <param name="OutOfOrderTicks">Ticks observed after a higher tick.</param>
/// <param name="ElapsedMilliseconds">Monotonic interval from the first measured sample to the last requested sample.</param>
/// <param name="Samples">The first semantic sample of each observed tick, in tick order.</param>
/// <param name="UnsupportedIdentityTypes">Host identity types with no public semantic encoding; such samples cannot qualify equivalence.</param>
public sealed record WorldPresentationTrace(ulong FirstTick, ulong LastTick, ulong MeasureFirstTick, int SampleCount, bool Complete,
    ulong[] Missing, ulong[] DuplicateTicks, ulong[] OutOfOrderTicks, double? ElapsedMilliseconds, WorldPresentationSample[] Samples, string[] UnsupportedIdentityTypes);

/// <summary>Public derived inputs of one actually composed frame; bit patterns preserve exact floating-point values.</summary>
/// <param name="Tick">The delivered authority tick.</param>
/// <param name="ClockWhole">The actual presentation clock's whole engine tick.</param>
/// <param name="ClockFractionBits">The clock fraction's double bit pattern as hexadecimal.</param>
/// <param name="TimeBits">The presentation seconds' float bits.</param>
/// <param name="DeltaBits">The effective presentation interval's float bits.</param>
/// <param name="AlphaBits">The effective interpolation fraction's float bits.</param>
/// <param name="Scope">The presenting logical authority.</param>
/// <param name="DocumentId">The authored document identity.</param>
/// <param name="Program">The current immutable program's content signature.</param>
/// <param name="Views">The actual camera/framing inputs' signature.</param>
/// <param name="Transforms">The actual public pose/copy/clip inputs' signature.</param>
/// <param name="ViewCount">The actual number of views.</param>
/// <param name="TransformCount">The actual number of dynamic-transform rows.</param>
/// <param name="TickLocked">Whether the frame pins presentation to completed ticks.</param>
/// <param name="ConvergesLighting">Whether the frame requires synchronous lighting convergence.</param>
/// <param name="Width">The actual render width.</param>
/// <param name="Height">The actual render height.</param>
/// <param name="MeshInstanceCount">The actual rigid-copy roster size.</param>
/// <param name="MeshAssetSlotCount">The actual shared asset slot count.</param>
/// <param name="HostDrawCount">The actual host-supplied mesh draw count before bake dressing.</param>
/// <param name="UnsupportedIdentityCount">Identity values without a semantic encoding; nonzero prevents equivalence qualification.</param>
public sealed record WorldPresentationSample(ulong Tick, ulong ClockWhole, string ClockFractionBits, uint TimeBits, uint DeltaBits, uint AlphaBits,
    string Scope, string? DocumentId, string Program, string Views, string Transforms, int ViewCount, int TransformCount, bool TickLocked, bool ConvergesLighting,
    uint Width, uint Height, int MeshInstanceCount, int MeshAssetSlotCount, int HostDrawCount, int UnsupportedIdentityCount);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(WorldPresentationTrace))]
internal partial class WorldPresentationTraceJsonContext : JsonSerializerContext { }
