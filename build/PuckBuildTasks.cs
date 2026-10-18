using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Xml;
using System.Xml.Linq;

using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;

/// <summary>
/// The repository's one inline MSBuild task (<c>build/PuckBuildTasks.targets</c>): every build operation the tree runs in
/// its own code, chosen by <see cref="Operation"/>, with the union of their parameters.
/// </summary>
/// <remarks>
/// <c>RoslynCodeTaskFactory</c> compiles a task's source once per task name in each build node, by starting the C#
/// compiler, which costs a second or more a compile, and a build that reuses no node (every agent and CI build) starts
/// every node cold. One name compiles once a node: separate tasks over the same sources compiled once a task a node,
/// the architecture gates twice in the node that builds <c>Puck.Analyzers</c>, which every project waits for. Each
/// operation is its own class below and runs as itself, on this task's build engine.
/// <list type="bullet">
/// <item><c>architecture</c>: <see cref="PuckArchitectureGate"/>.</item>
/// <item><c>denied-api</c>: <see cref="PuckArchitectureDeniedApiGate"/>.</item>
/// <item><c>shaders</c>: <see cref="PuckShaderBuild"/>, which cancels with the build.</item>
/// <item><c>d3d11-compile</c> and <c>d3d11-collect</c>: <see cref="PuckCompileDirect3D11Kernels"/> and
/// <see cref="PuckCollectDirect3D11Kernels"/>, whose bytecode is <see cref="Bytecode"/>.</item>
/// <item><c>remove-stale-world-packages</c>: <see cref="PuckRemoveStaleWorldPackages"/>.</item>
/// </list>
/// </remarks>
public sealed class PuckBuildTask : Task, ICancelableTask {
    private volatile PuckShaderBuild m_shaders;
    private volatile bool m_cancelled;

    /// <summary>The operation to run.</summary>
    public string Operation { get; set; } = "";

    /// <summary>See <see cref="PuckArchitectureGate.DeclaredReferences"/>.</summary>
    public ITaskItem[] DeclaredReferences { get; set; } = Array.Empty<ITaskItem>();
    /// <summary>See <see cref="PuckArchitectureGate.Kind"/>.</summary>
    public string Kind { get; set; } = "";
    /// <summary>See <see cref="PuckArchitectureGate.Kinds"/>.</summary>
    public ITaskItem[] Kinds { get; set; } = Array.Empty<ITaskItem>();
    /// <summary>See <see cref="PuckArchitectureGate.Layer"/>.</summary>
    public string Layer { get; set; } = "";
    /// <summary>See <see cref="PuckArchitectureGate.Layers"/>.</summary>
    public ITaskItem[] Layers { get; set; } = Array.Empty<ITaskItem>();
    /// <summary>See <see cref="PuckArchitectureGate.Profiles"/>.</summary>
    public ITaskItem[] Profiles { get; set; } = Array.Empty<ITaskItem>();
    /// <summary>The project's full path, for the architecture gates' diagnostics.</summary>
    public string ProjectFile { get; set; } = "";
    /// <summary>The project's name, for the architecture gates.</summary>
    public string ProjectName { get; set; } = "";
    /// <summary>See <see cref="PuckArchitectureGate.References"/>.</summary>
    public ITaskItem[] References { get; set; } = Array.Empty<ITaskItem>();
    /// <summary>See <see cref="PuckArchitectureGate.TransitiveReferences"/>.</summary>
    public ITaskItem[] TransitiveReferences { get; set; } = Array.Empty<ITaskItem>();

    /// <summary>See <see cref="PuckArchitectureDeniedApiGate.AssemblyPath"/>.</summary>
    public string AssemblyPath { get; set; } = "";
    /// <summary>See <see cref="PuckArchitectureDeniedApiGate.DeniedApis"/>.</summary>
    public ITaskItem[] DeniedApis { get; set; } = Array.Empty<ITaskItem>();

    /// <summary>See <see cref="PuckShaderBuild.CacheDirectory"/>.</summary>
    public string CacheDirectory { get; set; } = "";
    /// <summary>See <see cref="PuckShaderBuild.DxcCommand"/>.</summary>
    public string DxcCommand { get; set; } = "";
    /// <summary>See <see cref="PuckShaderBuild.Host"/>.</summary>
    public string Host { get; set; } = "";
    /// <summary>See <see cref="PuckShaderBuild.InputsFile"/>.</summary>
    public string InputsFile { get; set; } = "";
    /// <summary>See <see cref="PuckShaderBuild.LockFile"/>.</summary>
    public string LockFile { get; set; } = "";
    /// <summary>See <see cref="PuckShaderBuild.Mode"/>.</summary>
    public string Mode { get; set; } = "";
    /// <summary>See <see cref="PuckShaderBuild.Outputs"/>.</summary>
    public ITaskItem[] Outputs { get; set; } = Array.Empty<ITaskItem>();
    /// <summary>See <see cref="PuckShaderBuild.ProjectDirectory"/>.</summary>
    public string ProjectDirectory { get; set; } = "";
    /// <summary>See <see cref="PuckShaderBuild.RequestFile"/>.</summary>
    public string RequestFile { get; set; } = "";
    /// <summary>See <see cref="PuckShaderBuild.Tool"/>.</summary>
    public string Tool { get; set; } = "";

    /// <summary>The Direct3D 11 kernel sources, each carrying its <c>Entries</c>.</summary>
    public ITaskItem[] Sources { get; set; } = Array.Empty<ITaskItem>();
    /// <summary>The Direct3D 11 bytecode a <c>d3d11-compile</c> or <c>d3d11-collect</c> produced.</summary>
    [Output]
    public ITaskItem[] Bytecode { get; set; } = Array.Empty<ITaskItem>();

    /// <summary>See <see cref="PuckRemoveStaleWorldPackages.Deployed"/>.</summary>
    public ITaskItem[] Deployed { get; set; } = Array.Empty<ITaskItem>();
    /// <summary>See <see cref="PuckRemoveStaleWorldPackages.OutputDirectory"/>.</summary>
    public string OutputDirectory { get; set; } = "";
    /// <summary>See <see cref="PuckRemoveStaleWorldPackages.PackageDirectory"/>.</summary>
    public string PackageDirectory { get; set; } = "";

