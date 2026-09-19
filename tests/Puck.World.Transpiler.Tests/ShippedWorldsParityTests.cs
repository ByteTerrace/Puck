using System.Text.Json.Nodes;
using Puck.Abstractions.Documents;
using Puck.Transpiler.Ast;
using Puck.World.Transpiler.Decompiler;
using Puck.Transpiler.Diagnostics;
using Puck.World.Transpiler.Lowering;
using Puck.Transpiler.Parsing;
using Puck.World.Transpiler.Sql;
using Xunit;

namespace Puck.World.Transpiler.Tests;

public class ShippedWorldsParityTests {
    private readonly ITestOutputHelper? m_output;

    public ShippedWorldsParityTests(ITestOutputHelper? output = null) {
        m_output = output;
    }

    private static void AssertDecompilationRoundTrips(
        string originalJsonText,
        string? basePath,
        string label,
        bool sql = false,
        ITestOutputHelper? output = null
    ) {
        var originalNode = JsonNode.Parse(originalJsonText);

        Assert.NotNull(@object: originalNode);

        // 1. Decompile JSON -> .puck
        var decompiledPuck = WorldDecompiler.Decompile(jsonText: originalJsonText, sql: sql);

        Assert.False(
            condition: string.IsNullOrWhiteSpace(value: decompiledPuck),
            userMessage: $"Decompiled source was empty for {label}"
        );

        // 2. Parse .puck -> AST
        var diagnostics = new DiagnosticBag();
        var parseResult = PuckParser.ParseDocumentWithDiagnostics(
            source: decompiledPuck,
            diagnostics: diagnostics,
            vocabulary: WorldDocumentVocabulary.Instance
        );

        Assert.False(
            condition: diagnostics.HasErrors,
            userMessage: $"Parse errors for {label}:{Environment.NewLine}{diagnostics.FormatReport(decompiledPuck)}"
        );
        Assert.NotNull(@object: parseResult.Value);

        // Log projection metrics for --sql decompilation
        if (sql && output is not null) {
            var sqlBlocks = parseResult.Value.Statements.OfType<EmbeddedBlockNode>().Where(b => b.Language == "sql").ToList();
            var sqlTableCount = 0;
            var sqlInsertRowCount = 0;
            var sqlRuleCount = 0;
            foreach (var block in sqlBlocks) {
                var lexer = new StateSqlLexer(block.Body, block.BodyOffset, block.BodyLine, block.BodyColumn);
                var tokens = lexer.Tokenize(diagnostics);
                var parser = new StateSqlParser(tokens, diagnostics);
                var sqlStmts = parser.ParseStatements();
                foreach (var stmt in sqlStmts) {
                    if (stmt is SqlCreateTableStatement or SqlDeclareSlotStatement) {
                        sqlTableCount++;
                    } else if (stmt is SqlInsertStatement ins) {
                        sqlInsertRowCount += ins.ValuesRows.Count;
                    } else if (stmt is SqlCreateRuleStatement) {
                        sqlRuleCount++;
                    }
                }
            }
            var nativeRules = parseResult.Value.Statements.Count(s => s is WhenStatementNode or RuleBlockNode);
            var nativeStateBlocks = parseResult.Value.Statements.Count(s => s is BlockNode b && b.Identifier == "state");
            output.WriteLine($"[PARITY-SQL PROJECTION] {label}: Projected into SQL: {sqlTableCount} tables/slots, {sqlInsertRowCount} rows, {sqlRuleCount} rules. Native retained: {nativeStateBlocks} state blocks, {nativeRules} rules.");
        }

        // 3. Lower AST -> JSON
        var loweringDiagnostics = new DiagnosticBag();
        var loweringResult = WorldCompiler.Compile(
            basePath: basePath,
            cancellationToken: TestContext.Current.CancellationToken,
            diagnostics: loweringDiagnostics,
            source: decompiledPuck
        );

        Assert.False(
            condition: loweringDiagnostics.HasErrors,
            userMessage: $"Lowering errors for {label}:{Environment.NewLine}{loweringDiagnostics.FormatReport(decompiledPuck)}"
        );
        if (sql) {
            Assert.Empty(diagnostics);
            Assert.Empty(loweringDiagnostics);
        }
        Assert.NotNull(@object: loweringResult.Json);

        // 4. Parity check: Canonical serialization structural equivalence
        var originalCanonicalBytes = CanonicalJsonDocument.Serialize(node: originalNode);
        var roundtripCanonicalBytes = CanonicalJsonDocument.Serialize(node: loweringResult.Json);

        var originalCanonicalJson = System.Text.Encoding.UTF8.GetString(bytes: originalCanonicalBytes);
        var roundtripCanonicalJson = System.Text.Encoding.UTF8.GetString(bytes: roundtripCanonicalBytes);

        var originalRoundtripNode = JsonNode.Parse(originalCanonicalJson);
        var recompiledNode = JsonNode.Parse(roundtripCanonicalJson);

        var mismatch = JsonMismatch.Find(
            actual: recompiledNode,
            expected: originalRoundtripNode,
            path: $"{label}:"
        );

        Assert.True(condition: mismatch is null, userMessage: mismatch);
    }

