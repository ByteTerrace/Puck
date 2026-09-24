using System.Globalization;
using System.Text;

using Puck.Cli.Bench;

using Xunit;

namespace Puck.Cli.Tests;

// Every law here drives the walker over a hand-written disassembly, under one cycle of service for every
// instruction form, so a price reads as the number of instructions the maximum permitted path carries.
public sealed class ReferenceWalkerLawTests {
    private const string ThrowHelper = "S_P_CoreLib_System_ThrowHelper__ThrowArgumentOutOfRangeException";

    private static string Body(params string[] rows) {
        var builder = new StringBuilder();

        for (var index = 0; (index < rows.Length); ++index) {
            builder.Append(value: ((index + 1) * 0x1000).ToString(
                format: "x",
                provider: CultureInfo.InvariantCulture
            )).Append(value: ": ").Append(value: rows[index]).Append(value: '\n');
        }
        return builder.ToString();
    }
    private static ReferencePrice Price(
        IReadOnlyDictionary<string, string> bodies,
        string symbol,
        IReadOnlyDictionary<string, long>? loopBounds = null,
        IReadOnlySet<string>? unserved = null,
        ReferenceArchitecture architecture = ReferenceArchitecture.X64
    ) {
        var walker = Walk(
            architecture: architecture,
            bodies: bodies,
            symbol: symbol
        );
        var service = new Dictionary<string, ReferenceMeasurement>(comparer: StringComparer.Ordinal);

        foreach (var form in walker.Forms(symbol: symbol)) {
            if (unserved?.Contains(item: form) ?? false) { continue; }
            service.Add(
                key: form,
                value: new(
                    Latency: 1L,
                    ReciprocalThroughput: 1M
                )
            );
        }
        return walker.Price(
            loopBounds: (loopBounds ?? new Dictionary<string, long>(comparer: StringComparer.Ordinal)),
            service: service,
            symbol: symbol
        );
    }
    private static ReferenceWalker Walk(IReadOnlyDictionary<string, string> bodies, string symbol, ReferenceArchitecture architecture) {
        var walker = new ReferenceWalker(
            architecture: architecture,
            disassemble: name => (bodies.TryGetValue(
                key: name,
                value: out var text
            )
                ? ReferenceLowering.Decode(
                    architecture: architecture,
                    disassembly: text
                )
                : [])
        );

        _ = walker.Graph(symbol: symbol);
        return walker;
    }

