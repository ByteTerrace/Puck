using System.Text.Json.Serialization;

namespace Puck.World;

/// <summary>A bounded spatial read recorded by a guarded mutation. Coordinates are Q48.16 raw world XYZ
/// values. The commit gate re-queries the same region and refuses if its occupation, clearance, parent-frame,
/// or extent census changed.</summary>
/// <param name="MinXRaw">Inclusive minimum world X, in Q48.16 raw bits.</param>
/// <param name="MinZRaw">Inclusive minimum world Z, in Q48.16 raw bits.</param>
/// <param name="MaxXRaw">Inclusive maximum world X, in Q48.16 raw bits.</param>
/// <param name="MaxZRaw">Inclusive maximum world Z, in Q48.16 raw bits.</param>
/// <param name="Fingerprint">The canonical fingerprint observed while preparing the mutation.</param>
/// <param name="MinYRaw">Inclusive minimum world Y, in Q48.16 raw bits.</param>
/// <param name="MaxYRaw">Inclusive maximum world Y, in Q48.16 raw bits.</param>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record WorldSpatialReadDependency(
    long MinXRaw,
    long MinZRaw,
    long MaxXRaw,
    long MaxZRaw,
    string Fingerprint,
    long MinYRaw = -long.MaxValue,
    long MaxYRaw = long.MaxValue
) {
    /// <summary>Gets the exact region re-read at commit.</summary>
    [JsonIgnore]
    public WorldSpatialReadRegion Region => new(MinXRaw, MinZRaw, MaxXRaw, MaxZRaw, MinYRaw, MaxYRaw);
    /// <summary>Validates the wire shape without reading the world document.</summary>
    public bool TryValidate(out string reason) {
        if (MinXRaw > MaxXRaw || MinYRaw > MaxYRaw || MinZRaw > MaxZRaw ||
            (Int128)MaxXRaw - MinXRaw > (Int128)long.MaxValue * 2 ||
            (Int128)MaxYRaw - MinYRaw > (Int128)long.MaxValue * 2 ||
            (Int128)MaxZRaw - MinZRaw > (Int128)long.MaxValue * 2) {
            reason = "a spatial read region must have ordered bounds";
            return false;
        }
        if (Fingerprint is not { Length: 64 } || Fingerprint.Any(c => !char.IsAsciiHexDigit(c))) {
            reason = "a spatial read fingerprint must be a SHA-256 hex value";
            return false;
        }
        reason = string.Empty;
        return true;
    }
}
