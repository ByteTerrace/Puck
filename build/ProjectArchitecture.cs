#nullable disable
#pragma warning disable IDE0005 // MSBuild compiles this source without implicit usings.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml;
using System.Xml.Linq;

namespace Puck.Cli.Architecture;
// Shared source: MSBuild loads a facade-targeted task before any Puck project can build. The CLI targets the
// current runtime. Keeping facts and rules here avoids a bootstrap project dependency and two implementations.
public sealed class ArchitectureEdge {
    public string DeclaredIn { get; set; } = "";
    public int Line { get; set; }
    public string Name { get; set; } = "";
    public string ProjectPath { get; set; } = "";
    public bool OutputAssembly { get; set; }
}

public sealed class ArchitectureProject {
    public string File { get; set; } = "";
    public string Kind { get; set; } = "";
    public string Layer { get; set; } = "";
    public string Name { get; set; } = "";
    public string Provider { get; set; } = "";
    public IReadOnlyList<ArchitectureEdge> DeclaredEdges { get; set; } = Array.Empty<ArchitectureEdge>();
    public IReadOnlyList<string> Edges => DeclaredEdges.Where(e => e.OutputAssembly).Select(e => e.Name).ToArray();
    public IReadOnlyList<string> References => DeclaredEdges.Select(e => e.Name).ToArray();
    public IReadOnlyList<string> Assemblies { get; set; } = Array.Empty<string>();
}

public sealed class ArchitectureProfile {
    public IReadOnlyList<string> Forbid { get; set; } = Array.Empty<string>();
    public IReadOnlyList<string> Allow { get; set; } = Array.Empty<string>();
    public IReadOnlyList<string> Excluded(IEnumerable<string> closure, Func<string, string> layerOf) => closure.Where(name =>
        Forbid.Any(entry => ArchitectureModel.Equal(entry, name) || ArchitectureModel.Equal(entry, layerOf(name))) ||
        (Allow.Count > 0 && !Allow.Contains(name, StringComparer.OrdinalIgnoreCase))).ToArray();
}

public sealed class ArchitectureViolation {
    public string Code { get; }
    public string Message { get; }
    public string Target { get; }
    public ArchitectureViolation(string code, string message, string target = "") { Code = code; Message = message; Target = target; }
}

/// <summary>The transitive project walk, including seeds, for both assembly and build-order graphs.</summary>
public static class ArchitectureGraph {
    public static HashSet<string> Reach(IEnumerable<string> seeds, Func<string, IEnumerable<string>> references) {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var pending = new Stack<string>(seeds);
        while (pending.Count > 0) {
            var name = pending.Pop();
            if (!seen.Add(name)) { continue; }
            foreach (var reference in references(name)) { pending.Push(reference); }
        }
        return seen;
    }
}

