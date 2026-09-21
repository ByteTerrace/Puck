using System.Text.Json.Nodes;
using Puck.Transpiler.Diagnostics;
using Puck.Transpiler.Lowering;
using Puck.World.Transpiler.Lowering;
using Xunit;

namespace Puck.World.Transpiler.Tests;

/// <summary>Deferred composition work consumes the same bounded budget as module expansion.</summary>
public sealed class CompositionBudgetTests {
    [Fact]
    public void DoorCopiesAreChargedToTheCompilationBudgetAtTheLink() {
        var source = new JsonObject {
            ["prototypes"] = new JsonArray(new JsonObject { ["id"] = "arch", ["document"] = new JsonObject() }),
            ["placements"] = new JsonObject {
                ["rows"] = new JsonArray(new JsonObject {
                    ["id"] = "arch1",
                    ["prototypeId"] = "arch",
                    ["faceSources"] = new JsonArray(new JsonObject {
                        ["face"] = "portal",
                        ["source"] = new JsonObject { ["$type"] = "none" },
                    }),
                }),
            },
        };
        var destination = new JsonObject {
            ["spawnPoints"] = new JsonArray(new JsonObject {
                ["id"] = "arrival",
                ["position"] = new JsonArray(0, 0, 0),
                ["yawDegrees"] = 0,
            }),
        };
        var worlds = new Dictionary<string, JsonObject> { ["source"] = source, ["destination"] = destination };
        var span = new SourceSpan(Column: 1, Length: 32, Line: 7, Offset: 40);
        var link = new WorldCompositionLink("door", "source", "arch1", "destination", "arrival", new JsonObject(), span);
        var budget = new DocumentEvaluationBudget();

        budget.Spend(count: DocumentEvaluationBudget.WorkLimit, span: SourceSpan.None);
        var error = Assert.Throws<DocumentEvaluationException>(testCode: () => WorldCompositionLinks.Apply(
            budget: budget,
            diagnostics: new DiagnosticBag(),
            links: [link],
            worlds: worlds
        ));

        Assert.Equal(expected: PuckDiagnosticCodes.EvaluationLimit, actual: error.Code);
        Assert.Equal(expected: span, actual: error.Span);
    }
}
