using System.Reflection.PortableExecutable;
using System.Text.Json;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

using Puck.Cli.Format.Rewriters;
using Puck.Cli.Source;

namespace Puck.Cli.Format;

// The disk phase behind the semantic `named-args` pass: parses each target file's OWNING project into
// one Compilation (so a call's method symbol — framework or in-repo — resolves) referencing that
// project's real build closure, then runs NamedArgsRewriter against each file's semantic model. Symbol
// binding tolerates unrelated errors elsewhere, so a call is named whenever ITS own method resolves.
// Preserves source newline trivia like SourceRewrite; -WhatIf and -Verify report drift only.
internal static class NamedArgsPhase {
    // Fallback when a project has not been built (no generated global-usings file to read): the SDK's
    // default ImplicitUsings set. A built project supplies its real global usings.
    private const string DefaultGlobalUsings = """
        global using System;
        global using System.Collections.Generic;
        global using System.IO;
        global using System.Linq;
        global using System.Net.Http;
        global using System.Threading;
        global using System.Threading.Tasks;
        """;

    public static int Run(string rootArgument, bool whatIf, bool verify) {
        if (!SourceFiles.TryEnumerate(files: out var targetFiles, rootArgument: rootArgument, scanRoot: out _)) {
            return 2;
        }

        // Resolve symbols against each file's OWNING project (its own trees + build closure), never one
        // merged compilation — so a tree-wide root spanning many projects still binds every project's
        // calls correctly. Files are grouped by owning project and processed against that project's
        // compilation; each real method call then gets named.
        var parseOptions = new CSharpParseOptions(languageVersion: LanguageVersion.Preview);
        var byProject = targetFiles.GroupBy(
            keySelector: static file => (SourceFiles.FindOwningProjectDirectory(start: Path.GetDirectoryName(path: Path.GetFullPath(path: file))!) ?? ""),
            comparer: StringComparer.OrdinalIgnoreCase);

        var drifted = new List<string>();
        var corrupted = new List<string>();
        var degradedProjects = new List<string>();
        var ungrouped = 0;
        var unresolved = 0;

        foreach (var projectGroup in byProject) {
            if ((projectGroup.Key.Length == 0)
                || !SourceFiles.TryEnumerate(rootArgument: projectGroup.Key, scanRoot: out _, files: out var compilationFiles)) {
                ungrouped += projectGroup.Count();

                continue;
            }

            unresolved += ProcessProject(
                projectRoot: projectGroup.Key,
                targets: projectGroup,
                compilationFiles: compilationFiles,
                parseOptions: parseOptions,
                whatIf: whatIf,
                verify: verify,
                drifted: drifted,
                corrupted: corrupted,
                degradedProjects: degradedProjects);
        }

        // A file with no owning project was silently dropped before, so the run reported a clean bill of
        // health for source it never examined. Say so instead.
        if (ungrouped > 0) {
            Console.Error.WriteLine(value: $"named-args: {ungrouped} file(s) had no owning project — skipped");
        }

        if (degradedProjects.Count > 0) {
            Console.Error.WriteLine(
                value: $"named-args: {degradedProjects.Count} project(s) not built ({string.Join(separator: ", ", values: degradedProjects)}) — resolved against the framework only; some calls stay positional. Build for full coverage.");
        }

        if (unresolved > 0) {
            Console.Error.WriteLine(value: $"named-args: {unresolved} call(s) could not be resolved and were left positional (see the not-built note above, or check references).");
        }

        return RewriteIo.Report(
            label: "named-args",
            fileCount: targetFiles.Length,
            drifted: drifted,
            whatIf: (whatIf || verify),
            problems: [("have syntax errors before or after rewriting — SKIPPED", corrupted)]);
    }

