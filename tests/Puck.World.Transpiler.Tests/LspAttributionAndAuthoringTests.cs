using System.Text.Json.Nodes;
using Puck.Testing;
using Puck.Transpiler.Diagnostics;
using Puck.Transpiler.Parsing;
using Puck.World.Transpiler.Lsp;
using Puck.World.Transpiler.Validation;
using Xunit;

namespace Puck.World.Transpiler.Tests;

/// <summary>Covers cross-file diagnostic attribution (F1/F2), template &amp; reference diagnostics with suggestions
/// (F3/F4), LSP go-to-definition, cross-import hover, code actions, context-aware completion, authored messages,
/// structured validation pointers, per-statement emitter recovery, non-evicting row completion, and no-op formatting
/// (F5–F12).</summary>
public sealed class LspAttributionAndAuthoringTests {
    // --- F1: LSP publishes foreign-file findings on the authoring file's URI with use-site relatedInformation -------

    [Fact]
    public async Task LspPublishesImportedLibraryFindingsAgainstTheLibraryUriWithUseSiteRelatedInfo() {
        using var directory = new TemporaryDirectory(prefix: "puck-lsp-f1-");

        var libPath = directory.WriteText(
            name: "lib.puck",
            text: """
                template broken() {
                  documentId: 1 / 0
                }
                """
        );
        var rootPath = directory.WriteText(
            name: "root.puck",
            text: """
                import "lib"
                broken()
                """
        );
        var libUri = new Uri(uriString: libPath).AbsoluteUri;
        var rootUri = new Uri(uriString: rootPath).AbsoluteUri;
        var transcript = await LanguageServerClient.SessionAsync(
            text: File.ReadAllText(path: rootPath),
            uri: rootUri
        );

        var notifications = transcript.Notifications(method: "textDocument/publishDiagnostics").ToList();
        var libPublish = notifications.LastOrDefault(predicate: n => string.Equals(
            a: n["params"]?["uri"]?.ToString(),
            b: libUri,
            comparisonType: StringComparison.OrdinalIgnoreCase
        ));
        var rootPublish = notifications.LastOrDefault(predicate: n => string.Equals(
            a: n["params"]?["uri"]?.ToString(),
            b: rootUri,
            comparisonType: StringComparison.OrdinalIgnoreCase
        ));

        Assert.NotNull(@object: libPublish);
        Assert.NotNull(@object: rootPublish);

        var libDiagnostics = Assert.IsType<JsonArray>(@object: libPublish!["params"]?["diagnostics"]);
        var rootDiagnostics = Assert.IsType<JsonArray>(@object: rootPublish!["params"]?["diagnostics"]);

        // The error belongs to lib.puck line 1 (0-based), NOT root.puck.
        Assert.Empty(collection: rootDiagnostics);
        var finding = Assert.Single(collection: libDiagnostics);

        Assert.Equal(expected: 1, actual: finding?["range"]?["start"]?["line"]?.GetValue<int>());
        Assert.DoesNotContain(expectedSubstring: libPath, actualString: (finding?["message"]?.ToString() ?? ""));

        var related = Assert.IsType<JsonArray>(@object: finding?["relatedInformation"]);
        var useSite = Assert.Single(collection: related);

        Assert.Equal(
            expected: rootUri,
            actual: useSite?["location"]?["uri"]?.ToString(),
            ignoreCase: true
        );
        Assert.Equal(expected: 1, actual: useSite?["location"]?["range"]?["start"]?["line"]?.GetValue<int>());

        // Fixing the root clears the foreign diagnostic published to lib.puck.
        var fixedTranscript = await LanguageServerClient.ExchangeAsync(messages: [
            LanguageServerClient.Request(id: 1, method: "initialize", @params: []),
            LanguageServerClient.Open(text: File.ReadAllText(path: rootPath), uri: rootUri),
            LanguageServerClient.Notification(
                method: "textDocument/didChange",
                @params: new JsonObject {
                    ["textDocument"] = new JsonObject { ["uri"] = rootUri, ["version"] = 2 },
                    ["contentChanges"] = new JsonArray(new JsonObject { ["text"] = "import \"lib\"\n" }),
                }
            ),
            LanguageServerClient.Request(id: 2, method: "shutdown"),
            LanguageServerClient.Notification(method: "exit"),
        ]);
        var lastLibPublish = fixedTranscript
            .Notifications(method: "textDocument/publishDiagnostics")
            .Last(predicate: n => string.Equals(
                a: n["params"]?["uri"]?.ToString(),
                b: libUri,
                comparisonType: StringComparison.OrdinalIgnoreCase
            ));

        Assert.Empty(collection: Assert.IsType<JsonArray>(@object: lastLibPublish["params"]?["diagnostics"]));
    }

