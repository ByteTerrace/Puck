using System.Numerics;
using Puck.Maths;
using Puck.World.Authoring;

namespace Puck.World;

public static partial class WorldPlacementStamp {
    private sealed record SurfaceSource(WorldPlacement Row, CreationDocument Document, float Margin);
    private sealed record SurfaceDescriptor(WorldPlacementSurface Policy, SurfaceSource[] Sources);
    private sealed record SurfaceValidation(SurfaceDescriptor? Descriptor, string Reason);

    private sealed partial class CompilationContext {
        private readonly Dictionary<WorldPlacement, SurfaceValidation> m_descriptors = new(ReferenceEqualityComparer.Instance);
        private readonly Dictionary<WorldPlacement, string> m_parentValidation = new(ReferenceEqualityComparer.Instance);
        private readonly HashSet<WorldPlacement> m_validatingStamps = new(ReferenceEqualityComparer.Instance);
        private readonly HashSet<WorldPlacement> m_validatingFrames = new(ReferenceEqualityComparer.Instance);

        private static bool Literal(object? value) => value is null || !WorldStateDocumentValues.HasReference(value);
        private static bool LiteralRoot(WorldPlacement row) =>
            Literal(row.Position) && Literal(row.Distribution) && Literal(row.Mirror) && Literal(row.Surface);
        private static bool Finite(Vector3 v) => float.IsFinite(v.X) && float.IsFinite(v.Y) && float.IsFinite(v.Z);
        private static bool Invalid(out SurfaceDescriptor? descriptor, out string reason, string message) {
            descriptor = null; reason = message; return false;
        }
        public bool TryDescriptor(WorldPlacement row, out SurfaceDescriptor? descriptor, out string reason) {
            descriptor = null; reason = string.Empty;
            if (row.Surface is not { } surface) { return true; }
            var owned = Owned(row);
            if (owned && m_descriptors.TryGetValue(row, out var cached)) {
                descriptor = cached.Descriptor; reason = cached.Reason; return reason.Length == 0;
            }
            if (!m_validatingStamps.Add(row)) {
                return Invalid(out descriptor, out reason, $"placement '{row.Id}' has a cyclic surface/frame dependency.");
            }
            try {
                var valid = BuildDescriptor(row, surface, out descriptor, out reason);
                if (owned) { m_descriptors[row] = new SurfaceValidation(descriptor, reason); }
                return valid;
            } finally { m_validatingStamps.Remove(row); }
        }
        private bool BuildDescriptor(WorldPlacement row, WorldPlacementSurface surface,
            out SurfaceDescriptor? descriptor, out string reason) {
            descriptor = null; reason = string.Empty;
            var prefix = $"placement '{row.Id}'.surface";
            if (row.Attach is not null || row.Inhabit is not null || !LiteralRoot(row) || !m_creations.ContainsKey(row.ShownPrototypeId)) {
                return Invalid(out descriptor, out reason, $"{prefix} requires a literal static root, without live root attachment or inhabitance.");
            }
            if (surface.Targets is not { Count: > 0 }) {
                return Invalid(out descriptor, out reason, $"{prefix}.targets must name at least one static contact placement.");
            }
            Vector3 direction = surface.Direction; Vector3 from = surface.FromOffset;
            if (!Finite(direction) || !Finite(from) || FixedVector3.FromVector3(direction) == FixedVector3.Zero ||
                !float.IsFinite(surface.Reach) || surface.Reach <= 0 || FixedQ4816.FromDouble(surface.Reach) <= FixedQ4816.Zero ||
                !float.IsFinite(surface.Offset) || !float.IsFinite(surface.MinFacing) || surface.MinFacing < 0 || surface.MinFacing > 1) {
                return Invalid(out descriptor, out reason, $"{prefix} requires finite offsets, a nonzero fixed-point direction, positive reach and minFacing in [0,1].");
            }
            if (!TryParentFrame(row, out reason)) { return false; }
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var sources = new SurfaceSource[surface.Targets.Count];
            for (var i = 0; i < surface.Targets.Count; i++) {
                var id = surface.Targets[i];
                if (id == row.Id || !seen.Add(id) || !m_rows.TryGetValue(id, out var target) ||
                    !m_creations.TryGetValue(target.ShownPrototypeId, out var creation) || target.Solid is null ||
                    target.Deal is not null || target.Attach is not null || target.Inhabit is not null || !LiteralRoot(target) ||
                    CreationMotion.Animates(creation.Document) || WorldStateDocumentValues.HasReference(creation.Document) ||
                    !CreationStampEmitter.EmitsContact(creation.EngineDocument)) {
                    return Invalid(out descriptor, out reason, $"{prefix}.targets '{id}' must name a distinct literal static contact placement.");
                }
                if (!TryDescriptor(target, out _, out reason) || !TryParentFrame(target, out reason)) { return false; }
                sources[i] = new SurfaceSource(target, creation.EngineDocument, target.Solid.Margin);
            }
            descriptor = new SurfaceDescriptor(surface, sources);
            WorldBootWork.Count(WorldBootWork.SurfaceDescriptors);
            return true;
        }
        private bool TryParentFrame(WorldPlacement row, out string reason) {
            reason = string.Empty;
            var owned = Owned(row);
            if (owned && m_parentValidation.TryGetValue(row, out var cached)) { reason = cached; return reason.Length == 0; }
            if (!m_validatingFrames.Add(row)) { reason = $"placement '{row.Id}' has a cyclic parent frame."; return false; }
            try {
                if (row.Parent is { Length: > 0 } parentId) {
                    if (!m_rows.TryGetValue(parentId, out var parent) || parent.Attach is not null || parent.Inhabit is not null ||
                        !LiteralRoot(parent)) {
                        reason = $"placement '{row.Id}' requires a declared literal static parent frame '{parentId}'.";
                    } else if (parent.Surface is not null && SingleRoot(parent)) {
                        _ = TryDescriptor(parent, out _, out reason);
                    } else { _ = TryParentFrame(parent, out reason); }
                }
                if (owned) { m_parentValidation[row] = reason; }
                return reason.Length == 0;
            } finally { m_validatingFrames.Remove(row); }
        }
    }
}