    // Names the target files of ONE project against a compilation of that project's trees and its real
    // build closure; accumulates drift / corruption / not-built into the shared lists and returns the
    // count of calls that could not be resolved (left positional).
    private static int ProcessProject(
        string projectRoot,
        IEnumerable<string> targets,
        string[] compilationFiles,
        CSharpParseOptions parseOptions,
        bool whatIf,
        bool verify,
        List<string> drifted,
        List<string> corrupted,
        List<string> degradedProjects
    ) {
        var treesByPath = new Dictionary<string, SyntaxTree>(comparer: StringComparer.OrdinalIgnoreCase);

        foreach (var file in compilationFiles) {
            treesByPath[Path.GetFullPath(path: file)] = CSharpSyntaxTree.ParseText(text: File.ReadAllText(path: file), options: parseOptions, path: file);
        }

        var compilation = BuildProjectCompilation(projectRoot: projectRoot, trees: treesByPath.Values, parseOptions: parseOptions, degraded: out var degraded);

        if (degraded) {
            degradedProjects.Add(item: Path.GetFileName(path: projectRoot));
        }

        var unresolved = 0;

        foreach (var file in targets) {
            if (!treesByPath.TryGetValue(key: Path.GetFullPath(path: file), value: out var tree)) {
                continue;
            }

            var model = compilation.GetSemanticModel(syntaxTree: tree);

            unresolved += CountUnresolvedCalls(root: tree.GetRoot(), model: model);

            var rewritten = new NamedArgsRewriter(model: model).Visit(node: tree.GetRoot())!.ToFullString();
            var original = File.ReadAllText(path: file);

            if (RewriteIo.ContentEquals(a: rewritten, b: original)) {
                continue;
            }

            var relative = CliPaths.ToDisplay(fullPath: file);

            if (RewriteIo.HasSyntaxErrors(original: original, rewritten: rewritten)) {
                corrupted.Add(item: relative);

                continue;
            }

            drifted.Add(item: relative);

            // named-args only ADDS names where absent, so a second run is a no-op — idempotent by
            // construction. -Verify still audits (report drift, never write) plus the guard.
            if (!whatIf && !verify) {
                RewriteIo.WriteText(file: file, text: rewritten);
            }
        }

        return unresolved;
    }

    // Builds a compilation over the project's trees against its REAL build closure, so calls into
    // package / sibling-project types bind and get named instead of being silently skipped. Two inputs
    // match the actual build: the SDK-generated global-usings file (obj/**/*.GlobalUsings.g.cs — the
    // project's true implicit + explicit global usings) and every dependency assembly in the built output
    // (bin/**/*.dll, minus the project's own output and native DLLs), unioned with the shared framework
    // assemblies. Both need a prior build; without one, `degraded` is set and only the framework set plus
    // a default usings list are used, so coverage is reduced.
    internal static CSharpCompilation BuildProjectCompilation(string projectRoot, IEnumerable<SyntaxTree> trees, CSharpParseOptions parseOptions, out bool degraded) {
        var objDirectory = Path.Combine(path1: projectRoot, path2: "obj");
        var globalUsingsFile = (Directory.Exists(path: objDirectory)
            ? Directory.EnumerateFiles(path: objDirectory, searchOption: SearchOption.AllDirectories, searchPattern: "*.GlobalUsings.g.cs").FirstOrDefault()
            : null);
        var globalUsings = CSharpSyntaxTree.ParseText(
            text: ((globalUsingsFile is not null) ? File.ReadAllText(path: globalUsingsFile) : DefaultGlobalUsings),
            options: parseOptions);

        // Source-generator output (interop projections, etc.) is produced in-memory during build and is
        // absent from disk unless the project sets EmitCompilerGeneratedFiles. When it IS emitted
        // (obj/**/generated/**/*.cs), include it so calls into generated types resolve too; otherwise
        // those calls are reported as unresolved and left positional.
        var generatedTrees = (Directory.Exists(path: objDirectory)
            ? Directory.EnumerateFiles(path: objDirectory, searchOption: SearchOption.AllDirectories, searchPattern: "*.cs")
                .Where(predicate: static path => path.Contains(comparisonType: StringComparison.OrdinalIgnoreCase, value: $"{Path.DirectorySeparatorChar}generated{Path.DirectorySeparatorChar}"))
                .Select(selector: path => CSharpSyntaxTree.ParseText(text: File.ReadAllText(path: path), options: parseOptions, path: path))
            : Enumerable.Empty<SyntaxTree>());

        var frameworkDlls = ((string)AppContext.GetData(name: "TRUSTED_PLATFORM_ASSEMBLIES")!)
            .Split(options: StringSplitOptions.RemoveEmptyEntries, separator: Path.PathSeparator)
            .Where(predicate: static path => path.EndsWith(comparisonType: StringComparison.OrdinalIgnoreCase, value: ".dll"));
        var projectName = Path.GetFileNameWithoutExtension(path: (Directory.EnumerateFiles(path: projectRoot, searchPattern: "*.csproj").FirstOrDefault() ?? ""));
        var binDirectory = Path.Combine(path1: projectRoot, path2: "bin");
        var outputDlls = (Directory.Exists(path: binDirectory)
            ? Directory.EnumerateFiles(path: binDirectory, searchOption: SearchOption.AllDirectories, searchPattern: "*.dll")
                .Where(predicate: path =>
                    (!string.Equals(a: Path.GetFileNameWithoutExtension(path: path), b: projectName, comparisonType: StringComparison.OrdinalIgnoreCase)
                    && !path.Contains(comparisonType: StringComparison.OrdinalIgnoreCase, value: $"{Path.DirectorySeparatorChar}ref{Path.DirectorySeparatorChar}")))
            : Enumerable.Empty<string>());

        // Built dependency assemblies and package assemblies win over the framework on a name clash —
        // they are the exact versions the project compiles against.
        var references = outputDlls.Concat(second: PackageReferences(projectRoot: projectRoot)).Concat(second: frameworkDlls)
            .GroupBy(keySelector: static path => Path.GetFileName(path: path), comparer: StringComparer.OrdinalIgnoreCase)
            .Select(selector: static group => TryReference(path: group.First()))
            .Where(predicate: static reference => (reference is not null))
            .Select(selector: static reference => reference!);

        degraded = ((globalUsingsFile is null) || !Directory.Exists(path: binDirectory));

        return CSharpCompilation.Create(
            assemblyName: "named-args",
            syntaxTrees: trees.Append(element: globalUsings).Concat(second: generatedTrees),
            references: references,
            options: new CSharpCompilationOptions(outputKind: OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true));
    }