    // --- F2: SourcePath attribution for template errors, nested imports, cycles, and caller arguments ---------------

    [Fact]
    public void TemplateErrorNestedImportCycleAndCallerArgumentCarryExactSourcePathAndSpan() {
        using var directory = new TemporaryDirectory(prefix: "puck-f2-");

        // F2(a) & F2(d): library template body fails vs caller argument fails inside library template.
        var libPath = directory.WriteText(
            name: "lib.puck",
            text: """
                template bodyFails() {
                  documentId: 1 / 0
                }
                template wrap(v) {
                  documentId: v
                }
                """
        );
        var rootA = directory.WriteText(
            name: "root-a.puck",
            text: """
                import "lib"
                bodyFails()
                """
        );
        var compA = WorldCompileSession.Shared.CompileFile(path: rootA, cancellationToken: TestContext.Current.CancellationToken);
        var errA = Assert.Single(collection: compA.Diagnostics, predicate: static d => (d.Severity == DiagnosticSeverity.Error));

        Assert.Equal(expected: Path.GetFullPath(path: libPath), actual: Path.GetFullPath(path: errA.SourcePath!), ignoreCase: true);
        Assert.Equal(expected: 2, actual: errA.Span.Line);
        Assert.DoesNotContain(expectedSubstring: libPath, actualString: errA.Message);
        Assert.NotNull(@object: errA.Related);
        Assert.Equal(expected: 2, actual: errA.Related!.Span.Line);

        // F2(d): root argument evaluated inside library template attributes back to the root, NOT lib.puck.
        var rootD = directory.WriteText(
            name: "root-d.puck",
            text: """
                import "lib"
                wrap(v: 1 / 0)
                """
        );
        var compD = WorldCompileSession.Shared.CompileFile(path: rootD, cancellationToken: TestContext.Current.CancellationToken);
        var errD = Assert.Single(collection: compD.Diagnostics, predicate: static d => (d.Severity == DiagnosticSeverity.Error));

        Assert.Null(@object: errD.SourcePath);
        Assert.Equal(expected: 2, actual: errD.Span.Line);

        // F2(b): nested import missing reports against the importing library's path and span.
        var nestedLibPath = directory.WriteText(
            name: "nested.puck",
            text: """
                let a = 1
                import "does-not-exist"
                """
        );
        var rootB = directory.WriteText(
            name: "root-b.puck",
            text: "import \"nested\"\n"
        );
        var compB = WorldCompileSession.Shared.CompileFile(path: rootB, cancellationToken: TestContext.Current.CancellationToken);
        var errB = Assert.Single(collection: compB.Diagnostics, predicate: static d => (d.Code == PuckDiagnosticCodes.ImportTargetMissing));

        Assert.Equal(expected: Path.GetFullPath(path: nestedLibPath), actual: Path.GetFullPath(path: errB.SourcePath!), ignoreCase: true);
        Assert.Equal(expected: 2, actual: errB.Span.Line);

        // F2(c): import cycle reports under PUCK017 (ImportGraph) at the importing statement's span.
        var cycA = directory.WriteText(name: "cyc-a.puck", text: "let a = 1\nimport \"cyc-b\"\n");
        var cycB = directory.WriteText(name: "cyc-b.puck", text: "let b = 2\nimport \"cyc-a\"\n");
        var compC = WorldCompileSession.Shared.CompileFile(path: cycA, cancellationToken: TestContext.Current.CancellationToken);
        var errC = Assert.Single(collection: compC.Diagnostics, predicate: static d => (d.Code == PuckDiagnosticCodes.ImportGraph));

        Assert.Equal(expected: Path.GetFullPath(path: cycB), actual: Path.GetFullPath(path: errC.SourcePath!), ignoreCase: true);
        Assert.Equal(expected: 2, actual: errC.Span.Line);
    }

    // --- F3: Unknown template (PUCK134) and bad template argument (PUCK135) with suggestions and argument span ------

