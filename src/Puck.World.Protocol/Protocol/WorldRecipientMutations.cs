namespace Puck.World.Protocol;

/// <summary>The mutation vocabulary's adapter to the recipient capability. Policy is decided only by the view.</summary>
public static class WorldRecipientMutations {
    /// <summary>The actual typed mutation refusal for a row withheld by policy.</summary>
    public const string NotDisclosedCode = "world.mutation.undisclosed";
    /// <summary>Refuses every write or guard touching a row the recipient cannot read whole, before grants,
    /// membership, capacity, fingerprints or composition can disclose anything about that row, and every mutation of
    /// a program section (<see cref="WorldSectionFacts.IsProgram"/>) by its kind alone, before it is compiled: a rule
    /// or interaction reads any row and fires as the world, so authoring one is authoring the world.</summary>
    /// <param name="view">The recipient's capability.</param>
    /// <param name="mutation">The submitted mutation.</param>
    /// <param name="row">The first withheld row, or the program section's document name; empty when admitted.</param>
    /// <returns><see langword="true"/> when the recipient may submit the mutation to grants and composition.</returns>
    public static bool Admits(this WorldRecipientView view, WorldMutation mutation, out string row) {
        row = string.Empty;
        if (view.IsAuthority) { return true; }
        if (WorldMutationKindCatalog.SectionOf(mutation) is var section && section.IsProgram()) {
            row = section.ToString().ToLowerInvariant();
            return false;
        }
        switch (mutation) {
            case WorldMutation.UpsertStateCell write: row = write.Row; break;
            case WorldMutation.RemoveStateCell remove: row = remove.Row; break;
            case WorldMutation.UpsertStateRow replace:
                if (!view.AdmitsDeclaration(replace.Row, out row)) { return false; }
                break;
            case WorldMutation.RemoveStateRow remove: row = remove.Name; break;
            case WorldMutation.Generate generate: row = generate.Row; break;
            case WorldMutation.UpsertPlacement placement:
                if (!view.AdmitsPlacement(placement.Placement, out row)) { return false; }
                break;
            case WorldMutation.RemovePlacement placement:
                if (!view.AdmitsPlacement(placement.Id, out row)) { return false; }
                break;
            case WorldMutation.UpsertMachine machine:
                if (!view.AdmitsMachine(machine.Machine, machine.Machine.Name, out row)) { return false; }
                break;
            case WorldMutation.RemoveMachine machine:
                if (!view.AdmitsMachine(null, machine.Name, out row)) { return false; }
                break;
            case WorldMutation.UpsertKit kit:
                if (!view.AdmitsKit(kit.Kit, kit.Kit.Name, out row)) { return false; }
                break;
            case WorldMutation.RemoveKit kit:
                if (!view.AdmitsKit(null, kit.Name, out row)) { return false; }
                break;
            case WorldMutation.TransformState transform:
                foreach (var subject in transform.Transform.Subjects()) {
                    if (!view.Admits(subject.Name)) { row = subject.Name; return false; }
                }
                if (transform.Guard is { } guard && !view.Admits(guard.Row)) { row = guard.Row; return false; }
                if (!view.AdmitsTransform(transform.Transform, out row)) { return false; }
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
