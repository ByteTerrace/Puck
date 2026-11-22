using System.Numerics;
using Puck.Abstractions;
using Puck.Maths;
using Puck.World.Authoring;

namespace Puck.World;

public static partial class WorldPlacementStamp {
    private sealed record SurfaceSource(WorldPlacement Row, CreationDocument Document, float Margin);
    private sealed record SurfaceDescriptor(WorldPlacementSurface Policy, SurfaceSource[] Sources);
    // One lookup by id that a validation read, of a placement row or (Creation) of a creation, and what it found (null
    // for none).
    private readonly record struct RowRead(string Id, object? Row, bool Creation = false);
    // A validation's answer and every lookup it read, its nested validations' included. The answer is a function of the
    // row and those lookups alone, so a context whose index and creations answer each read with the same object answers
    // the same (Reads).
    private sealed record SurfaceValidation(SurfaceDescriptor? Descriptor, string Reason, RowRead[] Reads);
    private sealed record ParentValidation(string Reason, RowRead[] Reads);
    // The validation a row last had in any context: a new section that keeps the row object and every row and creation
    // its validation read validates it no further.
    private static readonly CodeDerivedTable<WorldPlacement, SurfaceValidation> Descriptors = new();

    private sealed partial class CompilationContext {
        private readonly Dictionary<WorldPlacement, SurfaceValidation> m_descriptors = new(ReferenceEqualityComparer.Instance);
        private readonly Dictionary<WorldPlacement, ParentValidation> m_parentValidation = new(ReferenceEqualityComparer.Instance);
        // The lookups the validations running now have read; each validation keeps those after its own start.
        private readonly List<RowRead> m_reads = [];

        // The one door a validation looks a row up by id through, so every lookup it reads is recorded.
        private bool Lookup(string id, out WorldPlacement row) {
            var found = m_rows.TryGetValue(id, out row!);

            m_reads.Add(new RowRead(Id: id, Row: (found ? row : null)));
            return found;
        }
        // The one door a validation looks a creation up by id through, recorded as Lookup records a row.
        private WorldPrototype? CreationRead(string id) {
            var creation = CreationOf(id: id);

            m_reads.Add(new RowRead(Creation: true, Id: id, Row: creation));
            return creation;
        }
        private bool Holds(RowRead[] reads) {
            foreach (var read in reads) {
                object? now = (read.Creation ? CreationOf(id: read.Id) : (m_rows.TryGetValue(read.Id, out var row) ? row : null));

                if (!ReferenceEquals(objA: now, objB: read.Row)) { return false; }
            }
            return true;
        }
        private RowRead[] ReadsSince(int start) => ((m_reads.Count == start) ? [] : m_reads.GetRange(index: start, count: (m_reads.Count - start)).ToArray());
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
            if (owned && (m_descriptors.TryGetValue(row, out var cached) || TryRemembered(row, out cached))) {
                m_reads.AddRange(cached.Reads);
                descriptor = cached.Descriptor; reason = cached.Reason; return reason.Length == 0;
            }
            if (!m_validatingStamps.Add(row)) {
                return Invalid(out descriptor, out reason, $"placement '{row.Id}' has a cyclic surface/frame dependency.");
            }
            try {
                var start = m_reads.Count;
                var valid = BuildDescriptor(row, surface, out descriptor, out reason);
                if (owned) {
                    var validation = new SurfaceValidation(descriptor, reason, ReadsSince(start: start));

                    m_descriptors[row] = validation;
                    if (CodeRevision.Current == m_revision) { Descriptors.AddOrUpdate(row, validation); }
                }
                return valid;
            } finally { m_validatingStamps.Remove(row); }
        }
        // The validation another context made of this row object, when this context's index and creations answer
        // everything it read as they did there.
        private bool TryRemembered(WorldPlacement row, out SurfaceValidation validation) {
            validation = null!;
            if (!Descriptors.TryGetValue(row, out var remembered) || !Holds(remembered.Reads)) { return false; }
            validation = remembered;
            m_descriptors[row] = validation;
            WorldBootWork.Count(WorldBootWork.SurfaceDescriptorsCarried);
            return true;
        }
        private bool BuildDescriptor(WorldPlacement row, WorldPlacementSurface surface,
            out SurfaceDescriptor? descriptor, out string reason) {
            descriptor = null; reason = string.Empty;
            var prefix = new Prefix(row.Id);
            if (row.Attach is not null || row.Inhabit is not null || !LiteralRoot(row) || (CreationRead(row.ShownPrototypeId) is null)) {
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
                if (id == row.Id || !seen.Add(id) || !Lookup(id, out var target) ||
                    (CreationRead(target.ShownPrototypeId) is not { } creation) || target.Solid is null ||
                    target.Deal is not null || target.Attach is not null || target.Inhabit is not null || !LiteralRoot(target) ||
                    CreationRig.Of(document: creation.Document).Animates || WorldStateDocumentValues.HasReference(creation.Document) ||
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
        // A refusal's subject, formatted only when a refusal names it.
        private readonly struct Prefix(string id) {
            public override string ToString() => $"placement '{id}'.surface";
        }
        private bool TryParentFrame(WorldPlacement row, out string reason) {
            reason = string.Empty;
            var owned = Owned(row);
            if (owned && m_parentValidation.TryGetValue(row, out var cached)) { m_reads.AddRange(cached.Reads); reason = cached.Reason; return reason.Length == 0; }
            if (!m_validatingFrames.Add(row)) { reason = $"placement '{row.Id}' has a cyclic parent frame."; return false; }
            var start = m_reads.Count;
            try {
                if (row.Parent is { Length: > 0 } parentId) {
                    if (!Lookup(parentId, out var parent) || parent.Attach is not null || parent.Inhabit is not null ||
                        !LiteralRoot(parent)) {
                        reason = $"placement '{row.Id}' requires a declared literal static parent frame '{parentId}'.";
                    } else if (parent.Surface is not null && SingleRoot(parent)) {
                        _ = TryDescriptor(parent, out _, out reason);
                    } else { _ = TryParentFrame(parent, out reason); }
                }
                if (owned) { m_parentValidation[row] = new ParentValidation(reason, ReadsSince(start: start)); }
                return reason.Length == 0;
            } finally { m_validatingFrames.Remove(row); }
        }
    }
}