    public override bool Execute() {
        switch (Operation) {
            case "architecture":
                return Run(task: new PuckArchitectureGate {
                    DeclaredReferences = DeclaredReferences,
                    Kind = Kind,
                    Kinds = Kinds,
                    Layer = Layer,
                    Layers = Layers,
                    Profiles = Profiles,
                    ProjectFile = ProjectFile,
                    ProjectName = ProjectName,
                    References = References,
                    TransitiveReferences = TransitiveReferences,
                });
            case "denied-api":
                return Run(task: new PuckArchitectureDeniedApiGate {
                    AssemblyPath = AssemblyPath,
                    DeniedApis = DeniedApis,
                    ProjectFile = ProjectFile,
                    ProjectName = ProjectName,
                });
            case "shaders":
                var shaders = new PuckShaderBuild {
                    CacheDirectory = CacheDirectory,
                    DxcCommand = DxcCommand,
                    Host = Host,
                    InputsFile = InputsFile,
                    LockFile = LockFile,
                    Mode = Mode,
                    Outputs = Outputs,
                    ProjectDirectory = ProjectDirectory,
                    RequestFile = RequestFile,
                    Tool = Tool,
                };

                m_shaders = shaders;
                if (m_cancelled) { shaders.Cancel(); }

                return Run(task: shaders);
            case "d3d11-compile":
                var compile = new PuckCompileDirect3D11Kernels { Sources = Sources };
                var compiled = Run(task: compile);

                Bytecode = compile.Bytecode;

                return compiled;
            case "d3d11-collect":
                var collect = new PuckCollectDirect3D11Kernels { Sources = Sources };
                var collected = Run(task: collect);

                Bytecode = collect.Bytecode;

                return collected;
            case "remove-stale-world-packages":
                return Run(task: new PuckRemoveStaleWorldPackages {
                    Deployed = Deployed,
                    OutputDirectory = OutputDirectory,
                    PackageDirectory = PackageDirectory,
                });
            default:
                Log.LogError(message: ("PuckBuildTask has no operation '" + Operation + "'."));

                return false;
        }
    }
    /// <summary>Cancels a running shader build; the other operations run to completion.</summary>
    public void Cancel() {
        m_cancelled = true;
        m_shaders?.Cancel();
    }

    private bool Run(Task task) {
        task.BuildEngine = BuildEngine;
        task.HostObject = HostObject;

        return task.Execute();
    }
}

/// <summary>
/// The architecture gate. Runs in every in-scope project's build, immediately before <c>CoreCompile</c>,
/// against the resolved reference set, so a transitive edge is checked exactly like a declared one. Policy
/// comes from <c>build/Architecture.props</c> as evaluated MSBuild items; per-project layer and kind
/// declarations are read from the csproj files themselves.
/// </summary>
/// <remarks>
/// <para>The hook point is load-bearing: <c>@(ReferencePathWithRefAssemblies)</c> is empty after
/// <c>ResolveReferences</c> and populated by <c>FindReferenceAssembliesForReferences</c> on the way into the
/// compile, so a gate hooked earlier examines nothing and passes everything. <c>BeforeTargets="CoreCompile"</c>
/// also fires on a fully up-to-date incremental build.</para>
/// <para>Compiled by <c>RoslynCodeTaskFactory</c>, whose host resolves <c>System.Xml.Linq</c> but not
/// <c>System.Text.Json</c>; that is why the ledger is an MSBuild props file. The file is outside the
/// repository's analyzer and nullable context.</para>
/// </remarks>
public sealed class PuckArchitectureGate : Task {
    private const string BackendsLayer = "Backends";
    private const string CompositionRootsLayer = "Composition roots";
    private const string PresentationLayer = "Presentation";
    private const string TestKind = "Test";

