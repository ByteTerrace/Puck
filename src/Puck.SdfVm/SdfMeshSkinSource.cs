using System.Numerics;
using Puck.SignedDistance;

namespace Puck.SdfVm;

/// <summary>
/// What a skinned bake group's CPU readings read: its instances' instructions and parts as the program the group was
/// planned from held them, so its influences and its rigid fallback are a function of the geometry that keys its mesh,
/// whatever program the bake carries by the time they run. <see cref="SdfMeshBakeGroup"/> makes one when it seals a dynamic
/// group whose geometry reads a slot besides its own, and <see cref="SdfMeshSkin.Build"/> and
/// <see cref="SdfMeshSkin.Split"/> read nothing else: no program reaches them.
/// <para>The instructions are copied out of the planned program on the first reading, under a lock, and the program is
/// let go then or when <see cref="Release"/> runs, so a group that bakes across restarts pins its program only until its
/// skin is read.</para>
/// </summary>
public sealed class SdfMeshSkinSource {
    private readonly Lock m_gate = new();
    private SdfProgram? m_program;
    private SdfProgram? m_instructions;
    private SdfInstancePart[]? m_parts;

    internal SdfMeshSkinSource(SdfProgram program, SdfMeshBakeGroup group) {
        m_program = program;
        Group = group;
    }

    /// <summary>Gets the group whose instances the source holds.</summary>
    public SdfMeshBakeGroup Group { get; }

    /// <summary>Returns the group's field in its own frame, every slot posed as its geometry keys it
    /// (<see cref="SdfMeshAudit.Field"/>), or at the given poses by slot offset.</summary>
    /// <param name="poses">The poses by slot offset from the group's slot, or <see langword="null"/> for the keyed ones.</param>
    /// <returns>The field.</returns>
    /// <exception cref="InvalidOperationException">The source was released before its first reading.</exception>
    public SdfProgram Field(IReadOnlyDictionary<int, DynamicTransform>? poses = null) {
        var instructions = Instructions(parts: out _);

        return SdfMeshAudit.FieldOf(group: Group, indices: All(program: instructions), poses: poses, program: instructions);
    }
    /// <summary>Returns each slot's own part of the group (<see cref="SdfMeshAudit.MemberFields"/>), carved by every
    /// carving shape that preceded it whatever slot that shape rides.</summary>
    /// <returns>Each slot and its part's field, the group's own slot first.</returns>
    /// <exception cref="InvalidOperationException">The source was released before its first reading.</exception>
    public IReadOnlyList<(int Slot, SdfProgram Field)> MemberFields() {
        var instructions = Instructions(parts: out _);

        return SdfMeshAudit.MemberFieldsOf(group: Group, indices: All(program: instructions), program: instructions);
    }
    /// <summary>Returns the slot each shape instruction of the group rides, in stream order: the slot its chain's last
    /// dynamic transform names since the chain's reset, or the group's own.</summary>
    /// <returns>The slots.</returns>
    /// <exception cref="InvalidOperationException">The source was released before its first reading.</exception>
    public IReadOnlyList<int> ShapeSlots() {
        var instructions = Instructions(parts: out _);
        var walk = SdfMeshAudit.SlotWalk(group: Group, indices: All(program: instructions), instructions: instructions.Instructions);

        return [.. walk.OrderBy(keySelector: static entry => entry.Key).Select(selector: static entry => entry.Value)];
    }
    /// <summary>Returns the box, in the group's frame, of the parts the group's instances declare on a slot: each part's
    /// box carried through the pose the geometry keys its slot at; the group's box when no part rides the slot.</summary>
    /// <param name="slot">The slot.</param>
    /// <returns>The box's corners.</returns>
    /// <exception cref="InvalidOperationException">The source was released before its first reading.</exception>
    public (Vector3 Low, Vector3 High) PartBox(int slot) {
        _ = Instructions(parts: out var parts);
        var low = new Vector3(value: float.MaxValue);
        var high = new Vector3(value: float.MinValue);
        var poses = Group.Geometry.Poses;

        foreach (var part in parts) {
            if ((part.Slot != slot) || !poses.TryGetValue(key: part.Slot, value: out var pose)) { continue; }
            var box = SdfMeshBox.Turned(center: (0.5f * (part.Low + part.High)), half: (0.5f * (part.High - part.Low)), offset: pose.Position, turn: ((pose.Orientation == default) ? Quaternion.Identity : pose.Orientation));
            var reach = box.AxisHalf;

            low = Vector3.Min(value1: low, value2: (box.Center - reach));
            high = Vector3.Max(value1: high, value2: (box.Center + reach));
        }

        return ((low.X <= high.X) ? (low, high) : (Group.Low, Group.High));
    }
    /// <summary>Lets go of the planned program; a source never read can then no longer be.</summary>
    public void Release() {
        lock (m_gate) { m_program = null; }
    }

    private SdfProgram Instructions(out SdfInstancePart[] parts) {
        lock (m_gate) {
            if (m_instructions is null) {
                var program = (m_program ?? throw new InvalidOperationException(message: "A released skin source has no instructions."));

                m_instructions = program.Excerpt(indices: Group.InstructionIndices(program: program));
                m_parts = [.. Group.InstanceIndices.SelectMany(selector: instance => program.Instances[instance].Parts)];
                m_program = null;
            }
            parts = m_parts!;

            return m_instructions;
        }
    }
    private static int[] All(SdfProgram program) => [.. Enumerable.Range(start: 0, count: program.Instructions.Count)];
}
