namespace Puck.World.Protocol;

/// <summary>The mutation vocabulary's adapter to the recipient capability. Policy is decided only by the view.</summary>
public static class WorldRecipientMutations {
    /// <summary>The actual typed mutation refusal for a row withheld by policy.</summary>
    public const string NotDisclosedCode = "world.mutation.undisclosed";
    /// <summary>Refuses every write or guard touching a row the recipient cannot read whole, before grants,
    /// membership, capacity, fingerprints or composition can disclose anything about that row.</summary>
    public static bool Admits(this WorldRecipientView view, WorldMutation mutation, out string row) {
        row = string.Empty;
        if (view.IsAuthority) { return true; }
        switch (mutation) {
            case WorldMutation.UpsertStateCell write: row = write.Row; break;
            case WorldMutation.RemoveStateCell remove: row = remove.Row; break;
            case WorldMutation.UpsertStateRow replace: row = replace.Row.Name.Value; break;
            case WorldMutation.RemoveStateRow remove: row = remove.Name; break;
            case WorldMutation.Generate generate: row = generate.Row; break;
            case WorldMutation.UpsertPlacement placement:
                if (!view.AdmitsPlacement(placement.Placement, out row)) { return false; }
                break;
            case WorldMutation.RemovePlacement placement:
                if (!view.AdmitsPlacement(placement.Id, out row)) { return false; }
                break;
            case WorldMutation.TransformState transform:
                foreach (var subject in transform.Transform.Subjects()) {
                    if (!view.Admits(subject.Name)) { row = subject.Name; return false; }
                }
                if (transform.Guard is { } guard && !view.Admits(guard.Row)) { row = guard.Row; return false; }
                break;
            case WorldMutation.Batch batch:
                if (!batch.TryValidateShape(out _)) { return true; }
                foreach (var cell in batch.ExpectedCells ?? []) {
                    if (!view.Admits(cell.Row)) { row = cell.Row; return false; }
                }
                if (batch.ExpectedDefinition is not null && !view.AdmitsDefinition(batch.ExpectedStateRows, out row)) { return false; }
                if (batch.ExpectedInputs is { } inputs) {
                    if (!view.AdmitsDefinition(inputs.StateRows, out row)) { return false; }
                    foreach (var id in inputs.PlacementIds) { if (!view.AdmitsPlacement(id, out row)) { return false; } }
                }
                if (batch.ExpectedSpatialReads is { Count: > 0 } && !view.AdmitsSpatial(out row)) { return false; }
                foreach (var member in batch.Mutations) { if (!view.Admits(member, out row)) { return false; } }
                return true;
        }
        if (row.Length != 0 && !view.Admits(row)) { return false; }
        return view.AdmitsValues(mutation, out row);
    }

    /// <summary>One guard implementation for live state batches and detached document composition.</summary>
    public static bool CheckGuards(this WorldRecipientView view, WorldMutation.Batch batch, out string reason) {
        reason = string.Empty;
        if (!view.Admits(batch, out var row)) { reason = view.Refusal("batch", row); return false; }
        if (batch.ExpectedDefinition is { } expected && expected != view.Fingerprint(batch.ExpectedStateRows)) {
            reason = "the proposal is stale: its base definition changed; preview again"; return false;
        }
        if (batch.ExpectedInputs is { } inputs && inputs.Fingerprint != view.Fingerprint(inputs)) {
            reason = "the proposal is stale: its named inputs changed; preview again"; return false;
        }
        foreach (var spatial in batch.ExpectedSpatialReads ?? []) {
            if (spatial.Fingerprint != view.Fingerprint(spatial.Region)) { reason = "the proposal is stale: its spatial read changed; preview again"; return false; }
        }
        foreach (var cell in batch.ExpectedCells ?? []) {
            if (!view.Fingerprint(cell)) { reason = $"the proposal is stale: state '{cell.Row}' changed; preview again"; return false; }
        }
        return true;
    }
}
