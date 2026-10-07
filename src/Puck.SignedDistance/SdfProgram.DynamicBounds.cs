using System.Numerics;

namespace Puck.SignedDistance;

public sealed partial class SdfProgram {
    /// <summary>Collects the existing packed geometry bounds influenced by each dynamic-transform slot. The caller
    /// owns the result; build it once per program upload, not for every posed row. A null entry influences no live
    /// segment; an infinite radius explicitly represents a dependency without a finite conservative bound.</summary>
    /// <param name="indirectTier">When supplied, collect only the tier's indirect casters; null collects every live dependency.</param>
    /// <param name="bodies">The dynamic-body override used with <paramref name="indirectTier"/>.</param>
    /// <returns>One optional sphere per required dynamic slot. Finite centers are offsets added to the slot position;
    /// their radii cover every orientation and lane allowed by the program's declared instance or segment bound.</returns>
    public SdfSkipSphere?[] BuildDynamicTransformBounds(SdfIndirectTier? indirectTier = null,
        SdfIndirectParticipation bodies = SdfIndirectParticipation.Default) {
        var bounds = new SdfSkipSphere?[RequiredDynamicTransformCapacity];
        var instance = 0;

        for (var segment = 0; (segment < SkipSegmentCount); segment++) {
            var word = (SegmentDirectoryWord() + ((DirectoryHeaderVectors + (BoundRecordVectors * segment)) * WordsPerVector));
            var first = ((int)m_words[((word + WordsPerVector) + 2)]);
            var end = ((int)m_words[((word + WordsPerVector) + 3)]);

            while ((instance < m_instances.Length) && (m_instances[instance].End <= first)) { instance++; }
            var owner = (((instance < m_instances.Length) && (m_instances[instance].First <= first)) ? instance : -1);

            if ((owner >= 0) && !m_instances[owner].Active) { continue; }
            if (IndirectInstancesComposable && (owner >= 0) && (indirectTier is { } tier) && (SdfIndirectPolicy.Resolve(m_instances[owner].Indirect,
                m_instances[owner].IsDynamic, tier, bodies) != SdfIndirectParticipation.Cast)) { continue; }
            var sphere = SegmentSkipSphere(segment: segment);

            if (sphere.Mode == BoundModeDynamic) {
                Add(sphere.Slot, sphere.Center, sphere.Radius);
                continue;
            }
            if (sphere.Mode == BoundModeStatic) { continue; }

            // An unskippable chain may still have its enclosing instance's certified cull bound. Read its actual
            // transform dependencies; an unrelated static plane must not make every dynamic slot unbounded.
            var radius = ((owner >= 0) ? m_instanceBinning[owner].Radius : float.PositiveInfinity);
            var finiteOwner = ((owner >= 0) && m_instances[owner].IsDynamic && (radius >= 0f) && (radius < UnmaskableBoundRadius));

            for (var instruction = first; (instruction < end); instruction++) {
                if (m_instructions[instruction].Op != SdfOp.TransformDynamic) { continue; }
                var slot = ((int)m_instructions[instruction].Data0.X);
                var bounded = (finiteOwner && (m_instances[owner].Slot == slot));

                Add(slot, (bounded ? m_instances[owner].Center : Vector3.Zero), (bounded ? radius : float.PositiveInfinity));
            }
        }
        return bounds;

        void Add(int slot, Vector3 center, float radius) {
            if (bounds[slot] is { } previous) {
                if (!float.IsFinite(f: previous.Radius) || !float.IsFinite(f: radius)) {
                    center = Vector3.Zero;
                    radius = float.PositiveInfinity;
                } else {
                    (center, radius) = EncloseSpheres(previous.Center, previous.Radius, center, radius);
                    radius = ((radius * BoundRadiusScale) + BoundRadiusPadding);
                    if (!float.IsFinite(f: radius)) { center = Vector3.Zero; radius = float.PositiveInfinity; }
                }
            }
            bounds[slot] = new SdfSkipSphere(Center: center, Mode: BoundModeDynamic, Radius: radius, Slot: slot);
        }
    }
}
