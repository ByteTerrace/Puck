using Puck.GamingBricks.Forge;

namespace Puck.AdvancedGamingBrick.Forge.Tests;

/// <summary>Covers battery-backed state reaching the cartridge's save window and coming back.</summary>
public sealed class CartridgeSaveTests {
    private static readonly CartridgeRefusal[] Refusals = [
        new(
            Name: "a save step without a save",
            Document: Bad() with { Rules = [Rule(body: [new CartridgeStatement(Kind: "save")])] },
            Path: "rules[0].body[0]",
            Fragment: "requires a declared save"
        ),
        new(
            Name: "an unknown variable",
            Document: Bad() with { Save = Persisting(
                arrays: [],
                variables: ["missing"]
            ) },
            Path: "save.variables",
            Fragment: "Unknown state variable"
        ),
        new(
            Name: "a variable persisted twice",
            Document: Bad() with { Save = Persisting(
                arrays: [],
                variables: ["x", "x"]
            ) },
            Path: "save.variables",
            Fragment: "persisted more than once"
        ),
        new(
            Name: "a save of nothing",
            Document: Bad() with { Save = Persisting(
                arrays: [],
                variables: []
            ) },
            Path: "save",
            Fragment: "at least one variable or array"
        ),
        new(
            Name: "a payload past the battery mirror",
            Document: Bad() with { Save = Persisting(
                arrays: ["big"],
                variables: []
            ) },
            Path: "save",
            Fragment: "battery-backed mirror holds"
        ),
    ];

    public static TheoryData<string> RefusalNames => CartridgeRefusal.Names(table: Refusals);

    private static CartridgeDocument Bad() => CartridgeDocuments.Create(
        target: "cgb",
        title: "SAVEBAD"
    ) with {
        Variables = [new CartridgeVariable(
            Name: "x",
            Initial: 0
        )],
        Arrays = [new CartridgeArray(
            Initial: new int[200],
            Name: "big"
        )],
    };
    private static CartridgeSave Persisting(string[] arrays, string[] variables) => new(
        Arrays: arrays,
        Variables: variables,
        Version: 1
    );
    // Runs its body on the frame the phase counter names, then advances it.
    private static CartridgeRule Once(int phase, CartridgeStatement[] body) => new(
        Name: $"phase{phase}",
        When: CartridgeExpressions.Gate(
            left: CartridgeExpressions.Of(state: "phase"),
            comparison: ExpressionOp.Equal,
            right: CartridgeExpressions.Of(constant: phase)
        ),
        Body: [.. body, Set(
                target: "phase",
                value: (phase + 1)
            )]
    );
    private static CartridgeRule Rule(CartridgeStatement[] body) => new(
        Name: "rule",
        Body: body
    );
    private static CartridgeStatement Set(string target, int value) =>
        new(
            Kind: "set",
            Target: new CartridgeTarget(State: target),
            Operation: null,
            Value: CartridgeExpressions.Of(constant: value)
        );

    [InlineData("cgb")]
    [InlineData("agb")]
    [Theory]
    public void SavedStateSurvivesBeingOverwrittenInMemory(string target) {
        var document = CartridgeDocuments.Create(
            target: target,
            title: "SAVE"
        ) with {
            Variables = [
                new CartridgeVariable(
                Name: "counter",
                Initial: 42
            ),
                new CartridgeVariable(
                Name: "phase",
                Initial: 0
            ),
            ],
            Arrays = [new CartridgeArray(
                Initial: [7, 8, 9],
                Name: "table"
            )],
            Save = new CartridgeSave(
            Arrays: ["table"],
            Variables: ["counter"],
            Version: 1
        ),
            Rules = [
                // Store the authored state, scribble over it in memory, then restore it from the save window.
                Once(
                phase: 0,
                body: [
                    new CartridgeStatement(Kind: "save"),
                    Set(
                        target: "counter",
                        value: 99
                    ),
                    new CartridgeStatement(
                        Kind: "set",
                        Target: new CartridgeTarget(
                            State: "table",
                            Key: CartridgeExpressions.Key(index: CartridgeExpressions.Of(constant: 1))
                        ),
                        Operation: null,
                        Value: CartridgeExpressions.Of(constant: 200)
                    ),
                ]
            ),
                Once(
                phase: 1,
                body: [new CartridgeStatement(Kind: "load")]
            ),
            ],
        };
        var result = CartridgeProbe.Compile(document: document);
        using var machine = new CartridgeProbe(
            label: "save",
            result: result
        );

        machine.Run(frames: 16);
        Assert.Equal(
            expected: 42,
            actual: machine.Read(address: result.Variables["counter"])
        );
        Assert.Equal(
            expected: 7,
            actual: machine.Read(address: result.Arrays["table"])
        );
        Assert.Equal(
            expected: 8,
            actual: machine.Read(address: (result.Arrays["table"] + 1))
        );
        Assert.Equal(
            expected: 9,
            actual: machine.Read(address: (result.Arrays["table"] + 2))
        );
    }
    [MemberData(memberName: nameof(RefusalNames))]
    [Theory]
    public void ValidationGatesSaveOnTargetDeclarationAndCapacity(string refusal) => CartridgeRefusal.Holds(
        name: refusal,
        table: Refusals
    );
}
