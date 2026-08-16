using System.Collections.Immutable;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Text;

namespace Puck.Analyzers.Tests;

/// <summary>Feeds a fixed diagnostic set to Fix All, so a case decides exactly what the batch fixer is asked to repair.</summary>
internal sealed class FixedDiagnosticProvider : FixAllContext.DiagnosticProvider {
    private readonly ImmutableArray<Diagnostic> m_diagnostics;

    public FixedDiagnosticProvider(ImmutableArray<Diagnostic> diagnostics) {
        m_diagnostics = diagnostics;
    }

    public override Task<IEnumerable<Diagnostic>> GetAllDiagnosticsAsync(Project project, CancellationToken cancellationToken) =>
        Task.FromResult<IEnumerable<Diagnostic>>(result: m_diagnostics);
    public override Task<IEnumerable<Diagnostic>> GetDocumentDiagnosticsAsync(Document document, CancellationToken cancellationToken) =>
        Task.FromResult<IEnumerable<Diagnostic>>(result: m_diagnostics.Where(predicate: diagnostic => string.Equals(a: diagnostic.Location.SourceTree?.FilePath, b: document.FilePath, comparisonType: StringComparison.Ordinal)));
    public override Task<IEnumerable<Diagnostic>> GetProjectDiagnosticsAsync(Project project, CancellationToken cancellationToken) =>
        Task.FromResult<IEnumerable<Diagnostic>>(result: []);
}
/// <summary>A workspace holding one project's branded source and its ledger, ready to be diagnosed and repaired.</summary>
internal sealed class FixSubject : IDisposable {
    private readonly AdhocWorkspace m_workspace;

    internal FixSubject(AdhocWorkspace workspace, DocumentId sourceId, DocumentId manifestId) {
        ManifestId = manifestId;
        SourceId = sourceId;
        m_workspace = workspace;
    }

    /// <summary>The ledger document the code fix is expected to rewrite.</summary>
    public DocumentId ManifestId { get; }
    /// <summary>The branded source document a diagnostic is reported on.</summary>
    public DocumentId SourceId { get; }
    /// <summary>The solution as it stands now.</summary>
    public Solution Solution =>
        m_workspace.CurrentSolution;

