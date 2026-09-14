using Puck.GamingBricks.Forge;
using Puck.HumbleGamingBrick;
using Puck.HumbleGamingBrick.Forge;
using Puck.HumbleGamingBrick.Forge.Framework;


namespace Puck.AdvancedGamingBrick.Forge.Tests;

/// <summary>Covers battery-backed state reaching the cartridge's save window and coming back.</summary>
public sealed class CartridgeSaveTests {
    // Runs its body on the frame the phase counter names, then advances it.
    private static CartridgeRule Once(int phase, CartridgeStatement[] body) => new(
        Name: $"phase{phase}",
        When: CartridgeExpressions.Gate(
            left: CartridgeExpressions.Of(state: "phase"),
            comparison: ActionStateComparison.Equal,
            right: CartridgeExpressions.Of(constant: phase)
        ),
        Body: [.. body, Set(
                target: "phase",
                value: (phase + 1)
            )]
    );
    private static void Refuses(CartridgeDocument document, string fragment) {
        var errors = CartridgeDocuments.Validate(document: document);

        Assert.Contains(
            collection: errors,
            filter: error => error.Message.Contains(
                comparisonType: StringComparison.Ordinal,
                value: fragment
            )
        );
    }
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
        ICartridgeCompiler compiler = ((target == "agb")
            ? new AgbCartridgeCompiler()
            : new HgbCartridgeCompiler()
        );
        var result = compiler.Compile(document: document);
        using var machine = new SaveProbe(result: result);

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
    [Fact]
    public void ValidationGatesSaveOnTargetDeclarationAndCapacity() {
        var document = CartridgeDocuments.Create(
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

        Refuses(
            document: document with { Rules = [Rule(body: [new CartridgeStatement(Kind: "save")])] },
            fragment: "requires a declared save"
        );
        Refuses(
            document: document with { Save = new CartridgeSave(
                Arrays: [],
                Variables: ["missing"],
                Version: 1
            ) },
            fragment: "Unknown state variable"
        );
        Refuses(
            document: document with { Save = new CartridgeSave(
                Arrays: [],
                Variables: ["x", "x"],
                Version: 1
            ) },
            fragment: "persisted more than once"
        );
        Refuses(
            document: document with { Save = new CartridgeSave(
                Arrays: [],
                Variables: [],
                Version: 1
            ) },
            fragment: "at least one variable or array"
        );
        Refuses(
            document: document with { Save = new CartridgeSave(
                Arrays: ["big"],
                Variables: [],
                Version: 1
            ) },
            fragment: "battery-backed mirror holds"
        );

    }

    private sealed class SaveProbe : IDisposable {
        private readonly AgbVerifyMachineDriver? m_agb;
        private readonly VerifyMachineDriver? m_hgb;

        public SaveProbe(CartridgeCompilation result) {
            if (result.Target == "agb") { m_agb = new AgbVerifyMachineDriver(
                rom: result.Rom,
                label: "save"
            ); } else { m_hgb = new VerifyMachineDriver(
                rom: result.Rom,
                label: "save"
            ); }
        }

        public void Dispose() { m_agb?.Dispose(); m_hgb?.Dispose(); }
        public byte Read(uint address) => (m_agb?.ReadByte(address: address) ?? m_hgb!.Read(address: ((ushort)address)));
        public void Run(int frames) {
            m_agb?.RunFrames(
                frames: frames,
                keys: AgbKeys.None
            );
            m_hgb?.RunFrames(
                buttons: JoypadButtons.None,
                frames: frames
            );
        }
    }
}
