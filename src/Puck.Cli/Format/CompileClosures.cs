using Microsoft.CodeAnalysis;

namespace Puck.Cli.Format;

// One format run's project closures. The semantic phases each ask for the same projects, and the answer cannot
// change while that run is rewriting source, so each project is evaluated once per configuration for the run. The
// next run evaluates afresh, because a build or clean in between changes both what a closure names and whether it is
// on disk. A run is single-threaded; concurrent runs each own their own instance.
internal sealed class CompileClosures {
    // Keyed by configuration and the project file's full path.
    private readonly Dictionary<(string Configuration, string Project), CompileClosure> m_evaluated = [];
    // A MetadataReference reads its assembly's metadata once and is safe to share between compilations, and sibling
    // projects overlap almost entirely in framework and package assemblies, so each path is read once for the run.
    private readonly Dictionary<string, MetadataReference> m_metadata = new(comparer: StringComparer.OrdinalIgnoreCase);

    // The closure's assemblies as compilation references.
    public IEnumerable<MetadataReference> MetadataReferences(CompileClosure closure) {
        foreach (var path in closure.References) {
            if (!m_metadata.TryGetValue(
                key: path,
                value: out var reference
            )) {
                reference = MetadataReference.CreateFromFile(path: path);
                m_metadata[path] = reference;
            }

            yield return reference;
        }
    }

    private static List<string> ProjectFiles(string projectRoot) =>
        Directory.EnumerateFiles(
            path: projectRoot,
            searchPattern: "*.csproj"
        ).Order(comparer: StringComparer.Ordinal).ToList();

    // Evaluates, in one MSBuild process, the closures of every project rooted in `projectRoots` that this run has not
    // yet evaluated for `configuration`, so projects sharing a reference graph evaluate it once between them. A project
    // the batch could not answer for is left to `Resolve`, which evaluates it alone and reports why.
    public void Prefetch(IEnumerable<string> projectRoots, string configuration) {
        var pending = projectRoots
            .Select(selector: static root => ProjectFiles(projectRoot: root))
            .Where(predicate: static projects => (projects.Count == 1))
            .Select(selector: static projects => Path.GetFullPath(path: projects[0]))
            .Where(predicate: project => !m_evaluated.ContainsKey(key: (configuration, project)))
            .Distinct(comparer: StringComparer.OrdinalIgnoreCase)
            .ToList();

        // One project gains nothing from a traversal, and `Resolve` evaluates it with its own diagnostics.
        if (pending.Count < 2) {
            return;
        }

        foreach (var (project, closure) in CompileClosure.EvaluateAll(
            configuration: configuration,
            projects: pending
        )) {
            m_evaluated[(configuration, project)] = closure;
        }
    }
    // The closure of the project rooted at `projectRoot`, for the requested configuration. A directory holding no
    // project file, or more than one, has no single closure to report.
    public CompileClosure Resolve(string projectRoot, string configuration) {
        var projects = ProjectFiles(projectRoot: projectRoot);

        if (projects.Count != 1) {
            return CompileClosure.Refuse(reason: ((projects.Count == 0)
                ? "it holds no project file"
                : $"it holds {projects.Count} project files"));
        }

        var key = (configuration, Path.GetFullPath(path: projects[0]));

        if (!m_evaluated.TryGetValue(
            key: key,
            value: out var closure
        )) {
            closure = CompileClosure.Evaluate(
                configuration: configuration,
                project: projects[0]
            );
            m_evaluated[key] = closure;
        }

        return closure;
    }
    // Records the closure a real build of `project` already reported for `configuration`, so the run does not evaluate
    // that project a second time.
    public void Record(string project, string configuration, CompileClosure closure) =>
        m_evaluated[(configuration, Path.GetFullPath(path: project))] = closure;
}
