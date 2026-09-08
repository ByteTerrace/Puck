namespace Puck.Cli.Parity;

/// <summary>A decoded <c>puck.parity.contract.v1</c> document — the <c>--contract</c> config naming every
/// per-station census floor and pixel threshold the comparator enforces. Nothing in the comparator itself
/// hardcodes a floor or a threshold; both come from here.</summary>
internal sealed record ParityContract(
    int TileSize,
    IReadOnlyDictionary<string, ParityStationContract> Stations
) {
    private const string FallbackStationName = "default";

    /// <summary>Resolves the thresholds for one manifest station: an exact-named entry first, then the
    /// <c>"default"</c> entry if the contract declares one.</summary>
    public bool TryResolveStation(string station, out ParityStationContract resolved) {
        if (Stations.TryGetValue(key: station, value: out var exact)) {
            resolved = exact;

            return true;
        }

        return Stations.TryGetValue(key: FallbackStationName, value: out resolved!);
    }
}
/// <summary>One station's gate floors and per-tile pixel thresholds.</summary>
/// <param name="CensusFloor">Minimum pixel count required, on both sides, for each named material index. A
/// material absent from a side's census reads as zero.</param>
/// <param name="TileMeanDelta">The largest admissible per-tile mean absolute channel delta, in LSB units.</param>
/// <param name="TileMaxDelta">The largest admissible per-tile single-channel delta, in LSB units.</param>
internal sealed record ParityStationContract(
    IReadOnlyDictionary<string, long> CensusFloor,
    double TileMeanDelta,
    int TileMaxDelta
);
