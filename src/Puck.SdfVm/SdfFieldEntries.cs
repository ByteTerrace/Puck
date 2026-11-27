using System.Numerics;
using Puck.Maths;
using Puck.SignedDistance;

namespace Puck.SdfVm;

/// <summary>
/// The field view's entries (<c>SdfWorldPackage.FieldEntries</c>): every run of program instances the field pass walks as
/// one, with the frame it is evaluated in, its bounding sphere in the world and the object it belongs to. The whole resident
/// world's field is the union of the entries' fields, so the pass answers at every point, not only where a template's copy
/// was baked:
/// <list type="bullet">
/// <item>the segments no instance owns, an unbounded entry that the walk reaches only through its world-segment list;</item>
/// <item>each placed copy's template (<see cref="SdfMeshInstance.TemplateRange"/>, or the instances riding its asset slots),
/// evaluated in the copy's frame and scaled by its size, since a template's instructions stand at its own origin and its
/// copies draw it through their matrices;</item>
/// <item>every other object of the program (an instance and those that join it), evaluated in world space through the
/// frame's live transforms.</item>
/// </list>
/// A template no copy names, and a copy drawn from an authored mesh, adds no entry. Each entry's object key is the one its
/// draws carry (<see cref="SdfMeshObjectKeys"/>), which the proximity view reads to leave a pixel's own object out.
/// <para>An entry is <c>SdfWorldPackage.FieldEntryRows</c> rows of four 32-bit words: the sphere's centre and radius
/// (negative for none: the entry may answer anywhere); the frame's position and uniform scale; the frame's orientation
/// quaternion; and, as words, the run's first and end instance, the object key, and the flags with the riding slot plus one
/// above <see cref="SlotShift"/>. <c>passes/sdf-world-field.comp.hlsl</c> reads the same rows.</para>
/// </summary>
public sealed class SdfFieldEntries {
    /// <summary>The words one entry holds.</summary>
    public const int EntryWords = 16;
    /// <summary>The flag of the entry holding the segments no instance owns, which the walk evaluates through its
    /// world-segment list.</summary>
    public const uint WorldSegmentsFlag = 1u;
    /// <summary>The flag of a moving copy's entry: a body, or a row riding one.</summary>
    public const uint BodyFlag = 2u;
    /// <summary>The bit the riding slot plus one starts at in the flags word: zero for an entry riding no slot.</summary>
    public const int SlotShift = 4;

    private uint[] m_words = new uint[EntryWords];
    private bool[] m_claimed = [];

    /// <summary>Gets how many entries the latest <see cref="Build"/> wrote.</summary>
    public int Count { get; private set; }
    /// <summary>Gets the latest entries' words, <see cref="EntryWords"/> an entry; valid until the next
    /// <see cref="Build"/>.</summary>
    public ReadOnlySpan<uint> Words => m_words.AsSpan(start: 0, length: (Count * EntryWords));

    /// <summary>Writes the entries of a frame: its program's instances, its copies and its live transforms.</summary>
    /// <param name="frame">The frame the residency's tables hold.</param>
    /// <exception cref="ArgumentNullException"><paramref name="frame"/> is <see langword="null"/>.</exception>
    public void Build(SdfFrame frame) {
        ArgumentNullException.ThrowIfNull(argument: frame);

        var program = frame.Program;
        var instances = program.Instances;
        var transforms = frame.DynamicTransforms;
        var copies = frame.MeshInstances;

        if (m_claimed.Length < instances.Count) { m_claimed = new bool[instances.Count]; }
        Array.Clear(array: m_claimed, index: 0, length: instances.Count);
        foreach (var range in frame.MeshTemplateRanges) { Claim(first: range.FirstInstance, end: (range.FirstInstance + range.InstanceCount), count: instances.Count); }
        for (var copy = 0; (copy < copies.Count); copy++) {
            if (copies[copy] is { TemplateRange: null, Mesh: null } riding) {
                for (var index = 0; (index < instances.Count); index++) {
                    m_claimed[index] |= RidesAsset(copy: riding, instance: instances[index]);
                }
            }
        }

        Count = 0;
        Append(center: Vector3.Zero, radius: -1f, position: Vector3.Zero, scale: 1f, orientation: Quaternion.Identity, first: 0, end: 0,
            key: SdfMeshObjectKeys.OfGroup(head: -1), flags: WorldSegmentsFlag);
        for (var copy = 0; (copy < copies.Count); copy++) {
            var placed = copies[copy];
            var key = SdfMeshObjectKeys.OfCopy(copy: copy);
            var flags = (placed.IsDynamic ? BodyFlag : 0u);

            if (placed.TemplateRange is { } range) {
                AppendRun(program: program, transforms: transforms, first: range.FirstInstance, end: Math.Min(val1: (range.FirstInstance + range.InstanceCount), val2: instances.Count),
                    copy: placed, key: key, flags: flags);
            } else if (placed.Mesh is null) {
                for (var index = 0; (index < instances.Count);) {
                    if (!RidesAsset(copy: placed, instance: instances[index])) { index++; continue; }

                    var end = (index + 1);

                    while ((end < instances.Count) && RidesAsset(copy: placed, instance: instances[end])) { end++; }
                    AppendRun(program: program, transforms: transforms, first: index, end: end, copy: placed, key: key, flags: flags);
                    index = end;
                }
            }
        }
        for (var index = 0; (index < instances.Count);) {
            if (m_claimed[index]) { index++; continue; }

            var end = (index + 1);

            while ((end < instances.Count) && instances[end].JoinsPreviousObject && !m_claimed[end]) { end++; }
            AppendRun(program: program, transforms: transforms, first: index, end: end, copy: null,
                key: SdfMeshObjectKeys.OfGroup(head: SdfMeshObjectKeys.HeadOf(instances: instances, instance: index)), flags: 0u);
            index = end;
        }
    }