    // The project's package compile assemblies, read from the restore output (obj/project.assets.json).
    // A LIBRARY project's transitive package DLLs are not copied to its own bin (they land in the
    // consuming app's output), so bin alone misses them; the assets file lists every package's compile
    // asset by path into the global packages folder.
    private static IEnumerable<string> PackageReferences(string projectRoot) {
        var assetsPath = Path.Combine(path1: projectRoot, path2: "obj", path3: "project.assets.json");

        if (!File.Exists(path: assetsPath)) {
            return [];
        }

        using var document = JsonDocument.Parse(json: File.ReadAllText(path: assetsPath));
        var root = document.RootElement;

        if (!root.TryGetProperty(propertyName: "packageFolders", value: out var folders)
            || !root.TryGetProperty(propertyName: "targets", value: out var targets)
            || !root.TryGetProperty(propertyName: "libraries", value: out var libraries)) {
            return [];
        }

        var packageRoots = folders.EnumerateObject().Select(selector: static folder => folder.Name).ToList();
        var results = new List<string>();

        foreach (var target in targets.EnumerateObject()) {
            foreach (var library in target.Value.EnumerateObject()) {
                if (!library.Value.TryGetProperty(propertyName: "compile", value: out var compile)
                    || !libraries.TryGetProperty(propertyName: library.Name, value: out var entry)
                    || !entry.TryGetProperty(propertyName: "path", value: out var libraryPath)) {
                    continue;
                }

                foreach (var asset in compile.EnumerateObject()) {
                    if (asset.Name.EndsWith(comparisonType: StringComparison.Ordinal, value: "_._")) {
                        continue;
                    }

                    var relative = asset.Name.Replace(newChar: Path.DirectorySeparatorChar, oldChar: '/');
                    var resolved = packageRoots
                        .Select(selector: packageRoot => Path.Combine(path1: packageRoot, path2: libraryPath.GetString()!, path3: relative))
                        .FirstOrDefault(predicate: File.Exists);

                    if (resolved is not null) {
                        results.Add(item: resolved);
                    }
                }
            }
        }

        return results;
    }
    // Some assemblies in a build output are native and are not valid managed metadata references.
    // CreateFromFile is lazy (it would not throw until the compilation reads the file), so probe the PE
    // eagerly: HasMetadata is true only for managed assemblies and — unlike GetAssemblyName — reads no
    // culture, so it is safe under globalization-invariant mode. Native / unreadable files are dropped.
    private static MetadataReference? TryReference(string path) {
        try {
            using var stream = File.OpenRead(path: path);
            using var peReader = new PEReader(peStream: stream);

            return (peReader.HasMetadata ? MetadataReference.CreateFromFile(path: path) : null);
        } catch (Exception exception) when ((exception is BadImageFormatException or IOException)) {
            return null;
        }
    }
    // Coverage probe: a call whose symbol binds to nothing (no symbol, no candidate) is one named-args
    // must leave positional. Driving this to zero is the point of the real build closure; a nonzero count
    // means the references are still incomplete. `nameof(...)` is syntactically an invocation but a
    // contextual operator with no method symbol, so it is excluded — counting it would be a false
    // positive.
    private static int CountUnresolvedCalls(SyntaxNode root, SemanticModel model) =>
        root.DescendantNodes().Count(predicate: node =>
            ((node is InvocationExpressionSyntax or BaseObjectCreationExpressionSyntax)
            && (node is not InvocationExpressionSyntax { Expression: IdentifierNameSyntax { Identifier.ValueText: "nameof" } })
            && (model.GetSymbolInfo(node: node) is { Symbol: null, CandidateSymbols.IsEmpty: true })));

}
