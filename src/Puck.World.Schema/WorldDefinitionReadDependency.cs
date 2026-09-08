using System.Text.Json.Serialization;

namespace Puck.World;

/// <summary>An optimistic read of explicitly named placement and state rows, including their absence and the
/// world generation seed. Payment values that may advance independently belong in cell expectations instead.</summary>
/// <param name="PlacementIds">Exact placement inputs; ancestors are captured by spatial reads where applicable.</param>
/// <param name="StateRows">Exact state inputs.</param>
/// <param name="Fingerprint">The canonical SHA-256 fingerprint of those inputs.</param>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record WorldDefinitionReadDependency(IReadOnlyList<string> PlacementIds, IReadOnlyList<string> StateRows, string Fingerprint) {
    /// <summary>Checks bounded, unique input names and fingerprint shape.</summary>
    public bool TryValidate() => PlacementIds is { Count: <= 256 } && StateRows is { Count: <= 256 } &&
        PlacementIds.All(name => !string.IsNullOrWhiteSpace(name)) &&
        StateRows.All(name => CellName.TryParse(name, out _, out _)) &&
        PlacementIds.Distinct(StringComparer.Ordinal).Count() == PlacementIds.Count &&
        StateRows.Distinct(StringComparer.Ordinal).Count() == StateRows.Count &&
        Fingerprint is { Length: 64 } && Fingerprint.All(char.IsAsciiHexDigit);
}
