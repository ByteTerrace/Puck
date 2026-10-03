using System.Text.Json.Nodes;
using Puck.Assets;
using Puck.Cli.Parity;
using Puck.Testing;
using Xunit;

namespace Puck.Cli.Tests;

/// <summary>
/// Proves the parity binding stations' exact references and the <c>puck parity</c> schedule read against the
/// checked-in parity documents: a frame both backends agree on still fails when it is not the reference, one wrong byte
/// names its side, the bound station's reference follows its row's stated steps rather than the graph default, a
/// reference the comparator cannot compute is refused by name, and a world whose schedule cannot be read or leaves no
/// room for the wait margin is a named refusal rather than an exception.
/// </summary>
public sealed class ParityBindingReferenceLawTests : IDisposable {
    private const string ContractPath = "tests/Puck.Parity/parity.contract.json";
    private const string StateHash = "0123456789abcdef";
    private const string Station = "binding";
    private const ulong Tick = 1205;
    private const string WorldPath = "tests/Puck.Parity/parity.world.json";

    private readonly TemporaryDirectory m_directory = new(bestEffortDelete: true, prefix: "puck-cli-tests-parity-reference-");

    private static (ParityContract Contract, ParityBindingReference Reference) LoadContract(string station = Station) {
        Assert.True(
            condition: ParityManifestLoader.TryLoadContract(
                contract: out var contract,
                error: out var error,
                path: RepositoryPaths.Resolve(relativePath: ContractPath)
            ),
            userMessage: error
        );
        Assert.True(condition: contract.TryResolveStation(
            resolved: out var resolved,
            station: station
        ));

        return (contract, Assert.IsType<ParityBindingReference>(@object: resolved.Reference));
    }
    private string WriteContract(Action<JsonNode> edit) {
        var contract = JsonNode.Parse(json: File.ReadAllText(path: RepositoryPaths.Resolve(relativePath: ContractPath)))!;

        edit(obj: contract);

        // The reference's graph and world resolve beside the contract, so a copy names the checked-in ones.
        foreach (var station in contract["stations"]!.AsObject()) {
            if (station.Value!["reference"] is { } reference) {
                reference["graph"] = RepositoryPaths.Resolve(relativePath: "tests/Puck.Parity/binding.graph.json");
                reference["world"] = RepositoryPaths.Resolve(relativePath: WorldPath);
            }
        }

        var path = Path.Combine(
            path1: m_directory.RootPath,
            path2: $"contract-{Guid.NewGuid():N}.json"
        );

        File.WriteAllText(
            contents: contract.ToJsonString(),
            path: path
        );

        return path;
    }
    private IReadOnlyList<ParityCaptureVerdict> Compare(ParityContract contract, byte[] left, byte[] right, int width, int height, string station = Station, ulong tick = Tick) {
        var leftDir = Path.Combine(
            path1: m_directory.RootPath,
            path2: $"left-{Guid.NewGuid():N}"
        );
        var rightDir = Path.Combine(
            path1: m_directory.RootPath,
            path2: $"right-{Guid.NewGuid():N}"
        );

        Directory.CreateDirectory(path: leftDir);
        Directory.CreateDirectory(path: rightDir);
        PngEncoder.Write(
            height: height,
            path: Path.Combine(
                path1: leftDir,
                path2: "binding.png"
            ),
            rgba: left,
            width: width
        );
        PngEncoder.Write(
            height: height,
            path: Path.Combine(
                path1: rightDir,
                path2: "binding.png"
            ),
            rgba: right,
            width: width
        );

        Assert.True(condition: ParityComparator.TryCompare(
            contract: contract,
            error: out _,
            left: Manifest(backend: "vulkan", station: station, tick: tick),
            leftDir: leftDir,
            outcomes: out var outcomes,
            right: Manifest(backend: "directx", station: station, tick: tick),
            rightDir: rightDir
        ));

        return Assert.Single(collection: outcomes).Verdicts;
    }
    private static ParityManifest Manifest(string backend, string station, ulong tick) =>
        new(
            Backend: backend,
            Captures: [new ParityManifestCapture(
                Census: new Dictionary<string, long> {
                    ["0"] = 5000,
                    ["1"] = 5000,
                },
                Detail: null,
                Frame: "binding.png",
                Refusal: null,
                RegionTick: tick,
                Station: station,
                StateHash: StateHash,
                Tick: tick
            )],
            World: "w"
        );
    private static ParityCaptureVerdict Verdict(IReadOnlyList<ParityCaptureVerdict> verdicts, string prefix) =>
        Assert.Single(
            collection: verdicts,
            predicate: verdict => verdict.Name.StartsWith(
                comparisonType: StringComparison.Ordinal,
                value: prefix
            )
        );
    private string WriteWorld(Action<JsonNode> edit) {
        var world = JsonNode.Parse(json: File.ReadAllText(path: RepositoryPaths.Resolve(relativePath: WorldPath)))!;

        edit(obj: world);

        var path = Path.Combine(
            path1: m_directory.RootPath,
            path2: $"world-{Guid.NewGuid():N}.json"
        );

        File.WriteAllText(
            contents: world.ToJsonString(),
            path: path
        );

        return path;
    }

