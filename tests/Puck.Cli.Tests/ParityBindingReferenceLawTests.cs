using System.Text.Json.Nodes;
using Puck.Assets;
using Puck.Cli.Parity;
using Xunit;

namespace Puck.Cli.Tests;

/// <summary>
/// Proves the parity binding station's exact reference and the <c>puck parity</c> schedule read against the checked-in
/// parity documents: a frame both backends agree on still fails when it is not the reference, one wrong byte names its
/// side, a reference the comparator cannot compute is refused by name, and a world whose schedule cannot be read or
/// leaves no room for the wait margin is a named refusal rather than an exception.
/// </summary>
public sealed class ParityBindingReferenceLawTests : IDisposable {
    private const string ContractPath = "tests/Puck.Parity/parity.contract.json";
    private const string StateHash = "0123456789abcdef";
    private const string Station = "binding";
    private const ulong Tick = 1205;
    private const string WorldPath = "tests/Puck.Parity/parity.world.json";

    private readonly string m_root;

    public ParityBindingReferenceLawTests() {
        m_root = Path.Combine(
            path1: Path.GetTempPath(),
            path2: $"puck-cli-tests-parity-reference-{Guid.NewGuid():N}"
        );

        Directory.CreateDirectory(path: m_root);
    }

    private (ParityContract Contract, ParityBindingReference Reference) LoadContract() {
        Assert.True(
            condition: ParityManifestLoader.TryLoadContract(
                contract: out var contract,
                error: out var error,
                path: RepositoryPaths.Resolve(relativePath: ContractPath)
            ),
            userMessage: error
        );
        Assert.True(condition: contract.TryResolveStation(
            resolved: out var station,
            station: Station
        ));

        return (contract, Assert.IsType<ParityBindingReference>(@object: station.Reference));
    }
    private IReadOnlyList<ParityCaptureVerdict> Compare(ParityContract contract, byte[] left, byte[] right, int width, int height) {
        var leftDir = Path.Combine(
            path1: m_root,
            path2: $"left-{Guid.NewGuid():N}"
        );
        var rightDir = Path.Combine(
            path1: m_root,
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
            left: Manifest(backend: "vulkan"),
            leftDir: leftDir,
            outcomes: out var outcomes,
            right: Manifest(backend: "directx"),
            rightDir: rightDir
        ));

        return Assert.Single(collection: outcomes).Verdicts;
    }
    private static ParityManifest Manifest(string backend) =>
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
                RegionTick: Tick,
                Station: Station,
                StateHash: StateHash,
                Tick: Tick
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
            path1: m_root,
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
            path1: m_root,
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
    public void TheWaitTickFollowsTheWorldsLastCaptureTick() {
        Assert.True(
            condition: ParityCommand.TryReadWaitTick(
                error: out var error,
                waitTick: out var waitTick,
                worldPath: RepositoryPaths.Resolve(relativePath: WorldPath)
            ),
            userMessage: error
        );
        Assert.Equal(
            actual: waitTick,
            expected: 1255UL
        );
    }
    [Fact]
    public void AScheduleWithNoRoomForTheWaitMarginIsRefusedByName() {
        var path = WriteWorld(edit: static world => world["captures"]!["rows"]![0]!["ticks"] = new JsonArray(JsonValue.Create(value: (ulong.MaxValue - 5UL))));

        Assert.False(condition: ParityCommand.TryReadWaitTick(
            error: out var error,
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
            path1: m_root,
            path2: "malformed.world.json"
        );

        File.WriteAllText(
            contents: "{ \"captures\": ",
            path: path
        );

        Assert.False(condition: ParityCommand.TryReadWaitTick(
            error: out var error,
            waitTick: out _,
            worldPath: path
        ));
        Assert.Contains(
            actualString: error,
            expectedSubstring: "could not be read for its capture schedule"
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
}