    private readonly Dictionary<string, ProjectFacts> m_facts = new Dictionary<string, ProjectFacts>(comparer: StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// This project's <c>@(ProjectReference)</c> as MSBuild evaluated it, which includes an edge contributed by
    /// an <c>&lt;Import&gt;</c> that reading the csproj text would miss. Semantics come from MSBuild; the csproj
    /// text is read only for provenance, where being incomplete degrades a message rather than a verdict.
    /// </summary>
    public ITaskItem[] DeclaredReferences { get; set; } = Array.Empty<ITaskItem>();
    /// <summary>The kind taxonomy: <c>Include</c> is the kind, <c>Ranked</c> is "true" or "false".</summary>
    public ITaskItem[] Kinds { get; set; } = Array.Empty<ITaskItem>();
    /// <summary>This project's declared <c>&lt;PuckKind&gt;</c>.</summary>
    public string Kind { get; set; } = "";
    /// <summary>This project's declared <c>&lt;PuckLayer&gt;</c>, empty for a terminal kind.</summary>
    public string Layer { get; set; } = "";
    /// <summary>The layer taxonomy: <c>Include</c> is the row name, <c>Rank</c> its index from the top.</summary>
    public ITaskItem[] Layers { get; set; } = Array.Empty<ITaskItem>();
    /// <summary>Exact-equality closures: <c>Include</c> is the project, <c>Closure</c> a semicolon list.</summary>
    public ITaskItem[] Profiles { get; set; } = Array.Empty<ITaskItem>();
    /// <summary>This project's full path, used for provenance and for the diagnostic's file position.</summary>
    public string ProjectFile { get; set; } = "";
    /// <summary>This project's name.</summary>
    public string ProjectName { get; set; } = "";
    /// <summary>
    /// The project references the SDK appends to <c>@(ProjectReference)</c> from the assets file
    /// (<c>@(_TransitiveProjectReferences)</c>) because a referenced project holds them. This project inherits
    /// them; it does not name them. Empty when the SDK stops producing the item, which leaves every evaluated
    /// reference counted as named here.
    /// </summary>
    public ITaskItem[] TransitiveReferences { get; set; } = Array.Empty<ITaskItem>();
    /// <summary>
    /// The WHOLE resolved reference set, unfiltered. Project-produced items carry
    /// <c>MSBuildSourceProjectFile</c> — the csproj that produced the assembly, which is what makes the
    /// referenced project's own declaration readable from here — and items without it are reached by some
    /// other route, which is itself a finding rather than a reason to skip them.
    /// </summary>
    public ITaskItem[] References { get; set; } = Array.Empty<ITaskItem>();

    public override bool Execute() {
        var kinds = ReadKinds();
        var ranks = ReadRanks();

        if (!TryValidateOwnDeclaration(kinds: kinds, ranks: ranks)) {
            return false;
        }

        var closure = ReadClosure(ok: out var ok);
        var ownRank = RankOf(kinds: kinds, ranks: ranks, kind: Kind, layer: Layer);

        foreach (var reference in closure) {
            ok &= CheckEdge(kinds: kinds, ownRank: ownRank, ranks: ranks, reference: reference);
        }

        ok &= CheckBackendQuarantine(closure: closure, kinds: kinds);
        ok &= CheckProfile(closure: closure);

        return ok;
    }

    private bool CheckBackendQuarantine(List<ProjectFacts> closure, Dictionary<string, bool> kinds) {
        var backends = closure.Where(predicate: f => (f.Layer == BackendsLayer)).ToArray();

        if (backends.Length == 0) {
            return true;
        }

        if ((Layer == PresentationLayer) || (Layer == CompositionRootsLayer)) {
            return true;
        }

        // A backend's own test project names the backend because the backend is what it tests: a Test-kind project
        // named Puck.X.Tests may hold Puck.X, and no other backend.
        if (string.Equals(a: Kind, b: TestKind, comparisonType: StringComparison.OrdinalIgnoreCase)
            && backends.All(predicate: backend => string.Equals(a: ProjectName, b: (backend.Name + ".Tests"), comparisonType: StringComparison.OrdinalIgnoreCase))) {
            return true;
        }

        // A terminal consumer inherits the closure of whatever it composes and never introduces a backend: every
        // ranked project in its closure passed this gate in its own build, so only an edge this project names
        // itself is checked.
        var terminal = (kinds.TryGetValue(key: Kind, value: out var ranked) && !ranked);
        var declared = EvaluatedReferenceNames();
        var introduced = backends.Where(predicate: b => declared.Contains(item: b.Name)).ToArray();

        if (terminal && (introduced.Length == 0)) {
            return true;
        }

        foreach (var backend in (terminal ? introduced : backends)) {
            var path = DescribePath(target: backend.Name);

            LogViolation(
                code: "PUCKARCH002",
                message:
                    (((((string)$"{Describe()} holds the Backends-row assembly '{backend.Name}' in its resolved closure. Only the Presentation row and composition roots may. Arrives by: {path}. ")
                    + "The .Presentation wrapper row exists precisely so engine code never names a backend, and a backend's own test project (Puck.X.Tests, ")
                    + "Test kind) holds only Puck.X. If this edge is genuinely right, the fix is a reasoned change to the quarantine in build/Architecture.props ")
                    + "— never a quiet widening, which would silently un-check every project at once."));
        }

        return false;
    }
    private bool CheckEdge(Dictionary<string, bool> kinds, Dictionary<string, int> ranks, int ownRank, ProjectFacts reference) {
        var ownRanked = (!kinds.TryGetValue(key: Kind, value: out var thisRanked) || thisRanked);

        // An UNDECLARED referent fails HERE, at the consumer. It used to fall through RankOf to int.MaxValue
        // — bottom of the world, and therefore referenceable by everyone — which the unit-1 review turned
        // into a working bypass: a project placed in a quarantined tree carries no declaration because the
        // gate does not run there, and a gated project could then absorb it with a green build.
        //
        // That is the second half of what quarantine has to mean. Ungated is only one direction; a tree that
        // gated code may still depend on is not quarantined, it is merely unchecked, and the difference is
        // the entire value of the word.
        if (string.IsNullOrEmpty(value: reference.Kind)) {
            LogViolation(
                code: "PUCKARCH007",
                message:
                    ((((((string)$"{Describe()} holds '{reference.Name}' in its resolved closure, and that project declares no <PuckKind> — it is outside the gate's scope (its project file is '{reference.ProjectPath}'). ")
                    + $"Arrives by: {DescribePath(target: reference.Name)}. ")
                    + "An undeclared project is not a project at the bottom of the layering; it is a project the rules have never been applied to, and depending on one ")
                    + "imports exactly the freedom it was excluded to have. If it belongs in the graph, bring it into scope and declare it; if it belongs outside, nothing ")
                    + "inside may reference it."));

            return false;
        }

        // Only a RANKED consumer is forbidden from holding a terminal-kind project: the rule exists so
        // ENGINE CODE never depends on tooling, and a terminal-to-terminal edge is not that. Puck.Analyzers
        // is referenced as an ordinary assembly by Puck.Analyzers.Tests, which instantiates the analyzer and
        // drives it over compilations it builds itself — a test depending on the thing it tests. Measured by
        // arming the rule and reading what it caught, which is the only way this narrowing was going to be
        // found: stated in the abstract, "nothing may reference a terminal kind" sounds exactly right.
        if (ownRanked && kinds.TryGetValue(key: reference.Kind, value: out var referenceRanked) && !referenceRanked) {
            LogViolation(
                code: "PUCKARCH003",
                message:
                    (((string)$"{Describe()} holds '{reference.Name}' in its resolved closure, and that project's kind is {reference.Kind} — a TERMINAL kind, which consumes the tree and is never consumed by it. Arrives by: {DescribePath(target: reference.Name)}. ")
                    + "Either the dependency is inverted, or the referenced project is misclassified and its <PuckKind> should be a ranked kind with a <PuckLayer> to match."));

            return false;
        }

        var referenceRank = RankOf(kind: reference.Kind, kinds: kinds, layer: reference.Layer, ranks: ranks);

        if (referenceRank >= ownRank) {
            return true;
        }

        LogViolation(
            code: "PUCKARCH001",
            message:
                ((((string)$"{Describe()} holds '{reference.Name}' ({reference.Layer}) in its resolved closure — an UPWARD edge. Dependencies point downward or sideways, never up. Arrives by: {DescribePath(target: reference.Name)}. ")
                + "Note this is the RESOLVED closure, not the declared one: an edge that arrives transitively is the same architectural fact as one written in this csproj, and reads ")
                + "differently only to a human reading project files."));

        return false;
    }
    private bool CheckProfile(List<ProjectFacts> closure) {
        var profile = Profiles.FirstOrDefault(predicate: p => string.Equals(a: p.ItemSpec, b: ProjectName, comparisonType: StringComparison.OrdinalIgnoreCase));

        if (profile is null) {
            return true;
        }

        var expected = new SortedSet<string>(
            collection: (profile.GetMetadata(metadataName: "Closure") ?? "").Split(options: StringSplitOptions.RemoveEmptyEntries, separator: new[] { ';' }).Select(selector: s => s.Trim()),
            comparer: StringComparer.OrdinalIgnoreCase);
        var actual = new SortedSet<string>(collection: closure.Select(selector: f => f.Name), comparer: StringComparer.OrdinalIgnoreCase);
        var added = actual.Except(second: expected).ToArray();
        var removed = expected.Except(second: actual).ToArray();

        if ((added.Length == 0) && (removed.Length == 0)) {
            return true;
        }

        var detail = new List<string>();

        // Cast to the sequence overload explicitly: a string[] is convertible to object[] by array
        // covariance, which leaves string.Join ambiguous between its two shapes.
        if (added.Length != 0) {
            detail.Add(item: $"gained {string.Join(separator: ", ", values: ((IEnumerable<string>)added))}");
        }

        if (removed.Length != 0) {
            detail.Add(item: $"lost {string.Join(separator: ", ", values: ((IEnumerable<string>)removed))}");
        }

        LogViolation(
            code: "PUCKARCH004",
            message:
                (((((string)$"{ProjectName} carries a lane profile, and its resolved closure no longer EQUALS it: {string.Join(separator: "; ", values: detail)}. Expected exactly [{string.Join(separator: ", ", values: expected)}]. ")
                + "Equality is deliberate — a removed edge has to be as visible as an added one, or a boundary can be dismantled a reference at a time and every check still passes. ")
                + "Every forbidden edge in this split is SAME-ROW, so the layer rule cannot see any of them; this profile is the only thing that can. ")
                + "If the change is right, update the profile in build/Architecture.props in the same commit and say why."));

        return false;
    }
    private bool TryValidateOwnDeclaration(Dictionary<string, bool> kinds, Dictionary<string, int> ranks) {
        if (string.IsNullOrEmpty(value: Kind)) {
            LogViolation(
                code: "PUCKARCH005",
                message:
                    (((string)$"{ProjectName} declares no <PuckKind>. Every project in the gate's scope declares one, so a new project is classified when it is created rather than whenever someone notices. Ranked kinds ({string.Join(separator: ", ", values: kinds.Where(predicate: k => k.Value).Select(selector: k => k.Key))}) also declare a <PuckLayer>; ")
                    + $"terminal kinds ({string.Join(separator: ", ", values: kinds.Where(predicate: k => !k.Value).Select(selector: k => k.Key))}) must not."));

            return false;
        }

        if (!kinds.TryGetValue(key: Kind, value: out var ranked)) {
            LogViolation(
                code: "PUCKARCH005",
                message: $"{ProjectName} declares <PuckKind>{Kind}</PuckKind>, which is not a kind in build/Architecture.props. Known kinds: {string.Join(separator: ", ", values: kinds.Keys)}.");

            return false;
        }

        if (ranked && string.IsNullOrEmpty(value: Layer)) {
            LogViolation(
                code: "PUCKARCH005",
                message: $"{ProjectName} is kind {Kind}, which is ranked, so it must declare a <PuckLayer>. Known layers: {string.Join(separator: ", ", values: ranks.Keys)}.");

            return false;
        }

        if (!ranked && !string.IsNullOrEmpty(value: Layer)) {
            LogViolation(
                code: "PUCKARCH005",
                message:
                    $"{ProjectName} is kind {Kind}, which is TERMINAL, so it must not declare a <PuckLayer> — it sits above every layer by construction, and a second declaration is only somewhere for the two to disagree.");

            return false;
        }

        if (ranked && !ranks.ContainsKey(key: Layer)) {
            LogViolation(
                code: "PUCKARCH005",
                message: $"{ProjectName} declares <PuckLayer>{Layer}</PuckLayer>, which is not a row in build/Architecture.props. Known layers: {string.Join(separator: ", ", values: ranks.Keys)}.");

            return false;
        }

        return true;
    }
    /// <summary>
    /// The names this project references directly, from MSBuild's evaluated <c>@(ProjectReference)</c> rather
    /// than the csproj text, so an edge contributed by an <c>&lt;Import&gt;</c> counts. An item the SDK appended
    /// from <see cref="TransitiveReferences"/> is inherited, not named, unless the csproj declares it too.
    /// </summary>
    private HashSet<string> EvaluatedReferenceNames() {
        var inherited = new HashSet<string>(comparer: StringComparer.OrdinalIgnoreCase);
        var own = new HashSet<string>(comparer: StringComparer.OrdinalIgnoreCase);
        var names = new HashSet<string>(comparer: StringComparer.OrdinalIgnoreCase);

        foreach (var reference in TransitiveReferences) {
            _ = inherited.Add(item: Path.GetFileNameWithoutExtension(path: reference.ItemSpec));
        }

        foreach (var edge in DeclaredEdges(projectFile: ProjectFile)) {
            _ = own.Add(item: edge.Name);
        }

        foreach (var reference in DeclaredReferences) {
            var name = Path.GetFileNameWithoutExtension(path: reference.ItemSpec);

            if (inherited.Contains(item: name) && !own.Contains(item: name)) {
                continue;
            }

            _ = names.Add(item: name);
        }

        return names;
    }
    private static List<Edge> DeclaredEdges(string projectFile) {
        var edges = new List<Edge>();

        if (!File.Exists(path: projectFile)) {
            return edges;
        }

        var document = XDocument.Load(options: LoadOptions.SetLineInfo, uri: projectFile);

        foreach (var element in document.Descendants().Where(predicate: e => (e.Name.LocalName == "ProjectReference"))) {
            // Update= counts as a declaration for provenance. An edge whose Include lives in
            // Directory.Build.props and whose metadata is amended here (Puck.Analyzers.Tests does exactly
            // this, turning the repo-wide analyzer extension into an ordinary assembly reference) is one a
            // reader has to come HERE to change, which is the only thing this walk is for.
            var include = (element.Attribute(name: "Include") ?? element.Attribute(name: "Update"));

            if (include is null) {
                continue;
            }

            // The analyzer edge is not an assembly reference — ReferenceOutputAssembly="false" keeps it out
            // of every resolved reference set — so it is not an architectural edge either.
            var outputAssembly =
                (element.Attribute(name: "ReferenceOutputAssembly")?.Value
                ?? element.Elements().FirstOrDefault(predicate: e => (e.Name.LocalName == "ReferenceOutputAssembly"))?.Value);

            if (string.Equals(a: outputAssembly, b: "false", comparisonType: StringComparison.OrdinalIgnoreCase)) {
                continue;
            }

            var relative = include.Value.Replace(newChar: Path.DirectorySeparatorChar, oldChar: '\\');
            var resolved = Path.GetFullPath(path: Path.Combine(path1: (Path.GetDirectoryName(path: projectFile) ?? "."), path2: relative));
            var lineInfo = ((IXmlLineInfo)element);

            edges.Add(
                item: new Edge {
                    DeclaredIn = projectFile,
                    Line = (lineInfo.HasLineInfo() ? lineInfo.LineNumber : 0),
                    Name = Path.GetFileNameWithoutExtension(path: resolved),
                    ProjectPath = resolved,
                });
        }

        return edges;
    }
    /// <summary>
    /// The resolved Puck closure, and the place PUCKARCH006 is raised: a Puck assembly that arrives by any
    /// route OTHER than a project reference is refused on the spot, before its layer is ever considered.
    /// </summary>
    /// <remarks>
    /// Checking the layer of a raw DLL would be the wrong repair. The defect the review found is not that a
    /// smuggled reference lands on the wrong row — it is that the project system stops describing the graph
    /// at all, so every later rule is reasoning about a picture that no longer matches the compile. Refusing
    /// the ROUTE is both simpler and stronger than policing its destination, and it closes the package case
    /// with the same sentence.
    /// </remarks>
    private List<ProjectFacts> ReadClosure(out bool ok) {
        var closure = new List<ProjectFacts>();
        var seen = new HashSet<string>(comparer: StringComparer.OrdinalIgnoreCase);

        ok = true;

        foreach (var reference in References) {
            var source = reference.GetMetadata(metadataName: "MSBuildSourceProjectFile");
            var identity = Path.GetFileNameWithoutExtension(path: reference.ItemSpec);

            if (string.IsNullOrEmpty(value: source)) {
                if (!identity.StartsWith(comparisonType: StringComparison.OrdinalIgnoreCase, value: "Puck.") || !seen.Add(item: identity)) {
                    continue;
                }

                var route = reference.GetMetadata(metadataName: "ReferenceSourceTarget");

                LogViolation(
                    code: "PUCKARCH006",
                    message:
                        (((((($"{Describe()} resolves the Puck assembly '{identity}' by a route that is not a project reference"
                        + (string.IsNullOrEmpty(value: route) ? " (a raw <Reference>, or a package)" : $" (ReferenceSourceTarget='{route}')"))
                        + $", from '{reference.ItemSpec}'. ")
                        + "There is no csproj line to name here, because nothing declared it as a project edge — that absence IS the finding. ")
                        + "Every architecture rule below this one reads the project graph, so an assembly reached around the project system is ")
                        + "invisible to all of them at once: it defeats the lane profiles, which are the only tier doing security work, and it ")
                        + "carries a backend past the quarantine from any row. Reference the project."));

                ok = false;

                continue;
            }

            var name = Path.GetFileNameWithoutExtension(path: source);

            if (!seen.Add(item: name)) {
                continue;
            }

            closure.Add(item: FactsFor(projectFile: source));
        }

        return closure;
    }
    private ProjectFacts FactsFor(string projectFile) {
        if (m_facts.TryGetValue(key: projectFile, value: out var cached)) {
            return cached;
        }

        var facts = new ProjectFacts { Name = Path.GetFileNameWithoutExtension(path: projectFile), ProjectPath = projectFile };

        if (File.Exists(path: projectFile)) {
            var document = XDocument.Load(uri: projectFile);

            facts.Kind = (document.Descendants().FirstOrDefault(predicate: e => (e.Name.LocalName == "PuckKind"))?.Value.Trim() ?? "");
            facts.Layer = (document.Descendants().FirstOrDefault(predicate: e => (e.Name.LocalName == "PuckLayer"))?.Value.Trim() ?? "");
        }

        m_facts[projectFile] = facts;

        return facts;
    }
    private Dictionary<string, bool> ReadKinds() {
        var kinds = new Dictionary<string, bool>(comparer: StringComparer.OrdinalIgnoreCase);

        foreach (var kind in Kinds) {
            kinds[kind.ItemSpec] = string.Equals(a: kind.GetMetadata(metadataName: "Ranked"), b: "true", comparisonType: StringComparison.OrdinalIgnoreCase);
        }

        return kinds;
    }
    private Dictionary<string, int> ReadRanks() {
        var ranks = new Dictionary<string, int>(comparer: StringComparer.OrdinalIgnoreCase);

        foreach (var layer in Layers) {
            ranks[layer.ItemSpec] = (int.TryParse(s: layer.GetMetadata(metadataName: "Rank"), result: out var rank) ? rank : int.MaxValue);
        }

        return ranks;
    }
    private static int RankOf(Dictionary<string, bool> kinds, Dictionary<string, int> ranks, string kind, string layer) {
        // A terminal kind sits above every layer by construction, so everything it references is below it
        // and the rank rule needs no special case for it.
        if (kinds.TryGetValue(key: kind, value: out var ranked) && !ranked) {
            return -1;
        }

        return (ranks.TryGetValue(key: layer, value: out var rank) ? rank : int.MaxValue);
    }
    /// <summary>
    /// The shortest declared path from this project to <paramref name="target"/>, each hop carrying the csproj
    /// and line that declares it: the resolved set proves an edge exists, only this says where to change it.
    /// </summary>
    private string DescribePath(string target) {
        var queue = new Queue<List<Edge>>();
        var visited = new HashSet<string>(comparer: StringComparer.OrdinalIgnoreCase) { ProjectName };

        foreach (var edge in DeclaredEdges(projectFile: ProjectFile)) {
            queue.Enqueue(item: new List<Edge> { edge });
        }

        while (queue.Count != 0) {
            var path = queue.Dequeue();
            var last = path[(path.Count - 1)];

            if (string.Equals(a: last.Name, b: target, comparisonType: StringComparison.OrdinalIgnoreCase)) {
                return ((ProjectName + " -> ") + string.Join(separator: " -> ", values: path.Select(selector: e => $"{e.Name} ({Path.GetFileName(path: e.DeclaredIn)}:{e.Line})")));
            }

            if (!visited.Add(item: last.Name)) {
                continue;
            }

            foreach (var next in DeclaredEdges(projectFile: last.ProjectPath)) {
                var extended = new List<Edge>(collection: path) { next };

                queue.Enqueue(item: extended);
            }
        }

        return $"{ProjectName} -> ... -> {target} (no declared path found — the reference resolves but no csproj in the walk declares it, which is itself worth understanding before anything else here is believed)";
    }
    private string Describe() {
        return (string.IsNullOrEmpty(value: Layer) ? $"{ProjectName} ({Kind})" : $"{ProjectName} ({Layer})");
    }
    private void LogViolation(string code, string message) {
        Log.LogError(
            subcategory: null,
            errorCode: code,
            helpKeyword: null,
            file: ProjectFile,
            lineNumber: 0,
            columnNumber: 0,
            endLineNumber: 0,
            endColumnNumber: 0,
            message: message);
    }

    private sealed class Edge {
        public string DeclaredIn = "";
        public int Line;
        public string Name = "";
        public string ProjectPath = "";
    }
    private sealed class ProjectFacts {
        public string Kind = "";
        public string Layer = "";
        public string Name = "";
        public string ProjectPath = "";
    }
}
/// <summary>
/// The denied-API gate: after a project compiles, reads its OUTPUT ASSEMBLY's own assembly-reference table and
/// fails when it names an assembly <c>build/Architecture.props</c> denies that project.
/// </summary>
/// <remarks>
/// <para>Hooked at <c>AfterTargets="CoreCompile"</c>, not before: a denied assembly such as <c>System.Console</c>
/// arrives through the shared framework, never a <c>&lt;ProjectReference&gt;</c>, so <see cref="PuckArchitectureGate"/>'s
/// resolved-closure walk cannot see it at all. Only the compiled output records whether the code actually pulled a
/// member from it in — the C# compiler emits an assembly-reference row only for an assembly a type or member is
/// actually read from, never for one merely available at compile time — so the check has to run once that output
/// exists.</para>
/// <para>Compiled by the same <c>RoslynCodeTaskFactory</c> host as <see cref="PuckArchitectureGate"/>, so it is
/// outside the repository's analyzer and nullable context too.</para>
/// </remarks>
public sealed class PuckArchitectureDeniedApiGate : Task {
    /// <summary>The freshly compiled output assembly to inspect (<c>@(IntermediateAssembly)</c>).</summary>
    public string AssemblyPath { get; set; } = "";
    /// <summary>The denial ledger: <c>Include</c> is the denied project, <c>Type</c> the denied assembly's name, <c>Enabled</c> "true" or "false".</summary>
    public ITaskItem[] DeniedApis { get; set; } = Array.Empty<ITaskItem>();
    /// <summary>This project's full path, used for the diagnostic's file position.</summary>
    public string ProjectFile { get; set; } = "";
    /// <summary>This project's name.</summary>
    public string ProjectName { get; set; } = "";

