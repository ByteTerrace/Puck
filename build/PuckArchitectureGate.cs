using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml;
using System.Xml.Linq;

using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;

/// <summary>
/// The architecture gate. Runs in every in-scope project's build, immediately before <c>CoreCompile</c>,
/// against the RESOLVED reference set — so an edge that arrives transitively is checked exactly like a
/// declared one. Policy comes from <c>build/Architecture.props</c> as evaluated MSBuild items; per-project
/// layer and kind declarations are read from the csproj files themselves.
/// </summary>
/// <remarks>
/// <para>The hook point is load-bearing and was chosen by measurement, not by reading. The obvious hook —
/// <c>AfterTargets="ResolveReferences"</c> — is WRONG: <c>@(ReferencePathWithRefAssemblies)</c> is empty
/// there (measured: count=0 on every project), because it is populated later by
/// <c>FindReferenceAssembliesForReferences</c> on the way into the compile. A gate hooked there would
/// examine nothing and pass everything, forever, and nobody would find out by running it — running it is
/// exactly what would not complain. <c>BeforeTargets="CoreCompile"</c> is populated (measured: 201 items on
/// Puck.Launcher, four of them Puck projects) and still fires on a fully up-to-date incremental build.</para>
/// <para>This task is compiled by <c>RoslynCodeTaskFactory</c>, whose host resolves
/// <c>System.Xml.Linq</c> but NOT <c>System.Text.Json</c>. That is why the ledger is an MSBuild props file.
/// It also means this file is outside the repository's analyzer and nullable context — keep it plain.</para>
/// </remarks>
public sealed class PuckArchitectureGate : Task {
    private const string BackendsLayer = "Backends";
    private const string CompositionRootsLayer = "Composition roots";
    private const string PresentationLayer = "Presentation";

    private readonly Dictionary<string, ProjectFacts> m_facts = new Dictionary<string, ProjectFacts>(comparer: StringComparer.OrdinalIgnoreCase);

    /// <summary>Named backend-quarantine exceptions: <c>Include</c> is the project, <c>Reason</c> is why.</summary>
    public ITaskItem[] BackendExceptions { get; set; } = Array.Empty<ITaskItem>();