    [Fact]
    public void TemplateUnknownNameAndBadArgumentReportOwnCodesSpansAndSuggestions() {
        var source = """
            template pillar(width = 1, height = 2) {
              w: width
              h: height
            }
            pilar(width: 2)
            pillar(
              width: 2,
              heigh: 4
            )
            """;
        var comp = WorldCompileSession.Shared.Compile(source: source, cancellationToken: TestContext.Current.CancellationToken);

        var unknownTmpl = Assert.Single(collection: comp.Diagnostics, predicate: static d => (d.Code == PuckDiagnosticCodes.UnknownTemplate));

        Assert.Contains(expectedSubstring: "nearest: 'pillar'", actualString: unknownTmpl.Message);
        Assert.Equal(expected: 5, actual: unknownTmpl.Span.Line);

        var badArg = Assert.Single(collection: comp.Diagnostics, predicate: static d => (d.Code == PuckDiagnosticCodes.TemplateArgument));

        Assert.Contains(expectedSubstring: "has no parameter 'heigh'", actualString: badArg.Message);
        Assert.Contains(expectedSubstring: "nearest: 'height'", actualString: badArg.Message);
        Assert.Contains(expectedSubstring: "(parameters: width, height)", actualString: badArg.Message);
        // Reported at the argument's own line (line 8), not the call's start line (line 6).
        Assert.Equal(expected: 8, actual: badArg.Span.Line);
    }

    // --- F4: Unresolved references are Warning in roots with suggestions; no "document arm" jargon ------------------

    [Fact]
    public void UnresolvedRootReferencesAreWarningsWithSuggestionsAndUnknownConstructAvoidsJargon() {
        using var directory = new TemporaryDirectory(prefix: "puck-f4-");

        _ = directory.WriteText(name: "basis.world.json", text: "{}");
        var document = new JsonObject {
            ["basis"] = "basis",
            ["prototypes"] = new JsonArray(new JsonObject { ["id"] = "chair" }),
            ["placements"] = new JsonObject {
                ["rows"] = new JsonArray(new JsonObject { ["id"] = "seat1", ["prototypeId"] = "chiar", ["parent"] = "seat2" }),
            },
            ["cameras"] = new JsonArray(new JsonObject { ["name"] = "mainCam" }),
            ["views"] = new JsonObject {
                ["layouts"] = new JsonArray(new JsonObject {
                    ["name"] = "play",
                    ["slots"] = new JsonArray(new JsonObject { ["camera"] = "mainCan" }),
                }),
            },
            ["state"] = new JsonObject { ["world"] = new JsonArray(new JsonObject { ["name"] = "health" }) },
            ["rules"] = new JsonArray(new JsonObject {
                ["name"] = "r",
                ["gate"] = new JsonObject { ["$type"] = "compareState", ["state"] = "helth", ["comparison"] = "Equal", ["value"] = 1 },
                ["effects"] = new JsonArray(),
            }),
        };
        var bag = new DiagnosticBag();

        PuckLinter.LintReferences(
            diagnostics: bag,
            document: document,
            sourceMap: null,
            sourcePath: directory.PathOf(name: "root.world.json")
        );

        var proto = Assert.Single(collection: bag, predicate: static d => (d.Code == "PUCK_LINT_006"));
        var parent = Assert.Single(collection: bag, predicate: static d => (d.Code == "PUCK_LINT_007"));
        var cam = Assert.Single(collection: bag, predicate: static d => (d.Code == "PUCK_LINT_008"));
        var state = Assert.Single(collection: bag, predicate: static d => (d.Code == "PUCK_LINT_005"));

        Assert.Equal(expected: DiagnosticSeverity.Warning, actual: proto.Severity);
        Assert.Contains(expectedSubstring: "nearest: 'chair'", actualString: proto.Message);
        Assert.Equal(expected: DiagnosticSeverity.Warning, actual: parent.Severity);
        Assert.Contains(expectedSubstring: "nearest: 'seat1'", actualString: parent.Message);
        Assert.Equal(expected: DiagnosticSeverity.Warning, actual: cam.Severity);
        Assert.Contains(expectedSubstring: "nearest: 'mainCam'", actualString: cam.Message);
        Assert.Equal(expected: DiagnosticSeverity.Warning, actual: state.Severity);
        Assert.Contains(expectedSubstring: "nearest: 'health'", actualString: state.Message);

        var constructComp = WorldCompileSession.Shared.Compile(source: "x: bogusConstruct(1)\n", cancellationToken: TestContext.Current.CancellationToken);
        var p041 = Assert.Single(collection: constructComp.Diagnostics, predicate: static d => (d.Code == PuckDiagnosticCodes.BuiltinRefused));

        Assert.DoesNotContain(expectedSubstring: "document arm", actualString: p041.Message, comparisonType: StringComparison.OrdinalIgnoreCase);
    }