    public override bool Execute() {
        var denials = DeniedApis
            .Where(predicate: d => string.Equals(a: d.ItemSpec, b: ProjectName, comparisonType: StringComparison.OrdinalIgnoreCase))
            .Where(predicate: d => string.Equals(a: d.GetMetadata(metadataName: "Enabled"), b: "true", comparisonType: StringComparison.OrdinalIgnoreCase))
            .ToArray();

        if ((denials.Length == 0) || string.IsNullOrEmpty(value: AssemblyPath) || !File.Exists(path: AssemblyPath)) {
            return true;
        }

        var referenced = ReadAssemblyReferenceNames(assemblyPath: AssemblyPath);
        var ok = true;

        foreach (var denial in denials) {
            var deniedAssembly = denial.GetMetadata(metadataName: "Type");

            if (string.IsNullOrEmpty(value: deniedAssembly) || !referenced.Contains(item: deniedAssembly)) {
                continue;
            }

            Log.LogError(
                subcategory: null,
                errorCode: "PUCKARCH008",
                helpKeyword: null,
                file: ProjectFile,
                lineNumber: 0,
                columnNumber: 0,
                endLineNumber: 0,
                endColumnNumber: 0,
                message:
                    ((string)$"{ProjectName}'s compiled output references the denied assembly '{deniedAssembly}' (build/Architecture.props, PuckArchitectureDeniedApi). Route the call through a seam {ProjectName} already exposes for it and bind the concrete implementation from a composition root or console project the ledger does not deny."));

            ok = false;
        }

        return ok;
    }