    /// <summary>
    /// This project's <c>@(ProjectReference)</c> as MSBUILD evaluated it — which is what makes it usable
    /// where reading the csproj text is not.
    /// </summary>
    /// <remarks>
    /// The "does this project introduce a backend itself" test used to read <c>&lt;ProjectReference&gt;</c>
    /// elements out of the csproj. An <c>&lt;Import&gt;</c> carrying a ProjectReference is invisible to that
    /// reading and visible to this one, and the unit-1 review proved the gap by importing a reference to a
    /// backend into a terminal-kind project, which built green and shipped the backend assembly. Semantics
    /// come from MSBuild; the csproj text is read only for PROVENANCE, where being incomplete degrades a
    /// message rather than a verdict.
    /// </remarks>
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
            ok &= CheckEdge(kinds: kinds, ranks: ranks, ownRank: ownRank, reference: reference);
        }

        ok &= CheckBackendQuarantine(closure: closure, kinds: kinds);
        ok &= CheckProfile(closure: closure);

        return ok;
    }

    // ---- rules -------------------------------------------------------------------------------------

    private bool CheckBackendQuarantine(List<ProjectFacts> closure, Dictionary<string, bool> kinds) {
        var backends = closure.Where(predicate: f => f.Layer == BackendsLayer).ToArray();

        if (backends.Length == 0) {
            return true;
        }

        if ((Layer == PresentationLayer) || (Layer == CompositionRootsLayer)) {
            return true;
        }

        var exception = BackendExceptions.FirstOrDefault(predicate: e => string.Equals(a: e.ItemSpec, b: ProjectName, comparisonType: StringComparison.OrdinalIgnoreCase));

        if (exception != null) {
            return true;
        }

        // Clause (b): a TERMINAL consumer inherits the closure of whatever it composes and never introduces
        // a backend. Safe by composition rather than by trust — every ranked project in this closure has
        // passed this same gate in its own build, so a backend arriving through one arrived legally. What is
        // checked here is only that this project does not NAME a backend itself.
        var terminal = kinds.TryGetValue(key: Kind, value: out var ranked) && !ranked;
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
                    $"{Describe()} holds the Backends-row assembly '{backend.Name}' in its resolved closure. Only the Presentation row and composition roots may. "
                    + $"Arrives by: {path}. "
                    + "The .Presentation wrapper row exists precisely so engine code never names a backend. If this edge is genuinely right, the fix is a "
                    + "PuckArchitectureBackendException in build/Architecture.props carrying the reason it is right — never a widening of the rule, which would "
                    + "silently un-check every project at once.");
        }

        return false;
    }

    private bool CheckEdge(Dictionary<string, bool> kinds, Dictionary<string, int> ranks, int ownRank, ProjectFacts reference) {
        var ownRanked = !kinds.TryGetValue(key: Kind, value: out var thisRanked) || thisRanked;

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
                    $"{Describe()} holds '{reference.Name}' in its resolved closure, and that project declares no <PuckKind> — it is outside the gate's scope "
                    + $"(its project file is '{reference.ProjectPath}'). "
                    + $"Arrives by: {DescribePath(target: reference.Name)}. "
                    + "An undeclared project is not a project at the bottom of the layering; it is a project the rules have never been applied to, and depending on one "
                    + "imports exactly the freedom it was excluded to have. If it belongs in the graph, bring it into scope and declare it; if it belongs outside, nothing "
                    + "inside may reference it.");

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
                    $"{Describe()} holds '{reference.Name}' in its resolved closure, and that project's kind is {reference.Kind} — a TERMINAL kind, which consumes the tree and is never consumed by it. "
                    + $"Arrives by: {DescribePath(target: reference.Name)}. "
                    + "Either the dependency is inverted, or the referenced project is misclassified and its <PuckKind> should be a ranked kind with a <PuckLayer> to match.");

            return false;
        }

        var referenceRank = RankOf(kinds: kinds, ranks: ranks, kind: reference.Kind, layer: reference.Layer);

        if (referenceRank >= ownRank) {
            return true;
        }

        LogViolation(
            code: "PUCKARCH001",
            message:
                $"{Describe()} holds '{reference.Name}' ({reference.Layer}) in its resolved closure — an UPWARD edge. Dependencies point downward or sideways, never up. "
                + $"Arrives by: {DescribePath(target: reference.Name)}. "
                + "Note this is the RESOLVED closure, not the declared one: an edge that arrives transitively is the same architectural fact as one written in this csproj, and reads "
                + "differently only to a human reading project files.");

        return false;
    }

    private bool CheckProfile(List<ProjectFacts> closure) {
        var profile = Profiles.FirstOrDefault(predicate: p => string.Equals(a: p.ItemSpec, b: ProjectName, comparisonType: StringComparison.OrdinalIgnoreCase));

        if (profile == null) {
            return true;
        }

        var expected = new SortedSet<string>(
            collection: (profile.GetMetadata(metadataName: "Closure") ?? "").Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries).Select(selector: s => s.Trim()),
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
            detail.Add(item: $"gained {string.Join(separator: ", ", values: (IEnumerable<string>)added)}");
        }

        if (removed.Length != 0) {
            detail.Add(item: $"lost {string.Join(separator: ", ", values: (IEnumerable<string>)removed)}");
        }

        LogViolation(
            code: "PUCKARCH004",
            message:
                $"{ProjectName} carries a lane profile, and its resolved closure no longer EQUALS it: {string.Join(separator: "; ", values: detail)}. "
                + $"Expected exactly [{string.Join(separator: ", ", values: expected)}]. "
                + "Equality is deliberate — a removed edge has to be as visible as an added one, or a boundary can be dismantled a reference at a time and every check still passes. "
                + "Every forbidden edge in this split is SAME-ROW, so the layer rule cannot see any of them; this profile is the only thing that can. "
                + "If the change is right, update the profile in build/Architecture.props in the same commit and say why.");

        return false;
    }

    private bool TryValidateOwnDeclaration(Dictionary<string, bool> kinds, Dictionary<string, int> ranks) {
        if (string.IsNullOrEmpty(value: Kind)) {
            LogViolation(
                code: "PUCKARCH005",
                message:
                    $"{ProjectName} declares no <PuckKind>. Every project in the gate's scope declares one, so a new project is classified when it is created rather than whenever someone notices. "
                    + $"Ranked kinds ({string.Join(separator: ", ", values: kinds.Where(predicate: k => k.Value).Select(selector: k => k.Key))}) also declare a <PuckLayer>; "
                    + $"terminal kinds ({string.Join(separator: ", ", values: kinds.Where(predicate: k => !k.Value).Select(selector: k => k.Key))}) must not.");

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

    // ---- reading -----------------------------------------------------------------------------------

    /// <summary>
    /// The names this project references directly, taken from MSBuild's EVALUATED <c>@(ProjectReference)</c>
    /// rather than from the csproj's text — so an edge contributed by an <c>&lt;Import&gt;</c> counts, which
    /// is the gap the unit-1 review used to walk a backend into a terminal-kind project.
    /// </summary>
    private HashSet<string> EvaluatedReferenceNames() {
        var names = new HashSet<string>(comparer: StringComparer.OrdinalIgnoreCase);

        foreach (var reference in DeclaredReferences) {
            _ = names.Add(item: Path.GetFileNameWithoutExtension(path: reference.ItemSpec));
        }

        return names;
    }

    private static List<Edge> DeclaredEdges(string projectFile) {
        var edges = new List<Edge>();

        if (!File.Exists(path: projectFile)) {
            return edges;
        }

        var document = XDocument.Load(uri: projectFile, options: LoadOptions.SetLineInfo);

        foreach (var element in document.Descendants().Where(predicate: e => e.Name.LocalName == "ProjectReference")) {
            // Update= counts as a declaration for provenance. An edge whose Include lives in
            // Directory.Build.props and whose metadata is amended here (Puck.Analyzers.Tests does exactly
            // this, turning the repo-wide analyzer extension into an ordinary assembly reference) is one a
            // reader has to come HERE to change, which is the only thing this walk is for.
            var include = element.Attribute(name: "Include") ?? element.Attribute(name: "Update");

            if (include == null) {
                continue;
            }

            // The analyzer edge is not an assembly reference — ReferenceOutputAssembly="false" keeps it out
            // of every resolved reference set — so it is not an architectural edge either.
            var outputAssembly =
                element.Attribute(name: "ReferenceOutputAssembly")?.Value
                ?? element.Elements().FirstOrDefault(predicate: e => e.Name.LocalName == "ReferenceOutputAssembly")?.Value;

            if (string.Equals(a: outputAssembly, b: "false", comparisonType: StringComparison.OrdinalIgnoreCase)) {
                continue;
            }

            var relative = include.Value.Replace(oldChar: '\\', newChar: Path.DirectorySeparatorChar);
            var resolved = Path.GetFullPath(path: Path.Combine(path1: Path.GetDirectoryName(path: projectFile) ?? ".", path2: relative));
            var lineInfo = (IXmlLineInfo)element;

            edges.Add(
                item: new Edge {
                    DeclaredIn = projectFile,
                    Line = lineInfo.HasLineInfo() ? lineInfo.LineNumber : 0,
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
                if (!identity.StartsWith(value: "Puck.", comparisonType: StringComparison.OrdinalIgnoreCase) || !seen.Add(item: identity)) {
                    continue;
                }

                var route = reference.GetMetadata(metadataName: "ReferenceSourceTarget");

                LogViolation(
                    code: "PUCKARCH006",
                    message:
                        $"{Describe()} resolves the Puck assembly '{identity}' by a route that is not a project reference"
                        + (string.IsNullOrEmpty(value: route) ? " (a raw <Reference>, or a package)" : $" (ReferenceSourceTarget='{route}')")
                        + $", from '{reference.ItemSpec}'. "
                        + "There is no csproj line to name here, because nothing declared it as a project edge — that absence IS the finding. "
                        + "Every architecture rule below this one reads the project graph, so an assembly reached around the project system is "
                        + "invisible to all of them at once: it defeats the lane profiles, which are the only tier doing security work, and it "
                        + "carries a backend past the quarantine from any row. Reference the project.");

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

            facts.Kind = document.Descendants().FirstOrDefault(predicate: e => e.Name.LocalName == "PuckKind")?.Value.Trim() ?? "";
            facts.Layer = document.Descendants().FirstOrDefault(predicate: e => e.Name.LocalName == "PuckLayer")?.Value.Trim() ?? "";
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
            ranks[layer.ItemSpec] = int.TryParse(s: layer.GetMetadata(metadataName: "Rank"), result: out var rank) ? rank : int.MaxValue;
        }

        return ranks;
    }

    private static int RankOf(Dictionary<string, bool> kinds, Dictionary<string, int> ranks, string kind, string layer) {
        // A terminal kind sits above every layer by construction, so everything it references is below it
        // and the rank rule needs no special case for it.
        if (kinds.TryGetValue(key: kind, value: out var ranked) && !ranked) {
            return -1;
        }

        return ranks.TryGetValue(key: layer, value: out var rank) ? rank : int.MaxValue;
    }

    // ---- provenance --------------------------------------------------------------------------------

    /// <summary>
    /// The shortest DECLARED path from this project to <paramref name="target"/>, each hop carrying the
    /// csproj and line that declares it. The resolved set proves an edge EXISTS; only this says where
    /// someone has to go to change it, which is the half of a diagnostic that costs nothing to act on.
    /// </summary>
    private string DescribePath(string target) {
        var queue = new Queue<List<Edge>>();
        var visited = new HashSet<string>(comparer: StringComparer.OrdinalIgnoreCase) { ProjectName };

        foreach (var edge in DeclaredEdges(projectFile: ProjectFile)) {
            queue.Enqueue(item: new List<Edge> { edge });
        }

        while (queue.Count != 0) {
            var path = queue.Dequeue();
            var last = path[path.Count - 1];

            if (string.Equals(a: last.Name, b: target, comparisonType: StringComparison.OrdinalIgnoreCase)) {
                return ProjectName + " -> " + string.Join(separator: " -> ", values: path.Select(selector: e => $"{e.Name} ({Path.GetFileName(path: e.DeclaredIn)}:{e.Line})"));
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

    // ---- diagnostics -------------------------------------------------------------------------------

    private string Describe() {
        return string.IsNullOrEmpty(value: Layer) ? $"{ProjectName} ({Kind})" : $"{ProjectName} ({Layer})";
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

    // ---- shapes ------------------------------------------------------------------------------------

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
