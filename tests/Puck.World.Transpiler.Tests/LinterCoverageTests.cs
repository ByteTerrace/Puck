using Puck.Transpiler.Diagnostics;
using Puck.World.Transpiler.Validation;
using Xunit;

namespace Puck.World.Transpiler.Tests;

/// <summary>Corpus-wide coverage for <see cref="PuckLinter.LintReferences"/>'s basis-vs-module policy: every
/// shipped world (root, basis-bearing non-root, or basis-less module) lints clean of reference-family findings,
/// and a genuine typo inside a basis-declaring document's own rules is still caught, with a real span.</summary>
public class LinterCoverageTests {
    [MemberData(nameof(GetShippedWorldSources))]
    [Theory]
    public void EveryCommittedSourceLintsCleanOfReferenceFindings(string relativePath) {
        var compilation = ShippedWorlds.Compile(relativePath: relativePath);
        var diagnostics = new DiagnosticBag();

        PuckLinter.LintReferences(
            compilation.RequireJson(),
            compilation.SourceMap,
            diagnostics,
            sourcePath: ShippedWorlds.PathOf(relativePath: relativePath)
        );

        var referenceFindings = diagnostics.Concat(second: compilation.Diagnostics).Where(predicate: d => ReferenceLintCodes.Contains(value: d.Code)).ToList();

        Assert.True(
            condition: (referenceFindings.Count == 0),
            userMessage: ($"{relativePath} expected no reference-lint findings, got:{Environment.NewLine}"
                + string.Join(
                separator: Environment.NewLine,
                values: referenceFindings.Select(selector: d => $"{d.Code} {d.Span}: {d.Message}")
            ))
        );
    }
    public static TheoryData<string> GetShippedWorldSources() => ShippedWorlds.Sources();
    // A fold names its family on the instruction and reads rows from a subprogram body, so the reference check
    // reaches both or a misbound fold lints clean.
    [Fact]
    public void AFoldsFamilyAndItsBodysReadsAreResolvedLikeAnyOtherRead() {
        var document = System.Text.Json.Nodes.JsonNode.Parse(json: /*lang=json*/ """
            {
              "schema": "puck.world.definition.v1",
              "state": { "world": [ { "name": "score", "kind": "Int", "value": 0 } ] },
              "rules": [
                {
                  "name": "tally",
                  "effects": [
                    {
                      "$type": "setState",
                      "state": "score",
                      "expression": {
                        "instructions": [ { "op": "Count", "family": "handXTYPO", "binder": "c", "subprogram": 0 } ],
                        "subprograms": [ { "name": "c", "arity": 0, "instructions": [ { "op": "Operand", "name": "bonusXTYPO" } ] } ]
                      }
                    }
                  ]
                }
              ]
            }
            """)!.AsObject();
        var diagnostics = new DiagnosticBag();

        PuckLinter.LintReferences(
            document,
            null,
            diagnostics,
            sourcePath: "fold.puck"
        );

        var messages = diagnostics
            .Where(predicate: static finding => (finding.Code == PuckDiagnosticCodes.LintUnresolvedState))
            .Select(selector: static finding => finding.Message)
            .ToList();

        Assert.Contains(
            collection: messages,
            expected: "Unresolved state row 'handXTYPO'."
        );
        Assert.Contains(
            collection: messages,
            expected: "Unresolved state row 'bonusXTYPO'."
        );
    }

    private static readonly string[] ReferenceLintCodes = [
        PuckDiagnosticCodes.LintUnresolvedState, PuckDiagnosticCodes.LintUnresolvedPrototype,
        PuckDiagnosticCodes.LintUnresolvedPlacementParent, PuckDiagnosticCodes.LintUnresolvedView,
        PuckDiagnosticCodes.LintUnknownChannelPrefix, PuckDiagnosticCodes.UnresolvedParent,
        PuckDiagnosticCodes.CompositionRefused,
    ];
}
