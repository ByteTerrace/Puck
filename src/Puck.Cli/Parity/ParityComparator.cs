using Puck.Assets;

namespace Puck.Cli.Parity;

/// <summary>One capture's named checks. A gate failure is the whole list — state and pixel never run once the
/// content gate refuses a capture, because there is nothing trustworthy left to measure.</summary>
internal sealed record ParityCaptureVerdict(string Name, bool Passed, string Detail);
/// <summary>One capture's full comparison outcome: every verdict computed for it, and — only when at least one
/// verdict failed — the decoded frames and delta heatmap evidence writing needs.</summary>
internal sealed record ParityCaptureOutcome(
    string Station,
    ulong Tick,
    IReadOnlyList<ParityCaptureVerdict> Verdicts,
    PngImage? LeftFrame,
    PngImage? RightFrame,
    byte[]? HeatmapRgba
) {
    public bool Failed => Verdicts.Any(predicate: static verdict => !verdict.Passed);
}
/// <summary>
/// The two-verdict comparator core: per capture, a content gate that must hold before any pixel work runs, then
/// an exact stateHash check and a per-tile pixel check — computed and reported independently of each other once
/// the gate holds. Reads frame files from the given left/right directories only; every threshold and floor comes
/// from the contract, never a literal in this class.
/// </summary>
internal static class ParityComparator {
    public static bool TryCompare(ParityManifest left, ParityManifest right, string leftDir, string rightDir, ParityContract contract, out IReadOnlyList<ParityCaptureOutcome> outcomes, out string error) {
        var results = new List<ParityCaptureOutcome>();

        outcomes = results;
        error = string.Empty;

        var leftByKey = left.Captures.ToDictionary(keySelector: static capture => (capture.Station, capture.Tick));
        var rightByKey = right.Captures.ToDictionary(keySelector: static capture => (capture.Station, capture.Tick));
        var orderedKeys = new List<(string Station, ulong Tick)>();
        var seen = new HashSet<(string Station, ulong Tick)>();

        foreach (var capture in left.Captures.Concat(second: right.Captures)) {
            if (seen.Add(item: (capture.Station, capture.Tick))) {
                orderedKeys.Add(item: (capture.Station, capture.Tick));
            }
        }

        foreach (var key in orderedKeys) {
            if (!contract.TryResolveStation(resolved: out var stationContract, station: key.Station)) {
                error = $"contract names no entry for station '{key.Station}' and declares no 'default' fallback.";

                return false;
            }

            leftByKey.TryGetValue(key: key, value: out var leftCapture);
            rightByKey.TryGetValue(key: key, value: out var rightCapture);

            results.Add(item: CompareCapture(station: key.Station, tick: key.Tick, leftCapture: leftCapture, rightCapture: rightCapture, leftDir: leftDir, rightDir: rightDir, stationContract: stationContract, tileSize: contract.TileSize));
        }

        return true;
    }

