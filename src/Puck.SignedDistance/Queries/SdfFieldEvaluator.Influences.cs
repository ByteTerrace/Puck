using Puck.Maths;

namespace Puck.SignedDistance.Queries;

public sealed partial class SdfFieldEvaluator {
    private InfluenceWalk? m_influenceWalk;

    /// <summary>Reads owner shares through the same field composition and scopes as the render point reading.
    /// Culling is disabled: a contributor with a small share must not disappear from the influence vector.</summary>
    /// <param name="position">The exact rest point.</param>
    /// <param name="shapeOwners">One zero-based owner index per shape instruction, in stream order.</param>
    /// <param name="shares">Receives one share per owner. The caller retains this buffer across readings.</param>
    /// <returns>False when the field or a cross-owner blend cannot be represented.</returns>
    public bool ReadInfluences(FixedPosition position, IReadOnlyList<int> shapeOwners, Span<FixedQ4816> shares) {
        if (!TryWorldPoint(position, out var point)) { return false; }
        var influence = m_influenceWalk;
        if ((influence is null) || (influence.Count != shares.Length)) { m_influenceWalk = influence = new(shares.Length); }
        influence.Reset(shapeOwners);
        var primer = PointPrimer.None;
        var power = new PowerWork();
        var walked = 0L;
        _ = WalkPoints(0, m_instructions.Length, point, [], ref primer, ref walked, ref power, influence);
        influence.Result.CopyTo(shares);
        return influence.Valid;
    }

    // The vector is kept beside the existing interpreter, never a second field walk. One reader belongs to one
    // evaluator, just like its power memo. Scope slots own their arrays so a nested candidate cannot overwrite a parent.
    private sealed class InfluenceWalk(int count) {
        private readonly FixedQ4816[][] m_vectors = Enumerable.Range(0, SdfProgramBuilder.MaxFieldScopeDepth + 1).Select(_ => new FixedQ4816[count]).ToArray();
        private IReadOnlyList<int> m_owners = [];
        private int m_leaf;
        private int m_depth;
        public int Count => count;
        public bool Valid { get; private set; }
        public ReadOnlySpan<FixedQ4816> Result => m_vectors[0];
        public void Reset(IReadOnlyList<int> owners) {
            m_owners = owners; m_leaf = 0; m_depth = 0; Valid = true;
            foreach (var vector in m_vectors) { Array.Clear(vector); }
        }
        public int NextLeaf() => ((m_leaf < m_owners.Count) ? m_owners[m_leaf++] : -1);
        public void Push() => Array.Clear(m_vectors[++m_depth]);
        public void Leaf(in CompiledInstruction instruction, FixedQ4816 current, FixedQ4816 candidate, int owner) {
            if ((owner < 0) || (owner >= count)) { Valid = false; return; }
            var vector = m_vectors[m_depth];
            var share = Share(instruction.Blend, instruction.Data1X, current, candidate, !vector.Where((value, at) => (value != FixedQ4816.Zero) && (at != owner)).Any());
            for (var at = 0; at < count; at++) {
                vector[at] = FixedQ4816.Lerp(vector[at], ((at == owner) ? FixedQ4816.One : FixedQ4816.Zero), share);
            }
        }
        public void Pop(in CompiledInstruction instruction, FixedQ4816 current, FixedQ4816 candidate) {
            if (instruction.ScopeScale > 0L) { candidate = ScaleScope(candidate, instruction.ScopeScale); }
            var child = m_vectors[m_depth--];
            var parent = m_vectors[m_depth];
            var share = Share(instruction.Blend, instruction.Data1X, current, candidate, parent.SequenceEqual(child) || parent.All(v => v == FixedQ4816.Zero));
            for (var at = 0; at < count; at++) { parent[at] = FixedQ4816.Lerp(parent[at], child[at], share); }
        }
        private FixedQ4816 Share(uint blend, FixedQ4816 smooth, FixedQ4816 current, FixedQ4816 candidate, bool sameOwner) {
            if ((smooth > FixedQ4816.Zero) && (blend is (uint)SdfBlendOp.SmoothUnion or (uint)SdfBlendOp.SmoothIntersection or (uint)SdfBlendOp.SmoothSubtraction)) {
                var difference = (blend switch {
                    (uint)SdfBlendOp.SmoothUnion => current - candidate,
                    (uint)SdfBlendOp.SmoothIntersection => candidate - current,
                    _ => -candidate - current,
                });
                return FixedQ4816.Clamp(value: Half + ((Half * difference) / smooth), minimum: FixedQ4816.Zero, maximum: FixedQ4816.One);
            }
            if (!sameOwner && blend is not ((uint)SdfBlendOp.Union or (uint)SdfBlendOp.Intersection or (uint)SdfBlendOp.Subtraction or (uint)SdfBlendOp.SmoothUnion or (uint)SdfBlendOp.SmoothIntersection or (uint)SdfBlendOp.SmoothSubtraction)) { Valid = false; }
            return (ResolveWinner(current, candidate, blend) ? FixedQ4816.One : FixedQ4816.Zero);
        }
    }
}