    // --- F5: Go-to-definition & hover across imports, plus code actions for PUCK040 and nearest-name fixes ----------

    [Fact]
    public async Task LspDefinitionAndHoverResolveAcrossImportsAndCodeActionsOfferQuickFixes() {
        using var directory = new TemporaryDirectory(prefix: "puck-f5-");

        var libPath = directory.WriteText(
            name: "pieces.puck",
            text: """
                // Standard piece width.
                let pieceWidth = 4
                // Places one piece.
                template piece(size = 2) {
                  w: size
                }
                """
        );
        var rootPath = directory.WriteText(
            name: "board.puck",
            text: """
                import "pieces" as p
                w: p.pieceWidth
                v: p.piece(size: 3)
                """
        );
        var libUri = new Uri(uriString: libPath).AbsoluteUri;
        var rootUri = new Uri(uriString: rootPath).AbsoluteUri;
        var rootText = File.ReadAllText(path: rootPath);

        var transcript = await LanguageServerClient.SessionAsync(
            requests: [
                // Definition of `p.piece` (line 2, char 6 -> 'piece')
                LanguageServerClient.PositionRequest(id: 2, method: "textDocument/definition", line: 2, character: 6, uri: rootUri),
                // Definition of `size` parameter at call site `v: p.piece(size: 3)` (line 2, char 12)
                LanguageServerClient.PositionRequest(id: 3, method: "textDocument/definition", line: 2, character: 12, uri: rootUri),
                // Hover on imported template `p.piece`
                LanguageServerClient.PositionRequest(id: 4, method: "textDocument/hover", line: 2, character: 6, uri: rootUri),
                // Hover on imported `let` `p.pieceWidth`
                LanguageServerClient.PositionRequest(id: 5, method: "textDocument/hover", line: 1, character: 7, uri: rootUri),
                // Hover on imported template parameter `size` at call site
                LanguageServerClient.PositionRequest(id: 6, method: "textDocument/hover", line: 2, character: 12, uri: rootUri),
                // Definition on `import "pieces"` string path (line 0, char 10)
                LanguageServerClient.PositionRequest(id: 7, method: "textDocument/definition", line: 0, character: 10, uri: rootUri),
            ],
            text: rootText,
            uri: rootUri
        );

        Assert.Equal(expected: libUri, actual: transcript.Result(id: 2)?["uri"]?.ToString(), ignoreCase: true);
        Assert.Equal(expected: 3, actual: transcript.Result(id: 2)?["range"]?["start"]?["line"]?.GetValue<int>());

        Assert.Equal(expected: libUri, actual: transcript.Result(id: 3)?["uri"]?.ToString(), ignoreCase: true);
        Assert.Equal(expected: 3, actual: transcript.Result(id: 3)?["range"]?["start"]?["line"]?.GetValue<int>());

        var tmplHover = LanguageServerClient.HoverText(result: transcript.Result(id: 4));

        Assert.NotNull(@object: tmplHover);
        Assert.Contains(expectedSubstring: "template p.piece(size = 2)", actualString: tmplHover!);
        Assert.Contains(expectedSubstring: "Places one piece.", actualString: tmplHover!);

        var letHover = LanguageServerClient.HoverText(result: transcript.Result(id: 5));

        Assert.NotNull(@object: letHover);
        Assert.Contains(expectedSubstring: "let pieceWidth = 4", actualString: letHover!);
        Assert.Contains(expectedSubstring: "Standard piece width.", actualString: letHover!);

        var paramHover = LanguageServerClient.HoverText(result: transcript.Result(id: 6));

        Assert.NotNull(@object: paramHover);
        Assert.Contains(expectedSubstring: "size = 2", actualString: paramHover!);

        Assert.Equal(expected: libUri, actual: transcript.Result(id: 7)?["uri"]?.ToString(), ignoreCase: true);

        // Code action for PUCK040 (drop colon) and PUCK134 (nearest template suggestion).
        var fixSource = "host: {\n  width: 1280\n}\ntemplate pillar(w = 1) { w: w }\npilar(w: 2)\n";
        var fixTranscript = await LanguageServerClient.SessionAsync(
            requests: [
                LanguageServerClient.Request(
                    id: 2,
                    method: "textDocument/codeAction",
                    @params: new JsonObject {
                        ["textDocument"] = new JsonObject { ["uri"] = LanguageServerClient.DefaultUri },
                        ["range"] = new JsonObject {
                            ["start"] = new JsonObject { ["line"] = 0, ["character"] = 0 },
                            ["end"] = new JsonObject { ["line"] = 4, ["character"] = 12 },
                        },
                        ["context"] = new JsonObject { ["diagnostics"] = new JsonArray() },
                    }
                ),
            ],
            text: fixSource
        );
        var actions = Assert.IsType<JsonArray>(@object: fixTranscript.Result(id: 2));

        Assert.Contains(collection: actions, filter: static a => (a?["title"]?.ToString() ?? "").Contains(value: "Drop the ':'", comparisonType: StringComparison.Ordinal));
        Assert.Contains(collection: actions, filter: static a => (a?["title"]?.ToString() ?? "").Contains(value: "Replace 'pilar' with 'pillar'", comparisonType: StringComparison.Ordinal));
    }

