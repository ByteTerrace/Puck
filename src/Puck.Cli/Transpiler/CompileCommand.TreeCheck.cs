using Puck.Shaders;
using Puck.World;

namespace Puck.Cli.Transpiler;

internal static partial class CompileCommand {
    // `--tree --check`: the tree run into a scratch directory, compared with --output instead of written to it. The
    // output holds exactly what a fresh run writes when every file the run writes is present with the same bytes and no
    // file of a kind the run owns (documents, compiled worlds, the bake pack, stored packages) is present that the run
    // did not write. A shipped catalog built from the same sources is therefore a proof that the tree compile is
    // deterministic across processes, and a catalog that disagrees names every file that does.
    private static int RunTreeCheck(string tree, string output, IReadOnlyList<string> paths, bool strict, bool validate, bool bundle) {
        var catalog = Path.GetFullPath(path: output);

        if (!Directory.Exists(path: catalog)) {
            Console.Error.WriteLine(value: $"error: '{catalog}' does not exist, so there is no output to check.");

            return 2;
        }

        var scratch = Directory.CreateTempSubdirectory(prefix: "puck-tree-check-").FullName;

        try {
            var fresh = Path.Combine(path1: scratch, path2: "output");
            var report = Path.Combine(path1: scratch, path2: "written");
            var ran = RunTree(
                bundle: bundle,
                output: fresh,
                paths: paths,
                report: report,
                strict: strict,
                tree: tree,
                validate: validate
            );

            if (ran != 0) {
                return ran;
            }

            var written = File.ReadAllLines(path: report).Where(predicate: static line => (line.Length > 0)).ToArray();
            var expected = new HashSet<string>(collection: written, comparer: StringComparer.Ordinal);
            var problems = new List<string>();

            foreach (var relative in written) {
                var shipped = Path.Combine(path1: catalog, path2: relative);

                if (!File.Exists(path: shipped)) {
                    problems.Add(item: $"missing {relative}: a fresh run writes it and '{catalog}' does not hold it.");
                } else if (!File.ReadAllBytes(path: shipped).AsSpan().SequenceEqual(other: File.ReadAllBytes(path: Path.Combine(path1: fresh, path2: relative)))) {
                    problems.Add(item: $"differs {relative}: its bytes are not what a fresh run writes.");
                }
            }

            var owned = ((string[])[WorldDocumentName.DocumentSuffix, CompiledWorld.Extension, WorldBakePack.Extension])
                .SelectMany(selector: suffix => Directory.EnumerateFiles(path: catalog, searchOption: SearchOption.AllDirectories, searchPattern: ("*" + suffix)))
                .Concat(second: (Directory.Exists(path: Path.Combine(path1: catalog, path2: ShaderPackager.StoreDirectoryName))
                    ? Directory.EnumerateFiles(path: Path.Combine(path1: catalog, path2: ShaderPackager.StoreDirectoryName), searchOption: SearchOption.AllDirectories, searchPattern: "*")
                    : []
                ))
                .Select(selector: file => Path.GetRelativePath(path: file, relativeTo: catalog).Replace(newChar: '/', oldChar: '\\'))
                .ToArray();

            foreach (var relative in owned.Where(predicate: relative => !expected.Contains(item: relative)).Order(comparer: StringComparer.Ordinal)) {
                problems.Add(item: $"stale {relative}: no source in the tree compiles to it.");
            }

            foreach (var group in owned.GroupBy(keySelector: static relative => relative, comparer: StringComparer.OrdinalIgnoreCase).Where(predicate: static group => (group.Count() > 1))) {
                problems.Add(item: $"repeated {group.Key}: the output holds it {group.Count()} times, ignoring case.");
            }

            if (problems.Count > 0) {
                foreach (var problem in problems) {
                    Console.Error.WriteLine(value: $"tree check: {problem}");
                }

                Console.Error.WriteLine(value: $"tree check: '{catalog}' is not what a fresh run of '{Path.GetFullPath(path: tree)}' writes ({problems.Count} problem(s)).");

                return 1;
            }

            Console.WriteLine(value: $"tree check: '{catalog}' holds exactly the {written.Length:N0} files a fresh run of '{Path.GetFullPath(path: tree)}' writes.");

            return 0;
        } finally {
            try {
                Directory.Delete(path: scratch, recursive: true);
            } catch (Exception exception) when ((exception is IOException or UnauthorizedAccessException)) {
                Console.Error.WriteLine(value: $"tree check: the scratch directory '{scratch}' could not be removed: {exception.Message}");
            }
        }
    }
}