    [Fact]
    public void AStraightLineCostsEveryInstructionItCarries() {
        var priced = Price(
            bodies: new Dictionary<string, string>(comparer: StringComparer.Ordinal) {
                ["line"] = Body(
                    "movq\t%rcx, %rax",
                    "addq\t%rbx, %rax",
                    "subq\t%rdx, %rax",
                    "retq"
                ),
            },
            symbol: "line"
        );

        Assert.Empty(collection: priced.Issues);
        Assert.Equal(
            4L,
            priced.Cycles
        );
    }
    [Fact]
    public void ADiamondCostsItsCostlierArmAndNotTheSumOfBoth() {
        // The cheaper arm is the branch target, so it is the first successor the condensation offers: a walk that
        // takes its first alternative rather than its largest reads 4 here.
        var bodies = new Dictionary<string, string>(comparer: StringComparer.Ordinal) {
            ["diamond"] = Body(
                "cmpq\t%rbx, %rax",
                "je\t0x6000",
                "subq\t%rbx, %rax",
                "imulq\t%rbx, %rax",
                "jmp\t0x7000",
                "addq\t%rbx, %rax",
                "retq"
            ),
        };
        var priced = Price(
            bodies: bodies,
            symbol: "diamond"
        );

        // Setup 2, the costlier arm 3, the join 1. The cheaper arm's 1 is not added, and neither arm is skipped.
        Assert.Empty(collection: priced.Issues);
        Assert.Equal(
            6L,
            priced.Cycles
        );
    }
    [Fact]
    public void ABoundedLoopCostsItsBodyOncePerDeclaredIteration() {
        var bodies = new Dictionary<string, string>(comparer: StringComparer.Ordinal) {
            ["loop"] = Body(
                "movq\t%rcx, %rax",
                "addq\t%rbx, %rax",
                "subq\t$1, %rcx",
                "jne\t0x2000",
                "retq"
            ),
        };
        var priced = Price(
            bodies: bodies,
            loopBounds: new Dictionary<string, long>(comparer: StringComparer.Ordinal) { ["loop"] = 7L },
            symbol: "loop"
        );

        // The preheader and the exit are charged once; the three-instruction body is charged seven times.
        Assert.Empty(collection: priced.Issues);
        Assert.Equal(
            23L,
            priced.Cycles
        );
    }
    [Fact]
    public void ALoopWithNoDeclaredBoundRefusesByName() {
        var priced = Price(
            bodies: new Dictionary<string, string>(comparer: StringComparer.Ordinal) {
                ["loop"] = Body(
                    "movq\t%rcx, %rax",
                    "addq\t%rbx, %rax",
                    "subq\t$1, %rcx",
                    "jne\t0x2000",
                    "retq"
                ),
            },
            symbol: "loop"
        );

        Assert.Equal(
            ["loop: loop with no declared source-contract iteration bound"],
            priced.Issues
        );
    }
    [Fact]
    public void AHelperIsPricedWithItsCaller() {
        var bodies = new Dictionary<string, string>(comparer: StringComparer.Ordinal) {
            ["caller"] = Body(
                "movq\t%rcx, %rax",
                "callq\t0x9000 <helper>",
                "retq"
            ),
            ["helper"] = Body(
                "addq\t%rbx, %rax",
                "addq\t%rbx, %rax",
                "retq"
            ),
        };
        var priced = Price(
            bodies: bodies,
            symbol: "caller"
        );

        Assert.Empty(collection: priced.Issues);
        Assert.Equal(
            6L,
            priced.Cycles
        );
    }
    [Fact]
    public void AHelperWithNoBodyInTheObjectRefusesByName() {
        var priced = Price(
            bodies: new Dictionary<string, string>(comparer: StringComparer.Ordinal) {
                ["caller"] = Body(
                    "movq\t%rcx, %rax",
                    "callq\t0x9000 <helper>",
                    "retq"
                ),
            },
            symbol: "caller"
        );

        Assert.Equal(
            ["no body for helper in the reference object"],
            priced.Issues
        );
    }
    [Fact]
    public void ARecursiveCallCycleRefusesByName() {
        var bodies = new Dictionary<string, string>(comparer: StringComparer.Ordinal) {
            ["caller"] = Body(
                "movq\t%rcx, %rax",
                "callq\t0x9000 <helper>",
                "retq"
            ),
            ["helper"] = Body(
                "addq\t%rbx, %rax",
                "callq\t0x9000 <caller>",
                "retq"
            ),
        };
        var priced = Price(
            bodies: bodies,
            symbol: "caller"
        );

        Assert.Equal(
            ["recursive call cycle through caller"],
            priced.Issues
        );
    }
    [Fact]
    public void AnIndirectExitWithNoCandidateArmRefusesByName() {
        var priced = Price(
            bodies: new Dictionary<string, string>(comparer: StringComparer.Ordinal) {
                ["dispatch"] = Body(
                    "movq\t(%rcx), %rax",
                    "jmpq\t*%rax"
                ),
            },
            symbol: "dispatch"
        );

        Assert.Equal(
            ["dispatch: 1 indirect exit(s) with no candidate target in the body"],
            priced.Issues
        );
    }
    [Fact]
    public void AnIndirectExitTakesTheCostliestBlockTheDirectWalkCannotReach() {
        var bodies = new Dictionary<string, string>(comparer: StringComparer.Ordinal) {
            ["dispatch"] = Body(
                "movq\t(%rcx), %rax",
                "jmpq\t*%rax",
                "addq\t%rbx, %rax",
                "retq",
                "subq\t%rbx, %rax",
                "imulq\t%rbx, %rax",
                "retq"
            ),
        };
        var priced = Price(
            bodies: bodies,
            symbol: "dispatch"
        );

        // The two blocks nothing branches to are the jump table's candidate arms: the dispatch is charged the
        // costlier of them, and the exit is no longer stranded.
        Assert.Empty(collection: priced.Issues);
        Assert.Equal(
            5L,
            priced.Cycles
        );
    }
    [Fact]
    public void AnInstructionTheModelDoesNotServeRefusesByName() {
        var priced = Price(
            bodies: new Dictionary<string, string>(comparer: StringComparer.Ordinal) {
                ["line"] = Body(
                    "movq\t%rcx, %rax",
                    "vpshufbitqmb\t%zmm1, %zmm2, %k1",
                    "retq"
                ),
            },
            symbol: "line",
            unserved: new HashSet<string>(comparer: StringComparer.Ordinal) { "vpshufbitqmb\t%zmm1, %zmm2, %k1" }
        );

        Assert.Equal(
            ["line: no published service for 'vpshufbitqmb\t%zmm1, %zmm2, %k1'"],
            priced.Issues
        );
    }
    [Fact]
    public void AContinuationIntoAManagedThrowIsExcludedRatherThanCharged() {
        var bodies = new Dictionary<string, string>(comparer: StringComparer.Ordinal) {
            ["guard"] = Body(
                "cmpq\t%rbx, %rax",
                "je\t0x5000",
                "addq\t%rbx, %rax",
                "retq",
                $"callq\t0x9000 <{ThrowHelper}>",
                "addq\t%rbx, %rax",
                "addq\t%rbx, %rax",
                "addq\t%rbx, %rax",
                "retq"
            ),
            [ThrowHelper] = Body(
                "pushq\t%rbp",
                "movq\t%rsp, %rbp",
                "addq\t%rbx, %rax",
                "addq\t%rbx, %rax",
                "addq\t%rbx, %rax",
                "addq\t%rbx, %rax",
                "addq\t%rbx, %rax",
                "addq\t%rbx, %rax",
                "addq\t%rbx, %rax",
                "retq"
            ),
        };
        var priced = Price(
            bodies: bodies,
            symbol: "guard"
        );

        // Reaching the call site is charged; the ten instructions of the throw helper's own body are not.
        Assert.Empty(collection: priced.Issues);
        Assert.Equal(
            7L,
            priced.Cycles
        );
    }
    [Fact]
    public void ACallIntoAnotherSectionIsNamedByTheRelocationPrintedAfterIt() {
        // An unlinked call prints the offset of its own next instruction; the relocation line beneath it carries the
        // symbol, which is the helper priced with the caller here and a fail-fast that ends the path below.
        var bodies = new Dictionary<string, string>(comparer: StringComparer.Ordinal) {
            ["caller"] = Body(
                "movq\t%rcx, %rax",
                "callq\t0x3000 <caller+0x2000>",
                "IMAGE_REL_AMD64_REL32\thelper",
                "retq"
            ),
            ["guarded"] = Body(
                "movq\t%rcx, %rax",
                "callq\t0x3000 <guarded+0x2000>",
                "IMAGE_REL_AMD64_REL32\tRhpFallbackFailFast",
                "retq"
            ),
            ["helper"] = Body(
                "addq\t%rbx, %rax",
                "addq\t%rbx, %rax",
                "retq"
            ),
        };
        var priced = Price(
            bodies: bodies,
            symbol: "caller"
        );
        var guarded = Price(
            bodies: bodies,
            symbol: "guarded"
        );

        Assert.Empty(collection: priced.Issues);
        Assert.Equal(
            6L,
            priced.Cycles
        );
        Assert.Empty(collection: guarded.Issues);
    }
    [Fact]
    public void ABranchThatTestsARegisterFirstStillNamesItsTarget() {
        // Rows sit at 0x1000, 0x2000, …: the compare-and-branch skips one instruction and the test-bit-and-branch
        // closes a loop, so both targets must be read from the last operand for the graph to have either edge.
        var bodies = new Dictionary<string, string>(comparer: StringComparer.Ordinal) {
            ["scan"] = Body(
                "cbz\tx1, 0x3000",
                "add\tx0, x0, x1",
                "sub\tx1, x1, #0x1",
                "tbnz\tw1, #0x3, 0x2000",
                "ret"
            ),
        };
        var unbounded = Price(
            architecture: ReferenceArchitecture.Arm64,
            bodies: bodies,
            symbol: "scan"
        );
        var bounded = Price(
            architecture: ReferenceArchitecture.Arm64,
            bodies: bodies,
            loopBounds: new Dictionary<string, long>(comparer: StringComparer.Ordinal) { ["scan"] = 4L },
            symbol: "scan"
        );

        Assert.Equal(
            ["scan: loop with no declared source-contract iteration bound"],
            unbounded.Issues
        );
        // The guard and the return are charged once; the three-instruction loop body four times.
        Assert.Empty(collection: bounded.Issues);
        Assert.Equal(
            14L,
            bounded.Cycles
        );
    }
    [Fact]
    public void TheArm64LoweringIsDecodedByItsOwnBranchMnemonicsAndCommentMarker() {
        var bodies = new Dictionary<string, string>(comparer: StringComparer.Ordinal) {
            ["caller"] = Body(
                "mov\tx0, x1                 // a comment the form excludes",
                "bl\t0x9000 <helper>",
                "ret"
            ),
            ["helper"] = Body(
                "add\tx0, x1, x2",
                "ret"
            ),
        };
        var walker = Walk(
            architecture: ReferenceArchitecture.Arm64,
            bodies: bodies,
            symbol: "caller"
        );

        Assert.Contains(
            collection: walker.Forms(symbol: "caller"),
            filter: static form => string.Equals(
                a: form,
                b: "mov\tx0, x1",
                comparisonType: StringComparison.Ordinal
            )
        );
        Assert.Equal(
            5L,
            Price(
                architecture: ReferenceArchitecture.Arm64,
                bodies: bodies,
                symbol: "caller"
            ).Cycles
        );
        Assert.Equal(
            ["dispatch: 1 indirect exit(s) with no candidate target in the body"],
            Price(
                architecture: ReferenceArchitecture.Arm64,
                bodies: new Dictionary<string, string>(comparer: StringComparer.Ordinal) {
                    ["dispatch"] = Body(
                        "ldr\tx0, [x1]",
                        "br\tx0"
                    ),
                },
                symbol: "dispatch"
            ).Issues
        );
    }
    [Fact]
    public void ABranchTargetIsFoldedOutOfTheFormButNotOutOfTheGraph() {
        var walker = Walk(
            architecture: ReferenceArchitecture.X64,
            bodies: new Dictionary<string, string>(comparer: StringComparer.Ordinal) {
                ["diamond"] = Body(
                    "cmpq\t%rbx, %rax",
                    "je\t0x4000",
                    "addq\t%rbx, %rax",
                    "retq"
                ),
            },
            symbol: "diamond"
        );
        var graph = walker.Graph(symbol: "diamond")!;

        Assert.Contains(
            collection: walker.Forms(symbol: "diamond"),
            filter: static form => string.Equals(
                a: form,
                b: "je\t0",
                comparisonType: StringComparison.Ordinal
            )
        );
        Assert.Equal(
            [0x1000L, 0x3000L, 0x4000L],
            graph.Order
        );
        Assert.Equal(
            [0x4000L, 0x3000L],
            graph.Edges[key: 0x1000L]
        );
    }
    [Fact]
    public void TheServiceOfOneFormIsTheLargerOfItsLatencyAndItsRoundedUpReciprocalThroughput() {
        Assert.Equal(
            1L,
            new ReferenceMeasurement(
                Latency: 0L,
                ReciprocalThroughput: 0.25M
            ).Service
        );
        Assert.Equal(
            5L,
            new ReferenceMeasurement(
                Latency: 5L,
                ReciprocalThroughput: 0.5M
            ).Service
        );
        Assert.Equal(
            3L,
            new ReferenceMeasurement(
                Latency: 1L,
                ReciprocalThroughput: 2.5M
            ).Service
        );
    }
}
