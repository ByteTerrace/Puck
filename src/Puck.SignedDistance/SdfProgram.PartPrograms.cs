using System.Numerics;
using Puck.Maths;

namespace Puck.SignedDistance;

public sealed partial class SdfProgram {
    /// <summary>Gets whether root field operands are independently unioned, so omitting a whole instance cannot remove an
    /// operand of another instance's subtraction, intersection or modifier. Internal scoped CSG is allowed.</summary>
    public bool InstancesComposable { get; }
    // Header .x packs the count in bits 0..30 and whole-root tracing admission in bit 31.
    // KEEP IN SYNC with sdfCanTracePartsIndependently in field/sdf-parts.hlsli.
    private const uint IndependentPartTracingFlag = 0x80000000u;

    /// <summary>The part entry's leaf-count bits, below its distance gain (<see cref="PartDistanceGainShift"/>) and its
    /// dynamic flag (bit 31).</summary>
    public const uint PartLeafCountMask = 0xffffu;

    /// <summary>Gets the packed-word reservation including worst-case part-program metadata at this program's
    /// instruction and instance ceilings. Unlike <see cref="WordCount"/>, this does not depend on which parts
    /// qualify for compilation or share geometry. Composition capacity probes reserve this value; their existing
    /// source-table and instance ceilings still govern other changes to the program. Includes auxiliary path-table
    /// reservations requested by SdfProgramBuilder.ReservePathTables without allocating dummy tables.</summary>
    public int PartCompilationWordCapacity { get; }