    private static HashSet<string> ReadAssemblyReferenceNames(string assemblyPath) {
        var names = new HashSet<string>(comparer: StringComparer.OrdinalIgnoreCase);

        using var stream = File.OpenRead(path: assemblyPath);
        using var peReader = new System.Reflection.PortableExecutable.PEReader(peStream: stream);

        if (!peReader.HasMetadata) {
            return names;
        }

        // The build-time host's copy of this library predates PEReader.GetMetadataReader(): its metadata block
        // is read back through the byte-array overload of MetadataReaderProvider.FromMetadataImage instead.
        using var provider = System.Reflection.Metadata.MetadataReaderProvider.FromMetadataImage(image: peReader.GetMetadata().GetContent());
        var metadata = provider.GetMetadataReader(options: System.Reflection.Metadata.MetadataReaderOptions.Default, utf8Decoder: null);

        foreach (var handle in metadata.AssemblyReferences) {
            var reference = metadata.GetAssemblyReference(handle: handle);

            _ = names.Add(item: metadata.GetString(handle: reference.Name));
        }

        return names;
    }
}

/// <summary>Collects existing Direct3D 11 entry-point bytecode for packaging, refusing missing or stale outputs without
/// compiling anything. Compilation belongs to the build, so a pack without a build ships exactly the built kernels.</summary>
public sealed class PuckCollectDirect3D11Kernels : Task {
    /// <summary>The kernel sources, each carrying its semicolon-separated <c>Entries</c>.</summary>
    [Required]
    public ITaskItem[] Sources { get; set; } = Array.Empty<ITaskItem>();
    /// <summary>The existing bytecode files, one per source and entry point.</summary>
    [Output]
    public ITaskItem[] Bytecode { get; private set; } = Array.Empty<ITaskItem>();