    // Whether an instance rides one of a copy's asset slots, the template a copy with no template range draws.
    private static bool RidesAsset(SdfMeshInstance copy, in SdfInstanceRange instance) {
        if (!instance.IsDynamic) { return false; }
        if (instance.Slot == copy.AssetSlot) { return true; }
        foreach (var slot in copy.OtherAssetSlots) {
            if (instance.Slot == slot) { return true; }
        }
        return false;
    }
    private void Claim(int first, int end, int count) {
        for (var index = Math.Max(val1: first, val2: 0); (index < Math.Min(val1: end, val2: count)); index++) { m_claimed[index] = true; }
    }
    // Appends the run [first, end) of the program's instances, in a copy's frame or, with none, in world space: its sphere
    // the union of its live instances' bound spheres, through the copy's frame. A run whose instances are all parked adds
    // nothing.
    private void AppendRun(SdfProgram program, IReadOnlyList<DynamicTransform> transforms, int first, int end, SdfMeshInstance? copy, uint key, uint flags) {
        var instances = program.Instances;
        var center = Vector3.Zero;
        var radius = -1f;
        var unbounded = false;
        var slot = -1;

        for (var index = first; (index < end); index++) {
            var bound = program.BoundRadiusOf(index: index);

            if (bound < 0f) { continue; }

            var instance = instances[index];
            var at = instance.Center;

            if (instance.IsDynamic) {
                if (slot < 0) { slot = instance.Slot; }
                if ((instance.Slot >= 0) && (instance.Slot < transforms.Count)) {
                    var pose = transforms[instance.Slot];

                    at = (pose.Position + (instance.BoundInSlotFrame ? Vector3.Transform(value: instance.Center, rotation: pose.Orientation) : instance.Center));
                }
            }
            if (!(bound < SdfProgram.UnmaskableBoundRadius) || !VectorFunctions.IsFinite(vector: at)) {
                unbounded = true;
            } else if (radius < 0f) {
                (center, radius) = (at, bound);
            } else {
                (center, radius) = VectorFunctions.EncloseSpheres(centerA: center, radiusA: radius, centerB: at, radiusB: bound);
            }
        }
        if (!unbounded && (radius < 0f)) { return; }

        var position = Vector3.Zero;
        var scale = 1f;
        var orientation = Quaternion.Identity;

        if (copy is not null) {
            position = copy.Transform.Position;
            orientation = copy.Transform.Orientation;
            scale = copy.Scale;
            center = (position + Vector3.Transform(value: (center * scale), rotation: orientation));
            radius *= MathF.Abs(x: scale);
        }
        Append(center: center, radius: (unbounded ? -1f : radius), position: position, scale: scale, orientation: orientation, first: first, end: end, key: key,
            flags: (flags | (((uint)(slot + 1)) << SlotShift)));
    }
    private void Append(Vector3 center, float radius, Vector3 position, float scale, Quaternion orientation, int first, int end, uint key, uint flags) {
        var at = (Count * EntryWords);

        if ((at + EntryWords) > m_words.Length) { Array.Resize(array: ref m_words, newSize: Math.Max(val1: (at + EntryWords), val2: (m_words.Length * 2))); }

        var words = m_words.AsSpan(start: at, length: EntryWords);

        words[0] = BitConverter.SingleToUInt32Bits(value: center.X);
        words[1] = BitConverter.SingleToUInt32Bits(value: center.Y);
        words[2] = BitConverter.SingleToUInt32Bits(value: center.Z);
        words[3] = BitConverter.SingleToUInt32Bits(value: radius);
        words[4] = BitConverter.SingleToUInt32Bits(value: position.X);
        words[5] = BitConverter.SingleToUInt32Bits(value: position.Y);
        words[6] = BitConverter.SingleToUInt32Bits(value: position.Z);
        words[7] = BitConverter.SingleToUInt32Bits(value: scale);
        words[8] = BitConverter.SingleToUInt32Bits(value: orientation.X);
        words[9] = BitConverter.SingleToUInt32Bits(value: orientation.Y);
        words[10] = BitConverter.SingleToUInt32Bits(value: orientation.Z);
        words[11] = BitConverter.SingleToUInt32Bits(value: orientation.W);
        words[12] = (uint)first;
        words[13] = (uint)end;
        words[14] = (key & SdfMeshRecord.ObjectKeyMask);
        words[15] = flags;
        Count++;
    }
}
