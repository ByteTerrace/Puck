using System.Globalization;
using Puck.Assets;
using Puck.Cli.Parity;
using Xunit;

namespace Puck.Cli.Tests;

/// <summary>
/// Proves the comparator against synthetic fixtures authored here — no dependency on a real parity capture run. Each fact
/// names the one law it proves: a gate refuses before any pixel work, state, tick and pixel are independent verdicts, a
/// capture served mid-burst fails the tick verdict rather than the pixel verdict, a localized pixel defect a whole-frame
/// mean would dilute away fails per-tile, and a malformed input document is a distinct exit code from a real parity
/// failure.
/// </summary>
public sealed class ParityComparatorTests : IDisposable {
    private const string OtherStateHash = "fedcba9876543210";
    private const string ValidStateHash = "0123456789abcdef";

    private readonly ITestOutputHelper m_output;
    private readonly string m_root;

    public ParityComparatorTests(ITestOutputHelper output) {
        m_output = output;
        m_root = Path.Combine(
            path1: Path.GetTempPath(),
            path2: $"puck-cli-tests-parity-{Guid.NewGuid():N}"
        );

        Directory.CreateDirectory(path: m_root);
    }

    private static void ApplyBlockDelta(byte[] rgba, int width, int height, int x0, int y0, int size, int deltaPerChannel) {
        for (var y = y0; (y < (y0 + size)); y++) {
            for (var x = x0; (x < (x0 + size)); x++) {
                var index = (((y * width) + x) * 4);

                rgba[index] = ((byte)Math.Clamp(
                    value: (rgba[index] + deltaPerChannel),
                    min: 0,
                    max: 255
                ));
                rgba[(index + 1)] = ((byte)Math.Clamp(
                    value: (rgba[(index + 1)] + deltaPerChannel),
                    min: 0,
                    max: 255
                ));
                rgba[(index + 2)] = ((byte)Math.Clamp(
                    value: (rgba[(index + 2)] + deltaPerChannel),
                    min: 0,
                    max: 255
                ));
            }
        }
    }
    private static byte[] BuildGradientRgba(int width, int height) {
        var rgba = new byte[((width * height) * 4)];

        for (var y = 0; (y < height); y++) {
            for (var x = 0; (x < width); x++) {
                var index = (((y * width) + x) * 4);

                rgba[index] = ((byte)((x * 4) % 256));
                rgba[(index + 1)] = ((byte)((y * 4) % 256));
                rgba[(index + 2)] = ((byte)(((x + y) * 2) % 256));
                rgba[(index + 3)] = 255;
            }
        }

        return rgba;
    }
    private static (ParityManifest Left, ParityManifest Right) BuildManifestPair(string station, ulong tick, string stateHashLeft, string stateHashRight, bool cameraInsideLeft, bool cameraInsideRight, string frame, long censusLeft, long censusRight, ulong? regionTickRight = null) {
        var left = new ParityManifest(
            Backend: "vulkan",
            World: "w",
            Captures: [new ParityManifestCapture(
                    Census: (cameraInsideLeft
            ? null
            : new Dictionary<string, long> { ["0"] = censusLeft }),
                    Detail: (cameraInsideLeft
            ? "inside"
            : null),
                    Frame: (cameraInsideLeft
            ? null
            : frame),
                    Refusal: (cameraInsideLeft
            ? "cameraInside"
            : null),
                    RegionTick: (cameraInsideLeft
            ? null
            : tick),
                    Station: station,
                    StateHash: stateHashLeft,
                    Tick: tick
                )]
        );
        var right = new ParityManifest(
            Backend: "directx",
            World: "w",
            Captures: [new ParityManifestCapture(
                    Census: (cameraInsideRight
            ? null
            : new Dictionary<string, long> { ["0"] = censusRight }),
                    Detail: (cameraInsideRight
            ? "inside"
            : null),
                    Frame: (cameraInsideRight
            ? null
            : frame),
                    Refusal: (cameraInsideRight
            ? "cameraInside"
            : null),
                    RegionTick: (cameraInsideRight
            ? null
            : (regionTickRight ?? tick)),
                    Station: station,
                    StateHash: stateHashRight,
                    Tick: tick
                )]
        );

        return (left, right);
    }
    private static string CaptureJson(string station, ulong tick, string stateHash, long censusMaterial0, string frame) =>
        (((((((((((("{\"station\":\"" + station) + "\",\"tick\":") + tick) + ",\"regionTick\":") + tick) + ",\"stateHash\":\"") + stateHash) + "\",\"frame\":\"") + frame) + "\",\"census\":{\"0\":") + censusMaterial0) + "}}");
    private string CreateSubdirectory(string name) {
        var path = Path.Combine(
            path1: m_root,
            path2: $"{name}-{Guid.NewGuid():N}"
        );

        Directory.CreateDirectory(path: path);

        return path;
    }
    private static ParityContract DefaultContract() =>
        new(
            TileSize: 16,
            Stations: new Dictionary<string, ParityStationContract> {
                ["default"] = new ParityStationContract(
                CensusFloor: new Dictionary<string, long>(),
                TileMaxDelta: 12,
                TileMeanDelta: 0.35
            ),
            }
        );
    private static int RunCompareCommand(string[] args, out string stdout, out string stderr) {
        var (exitCode, output, error) = ConsoleCapture.RunSplit(run: () => PuckRootCommand.Invoke(args: ["parity", "compare", .. args]));

        stdout = output;
        stderr = error;

        return exitCode;
    }
    private string WriteContractFile(int tileSize, double tileMeanDelta, int tileMaxDelta, long censusFloorMaterial0) {
        var path = Path.Combine(
            path1: m_root,
            path2: $"contract-{Guid.NewGuid():N}.json"
        );
        var json = $$"""
            {
              "schema": "puck.parity.contract.v1",
              "tileSize": {{tileSize}},
              "stations": {
                "default": {
                  "tileMeanDelta": {{tileMeanDelta.ToString(
            format: "0.####",
            provider: CultureInfo.InvariantCulture
        )}},
                  "tileMaxDelta": {{tileMaxDelta}},
                  "censusFloor": { "0": {{censusFloorMaterial0}} }
                }
              }
            }
            """;

        File.WriteAllText(
            contents: json,
            path: path
        );

        return path;
    }
    private static void WriteManifestFile(string path, string backend, string captureLine) =>
        File.WriteAllText(
            contents: (((("{\"schema\":\"puck.parity.manifest.v1\",\"backend\":\"" + backend) + "\",\"world\":\"w\",\"captures\":[") + captureLine) + "]}"),
            path: path
        );
    private static void WritePng(string directory, string fileName, byte[] rgba, int width, int height) =>
        PngEncoder.Write(
            height: height,
            path: Path.Combine(
                path1: directory,
                path2: fileName
            ),
            rgba: rgba,
            width: width
        );