    public override bool Execute() {
        var outputs = new List<ITaskItem>();

        foreach (var source in Sources) {
            var sourcePath = source.GetMetadata(metadataName: "FullPath");

            foreach (var entry in source.GetMetadata(metadataName: "Entries").Split(options: StringSplitOptions.RemoveEmptyEntries, separator: new[] { ';' })) {
                var output = Path.Combine(
                    path1: Path.GetDirectoryName(path: sourcePath)!,
                    path2: $"{Path.GetFileNameWithoutExtension(path: sourcePath)}.{entry.Trim()}.dxbc"
                );

                if (!File.Exists(path: output) || (File.GetLastWriteTimeUtc(path: output) < File.GetLastWriteTimeUtc(path: sourcePath))) {
                    Log.LogError(message: $"Shader bytecode '{output}' is missing or stale. Build normally before packing or publishing with --no-build.");
                    continue;
                }

                outputs.Add(item: new TaskItem(itemSpec: output));
            }
        }

        Bytecode = outputs.ToArray();

        return !Log.HasLoggedErrors;
    }
}
/// <summary>
/// Compiles each Direct3D 11 compute kernel source's entry points to <c>cs_5_0</c> DXBC at build: the kernel-class probe
/// kernels and the camera frame converter's conversion kernels, which run on a camera graph's own Direct3D 11 device, so
/// a kernel is created from bytecode there and nothing compiles at run time. Each item's <c>Entries</c> metadata lists
/// its entry points; entry <c>e</c> of <c>kernel.hlsl</c> is written to <c>kernel.e.dxbc</c> beside the source, the
/// name <c>ProbeKindManifest.KernelBytecodePath</c> and <c>Win32D3D11CameraFrameConverter</c> read. The compiler is
/// <c>D3DCompile</c> from <c>d3dcompiler_47.dll</c>, which every supported Windows ships; a source with an
/// <c>#include</c> is refused, because the compile resolves none.
/// </summary>
/// <remarks>An output newer than its source is kept, so an unchanged kernel compiles once.</remarks>
public sealed class PuckCompileDirect3D11Kernels : Task {
    /// <summary>The kernel sources; each carries <c>Entries</c>, its entry points separated by semicolons.</summary>
    [Required]
    public ITaskItem[] Sources { get; set; } = Array.Empty<ITaskItem>();
    /// <summary>The bytecode files, one per source and entry point.</summary>
    [Output]
    public ITaskItem[] Bytecode { get; private set; } = Array.Empty<ITaskItem>();

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate IntPtr GetBufferPointer(IntPtr blob);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate UIntPtr GetBufferSize(IntPtr blob);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate uint ReleaseBlob(IntPtr blob);

    [DllImport("d3dcompiler_47.dll", CallingConvention = CallingConvention.StdCall, CharSet = CharSet.Ansi, ExactSpelling = true)]
    private static extern int D3DCompile(byte[] sourceData, UIntPtr sourceDataSize, string sourceName, IntPtr defines, IntPtr include, string entryPoint, string target, uint flags1, uint flags2, out IntPtr code, out IntPtr errors);

    public override bool Execute() {
        var outputs = new List<ITaskItem>();

        foreach (var source in Sources) {
            var sourcePath = source.GetMetadata(metadataName: "FullPath");
            var entries = source.GetMetadata(metadataName: "Entries").Split(options: StringSplitOptions.RemoveEmptyEntries, separator: new[] { ';' });
            var bytes = File.ReadAllBytes(path: sourcePath);

            if (Encoding.UTF8.GetString(bytes: bytes).Contains(value: "#include")) {
                Log.LogError(message: $"Kernel '{sourcePath}' includes a file; a Direct3D 11 kernel compiles alone.");
                continue;
            }

            foreach (var entry in entries) {
                var output = Path.Combine(
                    path1: Path.GetDirectoryName(path: sourcePath)!,
                    path2: $"{Path.GetFileNameWithoutExtension(path: sourcePath)}.{entry.Trim()}.dxbc"
                );

                if (
                    !File.Exists(path: output) ||
                    (File.GetLastWriteTimeUtc(path: output) < File.GetLastWriteTimeUtc(path: sourcePath))
                ) {
                    if (!Compile(
                        entry: entry.Trim(),
                        output: output,
                        source: bytes,
                        sourcePath: sourcePath
                    )) {
                        continue;
                    }
                }

                outputs.Add(item: new TaskItem(itemSpec: output));
            }
        }

        Bytecode = outputs.ToArray();

        return !Log.HasLoggedErrors;
    }