/// <summary>One parser and policy evaluator over declared CLI facts or MSBuild's resolved closure.</summary>
public sealed class ArchitectureModel {
    private readonly Dictionary<string, ArchitectureProject> m_referents = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, bool> Kinds { get; } = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, int> Ranks { get; } = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
    public IReadOnlyList<string> Layers => Ranks.OrderBy(p => p.Value).Select(p => p.Key).ToArray();
    public Dictionary<string, ArchitectureProfile> Profiles { get; } = new Dictionary<string, ArchitectureProfile>(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, string[]> Providers { get; } = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, ArchitectureProject> Projects { get; } = new Dictionary<string, ArchitectureProject>(StringComparer.OrdinalIgnoreCase);
    public string RepositoryRoot { get; private set; } = "";

    public static bool Equal(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
    public static string[] Split(string value) => (value ?? "").Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries).Select(s => s.Trim()).Where(s => s.Length > 0).ToArray();
    private static string Property(XDocument document, string name) => document.Descendants().FirstOrDefault(e => e.Name.LocalName == name)?.Value.Trim() ?? "";

    public static ArchitectureProject ReadProject(string file) {
        var project = new ArchitectureProject { File = file, Name = Path.GetFileNameWithoutExtension(file) };
        if (!System.IO.File.Exists(file)) { return project; }
        var document = XDocument.Load(file, LoadOptions.SetLineInfo);
        project.Kind = Property(document, "PuckKind");
        project.Layer = Property(document, "PuckLayer");
        project.Provider = Property(document, "PuckProvider");
        project.DeclaredEdges = document.Descendants().Where(e => e.Name.LocalName == "ProjectReference")
            .Where(e => (e.Attribute("Include") ?? e.Attribute("Update")) != null).Select(e => {
                var include = (e.Attribute("Include") ?? e.Attribute("Update")).Value.Replace('\\', '/');
                var resolved = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(file) ?? ".", include));
                var output = e.Attribute("ReferenceOutputAssembly")?.Value ?? e.Elements().FirstOrDefault(c => c.Name.LocalName == "ReferenceOutputAssembly")?.Value;
                var line = (IXmlLineInfo)e;
                return new ArchitectureEdge { DeclaredIn = file, Line = line.HasLineInfo() ? line.LineNumber : 0,
                    Name = Path.GetFileNameWithoutExtension(resolved), ProjectPath = resolved, OutputAssembly = !Equal(output, "false") };
            }).OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase).ToArray();
        project.Assemblies = document.Descendants().Where(e => e.Name.LocalName == "PackageReference" || e.Name.LocalName == "Reference")
            .Select(e => (e.Attribute("Include") ?? e.Attribute("Update"))?.Value).Where(s => !string.IsNullOrEmpty(s)).ToArray();
        return project;
    }

    public static ArchitectureModel Load(string repositoryRoot) {
        var model = new ArchitectureModel { RepositoryRoot = repositoryRoot };
        var policy = XDocument.Load(Path.Combine(repositoryRoot, "build/Architecture.props"));
        foreach (var item in policy.Descendants()) {
            var name = item.Attribute("Include")?.Value;
            if (name == null) { continue; }
            switch (item.Name.LocalName) {
                case "PuckArchitectureKind": model.Kinds[name] = Equal(item.Attribute("Ranked")?.Value, "true"); break;
                case "PuckArchitectureLayer": model.Ranks[name] = int.Parse(item.Attribute("Rank").Value); break;
                case "PuckArchitectureProfile": model.Profiles[name] = new ArchitectureProfile { Forbid = Split(item.Attribute("Forbid")?.Value), Allow = Split(item.Attribute("Allow")?.Value) }; break;
                case "PuckArchitectureProviderAssemblies": model.Providers[name] = Split(item.Attribute("Assemblies")?.Value); break;
            }
        }
        foreach (var root in new[] { "src", "tests" }) {
            var directory = Path.Combine(repositoryRoot, root);
            if (!Directory.Exists(directory)) { continue; }
            foreach (var file in Directory.EnumerateFiles(directory, "*.csproj", SearchOption.AllDirectories)) {
                var relative = file.Substring(repositoryRoot.Length).Replace('\\', '/');
                if (relative.IndexOf("/bin/", StringComparison.OrdinalIgnoreCase) >= 0 || relative.IndexOf("/obj/", StringComparison.OrdinalIgnoreCase) >= 0 || relative.StartsWith("/src/Puck.Azure.Functions/", StringComparison.OrdinalIgnoreCase)) { continue; }
                var project = ReadProject(file);
                model.Projects[project.Name] = project;
            }
        }
        var pending = new Queue<ArchitectureProject>(model.Projects.Values);
        while (pending.Count > 0) {
            foreach (var edge in pending.Dequeue().DeclaredEdges) {
                if (model.Projects.ContainsKey(edge.Name) || model.m_referents.ContainsKey(edge.Name)) { continue; }
                var reference = ReadProject(edge.ProjectPath);
                model.m_referents[edge.Name] = reference;
                pending.Enqueue(reference);
            }
        }
        return model;
    }

    // Referenced projects outside report scope still contribute their real declarations and out-edges, just as
    // their assemblies do in the resolved build closure. Missing declarations remain PUCKARCH007 findings.
    public ArchitectureProject Project(string name) => Projects.TryGetValue(name, out var project) ? project :
        (m_referents.TryGetValue(name, out project) ? project : new ArchitectureProject { Name = name });

    public IReadOnlyList<string> Closure(string name) {
        var reached = ArchitectureGraph.Reach(new[] { name }, member => Project(member).Edges);
        reached.Remove(name);
        return reached.OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public int RankOf(ArchitectureProject project) => Kinds.TryGetValue(project.Kind, out var ranked) && !ranked ? int.MinValue : (Ranks.TryGetValue(project.Layer, out var rank) ? rank : int.MaxValue);

    public string BackendPermission(ArchitectureProject project, IReadOnlyList<ArchitectureProject> closure, IEnumerable<string> direct) {
        var backends = closure.Where(p => Equal(p.Layer, "Backends")).ToArray();
        if (backends.Length == 0) { return "permitted: no backend"; }
        if (Equal(project.Layer, "Presentation") || Equal(project.Layer, "Composition roots")) { return "permitted: " + project.Layer; }
        if (Equal(project.Kind, "Test") && backends.All(b => Equal(project.Name, b.Name + ".Tests"))) { return "permitted: the backend's own test project"; }
        if (Kinds.TryGetValue(project.Kind, out var ranked) && !ranked && !backends.Any(b => direct.Contains(b.Name, StringComparer.OrdinalIgnoreCase))) { return "permitted: terminal kind (" + project.Kind + "), inherits and introduces nothing"; }
        return "NOT PERMITTED";
    }

    public IReadOnlyList<ArchitectureViolation> Check(ArchitectureProject project, IReadOnlyList<ArchitectureProject> closure, IEnumerable<string> direct, IEnumerable<string> assemblies) {
        var failures = new List<ArchitectureViolation>();
        var description = project.Name + " (" + (project.Layer.Length == 0 ? project.Kind : project.Layer) + ")";
        if (!Kinds.TryGetValue(project.Kind, out var ranked)) { failures.Add(new ArchitectureViolation("PUCKARCH005", project.Name + " declares no valid <PuckKind> (found '" + project.Kind + "').")); }
        else if (ranked && !Ranks.ContainsKey(project.Layer)) { failures.Add(new ArchitectureViolation("PUCKARCH005", project.Name + " is ranked and declares no valid <PuckLayer> (found '" + project.Layer + "').")); }
        else if (!ranked && project.Layer.Length > 0) { failures.Add(new ArchitectureViolation("PUCKARCH005", project.Name + " is terminal and must not declare <PuckLayer>.")); }
        else {
            foreach (var reference in closure) {
                if (string.IsNullOrEmpty(reference.Kind)) { failures.Add(new ArchitectureViolation("PUCKARCH007", description + " holds undeclared project '" + reference.Name + "'. Bring it into scope and declare <PuckKind>, or remove the reference.", reference.Name)); }
                else if (ranked && Kinds.TryGetValue(reference.Kind, out var referenceRanked) && !referenceRanked) { failures.Add(new ArchitectureViolation("PUCKARCH003", description + " holds terminal-kind project '" + reference.Name + "'. Invert the dependency or correct its declaration.", reference.Name)); }
                else if (RankOf(reference) < RankOf(project)) { failures.Add(new ArchitectureViolation("PUCKARCH001", description + " holds '" + reference.Name + "' (" + reference.Layer + ") — upward edge.", reference.Name)); }
            }
            if (BackendPermission(project, closure, direct) == "NOT PERMITTED") { failures.Add(new ArchitectureViolation("PUCKARCH002", description + " holds a backend outside Presentation, composition roots or that backend's own tests. Terminal consumers may inherit but never introduce a backend.")); }
        }
        if (Profiles.TryGetValue(project.Name, out var profile)) {
            var excluded = profile.Excluded(closure.Select(p => p.Name), name => closure.First(p => Equal(p.Name, name)).Layer);
            if (excluded.Count > 0) { failures.Add(new ArchitectureViolation("PUCKARCH004", project.Name + " closure holds what its profile excludes: " + string.Join(", ", excluded) + ".")); }
        }
        if (!Equal(project.Kind, "Test")) {
            foreach (var reference in closure.Where(p => p.Provider.Length > 0 && !Equal(p.Provider, project.Provider))) { failures.Add(new ArchitectureViolation("PUCKARCH009", description + " holds provider project '" + reference.Name + "' (" + reference.Provider + "). Select an installed contribution through Puck's abstraction, or move provider code into its declared provider project.", reference.Name)); }
            foreach (var provider in Providers.Where(p => !Equal(p.Key, project.Provider))) {
                var held = assemblies.Where(name => provider.Value.Any(prefix => name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToArray();
                if (held.Length > 0) { failures.Add(new ArchitectureViolation("PUCKARCH009", description + " holds " + provider.Key + " SDK references: " + string.Join(", ", held) + ". Only a project declaring that <PuckProvider> may.")); }
            }
        }
        return failures;
    }
}