    // --- F7: Author declarations & aliased members in completion ----------------------------------------------------

    [Fact]
    public async Task CompletionOffersAuthorLetsTemplatesAndImportAliases() {
        using var directory = new TemporaryDirectory(prefix: "puck-f7-");

        _ = directory.WriteText(name: "kit.puck", text: "let kitSize = 3\ntemplate kitBox(w = 1) { width: w }\n");
        var rootPath = directory.PathOf(name: "world.puck");
        var rootUri = new Uri(uriString: rootPath).AbsoluteUri;
        var rootText = "import \"kit\" as k\nlet myCount = 5\ntemplate myPanel(h = 2) { height: h }\n|\n";
        var rootLabels = await LanguageServerClient.CompletionLabelsAsync(markedSource: rootText);

        Assert.Contains(expected: "myCount", set: rootLabels);
        Assert.Contains(expected: "myPanel", set: rootLabels);
        Assert.Contains(expected: "k", set: rootLabels);

        var dotCursor = MarkedSource.Parse(marked: "import \"kit\" as k\nv: k.|\n");
        var dotResult = await LanguageServerClient.RequestAtAsync(
            cursor: dotCursor,
            method: "textDocument/completion",
            uri: rootUri
        );
        var dotLabels = LanguageServerClient.Completions(result: dotResult).Select(selector: static c => c.Label).ToHashSet(comparer: StringComparer.Ordinal);

        Assert.Contains(expected: "kitSize", set: dotLabels);
        Assert.Contains(expected: "kitBox", set: dotLabels);
    }

    // --- F8, F9 & unverified checks: authored messages, 500ms duration check, PUCK035 on imports-only ---------------

