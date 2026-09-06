namespace Puck.State;

/// <summary>The first or last member's key in an ordered zone, read from the active store.</summary>
/// <param name="row">The zone's name.</param>
/// <param name="handle">The zone's compiled handle.</param>
/// <param name="last">Whether to read the last member rather than the first.</param>
public sealed class ZoneEndKey(string row, StateHandle handle, bool last) : KeyFact {
    /// <inheritdoc/>
    public override string Resolve(IRuleReader reader) {
        if (!StateReader.TryReadHandle(reader.Store, reader.Catalog, handle, null, reader.Tick, out var zone, out _, out _)) { return string.Empty; }
        var count = reader.Store.CellCount(zone);
        return count > 0 && reader.Store.TryKeyAt(zone, last ? count - 1 : 0, out var key) ? key.Value : string.Empty;
    }
    /// <inheritdoc/>
    public override void CollectReads(List<RuleAccess> into) => into.Add(new RuleAccess(row, null));
}
