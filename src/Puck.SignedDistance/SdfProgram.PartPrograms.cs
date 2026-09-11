using System.Numerics;

namespace Puck.SignedDistance;

public sealed partial class SdfProgram {
    // Header .x packs the count in bits 0..30 and whole-root tracing admission in bit 31.
    // KEEP IN SYNC with sdfTracePrimary in sdf-primary.hlsli.
    private const uint IndependentPartTracingFlag = 0x80000000u;

    /// <summary>Gets the packed-word reservation including worst-case part-program metadata at this program's
    /// instruction and instance ceilings. Unlike <see cref="Words"/> length, this does not depend on which parts
    /// qualify for compilation or share geometry. Composition capacity probes reserve this value; their existing
    /// source-table and instance ceilings still govern other changes to the program.</summary>
    public int PartCompilationWordCapacity { get; }

    // A part is one complete hard-union field scope. Its direct leaf program is shared by geometry identity;
    // poses/materials stay in a separate binding run per placement. Original instructions remain the CPU/dual
    // reference and supply canonical primitive payloads, including the packed polygon profile pointer.
    // KEEP IN SYNC with sdfComposePartProgram and the instance-header .y pointer in sdf-vm.hlsli.
    private PartProgramPlan CompilePartPrograms() {
        var assets = new List<PartLeafPlan[]>();
        var instances = new PartInstancePlan?[m_instances.Length];
        var keys = new Dictionary<uint[], int>(new PartProgramKeyComparer());
        var polygons = m_convexPolygonProfiles.ToDictionary(item => item.InstructionIndex, item => item.Vertices);
        var leafCount = 0;
        var bindingCount = 0;
        var compiledCount = 0;

        for (var index = 0; index < m_instances.Length; index++) {
            var instance = m_instances[index];
            if (!TryCompilePart(instance, polygons, out var leaves, out var bindings, out var key)) {
                continue;
            }

            if (!keys.TryGetValue(key, out var asset)) {
                asset = assets.Count;
                keys.Add(key, asset);
                assets.Add(leaves);
                leafCount += leaves.Length;
            }

            var scale = m_instructions[instance.End - 1].Data1.Y;
            instances[index] = new PartInstancePlan(asset, bindings, scale > 0f ? scale : 1f);
            bindingCount += bindings.Length;
            compiledCount++;
        }

        return new PartProgramPlan(assets, instances, leafCount, bindingCount, compiledCount);
    }