    public void Dispose() =>
        m_workspace.Dispose();
    /// <summary>Adds a second project sharing the same physical ledger path, for the linked-file case.</summary>
    public DocumentId AddLinkedProject(string assemblyName, string source, string manifestJson) {
        var projectId = ProjectId.CreateNewId();
        var manifestId = DocumentId.CreateNewId(projectId: projectId);

        var solution = FixHarness.AddProject(
            solution: Solution,
            projectId: projectId,
            assemblyName: assemblyName,
            source: source,
            sourceId: DocumentId.CreateNewId(projectId: projectId),
            manifestJson: manifestJson,
            manifestId: manifestId,
            manifestName: Harness.ManifestFileName,
            manifestPath: Harness.ManifestPath,
            encoding: null);

        if (!m_workspace.TryApplyChanges(newSolution: solution)) {
            throw new InvalidOperationException(message: "The harness workspace rejected the linked project.");
        }

        return manifestId;
    }
}
/// <summary>Drives <see cref="VerifiedCodeCodeFixProvider"/> the way an IDE would: real documents, real solution edits.</summary>
internal static class FixHarness {
    /// <summary>Builds a one-project workspace from branded source and a ledger.</summary>
    public static FixSubject Create(string source, string manifestJson, string assemblyName = Harness.DefaultAssemblyName, string manifestName = Harness.ManifestFileName, string? manifestPath = null, Encoding? encoding = null) {
        var workspace = new AdhocWorkspace();
        var projectId = ProjectId.CreateNewId();
        var sourceId = DocumentId.CreateNewId(projectId: projectId);
        var manifestId = DocumentId.CreateNewId(projectId: projectId);

        var solution = AddProject(
            solution: workspace.CurrentSolution,
            projectId: projectId,
            assemblyName: assemblyName,
            source: source,
            sourceId: sourceId,
            manifestJson: manifestJson,
            manifestId: manifestId,
            manifestName: manifestName,
            manifestPath: (manifestPath ?? Harness.ManifestPath),
            encoding: encoding);

        if (!workspace.TryApplyChanges(newSolution: solution)) {
            throw new InvalidOperationException(message: "The harness workspace rejected its initial solution.");
        }

        return new FixSubject(manifestId: manifestId, sourceId: sourceId, workspace: workspace);
    }
    /// <summary>Adds a second additional document with the same file name, for the ambiguous-ledger case.</summary>
    public static Solution AddSecondManifest(Solution solution, DocumentId manifestId, string manifestJson, string path) =>
        solution.AddAdditionalDocument(
            documentId: manifestId,
            name: Harness.ManifestFileName,
            text: SourceText.From(text: manifestJson, encoding: new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)),
            folders: null,
            filePath: path);

    internal static Solution AddProject(Solution solution, ProjectId projectId, string assemblyName, string source, DocumentId sourceId, string manifestJson, DocumentId manifestId, string manifestName, string manifestPath, Encoding? encoding) {
        var projectInfo = ProjectInfo
            .Create(
                id: projectId,
                version: VersionStamp.Default,
                name: assemblyName,
                assemblyName: assemblyName,
                language: LanguageNames.CSharp)
            .WithCompilationOptions(compilationOptions: new CSharpCompilationOptions(outputKind: OutputKind.DynamicallyLinkedLibrary))
            .WithMetadataReferences(metadataReferences: Harness.RuntimeReferences)
            .WithParseOptions(parseOptions: new CSharpParseOptions(languageVersion: LanguageVersion.Latest));

        return solution
            .AddProject(projectInfo: projectInfo)
            .AddDocument(documentId: DocumentId.CreateNewId(projectId: projectId), name: "GlobalUsings.cs", text: "global using System;\r\nglobal using Puck;\r\n")
            .AddDocument(documentId: DocumentId.CreateNewId(projectId: projectId), name: "VerifiedCodeAttribute.cs", text: Harness.BrandAttribute)
            .AddDocument(documentId: sourceId, name: "Subject.cs", text: SourceText.From(text: source, encoding: new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)), folders: null, filePath: Path.Combine(path1: Path.GetTempPath(), path2: "Subject.cs"))
            .AddAdditionalDocument(
                documentId: manifestId,
                name: manifestName,
                text: SourceText.From(text: manifestJson, encoding: (encoding ?? new UTF8Encoding(encoderShouldEmitUTF8Identifier: false))),
                folders: null,
                filePath: manifestPath);
    }

    /// <summary>The analyzer diagnostics for one project of <paramref name="solution"/>.</summary>
    public static async Task<ImmutableArray<Diagnostic>> DiagnoseAsync(Solution solution, ProjectId projectId, CancellationToken cancellationToken) {
        var project = solution.GetProject(projectId: projectId)!;
        var compilation = (await project.GetCompilationAsync(cancellationToken: cancellationToken).ConfigureAwait(continueOnCapturedContext: false))!;

        var withAnalyzers = compilation.WithAnalyzers(
            analyzers: ImmutableArray.Create<DiagnosticAnalyzer>(item: new VerifiedCodeAnalyzer()),
            analysisOptions: new CompilationWithAnalyzersOptions(
                options: project.AnalyzerOptions,
                onAnalyzerException: null,
                concurrentAnalysis: true,
                logAnalyzerExecutionTime: false));

        return await withAnalyzers.GetAnalyzerDiagnosticsAsync(cancellationToken: cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
    }
    /// <summary>The code actions the provider offers for <paramref name="diagnostic"/>.</summary>
    public static async Task<ImmutableArray<CodeAction>> ActionsAsync(Solution solution, DocumentId documentId, Diagnostic diagnostic, CancellationToken cancellationToken) {
        var actions = ImmutableArray.CreateBuilder<CodeAction>();

        var context = new CodeFixContext(
            document: solution.GetDocument(documentId: documentId)!,
            diagnostic: diagnostic,
            registerCodeFix: (action, _) => actions.Add(item: action),
            cancellationToken: cancellationToken);

        await new VerifiedCodeCodeFixProvider().RegisterCodeFixesAsync(context: context).ConfigureAwait(continueOnCapturedContext: false);

        return actions.ToImmutable();
    }
    /// <summary>The solution <paramref name="action"/> would produce, or <see langword="null"/> when it changes nothing.</summary>
    public static async Task<Solution?> ApplyAsync(CodeAction action, CancellationToken cancellationToken) {
        var operations = await action.GetOperationsAsync(cancellationToken: cancellationToken).ConfigureAwait(continueOnCapturedContext: false);

        return operations.OfType<ApplyChangesOperation>().FirstOrDefault()?.ChangedSolution;
    }
    /// <summary>Runs Fix All over <paramref name="diagnostics"/> at project scope.</summary>
    public static async Task<Solution?> FixAllAsync(Solution solution, DocumentId documentId, ImmutableArray<Diagnostic> diagnostics, string equivalenceKey, CancellationToken cancellationToken) {
        var provider = new VerifiedCodeCodeFixProvider();

        var context = new FixAllContext(
            document: solution.GetDocument(documentId: documentId)!,
            codeFixProvider: provider,
            scope: FixAllScope.Project,
            codeActionEquivalenceKey: equivalenceKey,
            diagnosticIds: [VerifiedCodeAnalyzer.Ver001FingerprintMismatch.Id],
            fixAllDiagnosticProvider: new FixedDiagnosticProvider(diagnostics: diagnostics),
            cancellationToken: cancellationToken);

        var action = await provider.GetFixAllProvider()!.GetFixAsync(fixAllContext: context).ConfigureAwait(continueOnCapturedContext: false);

        return ((action is null) ? null : await ApplyAsync(action: action, cancellationToken: cancellationToken).ConfigureAwait(continueOnCapturedContext: false));
    }
    /// <summary>The text of an additional document in <paramref name="solution"/>.</summary>
    public static async Task<SourceText> ManifestTextAsync(Solution solution, DocumentId manifestId, CancellationToken cancellationToken) =>
        await solution.GetAdditionalDocument(documentId: manifestId)!.GetTextAsync(cancellationToken: cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
}