    public static TheoryData<string> GetShippedWorldFiles() => ShippedWorlds.Files();
    public static TheoryData<string> GetShippedWorldSources() => ShippedWorlds.Sources();
    // Carries the same directive as its first `placements` row, plus placement rows a basis completes.
    [Fact]
    public void TestQuiltShardParity() {
        TestShippedWorldRoundTripParity(relativePath: "shards/quilt-ne.world.json");
    }
    // A `{"$replace": true}` basis-merge directive row in `rules` is not a rule.
    [Fact]
    public void TestReplaceDirectiveRuleRowParity() {
        const string Document = """
            {
              "basis": "avatars/moth.world.json",
              "rules": [
                { "$replace": true }
              ],
              "schema": "puck.world.definition.v1"
            }
            """;

        AssertDecompilationRoundTrips(
            originalJsonText: Document,
            basePath: ShippedWorlds.FindDirectory(),
            label: "replace-directive"
        );
    }
    [MemberData(nameof(GetShippedWorldFiles))]
    [Theory]
    public void TestShippedWorldRoundTripParity(string relativePath) {
        var worldsDir = ShippedWorlds.FindDirectory();
        var fullPath = Path.Combine(
            path1: worldsDir,
            path2: relativePath
        );

        Assert.True(
            condition: File.Exists(path: fullPath),
            userMessage: $"Shipped world file not found: {fullPath}"
        );

        AssertDecompilationRoundTrips(
            originalJsonText: File.ReadAllText(path: fullPath),
            basePath: Path.GetDirectoryName(path: fullPath),
            label: relativePath
        );
    }
    [MemberData(nameof(GetShippedWorldFiles))]
    [Theory]
    public void TestShippedWorldRoundTripParityWithSql(string relativePath) {
        var worldsDir = ShippedWorlds.FindDirectory();
        var fullPath = Path.Combine(
            path1: worldsDir,
            path2: relativePath
        );

        Assert.True(
            condition: File.Exists(path: fullPath),
            userMessage: $"Shipped world file not found: {fullPath}"
        );

        AssertDecompilationRoundTrips(
            originalJsonText: File.ReadAllText(path: fullPath),
            basePath: Path.GetDirectoryName(path: fullPath),
            label: $"{relativePath} (sql)",
            sql: true,
            output: m_output
        );
    }
    [MemberData(nameof(GetShippedWorldSources))]
    [Theory]
    public void TestSourceCompilesToTheGeneratedDocument(string relativePath) {
        var worldsDir = ShippedWorlds.FindDirectory();
        var sourcePath = Path.Combine(
            path1: worldsDir,
            path2: relativePath
        );
        var documentPath = Path.Combine(
            path1: worldsDir,
            path2: ShippedWorlds.DocumentOf(sourcePath: relativePath)
        );

        Assert.True(
            condition: File.Exists(path: documentPath),
            userMessage: $"{relativePath} has no generated document at {documentPath}"
        );

        var source = File.ReadAllText(path: sourcePath);
        var loweringResult = WorldCompiler.Compile(
            cancellationToken: TestContext.Current.CancellationToken,
            source: source,
            sourcePath: sourcePath
        );

        Assert.False(
            condition: loweringResult.Diagnostics.HasErrors,
            userMessage: $"Compile errors for {relativePath}:{Environment.NewLine}{loweringResult.Diagnostics.FormatReport(source)}"
        );

        // The regeneration gate: the source is canonical and the document is its generated output, so the two can
        // never drift apart without failing here. Byte identity is what `puck compile` writes.
        Assert.Equal(
            File.ReadAllBytes(path: documentPath),
            CanonicalJsonDocument.Serialize(node: loweringResult.RequireJson())
        );
    }