    private static IntPtr Slot(IntPtr instance, int slot) =>
        Marshal.ReadIntPtr(ptr: Marshal.ReadIntPtr(ptr: instance), ofs: (slot * IntPtr.Size));
    private static void Release(IntPtr blob) {
        if (blob != IntPtr.Zero) {
            // ID3DBlob: IUnknown's QueryInterface, AddRef, Release, then GetBufferPointer and GetBufferSize.
            Marshal.GetDelegateForFunctionPointer<ReleaseBlob>(ptr: Slot(instance: blob, slot: 2))(blob);
        }
    }
    private static byte[] Read(IntPtr blob) {
        var pointer = Marshal.GetDelegateForFunctionPointer<GetBufferPointer>(ptr: Slot(instance: blob, slot: 3))(blob);
        var size = Marshal.GetDelegateForFunctionPointer<GetBufferSize>(ptr: Slot(instance: blob, slot: 4))(blob);
        var bytes = new byte[checked((int)size.ToUInt64())];

        Marshal.Copy(source: pointer, destination: bytes, startIndex: 0, length: bytes.Length);

        return bytes;
    }
    private bool Compile(byte[] source, string sourcePath, string entry, string output) {
        var result = D3DCompile(
            code: out var code,
            defines: IntPtr.Zero,
            entryPoint: entry,
            errors: out var errors,
            flags1: 0,
            flags2: 0,
            include: IntPtr.Zero,
            sourceData: source,
            sourceDataSize: new UIntPtr(value: ((uint)source.Length)),
            sourceName: Path.GetFileName(path: sourcePath),
            target: "cs_5_0"
        );

        try {
            if (result < 0) {
                var message = ((errors == IntPtr.Zero)
                    ? $"HRESULT 0x{result:X8}"
                    : Encoding.UTF8.GetString(bytes: Read(blob: errors)).Trim());

                Log.LogError(message: $"Kernel '{sourcePath}' entry '{entry}' does not compile at cs_5_0: {message}");

                return false;
            }

            var temporary = (output + ".tmp");

            // The bytecode ships as content, so every output that ships it is a hard link to this file: deleting it and
            // moving the new bytes in replaces it, where a copy over it would rewrite every linked output in place. The
            // task compiles against a framework whose File.Move cannot overwrite.
            File.WriteAllBytes(path: temporary, bytes: Read(blob: code));
            File.Delete(path: output);
            File.Move(destFileName: output, sourceFileName: temporary);

            return true;
        } finally {
            Release(blob: code);
            Release(blob: errors);
        }
    }
}

/// <summary>
/// Removes every file under the World's deployed package store (<c>Assets/worlds/packages</c> in the output directory)
/// that this build no longer deploys, and then every directory the removal leaves empty.
/// </summary>
/// <remarks>
/// The store under the output directory is written only by the build: the tree run's report ships each package's files
/// there, and nothing in the source tree maps to it. A copy never deletes, so a package a source stops naming would stay
/// deployed and a World would still find it. The deployed set is every content item's target path, so a file the build
/// copies for any reason is kept.
/// </remarks>
public sealed class PuckRemoveStaleWorldPackages : Task {
    /// <summary>The project's output directory, which content target paths are relative to.</summary>
    [Required]
    public string OutputDirectory { get; set; } = string.Empty;
    /// <summary>The package store's path relative to <see cref="OutputDirectory"/>.</summary>
    [Required]
    public string PackageDirectory { get; set; } = string.Empty;
    /// <summary>Every content item this build deploys; each item's <c>TargetPath</c> metadata names its deployed
    /// path relative to <see cref="OutputDirectory"/>.</summary>
    public ITaskItem[] Deployed { get; set; } = Array.Empty<ITaskItem>();

    public override bool Execute() {
        var output = Path.GetFullPath(path: OutputDirectory);
        var store = Path.GetFullPath(path: Path.Combine(
            path1: output,
            path2: PackageDirectory
        ));

        if (!Directory.Exists(path: store)) {
            return true;
        }

        // Windows paths compare without case; the task also compiles for .NET Framework MSBuild, which lacks
        // OperatingSystem.
        var comparer = ((Path.DirectorySeparatorChar == '\\')
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal
        );
        var deployed = new HashSet<string>(comparer: comparer);

        foreach (var item in Deployed) {
            var targetPath = item.GetMetadata(metadataName: "TargetPath");

            if (targetPath.Length != 0) {
                deployed.Add(item: Path.GetFullPath(path: Path.Combine(
                    path1: output,
                    path2: targetPath
                )));
            }
        }

        foreach (var file in Directory.GetFiles(
            path: store,
            searchOption: SearchOption.AllDirectories,
            searchPattern: "*"
        )) {
            if (deployed.Contains(item: file)) {
                continue;
            }

            File.Delete(path: file);
            Log.LogMessage(
                importance: MessageImportance.High,
                message: $"Removed stale world package file '{file.Substring(startIndex: output.Length).TrimStart('\\', '/').Replace(newChar: '/', oldChar: '\\')}': this build no longer deploys it."
            );
        }

        RemoveEmptyDirectories(directory: store);

        return !Log.HasLoggedErrors;
    }

    // Removes every directory below this one that holds no file, deepest first; the store itself stays.
    private static void RemoveEmptyDirectories(string directory) {
        foreach (var child in Directory.GetDirectories(path: directory)) {
            RemoveEmptyDirectories(directory: child);

            if (Directory.GetFileSystemEntries(path: child).Length == 0) {
                Directory.Delete(path: child);
            }
        }
    }
}

/// <summary>
/// Hosts one project's shader build: runs <c>Puck.Shaders.Generator compile</c> or <c>check</c> over the project's
/// outputs and answers the compiler's core requests from MSBuild's own core budget (<see cref="IBuildEngine9"/>), so
/// every shader project of a parallel build shares the machine's cores instead of each starting its own number of
/// compilers. Everything else the build does, it does in the generator, through the one <c>ShaderCompiler</c>.
/// </summary>
/// <remarks>
/// The protocol is line-based. On standard output, <c>##puck-cores request &lt;n&gt;</c> asks for up to n more cores,
/// answered on standard input by <c>granted &lt;k&gt;</c>, and <c>##puck-cores release &lt;n&gt;</c> returns cores.
/// Only the first request reaches MSBuild: it is guaranteed not to wait for cores. Later requests are refused, since
/// MSBuild offers no way to cancel a blocked request and joining one can prevent the task from returning. The compiler
/// finishes on its initial grant, returning unused cores as work completes. Every other line is a message, and a line
/// in MSBuild's canonical error format is logged as an error. Cancellation asks the generator to join its compilers;
/// a generator that does not exit within ten seconds has its process tree killed.
/// </remarks>
public sealed class PuckShaderBuild : Task, ICancelableTask {
    private const string Prefix = "##puck-cores ";

    private readonly object m_gate = new object();

    private Process m_process;
    private volatile bool m_cancelled;
    private int m_held;
    private bool m_requested;

    /// <summary>The <c>dotnet</c> host that runs the generator.</summary>
    [Required]
    public string Host { get; set; } = "";
    /// <summary>The generator assembly.</summary>
    [Required]
    public string Tool { get; set; } = "";
    /// <summary><c>compile</c> or <c>check</c>.</summary>
    [Required]
    public string Mode { get; set; } = "";
    /// <summary>The project's directory.</summary>
    [Required]
    public string ProjectDirectory { get; set; } = "";
    /// <summary>The project's shader publication lock.</summary>
    [Required]
    public string LockFile { get; set; } = "";
    /// <summary>Where the request lines are written for the generator to read.</summary>
    [Required]
    public string RequestFile { get; set; } = "";
    /// <summary>Every output, with its <c>Stage</c>, <c>Backend</c> (a <c>ShaderTarget</c>) and <c>SourcePath</c> metadata.
    /// Empty when the project's last stage source was deleted, so the build still removes the bytecode it wrote.</summary>
    public ITaskItem[] Outputs { get; set; } = Array.Empty<ITaskItem>();
    /// <summary>The shader cache directory, or empty for the compiler's per-user default.</summary>
    public string CacheDirectory { get; set; } = "";
    /// <summary>The <c>dxc</c> command.</summary>
    public string DxcCommand { get; set; } = "";
    /// <summary>Where a compile writes every file it read once it succeeds, or empty for none.</summary>
    public string InputsFile { get; set; } = "";

    /// <inheritdoc/>
    public override bool Execute() {
        var requestPath = ((Path.GetFullPath(path: RequestFile) + ".") + Guid.NewGuid().ToString(format: "N"));

        try {
            return ExecuteBuild(requestPath: requestPath);
        } finally {
            try {
                StopCompiler();
                if (m_process is not null) { m_process.WaitForExit(); }
            } finally {
                try { ReleaseHeld(); } finally {
                    lock (m_gate) {
                        if (m_process is not null) { m_process.Dispose(); m_process = null; }
                    }
                    if (File.Exists(path: requestPath)) { File.Delete(path: requestPath); }
                }
            }
        }
    }