    [Fact]
    public void RuleMessagesDurationUnitsAndImportsOnlyCompositionRefusalReadInAuthoredTerms() {
        // F8: += "x" avoids engine-internal 'addState' / 'Text field' wording.
        var textAdd = PuckParser.ParseDocumentWithDiagnostics(source: "rule \"r\" {\n  hp += \"x\"\n}\n");
        var p009 = Assert.Single(collection: textAdd.Diagnostics, predicate: static d => (d.Code == PuckDiagnosticCodes.EffectRhsShape));

        Assert.DoesNotContain(expectedSubstring: "addState", actualString: p009.Message);
        Assert.DoesNotContain(expectedSubstring: "Text field", actualString: p009.Message);

        // Unverified 1: 500ms is recognized as a duration unit (allowed on +=, refused on *=).
        var msAdd = PuckParser.ParseDocumentWithDiagnostics(source: "rule \"r\" {\n  cooldown += 500ms\n}\n");

        Assert.False(condition: msAdd.Diagnostics.HasErrors, userMessage: msAdd.Diagnostics.FormatReport(""));

        var msMul = PuckParser.ParseDocumentWithDiagnostics(source: "rule \"r\" {\n  cooldown *= 500ms\n}\n");
        var msRefusal = Assert.Single(collection: msMul.Diagnostics, predicate: static d => (d.Code == PuckDiagnosticCodes.EffectRhsShape));

        Assert.DoesNotContain(expectedSubstring: "ValueSeconds", actualString: msRefusal.Message);

        // F9: PUCK035 on an imports-only world locates the import statement rather than line 1, with authored wording.
        using var directory = new TemporaryDirectory(prefix: "puck-f9-");

        _ = directory.WriteText(name: "a.puck", text: "basis: \"does-not-exist\"\nstate { world { slot shared = 1 } }\n");
        var rootPath = directory.WriteText(
            name: "root.puck",
            text: """
                schema: "puck.world.definition.v1"
                documentId: "conflict"
                import "a"
                """
        );
        var comp = WorldCompileSession.Shared.CompileFile(path: rootPath, cancellationToken: TestContext.Current.CancellationToken);
        var validationBag = new DiagnosticBag();

        _ = WorldSemanticValidator.ValidateComposedWorld(
            diagnostics: validationBag,
            loweredJson: comp.RequireJson(),
            sourceMap: comp.SourceMap,
            sourcePath: rootPath
        );
        var p035 = Assert.Single(collection: validationBag, predicate: static d => (d.Code == PuckDiagnosticCodes.CompositionRefused));

        Assert.Contains(expectedSubstring: "Could not compose world:", actualString: p035.Message);
        Assert.DoesNotContain(expectedSubstring: "Basis/import composition refused", actualString: p035.Message);
        Assert.Equal(expected: 3, actual: p035.Span.Line);
    }

    // --- F10: Emitter recovers per statement so multiple errors report in one compile -------------------------------

    [Fact]
    public void EmitterRecoversPerStatementAndReportsMultipleEvaluationErrors() {
        var source = """
            documentId: 1 / 0
            schema: 2 / 0
            """;
        var comp = WorldCompileSession.Shared.Compile(source: source, cancellationToken: TestContext.Current.CancellationToken);
        var errors = comp.Diagnostics.Where(predicate: static d => (d.Severity == DiagnosticSeverity.Error)).ToList();

        Assert.Equal(expected: 2, actual: errors.Count);
        Assert.Contains(collection: errors, filter: static e => (e.Span.Line == 1));
        Assert.Contains(collection: errors, filter: static e => (e.Span.Line == 2));
    }

    // --- F11: row. completion answers from AST / last compile without triggering a synchronous compile --------------

    [Fact]
    public void RowDotCompletionDoesNotTriggerSynchronousSourceDiagnosis() {
        var source = """
            state {
              world {
                table bag {
                  gold = 10
                  silver = 2
                }
              }
            }
            rule "r" {
              bag.
            }
            """;
        var server = new PuckLanguageServer();
        var written = new List<JsonObject>();

        server.Handle(message: LanguageServerClient.Request(id: 1, method: "initialize", @params: []).ToJsonString(), send: written.Add);
        server.Handle(message: LanguageServerClient.Open(text: source).ToJsonString(), send: written.Add);
        while (server.RunPendingDiagnosis(cancellationToken: TestContext.Current.CancellationToken, send: written.Add)) { }

        var diagnosesBefore = server.SourceDiagnoses;

        server.Handle(
            message: LanguageServerClient.PositionRequest(id: 2, method: "textDocument/completion", line: 9, character: 6).ToJsonString(),
            send: written.Add
        );

        Assert.Equal(expected: diagnosesBefore, actual: server.SourceDiagnoses);
        var response = written.Single(predicate: static m => (m["id"]?.GetValue<int>() == 2));
        var labels = LanguageServerClient.Completions(result: response["result"]).Select(selector: static c => c.Label).ToHashSet(comparer: StringComparer.Ordinal);

        Assert.Contains(expected: "gold", set: labels);
        Assert.Contains(expected: "silver", set: labels);
    }

    // --- F12: Formatting an already-formatted document returns [] ---------------------------------------------------

    [Fact]
    public async Task FormattingAnUnchangedDocumentReturnsNoEdits() {
        var formatted = PuckFormat.Format(source: "host { width: 1280, height: 720 }");
        var edits = Assert.IsType<JsonArray>(@object: await LanguageServerClient.DocumentRequestAsync(
            method: "textDocument/formatting",
            text: formatted
        ));

        Assert.Empty(collection: edits);
    }
}
