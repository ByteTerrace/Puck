using System.Text.Json.Nodes;
using Puck.Transpiler.Diagnostics;
using Puck.Transpiler.Formatting;
using Puck.Transpiler.Parsing;
using Xunit;

namespace Puck.World.Transpiler.Tests;

/// <summary>Checks the source-level module expander: typed parameters, recursive use, namespaces, and refusals.</summary>
public sealed class ModuleExpansionTests {
    [Fact]
    public void AliasedUsesCopyRowsAndRewriteTheirNames() {
        var compilation = WorldCompiler.Compile(cancellationToken: TestContext.Current.CancellationToken, source: """
            module counter(value: Angle) {
              state { world { slot score = value } }
              export read score
            }

            use counter as left(value: 1)
            use counter as right(value: 2)
            """);

        Assert.False(condition: compilation.Diagnostics.HasErrors, userMessage: compilation.Diagnostics.FormatReport(""));
        var rows = Assert.IsType<JsonArray>(@object: Assert.IsType<JsonObject>(@object: compilation.RequireJson()["state"])["world"]);

        Assert.Equal(["left_score", "right_score"], rows.Select(selector: static row => row!["name"]!.ToString()));
    }
    [Fact]
    public void ModuleArgumentsRequireTheirDeclaredExportsAndNestedUsesPrefixRecursively() {
        var good = WorldCompiler.Compile(cancellationToken: TestContext.Current.CancellationToken, source: """
            module counter() {
              state { world { slot score = 0 } }
              export read score
            }
            module wrapper(part: Module exporting score) {
              use part as child()
              export child.score
            }
            use wrapper as box(part: counter)
            """);

        Assert.False(condition: good.Diagnostics.HasErrors, userMessage: good.Diagnostics.FormatReport(""));
        var rows = Assert.IsType<JsonArray>(@object: Assert.IsType<JsonObject>(@object: good.RequireJson()["state"])["world"]);

        Assert.Equal("box_child_score", Assert.Single(collection: rows)!["name"]!.ToString());

        var bad = WorldCompiler.Compile(cancellationToken: TestContext.Current.CancellationToken, source: """
            module hidden() { state { world { slot secret = 0 } } }
            module wrapper(part: Module exporting score) { use part() }
            use wrapper(part: hidden)
            """);
        var error = Assert.Single(collection: bad.Diagnostics, predicate: static diagnostic => (diagnostic.Severity == DiagnosticSeverity.Error));

        Assert.Contains("exporting score", error.Message, StringComparison.Ordinal);
        Assert.Equal(3, error.Span.Line);
    }
    [Fact]
    public void RequiredExportContractsFollowThreeLevelReExportsAndRefuseCycles() {
        var accepted = WorldCompiler.Compile(cancellationToken: TestContext.Current.CancellationToken, source: """
            module leaf() {
              state { world { slot score = 0 } }
              export read score
            }
            module middle(part: Module exporting score) {
              use part as child()
              export child.score
            }
            module outer(part: Module exporting score) {
              use middle as nested(part: part)
              export nested.score
            }
            module consumer(part: Module exporting score) { state { world { slot accepted = 1 } } }
            use consumer(part: outer)
            """);

        Assert.False(condition: accepted.Diagnostics.HasErrors, userMessage: accepted.Diagnostics.FormatReport(""));

        var cyclic = WorldCompiler.Compile(cancellationToken: TestContext.Current.CancellationToken, source: """
            module first() {
              use second as child()
              export child.score
            }
            module second() {
              use first as child()
              export child.score
            }
            module consumer(part: Module exporting score) { state { world { slot accepted = 1 } } }
            use consumer(part: first)
            """);
        var error = Assert.Single(collection: cyclic.Diagnostics, predicate: static diagnostic => (diagnostic.Severity == DiagnosticSeverity.Error));

        Assert.Contains("exporting score", error.Message, StringComparison.Ordinal);
    }
    [Fact]
    public void DeepAcyclicReExportContractsRespectTheExpansionDepthLimit() {
        var source = new System.Text.StringBuilder(value: "module m0() { state { world { slot score = 0 } } export read score }\n");

        for (var index = 1; (index <= 70); index++) {
            source.Append(value: "module m").Append(value: index).Append(value: "() {\n  use m").Append(value: (index - 1)).Append(value: " as child()\n  export child.score\n}\n");
        }
        source.Append(value: "module consumer(part: Module exporting score) { state { world { slot accepted = 1 } } }\nuse consumer(part: m70)");

        var compilation = WorldCompiler.Compile(cancellationToken: TestContext.Current.CancellationToken, source: source.ToString());

        Assert.Contains(collection: compilation.Diagnostics, filter: static diagnostic => (diagnostic.Code == PuckDiagnosticCodes.EvaluationLimit));
    }
    [Fact]
    public void DuplicateAliasesAndRecursiveExpansionAreRefusedAtTheUse() {
        var duplicate = WorldCompiler.Compile(cancellationToken: TestContext.Current.CancellationToken, source: """
            module counter() { state { world { slot score = 0 } } }
            use counter as same()
            use counter as same()
            """);

        Assert.Contains(collection: duplicate.Diagnostics, filter: static diagnostic => diagnostic.Message.Contains(comparisonType: StringComparison.Ordinal, value: "alias 'same'"));

        var recursive = WorldCompiler.Compile(cancellationToken: TestContext.Current.CancellationToken, source: """
            module forever() { use forever() }
            use forever()
            """);

        Assert.Contains(collection: recursive.Diagnostics, filter: static diagnostic => (diagnostic.Code == PuckDiagnosticCodes.EvaluationLimit));
    }
    [Fact]
    public void FormattingPreservesModuleUseTypesAndReExports() {
        const string Source = "module wrapper(part: Module exporting score) { export part.score }\nuse wrapper as one(part: counter)";
        var document = PuckParser.ParseDocument(Source);
        var formatted = PuckPrinter.Print(document);
        var reparsed = PuckParser.ParseDocument(formatted);

        Assert.Contains(actualString: formatted, comparisonType: StringComparison.Ordinal, expectedSubstring: "module wrapper(part: Module exporting score)");
        Assert.Contains(actualString: formatted, comparisonType: StringComparison.Ordinal, expectedSubstring: "export part.score");
        Assert.Contains(actualString: formatted, comparisonType: StringComparison.Ordinal, expectedSubstring: "use wrapper as one(part: counter)");
        Assert.Equal(formatted, PuckPrinter.Print(reparsed));
    }
    [InlineData("Point", "[1, nope, 3]")]
    [InlineData("Asset", "42")]
    [InlineData("Row", "missing")]
    [InlineData("Pool", "missing")]
    [InlineData("Gate", "1")]
    [Theory]
    public void TypedParametersRejectValuesOutsideTheirDeclaredKind(string kind, string argument) {
        var compilation = WorldCompiler.Compile(cancellationToken: TestContext.Current.CancellationToken, source: $$"""
            module typed(value: {{kind}}) { state { world { slot score = 0 } } }
            use typed(value: {{argument}})
            """);

        var error = Assert.Single(collection: compilation.Diagnostics, predicate: static diagnostic => (diagnostic.Severity == DiagnosticSeverity.Error));

        Assert.Contains($"must be a {kind}", error.Message, StringComparison.Ordinal);
        Assert.Equal(2, error.Span.Line);
    }
    [Fact]
    public void OneImportedFileCanBeInstantiatedThroughTwoAliases() {
        var directory = Directory.CreateTempSubdirectory(prefix: "puck-modules-");

        try {
            var modulePath = Path.Combine(path1: directory.FullName, path2: "counter.puck");
            var rootPath = Path.Combine(path1: directory.FullName, path2: "root.puck");

            File.WriteAllText(contents: "module counter() { state { world { slot score = 0 } } export read score }", path: modulePath);
            File.WriteAllText(contents: """
                import "counter.puck" as first
                import "counter.puck" as second
                use first.counter as left()
                use second.counter as right()
                """, path: rootPath);

            var compilation = WorldCompiler.CompileFile(rootPath, cancellationToken: TestContext.Current.CancellationToken);

            Assert.False(condition: compilation.Diagnostics.HasErrors, userMessage: compilation.Diagnostics.FormatReport(""));
            Assert.Null(@object: compilation.RequireJson()["imports"]);
            var rows = Assert.IsType<JsonArray>(@object: Assert.IsType<JsonObject>(@object: compilation.RequireJson()["state"])["world"]);

            Assert.Equal(["left_score", "right_score"], rows.Select(selector: static row => row!["name"]!.ToString()));
        } finally {
            directory.Delete(recursive: true);
        }
    }
    [Fact]
    public void SourceImportDoesNotComposeTheLibrarysOrdinaryDocumentStatements() {
        var directory = Directory.CreateTempSubdirectory(prefix: "puck-module-import-only-");

        try {
            var modulePath = Path.Combine(path1: directory.FullName, path2: "library.puck");
            var rootPath = Path.Combine(path1: directory.FullName, path2: "root.puck");

            File.WriteAllText(contents: "state { world { slot accidental = 9 } }\nmodule wanted() { state { world { slot deliberate = 3 } } }", path: modulePath);
            File.WriteAllText(contents: "import \"library.puck\"\nuse wanted()", path: rootPath);

            var compilation = WorldCompiler.CompileFile(rootPath, cancellationToken: TestContext.Current.CancellationToken);

            Assert.False(condition: compilation.Diagnostics.HasErrors, userMessage: compilation.Diagnostics.FormatReport(""));
            Assert.Null(@object: compilation.RequireJson()["imports"]);
            var row = Assert.IsType<JsonObject>(@object: Assert.Single(collection: Assert.IsType<JsonArray>(@object: Assert.IsType<JsonObject>(@object: compilation.RequireJson()["state"])["world"])));

            Assert.Equal("deliberate", row["name"]!.ToString());
        } finally {
            directory.Delete(recursive: true);
        }
    }
    [Fact]
    public void TwoOuterAliasesRetainNestedModuleAndConstantClosures() {
        var directory = Directory.CreateTempSubdirectory(prefix: "puck-module-closures-");

        try {
            File.WriteAllText(Path.Combine(path1: directory.FullName, path2: "leaf.puck"), "let initial = 7\nmodule counter() { state { world { slot score = initial } } }");
            File.WriteAllText(Path.Combine(path1: directory.FullName, path2: "wrapper.puck"), "import \"leaf.puck\" as dep\nmodule wrapper() { use dep.counter as child() }");
            var rootPath = Path.Combine(path1: directory.FullName, path2: "root.puck");

            File.WriteAllText(contents: "import \"wrapper.puck\" as first\nimport \"wrapper.puck\" as second\nuse first.wrapper as left()\nuse second.wrapper as right()", path: rootPath);

            var compilation = WorldCompiler.CompileFile(rootPath, cancellationToken: TestContext.Current.CancellationToken);

            Assert.False(condition: compilation.Diagnostics.HasErrors, userMessage: compilation.Diagnostics.FormatReport(""));
            var rows = Assert.IsType<JsonArray>(@object: Assert.IsType<JsonObject>(@object: compilation.RequireJson()["state"])["world"]);

            Assert.Equal(["left_child_score", "right_child_score"], rows.Select(selector: static row => row!["name"]!.ToString()));
            Assert.All(rows, static row => Assert.Equal("7", row!["value"]!.ToString()));
        } finally {
            directory.Delete(recursive: true);
        }
    }
    [Fact]
    public void ARefusalInsideAnImportedModuleNamesItsDefiningSource() {
        var directory = Directory.CreateTempSubdirectory(prefix: "puck-module-diagnostic-");

        try {
            var modulePath = Path.Combine(path1: directory.FullName, path2: "broken.puck");
            var rootPath = Path.Combine(path1: directory.FullName, path2: "root.puck");

            File.WriteAllText(contents: "module broken() { missingTemplate() }", path: modulePath);
            File.WriteAllText(contents: "import \"broken.puck\"\nuse broken()", path: rootPath);

            var compilation = WorldCompiler.CompileFile(rootPath, cancellationToken: TestContext.Current.CancellationToken);
            var error = Assert.Single(collection: compilation.Diagnostics, predicate: static diagnostic => (diagnostic.Severity == DiagnosticSeverity.Error));

            Assert.Equal(modulePath, error.SourcePath);
            Assert.Contains(modulePath, error.Format(filePath: rootPath), StringComparison.Ordinal);
        } finally {
            directory.Delete(recursive: true);
        }
    }
    [Fact]
    public void CompilePrintCompilePreservesExpandedDocument() {
        const string Source = """
            module counter(value: Angle) {
              state { world { slot score = value } }
              export read score
            }
            use counter as left(value: 3)
            """;
        var first = WorldCompiler.Compile(cancellationToken: TestContext.Current.CancellationToken, source: Source);
        var printed = PuckPrinter.Print(PuckParser.ParseDocument(Source));
        var second = WorldCompiler.Compile(cancellationToken: TestContext.Current.CancellationToken, source: printed);

        Assert.False(condition: first.Diagnostics.HasErrors, userMessage: first.Diagnostics.FormatReport(""));
        Assert.False(condition: second.Diagnostics.HasErrors, userMessage: second.Diagnostics.FormatReport(""));
        Assert.True(condition: JsonNode.DeepEquals(node1: first.RequireJson(), node2: second.RequireJson()));
    }
    [Fact]
    public void EachExpandedInstanceKeepsTheDefiningRowSpan() {
        var sourceMap = new SourceMap();
        var compilation = WorldCompiler.Compile(cancellationToken: TestContext.Current.CancellationToken, sourceMap: sourceMap, source: """
            module first() {
              state { world { slot one = 1 } }
            }
            module second() {
              state { world { slot two = 2 } }
            }
            use first as a()
            use second as b()
            """);

        Assert.False(condition: compilation.Diagnostics.HasErrors, userMessage: compilation.Diagnostics.FormatReport(""));
        Assert.True(condition: sourceMap.TryGetSpan(jsonPointer: "/state/world/0", span: out var first));
        Assert.True(condition: sourceMap.TryGetSpan(jsonPointer: "/state/world/1", span: out var second));
        Assert.Equal(2, first.Line);
        Assert.Equal(5, second.Line);
    }
    [Fact]
    public void ImportedRowsAndRulesKeepDefinitionPathAndExpansionInstance() {
        var directory = Directory.CreateTempSubdirectory(prefix: "puck-module-origins-");

        try {
            var modulePath = Path.Combine(path1: directory.FullName, path2: "counter.puck");
            var rootPath = Path.Combine(path1: directory.FullName, path2: "root.puck");

            File.WriteAllText(contents: """
                module counter() {
                  state { world { slot score = false } }
                  rule tick {
                    when score == false
                    score = true
                  }
                }
                """, path: modulePath);
            File.WriteAllText(contents: "import \"counter.puck\"\nuse counter as first()\nuse counter as second()", path: rootPath);
            var sourceMap = new SourceMap();

            var compilation = WorldCompiler.CompileFile(rootPath, sourceMap: sourceMap, cancellationToken: TestContext.Current.CancellationToken);

            Assert.False(condition: compilation.Diagnostics.HasErrors, userMessage: compilation.Diagnostics.FormatReport(""));
            Assert.True(sourceMap.TryGetOrigin("/state/world/0", out var firstRow));
            Assert.True(sourceMap.TryGetOrigin("/rules/0", out var firstRule));
            Assert.True(sourceMap.TryGetOrigin("/state/world/1", out var secondRow));
            Assert.True(sourceMap.TryGetOrigin("/rules/1", out var secondRule));
            Assert.All(new[] { firstRow, firstRule, secondRow, secondRule }, origin => Assert.Equal(modulePath, origin.SourcePath));
            Assert.Equal("first", firstRow.ModuleInstancePath);
            Assert.Equal("first", firstRule.ModuleInstancePath);
            Assert.Equal("second", secondRow.ModuleInstancePath);
            Assert.Equal("second", secondRule.ModuleInstancePath);
        } finally {
            directory.Delete(recursive: true);
        }
    }
    [Fact]
    public void GateParameterCanDriveAnAuthoredRuleFromAHostRow() {
        var compilation = WorldCompiler.Compile(cancellationToken: TestContext.Current.CancellationToken, source: """
            state { world { slot enabled = true } }
            module guarded(condition: Gate) {
              state { world { slot fired = false } }
              rule fire {
                when condition == true
                fired = true
              }
            }
            use guarded as switch(condition: enabled)
            """);

        Assert.False(condition: compilation.Diagnostics.HasErrors, userMessage: compilation.Diagnostics.FormatReport(""));
        var rule = Assert.IsType<JsonObject>(@object: Assert.Single(collection: Assert.IsType<JsonArray>(@object: compilation.RequireJson()["rules"])));

        Assert.Contains("enabled", rule["gate"]!.ToJsonString(), StringComparison.Ordinal);
        _ = WorldCostAnalysis.Generate(compilation: compilation);
    }
    [Fact]
    public void ExternalRowArgumentIsNotCapturedByAnEqualModuleLocalName() {
        var compilation = WorldCompiler.Compile(cancellationToken: TestContext.Current.CancellationToken, source: """
            state { world { slot score = true } }
            module copy(source: Gate) {
              state { world { slot score = false } }
              rule observe {
                when source == true
                score = true
              }
            }
            use copy as one(source: score)
            """);

        Assert.False(condition: compilation.Diagnostics.HasErrors, userMessage: compilation.Diagnostics.FormatReport(""));
        var rows = Assert.IsType<JsonArray>(@object: Assert.IsType<JsonObject>(@object: compilation.RequireJson()["state"])["world"]);

        Assert.Equal(["score", "one_score"], rows.Select(selector: static row => row!["name"]!.ToString()));
        var rule = Assert.IsType<JsonObject>(@object: Assert.Single(collection: Assert.IsType<JsonArray>(@object: compilation.RequireJson()["rules"])));

        Assert.Contains("score", rule["gate"]!.ToJsonString(), StringComparison.Ordinal);
        Assert.DoesNotContain("one_score", rule["gate"]!.ToJsonString(), StringComparison.Ordinal);
    }
    [Fact]
    public void TypedDefaultsResolveEarlierBoundParameters() {
        var compilation = WorldCompiler.Compile(cancellationToken: TestContext.Current.CancellationToken, source: """
            module pair(a: Angle, b: Angle = a) { state { world { slot score = b } } }
            use pair(a: 3)
            """);

        Assert.False(condition: compilation.Diagnostics.HasErrors, userMessage: compilation.Diagnostics.FormatReport(""));
        var row = Assert.IsType<JsonObject>(@object: Assert.Single(collection: Assert.IsType<JsonArray>(@object: Assert.IsType<JsonObject>(@object: compilation.RequireJson()["state"])["world"])));

        Assert.Equal(3L, row["value"]!.GetValue<long>());
    }
    [Fact]
    public void RowsInsideUnusedModulesDoNotSatisfyTypedArguments() {
        var compilation = WorldCompiler.Compile(cancellationToken: TestContext.Current.CancellationToken, source: """
            module unused() { state { world { slot hidden = true } } }
            module consumer(value: Gate) { state { world { slot result = value } } }
            use consumer(value: hidden)
            """);

        var error = Assert.Single(collection: compilation.Diagnostics, predicate: static diagnostic => (diagnostic.Severity == DiagnosticSeverity.Error));

        Assert.Contains("must be a Gate", error.Message, StringComparison.Ordinal);
    }
    [Fact]
    public void APreviousModuleInstancesEmittedGateSatisfiesTheNextTypedUse() {
        var compilation = WorldCompiler.Compile(cancellationToken: TestContext.Current.CancellationToken, source: """
            module source() { state { world { slot enabled = true } } }
            module consumer(condition: Gate) {
              state { world { slot fired = false } }
              rule fire {
                when condition == true
                fired = true
              }
            }
            use source as first()
            use consumer as second(condition: first_enabled)
            """);

        Assert.False(condition: compilation.Diagnostics.HasErrors, userMessage: compilation.Diagnostics.FormatReport(""));
        var rule = Assert.IsType<JsonObject>(@object: Assert.Single(collection: Assert.IsType<JsonArray>(@object: compilation.RequireJson()["rules"])));

        Assert.Contains("first_enabled", rule["gate"]!.ToJsonString(), StringComparison.Ordinal);
    }
    [Fact]
    public void TypedReferenceDefaultsPreserveTheEarlierExternalBinding() {
        var compilation = WorldCompiler.Compile(cancellationToken: TestContext.Current.CancellationToken, source: """
            state { world { slot enabled = true } }
            module consumer(a: Gate, b: Gate = a) {
              state { world { slot fired = false } }
              rule observe {
                when b == true
                fired = true
              }
            }
            use consumer as copy(a: enabled)
            """);

        Assert.False(condition: compilation.Diagnostics.HasErrors, userMessage: compilation.Diagnostics.FormatReport(""));
        var rule = Assert.IsType<JsonObject>(@object: Assert.Single(collection: Assert.IsType<JsonArray>(@object: compilation.RequireJson()["rules"])));

        Assert.Contains("enabled", rule["gate"]!.ToJsonString(), StringComparison.Ordinal);
        Assert.DoesNotContain("__puck_arg_", rule.ToJsonString(), StringComparison.Ordinal);
        _ = WorldCostAnalysis.Generate(compilation: compilation);
    }
    [Fact]
    public void ImportedHelperTemplatesKeepTheirAliasAndLexicalConstant() {
        var directory = Directory.CreateTempSubdirectory(prefix: "puck-module-helpers-");

        try {
            var modulePath = Path.Combine(path1: directory.FullName, path2: "counter.puck");
            var rootPath = Path.Combine(path1: directory.FullName, path2: "root.puck");

            File.WriteAllText(contents: "let initial = 5\ntemplate rows() { state { world { slot score = initial } } }\nmodule counter() { rows() }", path: modulePath);
            File.WriteAllText(contents: "import \"counter.puck\" as first\nimport \"counter.puck\" as second\nuse first.counter as left()\nuse second.counter as right()", path: rootPath);

            var compilation = WorldCompiler.CompileFile(rootPath, cancellationToken: TestContext.Current.CancellationToken);

            Assert.False(condition: compilation.Diagnostics.HasErrors, userMessage: compilation.Diagnostics.FormatReport(""));
            var rows = Assert.IsType<JsonArray>(@object: Assert.IsType<JsonObject>(@object: compilation.RequireJson()["state"])["world"]);

            Assert.Equal(["left_score", "right_score"], rows.Select(selector: static row => row!["name"]!.ToString()));
            Assert.All(rows, static row => Assert.Equal(5L, row!["value"]!.GetValue<long>()));
        } finally {
            directory.Delete(recursive: true);
        }
    }
    [Fact]
    public void DuplicateImportedDeclarationsProduceDiagnosticsInsteadOfResolverExceptions() {
        var directory = Directory.CreateTempSubdirectory(prefix: "puck-module-duplicates-");

        try {
            var modulePath = Path.Combine(path1: directory.FullName, path2: "library.puck");
            var rootPath = Path.Combine(path1: directory.FullName, path2: "root.puck");

            File.WriteAllText(contents: "let value = 1\nlet value = 2\nmodule sample() { state { world { slot score = value } } }", path: modulePath);
            File.WriteAllText(contents: "import \"library.puck\" as library", path: rootPath);

            var compilation = WorldCompiler.CompileFile(path: rootPath, cancellationToken: TestContext.Current.CancellationToken);

            var error = Assert.Single(collection: compilation.Diagnostics, predicate: static diagnostic => (diagnostic.Severity == DiagnosticSeverity.Error));

            Assert.Contains("Duplicate constant 'library.value'", error.Message, StringComparison.Ordinal);
        } finally {
            directory.Delete(recursive: true);
        }
    }
    [Fact]
    public void DeepImportGraphsRespectTheExpansionDepthLimit() {
        var directory = Directory.CreateTempSubdirectory(prefix: "puck-module-import-depth-");

        try {
            const int LastIndex = 65;

            for (var index = 0; (index <= LastIndex); index++) {
                var source = ((index == LastIndex)
                    ? "module leaf() { state { world { slot score = 0 } } }"
                    : $"import \"level-{(index + 1)}.puck\"");

                File.WriteAllText(path: Path.Combine(path1: directory.FullName, path2: $"level-{index}.puck"), contents: source);
            }

            var compilation = WorldCompiler.CompileFile(
                path: Path.Combine(path1: directory.FullName, path2: "level-0.puck"),
                cancellationToken: TestContext.Current.CancellationToken
            );

            Assert.Contains(collection: compilation.Diagnostics, filter: static diagnostic => (diagnostic.Code == PuckDiagnosticCodes.EvaluationLimit));
        } finally {
            directory.Delete(recursive: true);
        }
    }
    [Fact]
    public void ImportGraphAtTheExpansionDepthLimitIsAccepted() {
        var directory = Directory.CreateTempSubdirectory(prefix: "puck-module-import-depth-edge-");

        try {
            const int LastIndex = 63;

            for (var index = 0; (index <= LastIndex); index++) {
                var source = ((index == LastIndex)
                    ? "module leaf() {}"
                    : $"import \"level-{(index + 1)}.puck\"");

                File.WriteAllText(path: Path.Combine(path1: directory.FullName, path2: $"level-{index}.puck"), contents: source);
            }

            var compilation = WorldCompiler.CompileFile(
                path: Path.Combine(path1: directory.FullName, path2: "level-0.puck"),
                cancellationToken: TestContext.Current.CancellationToken
            );

            Assert.False(condition: compilation.Diagnostics.HasErrors, userMessage: compilation.Diagnostics.FormatReport(""));
        } finally {
            directory.Delete(recursive: true);
        }
    }
    [Fact]
    public void AliasedImportDagIsRefusedBeforeExponentialExpansion() {
        var directory = Directory.CreateTempSubdirectory(prefix: "puck-module-import-work-");

        try {
            const int LastIndex = 22;

            File.WriteAllText(
                path: Path.Combine(path1: directory.FullName, path2: $"level-{LastIndex}.puck"),
                contents: ""
            );
            for (var index = (LastIndex - 1); (index >= 0); index--) {
                File.WriteAllText(
                    path: Path.Combine(path1: directory.FullName, path2: $"level-{index}.puck"),
                    contents: $"import \"level-{(index + 1)}.puck\" as left\nimport \"level-{(index + 1)}.puck\" as right"
                );
            }

            var compilation = WorldCompiler.CompileFile(
                path: Path.Combine(path1: directory.FullName, path2: "level-0.puck"),
                cancellationToken: TestContext.Current.CancellationToken
            );

            Assert.Contains(collection: compilation.Diagnostics, filter: static diagnostic => (diagnostic.Code == PuckDiagnosticCodes.EvaluationLimit));
        } finally {
            directory.Delete(recursive: true);
        }
    }
    [Fact]
    public void ImportedParameterDefaultsAndLoopBindersShadowGlobalConstants() {
        var directory = Directory.CreateTempSubdirectory(prefix: "puck-module-import-shadow-");

        try {
            var libraryPath = Path.Combine(path1: directory.FullName, path2: "library.puck");
            var rootPath = Path.Combine(path1: directory.FullName, path2: "root.puck");

            File.WriteAllText(contents: """
                let value = 9
                module sample(value: Angle, copy: Angle = value) {
                  for value in [copy] { state { world { slot score = value } } }
                }
                """, path: libraryPath);
            File.WriteAllText(contents: "import \"library.puck\" as library\nuse library.sample(value: 3)", path: rootPath);

            var compilation = WorldCompiler.CompileFile(path: rootPath, cancellationToken: TestContext.Current.CancellationToken);

            Assert.False(condition: compilation.Diagnostics.HasErrors, userMessage: compilation.Diagnostics.FormatReport(""));
            var row = Assert.IsType<JsonObject>(@object: Assert.Single(collection: Assert.IsType<JsonArray>(@object: Assert.IsType<JsonObject>(@object: compilation.RequireJson()["state"])["world"])));

            Assert.Equal(3L, row["value"]!.GetValue<long>());
        } finally {
            directory.Delete(recursive: true);
        }
    }
    [Fact]
    public void ImportAliasesRemainCaseSensitive() {
        var directory = Directory.CreateTempSubdirectory(prefix: "puck-module-import-case-");

        try {
            var libraryPath = Path.Combine(path1: directory.FullName, path2: "library.puck");
            var rootPath = Path.Combine(path1: directory.FullName, path2: "root.puck");

            File.WriteAllText(contents: "module sample() { state { world { slot score = 0 } } }", path: libraryPath);
            File.WriteAllText(contents: "import \"library.puck\" as a\nimport \"library.puck\" as A\nuse a.sample as lower()\nuse A.sample as upper()", path: rootPath);

            var compilation = WorldCompiler.CompileFile(path: rootPath, cancellationToken: TestContext.Current.CancellationToken);

            Assert.False(condition: compilation.Diagnostics.HasErrors, userMessage: compilation.Diagnostics.FormatReport(""));
            var rows = Assert.IsType<JsonArray>(@object: Assert.IsType<JsonObject>(@object: compilation.RequireJson()["state"])["world"]);

            Assert.Equal(["lower_score", "upper_score"], rows.Select(selector: static row => row!["name"]!.ToString()));
        } finally {
            directory.Delete(recursive: true);
        }
    }
}