    private static ParityCaptureOutcome CompareCapture(string station, ulong tick, ParityManifestCapture? leftCapture, ParityManifestCapture? rightCapture, string leftDir, string rightDir, ParityStationContract stationContract, int tileSize) {
        if ((leftCapture is null) || (rightCapture is null)) {
            var missingSide = ((leftCapture is null) ? "left" : "right");

            return GateFailure(reason: $"missing-frame: capture is absent from the {missingSide} manifest", station: station, tick: tick);
        }
        if (leftCapture.CameraInside || rightCapture.CameraInside) {
            var side = ((leftCapture.CameraInside && rightCapture.CameraInside) ? "both" : (leftCapture.CameraInside ? "left" : "right"));

            return GateFailure(reason: $"cameraInside: refused on {side}", station: station, tick: tick);
        }

        var leftFramePath = Path.Combine(path1: leftDir, path2: leftCapture.Frame!);
        var rightFramePath = Path.Combine(path1: rightDir, path2: rightCapture.Frame!);

        if (!File.Exists(path: leftFramePath) || !File.Exists(path: rightFramePath)) {
            var missingSide = (!File.Exists(path: leftFramePath) ? "left" : "right");

            return GateFailure(reason: $"missing-frame: {missingSide} frame file is not on disk", station: station, tick: tick);
        }

        foreach (var (material, floor) in stationContract.CensusFloor) {
            var leftCount = leftCapture.Census!.GetValueOrDefault(defaultValue: 0, key: material);
            var rightCount = rightCapture.Census!.GetValueOrDefault(defaultValue: 0, key: material);

            if ((leftCount < floor) || (rightCount < floor)) {
                return GateFailure(reason: $"census-below-floor: material '{material}' floor {floor}, left {leftCount}, right {rightCount}", station: station, tick: tick);
            }
        }

        PngImage leftImage, rightImage;

        try {
            leftImage = PngDecoder.Decode(pngBytes: File.ReadAllBytes(path: leftFramePath));
            rightImage = PngDecoder.Decode(pngBytes: File.ReadAllBytes(path: rightFramePath));
        } catch (Exception exception) when ((exception is IOException or InvalidDataException or UnauthorizedAccessException)) {
            return GateFailure(station: station, tick: tick, reason: $"missing-frame: a frame could not be decoded ({exception.Message.ReplaceLineEndings(replacementText: " ")})");
        }

        var verdicts = new List<ParityCaptureVerdict> {
            new(Detail: "content gate held", Name: "GATE-OK", Passed: true),
        };

        var stateMatches = string.Equals(a: leftCapture.StateHash, b: rightCapture.StateHash, comparisonType: StringComparison.Ordinal);

        verdicts.Add(item: (stateMatches
            ? new ParityCaptureVerdict(Name: "STATE-OK", Passed: true, Detail: $"stateHash {leftCapture.StateHash}")
            : new ParityCaptureVerdict(Name: "STATE-DIVERGED", Passed: false, Detail: $"left {leftCapture.StateHash} right {rightCapture.StateHash}")));

        if ((leftImage.Width != rightImage.Width) || (leftImage.Height != rightImage.Height)) {
            verdicts.Add(item: new ParityCaptureVerdict(Name: "PIXEL-FAILED", Passed: false, Detail: $"extent disagreement {leftImage.Width}x{leftImage.Height} vulkan-side vs {rightImage.Width}x{rightImage.Height} directx-side"));

            return new ParityCaptureOutcome(HeatmapRgba: null, LeftFrame: leftImage, RightFrame: rightImage, Station: station, Tick: tick, Verdicts: verdicts);
        }

        var tileComparison = ParityTileComparer.Compare(left: leftImage, right: rightImage, tileMaxDeltaThreshold: stationContract.TileMaxDelta, tileMeanDeltaThreshold: stationContract.TileMeanDelta, tileSize: tileSize);
        var worst = tileComparison.Worst;

        verdicts.Add(item: (tileComparison.Passed
            ? new ParityCaptureVerdict(Name: "PIXEL-OK", Passed: true, Detail: $"worst tile ({worst.TileX},{worst.TileY}) mean={worst.MeanDelta:0.###} max={worst.MaxDelta}")
            : new ParityCaptureVerdict(Name: "PIXEL-FAILED", Passed: false, Detail: $"worst tile ({worst.TileX},{worst.TileY}) mean={worst.MeanDelta:0.###} max={worst.MaxDelta}")));

        // Built whenever both frames decoded, not only on a pixel failure: a state-only divergence with
        // matching pixels is itself worth showing (an all-black heatmap proves the pixels, at least, agreed).
        var heatmap = ParityTileComparer.BuildHeatmap(left: leftImage, right: rightImage);

        return new ParityCaptureOutcome(HeatmapRgba: heatmap, LeftFrame: leftImage, RightFrame: rightImage, Station: station, Tick: tick, Verdicts: verdicts);
    }
    private static ParityCaptureOutcome GateFailure(string station, ulong tick, string reason) =>
        new(Station: station, Tick: tick, Verdicts: [new ParityCaptureVerdict(Detail: reason, Name: "GATE-FAILED", Passed: false)], LeftFrame: null, RightFrame: null, HeatmapRgba: null);
}
