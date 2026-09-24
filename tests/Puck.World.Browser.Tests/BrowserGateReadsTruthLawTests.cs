using System.Text;
using Puck.Testing;
using Xunit;

namespace Puck.World.Browser.Tests;

/// <summary>Proves the browser host's rule evaluator pins the same observable as the server: a world rule's gate
/// over an eased cell opens when the stored truth crosses, not when the follower arrives.</summary>
public sealed class BrowserGateReadsTruthLawTests {
    private static readonly DynamicsRow Slow = new(
        Damping: 1f,
        Frequency: 0.25f,
        Name: "slow",
        Response: 0f
    );

    private static WorldStateRow Gauge(string name, bool eased) => new(
        Name: CellName.Parse(candidate: name),
        Kind: CellKind.Int,
        Capacity: 4,
        Cells: [new StateCell(
                Key: CellName.Parse(candidate: "0"),
                Value: CellValue.Int(value: 0L),
                Dynamics: (eased ? new StateDynamics(Row: "slow") : null)
            )]
    );
    private static WorldStateRow Flag(string name) => new(
        Name: CellName.Parse(candidate: name),
        Kind: CellKind.Int,
        Cells: [new StateCell(
                Key: WorldStateRow.SlotKey,
                Value: CellValue.Int(value: 0L)
            )]
    );
    private static WorldRule Watch(string name, string gauge, string flag) => new(
        Name: CellName.Parse(candidate: name),
        Gate: new ActionPredicate.CompareState(
            State: gauge,
            Comparison: ExpressionOp.GreaterOrEqual,
            Value: 100m,
            Key: "0"
        ),
        Mode: ActionTriggerMode.Edge,
        Effects: [new ActionEffect.SetState(
                State: flag,
                Value: 1m
            )]
    );
    private static WorldDefinition BuildBaseDocument() {
        var errors = new List<string>();
        var deferred = new List<string>();
        var root = RepositoryPaths.RequireRoot();
        var basisBytes = File.ReadAllBytes(path: Path.Combine(
            root,
            "src",
            "Puck.World",
            "Assets",
            "worlds",
            "standard.world.json"
        ));
        var fragmentBytes = ShippedWorldDocuments.Read(path: Path.Combine(
            root,
            "src",
            "Puck.World",
            "Assets",
            "worlds",
            "games",
            "tictactoe.puck"
        ));

        Assert.True(
            condition: WorldDefinitionFileSource.TryComposeFragmentBytes(
                alias: "a",
                composed: out var composed,
                fragmentBytes: fragmentBytes,
                hostBytes: basisBytes,
                reason: out var composeReason
            ),
            userMessage: composeReason
        );

        Assert.True(
            condition: BrowserParser.TryParseAndValidate(
                utf8Json: Encoding.UTF8.GetBytes(s: composed!.ToJsonString()),
                errors: errors,
                deferred: deferred,
                definition: out var definition
            ),
            userMessage: string.Join(
                separator: "; ",
                values: errors
            )
        );

        return definition!;
    }

    [Fact]
    public void BrowserGateOverAnEasedCellOpensWhenTheTruthCrosses() {
        var baseDoc = BuildBaseDocument();
        var existingRows = (baseDoc.StateRaw?.World ?? []);
        var document = (baseDoc with {
            DynamicsRaw = [.. (baseDoc.DynamicsRaw ?? []), Slow],
            Rules = [.. (baseDoc.Rules ?? []), Watch(flag: "easedFired", gauge: "eased", name: "watchEased"), Watch(flag: "plainFired", gauge: "plain", name: "watchPlain")],
            StateRaw = (baseDoc.StateRaw! with {
                World = [.. existingRows, Gauge(eased: true, name: "eased"), Gauge(eased: false, name: "plain"), Flag(name: "easedFired"), Flag(name: "plainFired")],
            }),
        });

        var session = new BrowserSession(definition: document);

        Assert.True(condition: session.TryWriteRow(add: false, key: "0", reason: out var reasonEased, row: "eased", value: 300L), userMessage: reasonEased);
        Assert.True(condition: session.TryWriteRow(add: false, key: "0", reason: out var reasonPlain, row: "plain", value: 300L), userMessage: reasonPlain);

        var easedAt = -1;
        var plainAt = -1;

        for (var step = 1; (step <= 240); step++) {
            _ = session.Judge(tick: ((ulong)step));

            var plainCell = session.ReadRow(key: "$value", row: "plainFired");
            var easedCell = session.ReadRow(key: "$value", row: "easedFired");

            if ((plainAt < 0) && (plainCell.Value == "1")) {
                plainAt = step;
            }
            if ((easedAt < 0) && (easedCell.Value == "1")) {
                easedAt = step;
            }
        }

        Assert.True(condition: (plainAt > 0), userMessage: "the plain control gate never opened");
        Assert.True(
            condition: (plainAt == easedAt),
            userMessage: $"plain gate opened at step {plainAt}; eased-cell gate opened at step {easedAt}"
        );
    }
}