    [Fact]
    public void AFrameBothBackendsAgreeOnFailsWhenItIsNotTheReference() {
        var (contract, reference) = LoadContract();
        var width = ((int)reference.Width);
        var height = ((int)reference.Height);
        var expected = reference.Render(tick: Tick);
        var verdicts = Compare(
            contract: contract,
            height: height,
            left: expected,
            right: expected,
            width: width
        );

        Assert.All(
            action: static verdict => Assert.True(condition: verdict.Passed),
            collection: verdicts
        );
        Assert.Equal(
            actual: Verdict(
                prefix: "REFERENCE-",
                verdicts: verdicts
            ).Name,
            expected: "REFERENCE-OK"
        );

        // The next grain frame's image: both sides agree, so the pixel verdict holds, but neither is the capture tick's.
        var shared = reference.Render(tick: 1225);

        Assert.NotEqual(
            actual: shared,
            expected: expected
        );

        verdicts = Compare(
            contract: contract,
            height: height,
            left: shared,
            right: shared,
            width: width
        );

        Assert.True(condition: Verdict(
            prefix: "PIXEL-",
            verdicts: verdicts
        ).Passed);

        var failed = Verdict(
            prefix: "REFERENCE-",
            verdicts: verdicts
        );

        Assert.Equal(
            actual: failed.Name,
            expected: "REFERENCE-FAILED"
        );
        Assert.Contains(
            actualString: failed.Detail,
            expectedSubstring: "vulkan differs"
        );
        Assert.Contains(
            actualString: failed.Detail,
            expectedSubstring: "directx differs"
        );
    }
    [Fact]
    public void OneWrongByteFailsTheReferenceNamingItsSideAndPixel() {
        var (contract, reference) = LoadContract();
        var expected = reference.Render(tick: Tick);
        var right = expected.ToArray();
        var at = ((((5 * ((int)reference.Width)) + 7) * 4) + 2);

        right[at] ^= 1;

        var failed = Verdict(
            prefix: "REFERENCE-",
            verdicts: Compare(
                contract: contract,
                height: ((int)reference.Height),
                left: expected,
                right: right,
                width: ((int)reference.Width)
            )
        );

        Assert.False(condition: failed.Passed);
        Assert.DoesNotContain(
            actualString: failed.Detail,
            expectedSubstring: "vulkan"
        );
        Assert.Contains(
            actualString: failed.Detail,
            expectedSubstring: $"directx differs in 1 bytes, first at (7,5) channel 2: {right[at]} vs {expected[at]}"
        );
    }
    [Fact]
    public void AReferenceKindTheComparatorCannotComputeIsRefusedByName() {
        var contract = JsonNode.Parse(json: File.ReadAllText(path: RepositoryPaths.Resolve(relativePath: ContractPath)))!;

        contract["stations"]![Station]!["reference"]!["kind"] = "unknown";

        var path = Path.Combine(
            path1: m_directory.RootPath,
            path2: "contract.json"
        );

        File.WriteAllText(
            contents: contract.ToJsonString(),
            path: path
        );

        Assert.False(condition: ParityManifestLoader.TryLoadContract(
            contract: out _,
            error: out var error,
            path: path
        ));
        Assert.Contains(
            actualString: error,
            expectedSubstring: "reference kind 'unknown' is not a reference the comparator computes"
        );
    }
    [Fact]
    public void TheScheduleFollowsTheWorldsCaptureTicks() {
        Assert.True(
            condition: ParityCommand.TryReadSchedule(
                error: out var error,
                reconstructionTick: out var reconstructionTick,
                waitTick: out var waitTick,
                worldPath: RepositoryPaths.Resolve(relativePath: WorldPath)
            ),
            userMessage: error
        );
        // Past the converge station's last capture, and reconstruction on its lead before the station's first.
        Assert.Equal(
            actual: waitTick,
            expected: 1360UL
        );
        Assert.Equal(
            actual: reconstructionTick,
            expected: 1240UL
        );
    }
    // A station captured once reconstruction is on must converge, since every station that does not holds with
    // reconstruction off; a converging station with no room for the lead is refused too.
    [Fact]
    public void AStationThatDoesNotConvergeAfterReconstructionTurnsOnIsRefusedByName() {
        var late = WriteWorld(edit: static world => world["captures"]!["rows"]![0]!["ticks"] = new JsonArray(JsonValue.Create(value: 1320UL)));

        Assert.False(condition: ParityCommand.TryReadSchedule(
            error: out var error,
            reconstructionTick: out _,
            waitTick: out _,
            worldPath: late
        ));
        Assert.Contains(
            actualString: error,
            expectedSubstring: "without converging"
        );
        var early = WriteWorld(edit: static world => {
            var rows = world["captures"]!["rows"]!.AsArray();

            rows[(rows.Count - 1)]!["ticks"] = new JsonArray(JsonValue.Create(value: 30UL));
        });

        Assert.False(condition: ParityCommand.TryReadSchedule(
            error: out error,
            reconstructionTick: out _,
            waitTick: out _,
            worldPath: early
        ));
        Assert.Contains(
            actualString: error,
            expectedSubstring: "leaves no room for the 60-tick reconstruction lead"
        );
    }
    /// <summary>The <c>bound</c> station's grain seed is bound to a state row the world's rules move from 0 to 13 at
    /// tick 1210, its captures sit on both sides of the move, and its contract states the row's steps. The frame the
    /// row draws holds the reference at both ticks. A seed that does not follow the row, both backends agreeing, fails
    /// one capture or both: the literal 0 fails the capture after the move, the literal 13 the capture before it, and
    /// the graph's default, which the pane shows when the binding does not resolve, both.</summary>
    [Fact]
    public void TheBoundStationsCapturesFailEverySeedThatDoesNotFollowTheRow() {
        const string BoundStation = "bound";
        const ulong Before = 1195UL;
        const ulong After = 1215UL;

        var (contract, bound) = LoadContract(station: BoundStation);
        var (_, defaults) = LoadContract();
        var literalZero = LoadBound(parameters: """{ "grain": { "seed": { "0": 0 } } }""");
        var literalThirteen = LoadBound(parameters: """{ "grain": { "seed": { "0": 13 } } }""");

        string Judge(ParityBindingReference drawn, ulong tick) {
            var frame = drawn.Render(tick: tick);

            return Verdict(
                prefix: "REFERENCE-",
                verdicts: Compare(
                    contract: contract,
                    height: ((int)bound.Height),
                    left: frame,
                    right: frame,
                    station: BoundStation,
                    tick: tick,
                    width: ((int)bound.Width)
                )
            ).Name;
        }

        Assert.Equal(
            actual: ((string[])[
                Judge(drawn: bound, tick: Before),
                Judge(drawn: bound, tick: After),
                Judge(drawn: literalZero, tick: Before),
                Judge(drawn: literalZero, tick: After),
                Judge(drawn: literalThirteen, tick: Before),
                Judge(drawn: literalThirteen, tick: After),
                Judge(drawn: defaults, tick: Before),
                Judge(drawn: defaults, tick: After),
            ]),
            expected: ["REFERENCE-OK", "REFERENCE-OK", "REFERENCE-OK", "REFERENCE-FAILED", "REFERENCE-FAILED", "REFERENCE-OK", "REFERENCE-FAILED", "REFERENCE-FAILED"]
        );
    }