    [Fact]
    public void ShippedWorldsSqlProjectionCounts() {
        var worldsDir = ShippedWorlds.FindDirectory();
        var filePaths = ShippedWorlds.FilePaths().ToList();
        var totalProjectedTables = 0;
        var totalProjectedRows = 0;
        var totalProjectedRules = 0;
        var totalNativeStateBlocks = 0;
        var totalNativeRules = 0;

        foreach (var relativePath in filePaths) {
            var fullPath = Path.Combine(worldsDir, relativePath);
            var jsonText = File.ReadAllText(fullPath);
            var decompiledPuck = WorldDecompiler.Decompile(jsonText: jsonText, sql: true);
            var diagnostics = new DiagnosticBag();
            var parseResult = PuckParser.ParseDocumentWithDiagnostics(
                source: decompiledPuck,
                diagnostics: diagnostics,
                vocabulary: WorldDocumentVocabulary.Instance
            );

            Assert.False(diagnostics.HasErrors, diagnostics.FormatReport(decompiledPuck));
            Assert.NotNull(parseResult.Value);

            var sqlBlocks = parseResult.Value.Statements.OfType<EmbeddedBlockNode>().Where(b => b.Language == "sql").ToList();
            var sqlTableCount = 0;
            var sqlRowCount = 0;
            var sqlRuleCount = 0;
            foreach (var block in sqlBlocks) {
                var lexer = new StateSqlLexer(block.Body, block.BodyOffset, block.BodyLine, block.BodyColumn);
                var tokens = lexer.Tokenize(diagnostics);
                var parser = new StateSqlParser(tokens, diagnostics);
                var sqlStmts = parser.ParseStatements();
                foreach (var stmt in sqlStmts) {
                    if (stmt is SqlCreateTableStatement or SqlDeclareSlotStatement) {
                        sqlTableCount++;
                    } else if (stmt is SqlInsertStatement ins) {
                        sqlRowCount += ins.ValuesRows.Count;
                    } else if (stmt is SqlCreateRuleStatement) {
                        sqlRuleCount++;
                    }
                }
            }

            var nativeRules = parseResult.Value.Statements.Count(s => s is WhenStatementNode or RuleBlockNode);
            var nativeStateBlocks = parseResult.Value.Statements.Count(s => s is BlockNode b && b.Identifier == "state");

            totalProjectedTables += sqlTableCount;
            totalProjectedRows += sqlRowCount;
            totalProjectedRules += sqlRuleCount;
            totalNativeStateBlocks += nativeStateBlocks;
            totalNativeRules += nativeRules;

            m_output?.WriteLine($"[PROJECTION] {relativePath}: SQL({sqlTableCount} tables/slots, {sqlRowCount} rows, {sqlRuleCount} rules) | Native({nativeStateBlocks} state blocks, {nativeRules} rules)");
        }

        m_output?.WriteLine($"[PROJECTION TOTAL] SQL: {totalProjectedTables} tables/slots, {totalProjectedRows} rows, {totalProjectedRules} rules. Native: {totalNativeStateBlocks} state blocks, {totalNativeRules} rules.");
    }
}
