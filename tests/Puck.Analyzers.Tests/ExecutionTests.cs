using System.Globalization;
using System.Text;
using Xunit;

namespace Puck.Analyzers.Tests;

/// <summary>
/// How the analyzer behaves as a compiler extension rather than as a rule: it opts into concurrent execution and
/// accumulates every claim across all of it, and it runs under a cancellation token the compiler and the IDE both
/// pull on.
/// </summary>
public sealed class ExecutionTests {
    private const int BrandCount = 40;

    private static SourceFile[] ManyBrandedFiles() {
        var files = new SourceFile[10];

        for (var file = 0; (file < files.Length); file++) {
            var builder = new StringBuilder();

            builder.Append(value: "namespace Subject.Assembly;\r\n\r\ninternal static class Subject").Append(value: file.ToString(provider: CultureInfo.InvariantCulture)).Append(value: " {\r\n");

            for (var member = 0; (member < (BrandCount / files.Length)); member++) {
                var id = ((file * (BrandCount / files.Length)) + member).ToString(provider: CultureInfo.InvariantCulture);

                builder
                    .Append(value: "    [VerifiedCode(\"brand-").Append(value: id).Append(value: "\")]\r\n")
                    .Append(value: "    public static int Target").Append(value: id).Append(value: "() {\r\n        return ").Append(value: id).Append(value: ";\r\n    }\r\n");
            }

            builder.Append(value: "}\r\n");

            files[file] = new SourceFile(Name: ("Subject" + file.ToString(provider: CultureInfo.InvariantCulture) + ".cs"), Text: builder.ToString());
        }

        return files;
    }

    [Fact]
    public void EveryBrandIsReportedWhenTheAnalyzerRunsConcurrently() {
        var result = Harness.AnalyzeCore(
            compilation: Harness.Compile(assemblyName: Harness.DefaultAssemblyName, sources: ManyBrandedFiles()),
            additionalFiles: [new HarnessAdditionalText(path: Harness.ManifestPath, text: Manifest.Empty)],
            concurrent: true,
            cancellationToken: CancellationToken.None);

        Assert.True(condition: result.CompilesCleanly, userMessage: result.CompilerErrorText);
        Assert.Equal(expected: BrandCount, actual: result.WithId(id: "VER001").Length);
    }

    [Fact]
    public void ConcurrentAndSequentialExecutionReportTheSameThing() {
        var files = ManyBrandedFiles();

        var manifest = Manifest.Of(
            Enumerable
                .Range(start: 0, count: BrandCount)
                .Select(selector: index => new ManifestEntry {
                    Id = ("brand-" + index.ToString(provider: CultureInfo.InvariantCulture)),
                    Sha256 = new string(c: '0', count: 64),
                    Symbol = ("M:Subject.Assembly.Subject.Target" + index.ToString(provider: CultureInfo.InvariantCulture)),
                })
                .ToArray());

        var concurrent = Harness.AnalyzeCore(
            compilation: Harness.Compile(assemblyName: Harness.DefaultAssemblyName, sources: files),
            additionalFiles: [new HarnessAdditionalText(path: Harness.ManifestPath, text: manifest)],
            concurrent: true,
            cancellationToken: CancellationToken.None);

        var sequential = Harness.AnalyzeCore(
            compilation: Harness.Compile(assemblyName: Harness.DefaultAssemblyName, sources: files),
            additionalFiles: [new HarnessAdditionalText(path: Harness.ManifestPath, text: manifest)],
            concurrent: false,
            cancellationToken: CancellationToken.None);

        Assert.Equal(expected: sequential.Ids, actual: concurrent.Ids);
    }

    [Fact]
    public void ConcurrentExecutionLosesNoClaimSoNoEntryIsSweptAsUnclaimed() {
        var files = ManyBrandedFiles();

        var manifest = Manifest.Of(
            Enumerable
                .Range(start: 0, count: BrandCount)
                .Select(selector: index => new ManifestEntry {
                    Id = ("brand-" + index.ToString(provider: CultureInfo.InvariantCulture)),
                    Sha256 = new string(c: '0', count: 64),
                    Symbol = ("M:Subject.Assembly.Subject.Target" + index.ToString(provider: CultureInfo.InvariantCulture)),
                })
                .ToArray());

        var result = Harness.AnalyzeCore(
            compilation: Harness.Compile(assemblyName: Harness.DefaultAssemblyName, sources: files),
            additionalFiles: [new HarnessAdditionalText(path: Harness.ManifestPath, text: manifest)],
            concurrent: true,
            cancellationToken: CancellationToken.None);

        Assert.Empty(collection: result.WithId(id: "VER002"));
    }

    [Fact]
    public void RepeatedRunsOfOneCompilationAgreeWithEachOther() {
        var compilation = Harness.Compile(assemblyName: Harness.DefaultAssemblyName, sources: ManyBrandedFiles());

        var runs = Enumerable
            .Range(start: 0, count: 8)
            .AsParallel()
            .Select(selector: _ => string.Join(
                separator: ",",
                values: Harness
                    .AnalyzeCore(
                        compilation: compilation,
                        additionalFiles: [new HarnessAdditionalText(path: Harness.ManifestPath, text: Manifest.Empty)],
                        concurrent: true,
                        cancellationToken: CancellationToken.None)
                    .Analyzer
                    .Select(selector: diagnostic => diagnostic.GetMessage())
                    .OrderBy(keySelector: message => message, comparer: StringComparer.Ordinal)))
            .Distinct(comparer: StringComparer.Ordinal)
            .ToArray();

        Assert.Single(collection: runs);
    }

    [Fact]
    public void AnalysisRefusesToRunOnAnAlreadyCancelledToken() {
        using var cancellation = new CancellationTokenSource();

        cancellation.Cancel();

        Assert.ThrowsAny<OperationCanceledException>(testCode: () => Harness.AnalyzeCore(
            compilation: Harness.Compile(assemblyName: Harness.DefaultAssemblyName, sources: ManyBrandedFiles()),
            additionalFiles: [new HarnessAdditionalText(path: Harness.ManifestPath, text: Manifest.Empty)],
            concurrent: true,
            cancellationToken: cancellation.Token));
    }
}
