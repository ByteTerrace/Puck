using Puck.Maths;
using Puck.World.Authoring;

namespace Puck.World.Server;

public sealed partial class WorldColliderSet {
    /// <summary>Captures the sampled carrier transforms, which may precede their current post-rule poses.</summary>
    public WorldAttachedSolidCheckpoint[] CaptureAttached() => Enumerable.Range(0, m_attachedRows.Count)
        .Select(selector: row => new WorldAttachedSolidCheckpoint(m_attachedResolved[row], m_attachedOrigins[row], m_attachedRotations[row])).ToArray();
    /// <summary>Checks the sampled-solid inventory before population restoration changes any state.</summary>
    public void ValidateAttached(IReadOnlyList<WorldAttachedSolidCheckpoint> rows) {
        if (rows.Count != m_attachedRows.Count) { throw new InvalidOperationException(message: "Checkpoint attached-solid inventory differs from its document."); }
    }
    /// <summary>Reconstructs the last collision sample without consuming the next carrier displacement.</summary>
    public void RestoreAttached(IReadOnlyList<WorldAttachedSolidCheckpoint> rows) {
        ValidateAttached(rows: rows);
        m_attachedColliders.Clear(); m_attachedColliderRows.Clear();
        m_previousAttachedColliders.Clear(); m_previousAttachedColliderRows.Clear();
        Array.Clear(array: m_previousAttachedResolved);
        m_refreshHash = Fnv1aHash.Create();
        for (var row = 0; (row < rows.Count); row++) {
            var image = rows[row];

            m_attachedResolved[row] = image.Resolved;
            m_attachedOrigins[row] = image.Origin;
            m_attachedRotations[row] = image.Rotation;
            if (image.Resolved) { RestoreAttachedRow(row, image.Origin, image.Rotation); }
        }
        m_attachedHash = m_refreshHash.Value;
        m_attachedMoves = default;
        m_attachedSet.Refresh(colliders: System.Runtime.InteropServices.CollectionsMarshal.AsSpan(list: m_attachedColliders));
    }

    private void RestoreAttachedRow(int row, FixedVector3 origin, FixedQuaternion rotation) {
        var (placement, creation) = m_attachedRows[row];
        m_attachedOrigins[row] = origin; m_attachedRotations[row] = rotation; m_attachedResolved[row] = true;
        m_refreshMargin = FixedQ4816.FromDouble(value: placement.Solid!.Margin); m_refreshRow = row;
        CreationStampEmitter.VisitFixedPrimitiveCopies(document: creation.EngineDocument,
            transform: new WorldPosedSolid(Creation: creation, Origin: origin, Placement: placement, Rotation: rotation).Transform, visitor: m_attachedVisitor);
    }
}