    [Fact]
    public void ACensusBelowItsStationFloorGatesBeforeAnyPixelOrStateComparison() {
        const int Width = 8;
        const int Height = 8;

        var leftDir = CreateSubdirectory(name: "left");
        var rightDir = CreateSubdirectory(name: "right");
        var rgba = BuildGradientRgba(
            height: Height,
            width: Width
        );

        WritePng(
            directory: leftDir,
            fileName: "s~1.png",
            height: Height,
            rgba: rgba,
            width: Width
        );
        WritePng(
            directory: rightDir,
            fileName: "s~1.png",
            height: Height,
            rgba: rgba,
            width: Width
        );

        var manifests = BuildManifestPair(
            cameraInsideLeft: false,
            cameraInsideRight: false,
            censusLeft: 40,
            censusRight: 500,
            frame: "s~1.png",
            stateHashLeft: ValidStateHash,
            stateHashRight: ValidStateHash,
            station: "s",
            tick: 1
        );
        var contract = new ParityContract(
            TileSize: 16,
            Stations: new Dictionary<string, ParityStationContract> {
                ["default"] = new ParityStationContract(
                CensusFloor: new Dictionary<string, long> { ["0"] = 500 },
                TileMaxDelta: 12,
                TileMeanDelta: 0.35
            ),
            }
        );

        Assert.True(condition: ParityComparator.TryCompare(
            contract: contract,
            error: out _,
            left: manifests.Left,
            leftDir: leftDir,
            outcomes: out var outcomes,
            right: manifests.Right,
            rightDir: rightDir
        ));

        var outcome = Assert.Single(collection: outcomes);
        var verdict = Assert.Single(collection: outcome.Verdicts);

        Assert.Equal(
            expected: "GATE-FAILED",
            actual: verdict.Name
        );
        Assert.Contains(
            expectedSubstring: "census-below-floor",
            actualString: verdict.Detail
        );
        // The left side's 40 is what tripped the 500 floor; the right side's 500 alone would have held.
        Assert.Contains(
            expectedSubstring: "left 40",
            actualString: verdict.Detail
        );
    }
    [Fact]
    public void ALocalizedTileDefectFailsPerTileWhileTheOldWholeFrameMeanWouldHavePassed() {
        const int Width = 64;
        const int Height = 64;

        var leftRgba = BuildGradientRgba(
            height: Height,
            width: Width
        );
        var rightRgba = ((byte[])leftRgba.Clone());

        // A 4x4 block, entirely inside tile (1,1) of a 16px grid, +10 LSB on every channel — exactly the
        // "localized defect a global mean dilutes" shape the old whole-frame comparator could not see.
        ApplyBlockDelta(
            deltaPerChannel: 10,
            height: Height,
            rgba: rightRgba,
            size: 4,
            width: Width,
            x0: 20,
            y0: 20
        );

        var leftImage = new PngImage(
            Height: Height,
            RgbaPixels: leftRgba,
            Width: Width
        );
        var rightImage = new PngImage(
            Height: Height,
            RgbaPixels: rightRgba,
            Width: Width
        );

        var oldEnvelopeVerdict = ParityEnvelope.Compare(
            left: leftImage,
            right: rightImage
        );
        var newTileResult = ParityTileComparer.Compare(
            left: leftImage,
            right: rightImage,
            tileMaxDeltaThreshold: 50,
            tileMeanDeltaThreshold: ParityEnvelope.MaxMeanDelta,
            tileSize: 16
        );

        m_output.WriteLine(message: $"old whole-frame mean delta: {oldEnvelopeVerdict.MeanDelta.ToString(
            format: "0.####",
            provider: CultureInfo.InvariantCulture
        )} LSB (envelope ceiling {ParityEnvelope.MaxMeanDelta})");
        m_output.WriteLine(message: $"new worst-tile mean delta: {newTileResult.Worst.MeanDelta.ToString(
            format: "0.####",
            provider: CultureInfo.InvariantCulture
        )} LSB at tile ({newTileResult.Worst.TileX},{newTileResult.Worst.TileY})");

        // The old whole-frame comparator would have called this pair agreeing.
        Assert.True(condition: oldEnvelopeVerdict.Passed);
        Assert.True(condition: (oldEnvelopeVerdict.MeanDelta <= ParityEnvelope.MaxMeanDelta));
        // The new per-tile comparator, given the SAME threshold value as the mean ceiling, refuses it.
        Assert.False(condition: newTileResult.Passed);
        Assert.True(condition: (newTileResult.Worst.MeanDelta > ParityEnvelope.MaxMeanDelta));

        var leftDir = CreateSubdirectory(name: "left");
        var rightDir = CreateSubdirectory(name: "right");

        WritePng(
            directory: leftDir,
            fileName: "s~1.png",
            height: Height,
            rgba: leftRgba,
            width: Width
        );
        WritePng(
            directory: rightDir,
            fileName: "s~1.png",
            height: Height,
            rgba: rightRgba,
            width: Width
        );

        var manifests = BuildManifestPair(
            cameraInsideLeft: false,
            cameraInsideRight: false,
            censusLeft: 10,
            censusRight: 10,
            frame: "s~1.png",
            stateHashLeft: ValidStateHash,
            stateHashRight: ValidStateHash,
            station: "s",
            tick: 1
        );
        var contract = new ParityContract(
            TileSize: 16,
            Stations: new Dictionary<string, ParityStationContract> {
                ["default"] = new ParityStationContract(
                CensusFloor: new Dictionary<string, long>(),
                TileMaxDelta: 50,
                TileMeanDelta: ParityEnvelope.MaxMeanDelta
            ),
            }
        );

        Assert.True(condition: ParityComparator.TryCompare(
            contract: contract,
            error: out _,
            left: manifests.Left,
            leftDir: leftDir,
            outcomes: out var outcomes,
            right: manifests.Right,
            rightDir: rightDir
        ));

        var outcome = Assert.Single(collection: outcomes);

        Assert.True(condition: outcome.Failed);
        Assert.Contains(
            collection: outcome.Verdicts,
            filter: verdict => ((verdict.Name == "PIXEL-FAILED") && !verdict.Passed)
        );
        Assert.Contains(
            collection: outcome.Verdicts,
            filter: verdict => ((verdict.Name == "STATE-OK") && verdict.Passed)
        );
        Assert.NotNull(@object: outcome.HeatmapRgba);
    }
    [Fact]
    public void AMalformedManifestExitsRefusedDistinctFromAParityFailure() {
        var leftDir = CreateSubdirectory(name: "left");
        var rightDir = CreateSubdirectory(name: "right");
        var rgba = BuildGradientRgba(
            height: 8,
            width: 8
        );

        WritePng(
            directory: rightDir,
            fileName: "s~1.png",
            height: 8,
            rgba: rgba,
            width: 8
        );
        File.WriteAllText(
            contents: "{ not valid json",
            path: Path.Combine(
                path1: leftDir,
                path2: "manifest.json"
            )
        );
        WriteManifestFile(
            backend: "directx",
            captureLine: CaptureJson(
                censusMaterial0: 10,
                frame: "s~1.png",
                stateHash: ValidStateHash,
                station: "s",
                tick: 1
            ),
            path: Path.Combine(
                path1: rightDir,
                path2: "manifest.json"
            )
        );

        var contractPath = WriteContractFile(
            censusFloorMaterial0: 1,
            tileMaxDelta: 12,
            tileMeanDelta: 0.35,
            tileSize: 16
        );
        var exitCode = RunCompareCommand(
            args: [leftDir, rightDir, "--contract", contractPath],
            stderr: out var stderr,
            stdout: out _
        );

        Assert.Equal(
            actual: exitCode,
            expected: CliExit.Refused
        );
        Assert.Contains(
            actualString: stderr,
            expectedSubstring: "ERROR:"
        );
        Assert.Contains(
            actualString: stderr,
            expectedSubstring: "manifest"
        );
    }
    [Fact]
    public void AStateHashMismatchYieldsStateDivergedEvenWhenPixelsAreIdentical() {
        const int Width = 16;
        const int Height = 16;

        var leftDir = CreateSubdirectory(name: "left");
        var rightDir = CreateSubdirectory(name: "right");
        var rgba = BuildGradientRgba(
            height: Height,
            width: Width
        );

        WritePng(
            directory: leftDir,
            fileName: "s~1.png",
            height: Height,
            rgba: rgba,
            width: Width
        );
        WritePng(
            directory: rightDir,
            fileName: "s~1.png",
            height: Height,
            rgba: rgba,
            width: Width
        );

        var manifests = BuildManifestPair(
            cameraInsideLeft: false,
            cameraInsideRight: false,
            censusLeft: 10,
            censusRight: 10,
            frame: "s~1.png",
            stateHashLeft: ValidStateHash,
            stateHashRight: OtherStateHash,
            station: "s",
            tick: 1
        );
        var contract = DefaultContract();

        Assert.True(condition: ParityComparator.TryCompare(
            contract: contract,
            error: out _,
            left: manifests.Left,
            leftDir: leftDir,
            outcomes: out var outcomes,
            right: manifests.Right,
            rightDir: rightDir
        ));

        var outcome = Assert.Single(collection: outcomes);

        Assert.True(condition: outcome.Failed);
        Assert.Contains(
            collection: outcome.Verdicts,
            filter: verdict => ((verdict.Name == "GATE-OK") && verdict.Passed)
        );
        Assert.Contains(
            collection: outcome.Verdicts,
            filter: verdict => ((verdict.Name == "STATE-DIVERGED") && !verdict.Passed)
        );
        Assert.Contains(
            collection: outcome.Verdicts,
            filter: verdict => ((verdict.Name == "PIXEL-OK") && verdict.Passed)
        );
    }
    /// <summary>A capture requested at a tick in the middle of a catch-up burst, as a <c>world.screenshot</c> is, is
    /// served by the frame composed after the burst, which refreshed its bound regions at a later tick. Its frame and
    /// state agree with the other side's, so it fails the tick verdict, between the state and pixel verdicts, and passes
    /// the pixel verdict.</summary>
    [Fact]
    public void AMidBurstCaptureFailsTheTickVerdictRatherThanThePixelVerdict() {
        const int Width = 16;
        const int Height = 16;

        var leftDir = CreateSubdirectory(name: "left");
        var rightDir = CreateSubdirectory(name: "right");
        var rgba = BuildGradientRgba(
            height: Height,
            width: Width
        );

        foreach (var directory in ((string[])[leftDir, rightDir])) {
            WritePng(
                directory: directory,
                fileName: "s~1.png",
                height: Height,
                rgba: rgba,
                width: Width
            );
        }

        var manifests = BuildManifestPair(
            cameraInsideLeft: false,
            cameraInsideRight: false,
            censusLeft: 10,
            censusRight: 10,
            frame: "s~1.png",
            regionTickRight: 5,
            stateHashLeft: ValidStateHash,
            stateHashRight: ValidStateHash,
            station: "s",
            tick: 1
        );

        Assert.True(condition: ParityComparator.TryCompare(
            contract: DefaultContract(),
            error: out _,
            left: manifests.Left,
            leftDir: leftDir,
            outcomes: out var outcomes,
            right: manifests.Right,
            rightDir: rightDir
        ));

        var outcome = Assert.Single(collection: outcomes);

        Assert.True(condition: outcome.Failed);
        Assert.Equal(
            actual: outcome.Verdicts.Select(selector: static verdict => (verdict.Name, verdict.Passed)).ToArray(),
            expected: [("GATE-OK", true), ("STATE-OK", true), ("TICK-FAILED", false), ("PIXEL-OK", true)]
        );
        Assert.Equal(
            actual: outcome.Verdicts[2].Detail,
            expected: "armed at tick 1; the left frame refreshed its regions at tick 1, the right at tick 5"
        );
    }
    [Fact]
    public void CameraInsideGatesBeforeAnyPixelOrStateComparison() {
        var leftDir = CreateSubdirectory(name: "left");
        var rightDir = CreateSubdirectory(name: "right");
        var rightRgba = BuildGradientRgba(
            height: 8,
            width: 8
        );

        WritePng(
            directory: rightDir,
            fileName: "s~1.png",
            height: 8,
            rgba: rightRgba,
            width: 8
        );

        var left = new ParityManifest(
            Backend: "vulkan",
            World: "w",
            Captures: [new ParityManifestCapture(
                    Census: null,
                    Detail: "the camera is inside geometry",
                    Frame: null,
                    Refusal: "cameraInside",
                    RegionTick: null,
                    StateHash: ValidStateHash,
                    Station: "s",
                    Tick: 1
                )]
        );
        var right = new ParityManifest(
            Backend: "directx",
            World: "w",
            Captures: [new ParityManifestCapture(
                    Census: new Dictionary<string, long> { ["0"] = 10 },
                    Detail: null,
                    Frame: "s~1.png",
                    Refusal: null,
                    RegionTick: 1,
                    Station: "s",
                    StateHash: OtherStateHash,
                    Tick: 1
                )]
        );
        var contract = DefaultContract();

        Assert.True(condition: ParityComparator.TryCompare(
            contract: contract,
            error: out _,
            left: left,
            leftDir: leftDir,
            outcomes: out var outcomes,
            right: right,
            rightDir: rightDir
        ));

        var outcome = Assert.Single(collection: outcomes);
        var verdict = Assert.Single(collection: outcome.Verdicts);

        Assert.Equal(
            expected: "GATE-FAILED",
            actual: verdict.Name
        );
        Assert.False(condition: verdict.Passed);
        Assert.Contains(
            expectedSubstring: "cameraInside",
            actualString: verdict.Detail
        );
        Assert.Null(@object: outcome.LeftFrame);
        Assert.Null(@object: outcome.RightFrame);
    }
    [Fact]
    public void AProducerRefusalFailsTheGateThroughTheCliVerbNamingItsDetail() {
        var leftDir = CreateSubdirectory(name: "left");
        var rightDir = CreateSubdirectory(name: "right");
        var rgba = BuildGradientRgba(
            height: 8,
            width: 8
        );

        WritePng(
            directory: leftDir,
            fileName: "s~1.png",
            height: 8,
            rgba: rgba,
            width: 8
        );
        WriteManifestFile(
            backend: "vulkan",
            captureLine: CaptureJson(
                censusMaterial0: 10,
                frame: "s~1.png",
                stateHash: ValidStateHash,
                station: "s",
                tick: 1
            ),
            path: Path.Combine(
                path1: leftDir,
                path2: "manifest.json"
            )
        );
        WriteManifestFile(
            backend: "directx",
            captureLine: (("{\"station\":\"s\",\"tick\":1,\"stateHash\":\"" + ValidStateHash) + "\",\"refusal\":\"stale\",\"detail\":\"armed at tick 1, but the frame that served it showed tick 21\"}"),
            path: Path.Combine(
                path1: rightDir,
                path2: "manifest.json"
            )
        );

        var contractPath = WriteContractFile(
            censusFloorMaterial0: 1,
            tileMaxDelta: 12,
            tileMeanDelta: 0.35,
            tileSize: 16
        );
        var exitCode = RunCompareCommand(
            args: [leftDir, rightDir, "--contract", contractPath],
            stderr: out var stderr,
            stdout: out var stdout
        );

        Assert.Equal(
            actual: exitCode,
            expected: CliExit.Failed
        );
        Assert.Contains(
            actualString: (stdout + stderr),
            expectedSubstring: "stale: refused on right (armed at tick 1, but the frame that served it showed tick 21)"
        );
    }
    [Fact]
    public void ARefusalThatAlsoCarriesAFrameIsMalformed() {
        var leftDir = CreateSubdirectory(name: "left");
        var rightDir = CreateSubdirectory(name: "right");
        var line = CaptureJson(
            censusMaterial0: 10,
            frame: "s~1.png",
            stateHash: ValidStateHash,
            station: "s",
            tick: 1
        );

        WriteManifestFile(
            backend: "vulkan",
            captureLine: line,
            path: Path.Combine(
                path1: leftDir,
                path2: "manifest.json"
            )
        );
        WriteManifestFile(
            backend: "directx",
            captureLine: line.Replace(
                newValue: ",\"refusal\":\"busy\",\"detail\":\"x\",\"frame\"",
                oldValue: ",\"frame\""
            ),
            path: Path.Combine(
                path1: rightDir,
                path2: "manifest.json"
            )
        );

        var contractPath = WriteContractFile(
            censusFloorMaterial0: 1,
            tileMaxDelta: 12,
            tileMeanDelta: 0.35,
            tileSize: 16
        );
        var exitCode = RunCompareCommand(
            args: [leftDir, rightDir, "--contract", contractPath],
            stderr: out var stderr,
            stdout: out _
        );

        Assert.Equal(
            actual: exitCode,
            expected: CliExit.Refused
        );
        Assert.Contains(
            actualString: stderr,
            expectedSubstring: "carries a refusal, so frame, census and regionTick must be absent"
        );
    }
    public void Dispose() {
        try {
            Directory.Delete(
                path: m_root,
                recursive: true
            );
        } catch (Exception exception) when ((exception is IOException or UnauthorizedAccessException)) {
        }
    }
    [Fact]
    public void IdenticalFramesAndManifestsPassEveryVerdictThroughTheCliVerb() {
        var leftDir = CreateSubdirectory(name: "left");
        var rightDir = CreateSubdirectory(name: "right");
        var outDir = CreateSubdirectory(name: "out");
        var rgba = BuildGradientRgba(
            height: 32,
            width: 32
        );

        WritePng(
            directory: leftDir,
            fileName: "s~1.png",
            height: 32,
            rgba: rgba,
            width: 32
        );
        WritePng(
            directory: rightDir,
            fileName: "s~1.png",
            height: 32,
            rgba: rgba,
            width: 32
        );
        WriteManifestFile(
            backend: "vulkan",
            captureLine: CaptureJson(
                censusMaterial0: 10,
                frame: "s~1.png",
                stateHash: ValidStateHash,
                station: "s",
                tick: 1
            ),
            path: Path.Combine(
                path1: leftDir,
                path2: "manifest.json"
            )
        );
        WriteManifestFile(
            backend: "directx",
            captureLine: CaptureJson(
                censusMaterial0: 10,
                frame: "s~1.png",
                stateHash: ValidStateHash,
                station: "s",
                tick: 1
            ),
            path: Path.Combine(
                path1: rightDir,
                path2: "manifest.json"
            )
        );

        var contractPath = WriteContractFile(
            censusFloorMaterial0: 1,
            tileMaxDelta: 12,
            tileMeanDelta: 0.35,
            tileSize: 16
        );
        var exitCode = RunCompareCommand(
            args: [leftDir, rightDir, "--contract", contractPath, "--output", outDir],
            stderr: out _,
            stdout: out var stdout
        );

        Assert.Equal(
            actual: exitCode,
            expected: CliExit.Success
        );
        Assert.Contains(
            actualString: stdout,
            expectedSubstring: "PASS:"
        );
        // Nothing failed, so no evidence subdirectory should have been written under --output.
        Assert.Empty(collection: Directory.EnumerateFileSystemEntries(path: outDir));
    }
}