    private bool ExecuteBuild(string requestPath) {
        var request = new StringBuilder();

        foreach (var output in (Outputs ?? Array.Empty<ITaskItem>())) {
            request.Append(value: output.GetMetadata(metadataName: "Stage")).Append(value: '\t').Append(value: output.GetMetadata(metadataName: "Backend")).Append(value: '\t')
                .Append(value: Path.GetFullPath(path: output.GetMetadata(metadataName: "SourcePath"))).Append(value: '\t').Append(value: output.GetMetadata(metadataName: "FullPath")).Append(value: '\n');
        }
        Directory.CreateDirectory(path: Path.GetDirectoryName(path: requestPath));
        File.WriteAllText(requestPath, request.ToString());

        var start = new ProcessStartInfo(fileName: Host) {
            RedirectStandardError = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            StandardErrorEncoding = Encoding.UTF8,
            StandardOutputEncoding = Encoding.UTF8,
            UseShellExecute = false,
            WorkingDirectory = ProjectDirectory,
        };

        var arguments = new StringBuilder();

        var argumentList = new System.Collections.Generic.List<string> { Tool, Mode, "--project", ProjectDirectory, "--request", requestPath, "--lock", LockFile, "--cache", CacheDirectory, "--dxc", DxcCommand };

        if (!string.IsNullOrEmpty(value: InputsFile)) {
            argumentList.Add(item: "--inputs");
            argumentList.Add(item: InputsFile);
        }
        foreach (var argument in argumentList) {
            arguments.Append(value: ((arguments.Length == 0) ? "" : " ")).Append(value: Quote(argument: argument));
        }
        start.Arguments = arguments.ToString();

        lock (m_gate) {
            if (m_cancelled) {
                return false;
            }
            m_process = Process.Start(startInfo: start);
        }
        m_process.ErrorDataReceived += (_, received) => { if (received.Data is not null) { Log.LogMessageFromText(lineOfText: received.Data, messageImportance: MessageImportance.High); } };
        m_process.BeginErrorReadLine();

        string line;

        while ((line = m_process.StandardOutput.ReadLine()) is not null) {
            if (line.StartsWith(comparisonType: StringComparison.Ordinal, value: (Prefix + "request "))) {
                Grant(count: int.Parse(s: line.Substring(startIndex: (Prefix + "request ").Length)));
            } else if (line.StartsWith(comparisonType: StringComparison.Ordinal, value: (Prefix + "release "))) {
                Release(count: int.Parse(s: line.Substring(startIndex: (Prefix + "release ").Length)));
            } else {
                Log.LogMessageFromText(lineOfText: line, messageImportance: ((line.StartsWith(comparisonType: StringComparison.Ordinal, value: "Compiled ") || (line.IndexOf(comparisonType: StringComparison.Ordinal, value: " shader output(s): ") >= 0)) ? MessageImportance.High : MessageImportance.Normal));
            }
        }
        m_process.WaitForExit();

        // A generator that fails without a line in MSBuild's error format (a host that cannot start the tool, a crash)
        // still fails the build by name: a task never returns false without an error.
        if ((m_process.ExitCode != 0) && !Log.HasLoggedErrors && !m_cancelled) {
            Log.LogError(message: ("The shader build (" + Mode + ") exited with code " + m_process.ExitCode + " without reporting an error; its output is logged above. Tool: " + Tool));
        }

        return ((m_process.ExitCode == 0) && !Log.HasLoggedErrors && !m_cancelled);
    }
    private void Grant(int count) {
        var granted = 0;

        try {
            if (!m_requested && !m_cancelled && (count > 0)) {
                m_requested = true;
                if (BuildEngine is IBuildEngine9 engine) {
                    granted = engine.RequestCores(requestedCores: count);
                    lock (m_gate) { m_held += granted; }
                } else {
                    granted = 1;
                }
            }
        } catch (Exception exception) {
            Log.LogMessage(MessageImportance.Low, "Shader core request ended: {0}", exception.Message);
        }
        lock (m_gate) {
            try {
                m_process.StandardInput.WriteLine(value: ((granted > 0) ? ("granted " + granted) : "closed"));
                m_process.StandardInput.Flush();
            } catch (Exception exception) when (((exception is IOException) || (exception is ObjectDisposedException) || (exception is InvalidOperationException))) {
                // Execute releases the grant even if cancellation closed the transport before the answer arrived.
            }
        }
    }
    private void ReleaseHeld() {
        lock (m_gate) {
            if (m_held > 0) {
                ((IBuildEngine9)BuildEngine).ReleaseCores(coresToRelease: m_held);
                m_held = 0;
            }
        }
    }
    private void Release(int count) {
        lock (m_gate) {
            count = Math.Min(val1: count, val2: m_held);
            if (count <= 0) { return; }
            ((IBuildEngine9)BuildEngine).ReleaseCores(coresToRelease: count);
            m_held -= count;
        }
    }

    /// <summary>Cancels the generator, with a bounded process-tree kill fallback; <see cref="Execute"/> returns false.</summary>
    public void Cancel() {
        lock (m_gate) {
            m_cancelled = true;
        }
        StopCompiler();
    }

    // Asks a running generator to cancel and join its compilers, then kills its process tree if it has not exited within
    // ten seconds. The wait holds no lock: the reader thread must keep applying the releases the generator writes while
    // it unwinds.
    private void StopCompiler() {
        Process process;

        lock (m_gate) {
            process = m_process;
            if (process is null) { return; }
            try {
                if (process.HasExited) { return; }
                process.StandardInput.WriteLine(value: "cancel");
                process.StandardInput.Flush();
            } catch (Exception exception) when (((exception is IOException) || (exception is InvalidOperationException) || (exception is ObjectDisposedException))) { }
        }
        try {
            if (process.WaitForExit(milliseconds: 10000)) { return; }
            // Kill(bool) takes the compiler's own children with it; an engine on .NET Framework has only Kill().
            var tree = typeof(Process).GetMethod(name: "Kill", types: new[] { typeof(bool) });

            if (tree != null) { tree.Invoke(obj: process, parameters: new object[] { true }); } else { process.Kill(); }
        } catch (Exception exception) when (((exception is InvalidOperationException) || (exception is ObjectDisposedException) || (exception is System.ComponentModel.Win32Exception) || (exception is System.Reflection.TargetInvocationException))) { }
    }
    // One argument as the C runtime's command-line parser reads it back: quoted, with each run of backslashes before a
    // quote, or before the closing quote, doubled.
    private static string Quote(string argument) {
        var quoted = new StringBuilder(value: "\"");
        var backslashes = 0;

        foreach (var character in (argument ?? "")) {
            if (character == '\\') {
                backslashes++;
                continue;
            }
            quoted.Append(repeatCount: ((character == '"') ? ((backslashes * 2) + 1) : backslashes), value: '\\');
            backslashes = 0;
            quoted.Append(value: character);
        }

        return quoted.Append(repeatCount: (backslashes * 2), value: '\\').Append(value: '"').ToString();
    }
}