    // A part is one complete hard-union field scope. Its direct leaf program is shared by geometry identity;
    // poses/materials stay in a separate binding run per placement. Original instructions remain the CPU/dual
    // reference and supply canonical primitive payloads, including the packed polygon profile pointer.
    // KEEP IN SYNC with sdfComposePartProgram in field/sdf-parts.hlsli and the instance-header .y pointer mapCore reads.
    private PartProgramPlan CompilePartPrograms() {
        var assets = new List<PartLeafPlan[]>();
        var instances = new PartInstancePlan?[m_instances.Length];
        var keys = new Dictionary<uint[], int>(comparer: SequenceEqualityComparer<uint>.Instance);
        var polygons = m_convexPolygonProfiles.ToDictionary(
            elementSelector: item => item.Vertices,
            keySelector: item => item.InstructionIndex
        );
        var leafCount = 0;
        var bindingCount = 0;
        var compiledCount = 0;

        for (var index = 0; (index < m_instances.Length); index++) {
            var instance = m_instances[index];

            if (!TryCompilePart(
                bindings: out var bindings,
                instance: instance,
                key: out var key,
                leaves: out var leaves,
                polygons: polygons
            )) {
                continue;
            }

            if (!keys.TryGetValue(
                key: key,
                value: out var asset
            )) {
                asset = assets.Count;
                keys.Add(
                    key: key,
                    value: asset
                );
                assets.Add(item: leaves);
                leafCount += leaves.Length;
            }

            var scale = InstructionRows[(instance.End - 1)].Data1.Y;

            instances[index] = new PartInstancePlan(
                Asset: asset,
                Bindings: bindings,
                Scale: ((scale > 0f)
                ? scale
                : 1f)
            );
            bindingCount += bindings.Length;
            compiledCount++;
        }

        return new PartProgramPlan(
            Assets: assets,
            BindingCount: bindingCount,
            CompiledCount: compiledCount,
            Instances: instances,
            LeafCount: leafCount
        );
    }
    private bool TryCompilePart(SdfInstanceRange instance, Dictionary<int, Vector2[]> polygons,
        out PartLeafPlan[] leaves, out PartBinding[] bindings, out uint[] key) {
        leaves = [];
        bindings = [];
        key = [];
        if (
            !instance.Active ||
            ((instance.End - instance.First) < 4) ||
            (InstructionRows[instance.First].Op != SdfOp.PushField) ||
            (InstructionRows[(instance.End - 1)].Op != SdfOp.PopField) ||
            (InstructionRows[(instance.End - 1)].Blend != ((uint)SdfBlendOp.Union))
        ) {
            return false;
        }

        // The direct evaluator discards the scope's final point/pose state. Nothing after the instance can observe it: the
        // program refuses a stream whose next segment reads a point without a ResetPoint of its own
        // (RequireSegmentsStartAtTheWorldPoint), so there is no tail to inspect here.

        var program = new List<PartLeafPlan>();
        var placement = new List<PartBinding>();
        var signature = new List<uint>();
        var cursor = (instance.First + 1);
        var end = (instance.End - 1);

        while (cursor < end) {
            if (
                (InstructionRows[cursor++].Op != SdfOp.ResetPoint) ||
                (cursor >= end)
            ) {
                return false;
            }

            var slot = NoDynamicTransformSlot;

            if (InstructionRows[cursor].Op == SdfOp.TransformDynamic) {
                slot = ((int)InstructionRows[cursor++].Data0.X);
            }
            if (cursor >= end) {
                return false;
            }

            var domain = -1;

            if (SdfFieldOps.PartDomainOf(value: InstructionRows[cursor].Op)) {
                domain = cursor++;
            }
            if (
                (cursor >= end) ||
                (InstructionRows[cursor].Op != SdfOp.ShapeBlend) ||
                (ShapeStepScaleAt(instructionIndex: cursor) != 1f)
            ) {
                return false;
            }

            var shape = InstructionRows[cursor];
            // Side-table content needs its own shared-part key. Retain instruction-local ownership until then.
            var sharing = SdfFieldShapes.PartShapeOf(value: ((SdfShapeType)shape.Shape));

            if (sharing == SdfPartShapeAction.Unshared) {
                return false;
            }
            // The authored Detail flag stays part of the shape's identity, in a bit no shape-type id reaches.
            signature.Add(item: shape.Shape | (shape.Detail
                ? 0x80000000u
                : 0u));
            signature.Add(item: shape.Blend);
            var data0 = shape.Data0;

            if (sharing == SdfPartShapeAction.Polygon) {
                // A profile's packed pointer differs between otherwise identical placements. Key the actual
                // vertices, then let every shared leaf read the first occurrence's correctly patched pointer.
                if (!polygons.TryGetValue(
                    key: cursor,
                    value: out var vertices
                )) {
                    return false;
                }
                data0.X = 0f;
                signature.Add(item: ((uint)vertices.Length));
                foreach (var vertex in vertices) {
                    signature.Add(item: BitConverter.SingleToUInt32Bits(value: vertex.X));
                    signature.Add(item: BitConverter.SingleToUInt32Bits(value: vertex.Y));
                }
            }
            AddPartKeyVector(
                key: signature,
                value: data0
            );
            AddPartKeyVector(
                key: signature,
                value: shape.Data1
            );
            signature.Add(item: ((domain < 0)
                ? 0u
                : (uint)InstructionRows[domain].Op));
            if (domain >= 0) {
                var operation = InstructionRows[domain];

                signature.Add(item: operation.Shape);
                signature.Add(item: operation.Blend);
                AddPartKeyVector(
                    key: signature,
                    value: operation.Data0
                );
                AddPartKeyVector(
                    key: signature,
                    value: operation.Data1
                );
            }

            program.Add(item: new PartLeafPlan(
                DomainInstruction: domain,
                ShapeInstruction: cursor
            ));
            placement.Add(item: new PartBinding(
                DynamicSlot: slot,
                Material: shape.Material,
                ShapeInstruction: cursor
            ));
            cursor++;
        }

        if (program.Count == 0) {
            return false;
        }
        leaves = program.ToArray();
        bindings = placement.ToArray();
        key = signature.ToArray();
        return true;
    }
    private static void AddPartKeyVector(List<uint> key, Vector4 value) {
        key.Add(item: BitConverter.SingleToUInt32Bits(value: value.X));
        key.Add(item: BitConverter.SingleToUInt32Bits(value: value.Y));
        key.Add(item: BitConverter.SingleToUInt32Bits(value: value.Z));
        key.Add(item: BitConverter.SingleToUInt32Bits(value: value.W));
    }
    // Whether the root of the stream is a hard union: every shape outside a scope and every scope's pop joins by Union,
    // and nothing outside a scope edits the accumulator. Then the answer is the least of the root's candidates, and a
    // reader may take a whole instance apart from the stream. The one certificate both readers' whole-instance skips
    // rest on, read two ways. The kernels' part walk (parts) evaluates an instance on its own, detail shapes included,
    // so every other op at the root must be a pose prefix: any other can carry state across the root. The CPU
    // evaluator's primer walks the stream itself and omits detail shapes, so any op that leaves the accumulator alone
    // may stand at the root.
    // Scope validation bounds nesting and forbids crossing instance ownership, so each scope is a complete root operand;
    // cuts and field edits inside one stay inside it.
    internal bool ComposesByHardUnion(bool parts) {
        var depth = 0;

        foreach (var instruction in InstructionRows) {
            switch (SdfFieldOps.CullInstancesOf(value: instruction.Op)) {
                case SdfCullInstancesAction.PushField: {
                        depth++;
                        break;
                    }
                case SdfCullInstancesAction.PopField: {
                        if ((depth <= 0) || ((depth == 1) && (instruction.Blend != ((uint)SdfBlendOp.Union)))) {
                            return false;
                        }

                        depth--;
                        break;
                    }
                case SdfCullInstancesAction.ShapeBlend: {
                        if ((depth == 0) && (parts || !instruction.Detail) && (instruction.Blend != ((uint)SdfBlendOp.Union))) {
                            return false;
                        }

                        break;
                    }
                case SdfCullInstancesAction.EditsField: {
                        if (depth == 0) {
                            return false;
                        }

                        break;
                    }
                case SdfCullInstancesAction.Ignore: {
                        if (parts && (depth == 0) && !SdfFieldOps.PartPosePrefixOf(value: instruction.Op)) {
                            return false;
                        }

                        break;
                    }
                default:
                    throw new InvalidOperationException(message: "Invalid CullInstances action.");
            }
        }

        return (depth == 0);
    }
    private void PackPartPrograms(int offset, int instanceOffset, PartProgramPlan plan) {
        if (plan.CompiledCount == 0) {
            return;
        }
        PackedWords[((instanceOffset * WordsPerVector) + InstancePartProgramsLane)] = ((uint)offset);
        var header = (offset * WordsPerVector);

        PackedWords[header] = ((uint)plan.CompiledCount) | (InstancesComposable
            ? IndependentPartTracingFlag
            : 0u
        );
        PackedWords[(header + 1)] = ((uint)plan.Assets.Count);
        PackedWords[(header + 2)] = ((uint)plan.LeafCount);
        PackedWords[(header + 3)] = ((uint)plan.BindingCount);
        var cursor = ((offset + 1) + plan.Instances.Length);
        var assetOffsets = new int[plan.Assets.Count];

        for (var asset = 0; (asset < plan.Assets.Count); asset++) {
            assetOffsets[asset] = cursor;
            foreach (var leaf in plan.Assets[asset]) {
                PackedWords[(cursor * WordsPerVector)] = ((uint)leaf.ShapeInstruction);
                PackedWords[((cursor * WordsPerVector) + 1)] = ((uint)(leaf.DomainInstruction + 1));
                cursor++;
            }
        }

        for (var index = 0; (index < plan.Instances.Length); index++) {
            var entry = (((offset + 1) + index) * WordsPerVector);
            // mapCore's whole-instance skip (sdfInstanceCannotWin) rejects an instance against its packed bound at this
            // gain, a compiled part always and a generic one under the root-union certificate. A generic entry's zero
            // leaf count keeps it on the generic walk.
            var gainBits = (EncodePartDistanceGain(gain: InstanceDistanceGain(index: index)) << PartDistanceGainShift);

            PackedWords[(entry + 2)] = gainBits;

            if (plan.Instances[index] is not { } placement) {
                continue;
            }

            if (placement.Bindings.Length > PartLeafCountMask) {
                throw new InvalidOperationException(message: $"A compiled part holds at most {PartLeafCountMask} leaves; instance {index} has {placement.Bindings.Length}.");
            }

            PackedWords[entry] = ((uint)assetOffsets[placement.Asset]);
            PackedWords[(entry + 1)] = ((uint)cursor);
            // A non-world shader without pose bindings must leave a dynamic part on its existing fallback path.
            PackedWords[(entry + 2)] = ((uint)placement.Bindings.Length) | gainBits
                | (placement.Bindings.Any(predicate: binding => (binding.DynamicSlot != NoDynamicTransformSlot))
                ? 0x80000000u
                : 0u
            );
            PackedWords[(entry + 3)] = BitConverter.SingleToUInt32Bits(value: placement.Scale);
            foreach (var binding in placement.Bindings) {
                PackedWords[(cursor * WordsPerVector)] = PackTransformSlot(slot: binding.DynamicSlot);
                PackedWords[((cursor * WordsPerVector) + 1)] = binding.Material;
                PackedWords[((cursor * WordsPerVector) + 2)] = ((uint)binding.ShapeInstruction);
                cursor++;
            }
        }
    }

    private readonly record struct PartLeafPlan(int ShapeInstruction, int DomainInstruction);
    private readonly record struct PartBinding(int DynamicSlot, uint Material, int ShapeInstruction);
    private sealed record PartInstancePlan(int Asset, PartBinding[] Bindings, float Scale);
    private sealed record PartProgramPlan(List<PartLeafPlan[]> Assets, PartInstancePlan?[] Instances,
        int LeafCount, int BindingCount, int CompiledCount) {
        public int VectorCount => ((CompiledCount == 0)
            ? 0
            : (((1 + Instances.Length) + LeafCount) + BindingCount)
        );
    }
}