    private bool TryCompilePart(SdfInstanceRange instance, Dictionary<int, Vector2[]> polygons,
        out PartLeafPlan[] leaves, out PartBinding[] bindings, out uint[] key) {
        leaves = [];
        bindings = [];
        key = [];
        if (!instance.Active || instance.End - instance.First < 4
            || m_instructions[instance.First].Op != SdfOp.PushField
            || m_instructions[instance.End - 1].Op != SdfOp.PopField
            || m_instructions[instance.End - 1].Blend != (uint)SdfBlendOp.Union) {
            return false;
        }

        // The direct evaluator discards the scope's final point/pose state. A following scope may save the parent
        // field before ResetPoint, but no later instruction may observe the discarded point or carrier.
        for (var next = instance.End; next < m_instructions.Length; next++) {
            if (m_instructions[next].Op == SdfOp.ResetPoint) {
                break;
            }
            if (m_instructions[next].Op != SdfOp.PushField) {
                return false;
            }
        }

        var program = new List<PartLeafPlan>();
        var placement = new List<PartBinding>();
        var signature = new List<uint>();
        var cursor = instance.First + 1;
        var end = instance.End - 1;
        while (cursor < end) {
            if (m_instructions[cursor++].Op != SdfOp.ResetPoint || cursor >= end) {
                return false;
            }

            var slot = -1;
            if (m_instructions[cursor].Op == SdfOp.TransformDynamic) {
                slot = (int)m_instructions[cursor++].Data0.X;
            }
            if (cursor >= end) {
                return false;
            }

            var domain = -1;
            if (m_instructions[cursor].Op is SdfOp.Scale or SdfOp.AxialProfile or SdfOp.Shear) {
                domain = cursor++;
            }
            if (cursor >= end || m_instructions[cursor].Op != SdfOp.ShapeBlend) {
                return false;
            }

            var shape = m_instructions[cursor];
            // Sweep's external curve table needs its own content key before it can share a program safely.
            if ((SdfShapeType)shape.Shape == SdfShapeType.Sweep) {
                return false;
            }
            signature.Add(shape.Shape | (shape.Detail ? ShapeDetailFlag : 0u)
                | (shape.Secondary ? 0u : ShapeNoSecondaryFlag));
            signature.Add(shape.Blend);
            var data0 = shape.Data0;
            if ((SdfShapeType)shape.Shape == SdfShapeType.ConvexPolygon) {
                // A profile's packed pointer differs between otherwise identical placements. Key the actual
                // vertices, then let every shared leaf read the first occurrence's correctly patched pointer.
                if (!polygons.TryGetValue(cursor, out var vertices)) {
                    return false;
                }
                data0.X = 0f;
                signature.Add((uint)vertices.Length);
                foreach (var vertex in vertices) {
                    signature.Add(BitConverter.SingleToUInt32Bits(vertex.X));
                    signature.Add(BitConverter.SingleToUInt32Bits(vertex.Y));
                }
            }
            AddPartKeyVector(signature, data0);
            AddPartKeyVector(signature, shape.Data1);
            signature.Add(domain < 0 ? 0u : (uint)m_instructions[domain].Op);
            if (domain >= 0) {
                var operation = m_instructions[domain];
                signature.Add(operation.Shape);
                signature.Add(operation.Blend);
                AddPartKeyVector(signature, operation.Data0);
                AddPartKeyVector(signature, operation.Data1);
            }

            program.Add(new PartLeafPlan(cursor, domain));
            placement.Add(new PartBinding(slot, shape.Material));
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
        key.Add(BitConverter.SingleToUInt32Bits(value.X));
        key.Add(BitConverter.SingleToUInt32Bits(value.Y));
        key.Add(BitConverter.SingleToUInt32Bits(value.Z));
        key.Add(BitConverter.SingleToUInt32Bits(value.W));
    }

    private bool CanTracePartsIndependently() {
        var depth = 0;
        foreach (var instruction in m_instructions) {
            if (instruction.Op == SdfOp.PushField) {
                depth++;
            } else if (instruction.Op == SdfOp.PopField) {
                if (depth <= 0 || (depth == 1 && instruction.Blend != (uint)SdfBlendOp.Union)) {
                    return false;
                }
                depth--;
            } else if (depth == 0) {
                if (instruction.Op == SdfOp.ShapeBlend) {
                    if (instruction.Blend != (uint)SdfBlendOp.Union) {
                        return false;
                    }
                } else if (instruction.Op is not (SdfOp.ResetPoint or SdfOp.Translate or SdfOp.Rotate
                    or SdfOp.Scale or SdfOp.TransformDynamic)) {
                    // Other root operations can carry state or alter the accumulated field. Keep the full march.
                    return false;
                }
            }
        }
        // Scope validation already forbids nesting and crossing instance ownership, so each compiled scope
        // is a complete root operand. Internal cuts and field modifiers remain inside their owning scope.
        return depth == 0;
    }

    private void PackPartPrograms(int offset, int instanceOffset, PartProgramPlan plan) {
        if (plan.CompiledCount == 0) {
            return;
        }
        m_words[instanceOffset * WordsPerVector + 1] = (uint)offset;
        var header = offset * WordsPerVector;
        m_words[header] = (uint)plan.CompiledCount | (CanTracePartsIndependently() ? IndependentPartTracingFlag : 0u);
        m_words[header + 1] = (uint)plan.Assets.Count;
        m_words[header + 2] = (uint)plan.LeafCount;
        m_words[header + 3] = (uint)plan.BindingCount;
        var cursor = offset + 1 + plan.Instances.Length;
        var assetOffsets = new int[plan.Assets.Count];
        for (var asset = 0; asset < plan.Assets.Count; asset++) {
            assetOffsets[asset] = cursor;
            foreach (var leaf in plan.Assets[asset]) {
                m_words[cursor * WordsPerVector] = (uint)leaf.ShapeInstruction;
                m_words[cursor * WordsPerVector + 1] = (uint)(leaf.DomainInstruction + 1);
                cursor++;
            }
        }

        for (var index = 0; index < plan.Instances.Length; index++) {
            if (plan.Instances[index] is not { } placement) {
                continue;
            }
            var entry = (offset + 1 + index) * WordsPerVector;
            m_words[entry] = (uint)assetOffsets[placement.Asset];
            m_words[entry + 1] = (uint)cursor;
            // A non-world shader without pose bindings must leave a dynamic part on its existing fallback path.
            m_words[entry + 2] = (uint)placement.Bindings.Length
                | (placement.Bindings.Any(binding => binding.DynamicSlot >= 0) ? 0x80000000u : 0u);
            m_words[entry + 3] = BitConverter.SingleToUInt32Bits(placement.Scale);
            foreach (var binding in placement.Bindings) {
                m_words[cursor * WordsPerVector] = (uint)(binding.DynamicSlot + 1);
                m_words[cursor * WordsPerVector + 1] = binding.Material;
                cursor++;
            }
        }
    }

    private sealed class PartProgramKeyComparer : IEqualityComparer<uint[]> {
        public bool Equals(uint[]? x, uint[]? y) => ReferenceEquals(x, y)
            || (x is not null && y is not null && x.AsSpan().SequenceEqual(y));

        public int GetHashCode(uint[] words) {
            var hash = new HashCode();
            foreach (var word in words) {
                hash.Add(word);
            }
            return hash.ToHashCode();
        }
    }

    private readonly record struct PartLeafPlan(int ShapeInstruction, int DomainInstruction);
    private readonly record struct PartBinding(int DynamicSlot, uint Material);
    private sealed record PartInstancePlan(int Asset, PartBinding[] Bindings, float Scale);
    private sealed record PartProgramPlan(List<PartLeafPlan[]> Assets, PartInstancePlan?[] Instances,
        int LeafCount, int BindingCount, int CompiledCount) {
        public int VectorCount => CompiledCount == 0 ? 0 : 1 + Instances.Length + LeafCount + BindingCount;
    }
}
