using Puck.Maths;
using Puck.Physics;
using Puck.World.Authoring;

namespace Puck.World.Server;

public sealed partial class WorldColliderSet {
    // A rebuild changes the compiled inventory, not the latest contact sample of every unchanged moving root.
    internal void CarrySamplesFrom(WorldColliderSet? previous) {
        if (previous is null || ReferenceEquals(previous, this)) { return; }
        var rows = new WorldAttachedSolidCheckpoint[m_attachedRows.Count];
        var lookup = previous.m_attachedRows.Select((moving, row) => (moving, row)).GroupBy(pair =>
            (pair.moving.Placement.Id, pair.moving.Creation.Hash, pair.moving.Origin, pair.moving.Rotation, pair.moving.ReflectionNormal,
                pair.moving.Placement.Scale, pair.moving.Placement.Solid!.Margin, pair.moving.Placement.Attach)).ToDictionary(group => group.Key, group => group.First().row);
        for (var row = 0; row < rows.Length; row++) {
            var moving = m_attachedRows[row];
            rows[row] = !lookup.TryGetValue((moving.Placement.Id, moving.Creation.Hash, moving.Origin, moving.Rotation, moving.ReflectionNormal,
                moving.Placement.Scale, moving.Placement.Solid!.Margin, moving.Placement.Attach), out var old)
                ? new(false, default, FixedQuaternion.Identity, FixedQ4816.Zero) :
                new(previous.m_attachedResolved[old], previous.m_attachedOrigins[old], previous.m_attachedRotations[old], previous.m_attachedScales[old]);
        }
        RestoreAttached(rows);
    }
    /// <summary>Captures the sampled carrier transforms, which may precede their current post-rule poses.</summary>
    public WorldAttachedSolidCheckpoint[] CaptureAttached() => Enumerable.Range(0, m_attachedRows.Count)
        .Select(selector: row => new WorldAttachedSolidCheckpoint(m_attachedResolved[row], m_attachedOrigins[row], m_attachedRotations[row], m_attachedScales[row])).ToArray();
    /// <summary>Checks the sampled-solid inventory before population restoration changes any state.</summary>
    public void ValidateAttached(IReadOnlyList<WorldAttachedSolidCheckpoint> rows) {
        if (rows.Count != m_attachedRows.Count) { throw new InvalidOperationException(message: "Checkpoint attached-solid inventory differs from its document."); }
    }
    /// <summary>Reconstructs the last collision sample without consuming the next carrier displacement.</summary>
    public void RestoreAttached(IReadOnlyList<WorldAttachedSolidCheckpoint> rows) {
        ValidateAttached(rows: rows);
        m_attachedColliders.Clear(); m_attachedColliderRows.Clear();
        m_movingHoldable.Clear(); m_movingGripOverrides.Clear();
        m_previousAttachedColliders.Clear(); m_previousAttachedColliderRows.Clear();
        Array.Clear(array: m_previousAttachedResolved);
        Array.Clear(array: m_liveFields);
        m_refreshHash = Fnv1aHash.Create();
        for (var row = 0; (row < rows.Count); row++) {
            var image = rows[row];

            m_attachedResolved[row] = image.Resolved;
            m_attachedOrigins[row] = image.Origin;
            m_attachedRotations[row] = image.Rotation;
            m_attachedScales[row] = image.Scale;
            if (image.Resolved) { RestoreAttachedRow(row, image.Origin, image.Rotation, image.Scale); m_refreshHash.Add(value: image.Scale.Value); }
        }
        m_attachedHash = m_refreshHash.Value;
        m_attachedMoves = default;
        m_attachedSet.Refresh(colliders: System.Runtime.InteropServices.CollectionsMarshal.AsSpan(list: m_attachedColliders));
    }

    private void RestoreAttachedRow(int row, FixedVector3 origin, FixedQuaternion rotation, FixedQ4816 scale) {
        var (placement, creation, _, _, reflection) = m_attachedRows[row];
        m_attachedOrigins[row] = origin; m_attachedRotations[row] = rotation; m_attachedResolved[row] = true; m_attachedScales[row] = scale;
        if (scale <= FixedQ4816.Zero) { return; }
        m_refreshMargin = FixedQ4816.FromDouble(value: placement.Solid!.Margin) * scale; m_refreshRow = row;
        m_liveFields[row] = PosedAttachedField(origin: origin, rotation: rotation, row: row, scale: scale);
        if (m_fieldContactRows[row]) {
            if (m_liveFields[row] is { } field) { RecordAttachedCollider(attached: field); }
            return;
        }
        CreationStampEmitter.VisitFixedPrimitiveCopies(document: creation.EngineDocument,
            transform: new WorldPosedSolid(Creation: creation, Origin: origin, Placement: placement, Rotation: rotation, Scale: (FixedQ4816.FromDouble(value: placement.Scale) * scale), ReflectionNormal: reflection).Transform, visitor: m_attachedVisitor);
    }
    private FixedStaticCollider? PosedAttachedField(int row, FixedVector3 origin, FixedQuaternion rotation, FixedQ4816 scale) {
        if ((m_attachedFields[row] is not { } field) || (scale <= FixedQ4816.Zero)) { return null; }
        if (scale != FixedQ4816.One) {
            if (m_scaledAttachedSizes[row] != scale) {
                var scaled = new UniformScaledField(field.Field!.Field, field.Field.Bounds, scale);

                m_scaledAttachedFields[row] = field with {
                    Center = (field.Center * scale),
                    Extent = new FixedVector3(
                        (FixedInterval.FromPoint(field.Extent.X) * FixedInterval.FromPoint(scale)).Upper + FixedQ4816.Epsilon,
                        (FixedInterval.FromPoint(field.Extent.Y) * FixedInterval.FromPoint(scale)).Upper + FixedQ4816.Epsilon,
                        (FixedInterval.FromPoint(field.Extent.Z) * FixedInterval.FromPoint(scale)).Upper + FixedQ4816.Epsilon),
                    Field = new FixedColliderField(scaled, FixedQ4816.Max(x: FixedQ4816.FromRawBits(value: 1), y: (field.Field.GradientStep * scale)), field.Field.Bounded) { Bounds = scaled },
                };
                m_scaledAttachedSizes[row] = scale;
            }
            field = m_scaledAttachedFields[row]!.Value;
        }
        return field with { Center = (origin + rotation.Rotate(vector: field.Center)), Rotation = rotation };
    }
}