    private ParityBindingReference LoadBound(string parameters) {
        Assert.True(
            condition: ParityManifestLoader.TryLoadContract(
                contract: out var contract,
                error: out var error,
                path: WriteContract(edit: contract => contract["stations"]!["bound"]!["reference"]!["parameters"] = JsonNode.Parse(json: parameters))
            ),
            userMessage: error
        );
        Assert.True(condition: contract.TryResolveStation(
            resolved: out var resolved,
            station: "bound"
        ));

        return Assert.IsType<ParityBindingReference>(@object: resolved.Reference);
    }

    [Fact]
    public void AReferenceParameterStepThatDoesNotNameATickIsRefusedByName() {
        var path = WriteContract(edit: static contract => contract["stations"]!["bound"]!["reference"]!["parameters"] = JsonNode.Parse(json: """{ "grain": { "seed": { "later": 13 } } }"""));

        Assert.False(condition: ParityManifestLoader.TryLoadContract(
            contract: out _,
            error: out var error,
            path: path
        ));
        Assert.Contains(
            actualString: error,
            expectedSubstring: "parameters.grain.seed key 'later' must be the simulation tick the value starts at"
        );
    }
    [Fact]
    public void AReferenceParameterNamingAFieldTheReferenceDoesNotReadIsRefusedByName() {
        var path = WriteContract(edit: static contract => contract["stations"]!["bound"]!["reference"]!["parameters"] = JsonNode.Parse(json: """{ "grain": { "levels": { "0": 3 } } }"""));

        Assert.False(condition: ParityManifestLoader.TryLoadContract(
            contract: out _,
            error: out var error,
            path: path
        ));
        Assert.Contains(
            actualString: error,
            expectedSubstring: "the reference reads no scalar field 'grain.levels'"
        );
    }
    [Fact]
    public void AReferenceParameterThatIsNotAWholeNumberIsRefusedByName() {
        var path = WriteContract(edit: static contract => contract["stations"]!["bound"]!["reference"]!["parameters"] = JsonNode.Parse(json: """{ "grain": { "seed": { "0": -1 } } }"""));

        Assert.False(condition: ParityManifestLoader.TryLoadContract(
            contract: out _,
            error: out var error,
            path: path
        ));
        Assert.Contains(
            actualString: error,
            expectedSubstring: "parameters.grain.seed.0 must be a whole number"
        );
    }
    [Fact]
    public void AScheduleWithNoRoomForTheWaitMarginIsRefusedByName() {
        var path = WriteWorld(edit: static world => world["captures"]!["rows"]![0]!["ticks"] = new JsonArray(JsonValue.Create(value: (ulong.MaxValue - 5UL))));

        Assert.False(condition: ParityCommand.TryReadSchedule(
            error: out var error,
            reconstructionTick: out _,
            waitTick: out _,
            worldPath: path
        ));
        Assert.Contains(
            actualString: error,
            expectedSubstring: "leaves no room for the 30-tick wait margin"
        );
    }
    [Fact]
    public void AMalformedWorldIsRefusedByNameRatherThanThrown() {
        var path = Path.Combine(
            path1: m_directory.RootPath,
            path2: "malformed.world.json"
        );

        File.WriteAllText(
            contents: "{ \"captures\": ",
            path: path
        );

        Assert.False(condition: ParityCommand.TryReadSchedule(
            error: out var error,
            reconstructionTick: out _,
            waitTick: out _,
            worldPath: path
        ));
        Assert.Contains(
            actualString: error,
            expectedSubstring: "could not be read for its capture schedule"
        );
    }
    public void Dispose() => m_directory.Dispose();
}
