using System.CommandLine;
using System.Text.Json;
using Puck.Abstractions;
using Puck.Assets;

namespace Puck.Cli.Branding;

// `puck branding` is the report-and-sync surface for branding/manifest.json. The default writes every
// non-deferred copy from its canonical source; --check is read-only and reports source, copy, and wiring drift.
// All sources, hashes, destinations, and wiring are preflighted before the default mode writes anything.
// Exit 0 wrote/matched, 1 found a branding problem, 2 could not locate the repository root.
internal static class BrandingCommand {
    private const string ManifestRelativePath = "branding/manifest.json";
    private const string ManifestSchema = "puck.branding.v1";

    private sealed record Asset(string Id, string Source, string Hash);
    private sealed record Copy(string AssetId, string Path, bool Deferred);
    private sealed record Wiring(string Path, string Contains, string Description);
    private sealed record CopyPlan(Asset Asset, Copy Copy, string SourcePath, string DestinationPath, bool NeedsSync);

    internal static int Run(string repositoryRoot, bool check) {
        var problems = new List<string>();
        var manifestPath = Path.Combine(
            path1: repositoryRoot,
            path2: ManifestRelativePath.Replace(
                newChar: Path.DirectorySeparatorChar,
                oldChar: '/'
            )
        );

        if (!File.Exists(path: manifestPath)) {
            problems.Add(item: $"missing manifest: {ManifestRelativePath}");
            return Report(
                deferredCopies: 0,
                problems: problems
            );
        }

        JsonDocument document;

        try {
            document = JsonDocument.Parse(json: File.ReadAllText(path: manifestPath));
        } catch (JsonException exception) {
            problems.Add(item: $"manifest is invalid JSON: {exception.Message}");
            return Report(
                deferredCopies: 0,
                problems: problems
            );
        } catch (Exception exception) when ((exception is IOException or UnauthorizedAccessException)) {
            problems.Add(item: $"manifest could not be read: {exception.Message}");
            return Report(
                deferredCopies: 0,
                problems: problems
            );
        }

        using (document) {
            var root = document.RootElement;

            if (
                (root.ValueKind != JsonValueKind.Object) ||
                !HasString(
                expected: ManifestSchema,
                property: "schema",
                root: root
            )
            ) {
                problems.Add(item: $"manifest schema must be {ManifestSchema}");
                return Report(
                    deferredCopies: 0,
                    problems: problems
                );
            }

            var assets = LoadAssets(
                problems: problems,
                root: root
            );
            var copies = LoadCopies(
                problems: problems,
                root: root
            );
            var wirings = LoadWirings(
                problems: problems,
                root: root
            );
            var deferredCopies = copies.Count(predicate: static copy => copy.Deferred);

            var assetById = assets.ToDictionary(
                keySelector: static asset => asset.Id,
                comparer: StringComparer.Ordinal
            );
            var canonicalPaths = new HashSet<string>(comparer: PuckPaths.Comparer);
            var canonicalFiles = new Dictionary<string, string>(comparer: StringComparer.Ordinal);

            foreach (var asset in assets) {
                if (!TryResolveRepositoryPath(
                    repositoryRoot: repositoryRoot,
                    relativePath: asset.Source,
                    kind: "canonical asset",
                    fullPath: out var sourcePath,
                    problem: out var problem
                )) {
                    problems.Add(item: problem!);
                    continue;
                }

                if (!canonicalPaths.Add(item: sourcePath!)) {
                    problems.Add(item: $"duplicate canonical source: {asset.Source}");
                }

                canonicalFiles[asset.Id] = sourcePath!;
                if (!File.Exists(path: sourcePath!)) {
                    problems.Add(item: $"missing canonical asset: {asset.Source}");
                    continue;
                }

                if (!TryHashFile(
                    hash: out var actualHash,
                    path: sourcePath!,
                    problem: out var hashProblem
                )) {
                    problems.Add(item: hashProblem!);
                    continue;
                }
                if (!string.Equals(
                    a: actualHash,
                    b: asset.Hash,
                    comparisonType: StringComparison.OrdinalIgnoreCase
                )) {
                    problems.Add(item: $"canonical hash drift: {asset.Source} (expected {asset.Hash}, found {actualHash})");
                }
            }

            foreach (var wiring in wirings) {
                if (!TryResolveRepositoryPath(
                    repositoryRoot: repositoryRoot,
                    relativePath: wiring.Path,
                    kind: "wiring file",
                    fullPath: out var wiringPath,
                    problem: out var problem
                )) {
                    problems.Add(item: problem!);
                    continue;
                }

                if (!File.Exists(path: wiringPath!)) {
                    problems.Add(item: $"missing wiring file: {wiring.Path}");
                    continue;
                }

                string wiringText;

                try {
                    wiringText = File.ReadAllText(path: wiringPath!);
                } catch (Exception exception) when ((exception is IOException or UnauthorizedAccessException)) {
                    problems.Add(item: $"wiring file could not be read: {wiring.Path} ({exception.Message})");
                    continue;
                }

                if (!wiringText.Contains(
                    value: wiring.Contains,
                    comparisonType: StringComparison.Ordinal
                )) {
                    problems.Add(item: $"missing required branding wiring: {wiring.Path} ({wiring.Description})");
                }
            }

            var destinationPaths = new HashSet<string>(comparer: PuckPaths.Comparer);
            var plans = new List<CopyPlan>();

            foreach (var copy in copies) {
                if (!assetById.TryGetValue(
                    key: copy.AssetId,
                    value: out var asset
                )) {
                    problems.Add(item: $"copy names unknown asset: {copy.AssetId}");
                    continue;
                }

                if (!canonicalFiles.TryGetValue(
                    key: copy.AssetId,
                    value: out var sourcePath
                )) {
                    continue;
                }

                if (!TryResolveRepositoryPath(
                    repositoryRoot: repositoryRoot,
                    relativePath: copy.Path,
                    kind: "distributed copy",
                    fullPath: out var destinationPath,
                    problem: out var problem
                )) {
                    problems.Add(item: problem!);
                    continue;
                }

                if (!destinationPaths.Add(item: destinationPath!)) {
                    problems.Add(item: $"duplicate distributed copy destination: {copy.Path}");
                    continue;
                }

                if (canonicalPaths.Contains(item: destinationPath!)) {
                    problems.Add(item: $"distributed copy destination overlaps a canonical source: {copy.Path}");
                    continue;
                }

                if (Directory.Exists(path: destinationPath!)) {
                    problems.Add(item: $"distributed copy destination is a directory: {copy.Path}");
                    continue;
                }

                if (copy.Deferred) {
                    // Deferred consumers are intentionally outside this command's ownership boundary. Their
                    // destination still receives lexical, containment, reparse-point, collision, and overlap checks.
                    plans.Add(item: new CopyPlan(
                        Asset: asset,
                        Copy: copy,
                        DestinationPath: destinationPath!,
                        NeedsSync: false,
                        SourcePath: sourcePath
                    ));
                    continue;
                }

                var parentPath = Path.GetDirectoryName(path: destinationPath!);

                if (
                    (parentPath is null) ||
                    !Directory.Exists(path: parentPath)
                ) {
                    problems.Add(item: $"distributed copy parent is missing: {copy.Path}");
                    continue;
                }

                var needsSync = !File.Exists(path: destinationPath!);

                if (
                    !check &&
                    needsSync
                ) {
                    // A missing file is a valid sync target, provided its containing directory passed the preflight above.
                } else if (
                    check &&
                    needsSync
                ) {
                    problems.Add(item: $"missing distributed copy: {copy.Path}");
                    continue;
                }

                string? destinationHash = null;

                if (
                    !needsSync &&
                    !TryHashFile(
                    hash: out destinationHash,
                    path: destinationPath!,
                    problem: out var destinationHashProblem
                )
                ) {
                    problems.Add(item: destinationHashProblem!);
                    continue;
                }

                needsSync = (needsSync || !string.Equals(
                    a: destinationHash,
                    b: asset.Hash,
                    comparisonType: StringComparison.OrdinalIgnoreCase
                ));
                plans.Add(item: new CopyPlan(
                    Asset: asset,
                    Copy: copy,
                    DestinationPath: destinationPath!,
                    NeedsSync: needsSync,
                    SourcePath: sourcePath
                ));
            }

            if (problems.Count > 0) {
                return Report(
                    deferredCopies: deferredCopies,
                    problems: problems
                );
            }

            if (!check) {
                foreach (var plan in plans.Where(predicate: static plan => (!plan.Copy.Deferred && plan.NeedsSync))) {
                    try {
                        File.Copy(
                            sourceFileName: plan.SourcePath,
                            destFileName: plan.DestinationPath,
                            overwrite: true
                        );
                    } catch (Exception exception) when ((exception is IOException or UnauthorizedAccessException)) {
                        problems.Add(item: $"distributed copy could not be synchronized: {plan.Copy.Path} ({exception.Message})");
                    }
                }
            }

            var checkedCopies = 0;

            foreach (var plan in plans.Where(predicate: static plan => !plan.Copy.Deferred)) {
                if (!File.Exists(path: plan.DestinationPath)) {
                    problems.Add(item: $"missing distributed copy: {plan.Copy.Path}");
                    continue;
                }

                checkedCopies++;
                if (!TryHashFile(
                    path: plan.DestinationPath,
                    hash: out var actualHash,
                    problem: out var destinationHashProblem
                )) {
                    problems.Add(item: destinationHashProblem!);
                    continue;
                }

                if (!string.Equals(
                    a: actualHash,
                    b: plan.Asset.Hash,
                    comparisonType: StringComparison.OrdinalIgnoreCase
                )) {
                    problems.Add(item: $"distributed copy drift: {plan.Copy.Path} (source {plan.Asset.Id}, found {actualHash})");
                }
            }

            if (problems.Count > 0) {
                return Report(
                    deferredCopies: deferredCopies,
                    problems: problems
                );
            }

            var mode = (check
                ? "matched"
                : "synchronized"
            );

            Console.Out.WriteLine(value: $"branding: {mode} {assets.Count} canonical assets and {checkedCopies} distributed copies; deferred copies: {deferredCopies}; all hashes match.");
            return 0;
        }
    }

    private static bool ContainsReparsePoint(string repositoryRoot, string candidate) {
        var relative = Path.GetRelativePath(
            path: repositoryRoot,
            relativeTo: candidate
        );
        var current = repositoryRoot;

        foreach (var segment in relative.Split(
            options: StringSplitOptions.RemoveEmptyEntries,
            separator: [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar]
        )) {
            current = Path.Combine(
                path1: current,
                path2: segment
            );
            if (
                File.Exists(path: current) ||
                Directory.Exists(path: current)
            ) {
                var attributes = File.GetAttributes(path: current);

                if ((attributes & FileAttributes.ReparsePoint) != 0) {
                    return true;
                }
            }
        }

        return false;
    }
    private static string? GetString(JsonElement element, string property) => (((element.ValueKind == JsonValueKind.Object) && element.TryGetProperty(
        propertyName: property,
        value: out var value
    ) && (value.ValueKind == JsonValueKind.String))
        ? value.GetString()
        : null
    );
    private static bool HasString(JsonElement root, string property, string expected) => string.Equals(
        a: GetString(
            element: root,
            property: property
        ),
        b: expected,
        comparisonType: StringComparison.Ordinal
    );
    private static List<Asset> LoadAssets(JsonElement root, List<string> problems) {
        var assets = new List<Asset>();

        if (
            !root.TryGetProperty(
            propertyName: "assets",
            value: out var value
        ) ||
            (value.ValueKind != JsonValueKind.Array)
        ) {
            problems.Add(item: "manifest assets must be an array");
            return assets;
        }

        var ids = new HashSet<string>(comparer: StringComparer.Ordinal);

        foreach (var item in value.EnumerateArray()) {
            if (item.ValueKind != JsonValueKind.Object) {
                problems.Add(item: "every branding asset entry must be an object");
                continue;
            }

            var id = GetString(
                element: item,
                property: "id"
            );
            var source = GetString(
                element: item,
                property: "source"
            );
            var hash = GetString(
                element: item,
                property: "sha256"
            );

            if (
                (id is null) ||
                (source is null) ||
                (hash is null)
            ) {
                problems.Add(item: "every branding asset requires id, source, and sha256");
                continue;
            }

            if (!ids.Add(item: id)) {
                problems.Add(item: $"duplicate branding asset id: {id}");
                continue;
            }

            if (!ContentPin.TryParseHex(
                hex: hash,
                pin: out _
            )) {
                problems.Add(item: $"branding asset {id} has an invalid SHA-256 hash");
                continue;
            }

            assets.Add(item: new Asset(
                Hash: hash,
                Id: id,
                Source: source
            ));
        }

        return assets;
    }
    private static List<Copy> LoadCopies(JsonElement root, List<string> problems) {
        var copies = new List<Copy>();

        if (
            !root.TryGetProperty(
            propertyName: "copies",
            value: out var value
        ) ||
            (value.ValueKind != JsonValueKind.Array)
        ) {
            problems.Add(item: "manifest copies must be an array");
            return copies;
        }

        foreach (var item in value.EnumerateArray()) {
            if (item.ValueKind != JsonValueKind.Object) {
                problems.Add(item: "every branding copy entry must be an object");
                continue;
            }

            var asset = GetString(
                element: item,
                property: "asset"
            );
            var path = GetString(
                element: item,
                property: "path"
            );

            if (
                (asset is null) ||
                (path is null)
            ) {
                problems.Add(item: "every branding copy requires asset and path");
                continue;
            }

            var deferred = false;

            if (item.TryGetProperty(
                propertyName: "deferred",
                value: out var deferredValue
            )) {
                if (
                    (deferredValue.ValueKind != JsonValueKind.True) &&
                    (deferredValue.ValueKind != JsonValueKind.False)
                ) {
                    problems.Add(item: $"branding copy {path} deferred must be a boolean");
                    continue;
                }

                deferred = deferredValue.GetBoolean();
            }

            copies.Add(item: new Copy(
                AssetId: asset,
                Deferred: deferred,
                Path: path
            ));
        }

        return copies;
    }
    private static List<Wiring> LoadWirings(JsonElement root, List<string> problems) {
        var wirings = new List<Wiring>();

        if (
            !root.TryGetProperty(
            propertyName: "wiring",
            value: out var value
        ) ||
            (value.ValueKind != JsonValueKind.Array)
        ) {
            problems.Add(item: "manifest wiring must be an array");
            return wirings;
        }

        foreach (var item in value.EnumerateArray()) {
            if (item.ValueKind != JsonValueKind.Object) {
                problems.Add(item: "every branding wiring entry must be an object");
                continue;
            }

            var path = GetString(
                element: item,
                property: "path"
            );
            var contains = GetString(
                element: item,
                property: "contains"
            );
            var description = (GetString(
                element: item,
                property: "description"
            ) ?? "required branding reference");

            if (
                (path is null) ||
                (contains is null)
            ) {
                problems.Add(item: "every branding wiring entry requires path and contains");
                continue;
            }

            wirings.Add(item: new Wiring(
                Contains: contains,
                Description: description,
                Path: path
            ));
        }

        return wirings;
    }
    private static int Report(List<string> problems, int deferredCopies) {
        Console.Error.WriteLine(value: $"branding: {problems.Count} problem(s) found; deferred copies: {deferredCopies}.");
        foreach (var problem in problems) {
            Console.Error.WriteLine(value: $"  {problem}");
        }

        return 1;
    }
    private static int Run(bool check) {
        if (!CliPaths.TryGetRepositoryRoot(repositoryRoot: out var repositoryRoot)) {
            return 2;
        }

        return Run(
            check: check,
            repositoryRoot: repositoryRoot
        );
    }
    private static bool TryHashFile(string path, out string? hash, out string? problem) {
        hash = null;
        problem = null;
        try {
            hash = ContentPin.OfFile(path: path).Hex;
            return true;
        } catch (Exception exception) when ((exception is IOException or UnauthorizedAccessException)) {
            problem = $"branding file could not be hashed: {path} ({exception.Message})";
            return false;
        }
    }
    private static bool TryResolveRepositoryPath(string repositoryRoot, string relativePath, string kind, out string? fullPath, out string? problem) {
        fullPath = null;
        problem = null;
        if (
            string.IsNullOrWhiteSpace(value: relativePath) ||
            Path.IsPathRooted(path: relativePath)
        ) {
            problem = $"{kind} must be a non-empty repository-relative path: {relativePath}";
            return false;
        }

        try {
            fullPath = Path.GetFullPath(path: Path.Combine(
                path1: repositoryRoot,
                path2: relativePath
            ));
        } catch (Exception exception) when ((exception is ArgumentException or IOException or NotSupportedException)) {
            problem = $"{kind} is not a valid path: {relativePath}";
            return false;
        }

        var root = Path.GetFullPath(path: repositoryRoot).TrimEnd(
            Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar
        );
        var prefix = (root + Path.DirectorySeparatorChar);

        if (!fullPath.StartsWith(
            value: prefix,
            comparisonType: PuckPaths.Comparison
        )) {
            problem = $"{kind} escapes the repository: {relativePath}";
            return false;
        }

        try {
            if (ContainsReparsePoint(
                candidate: fullPath,
                repositoryRoot: root
            )) {
                problem = $"{kind} crosses a reparse point: {relativePath}";
                return false;
            }
        } catch (Exception exception) when ((exception is IOException or UnauthorizedAccessException)) {
            problem = $"{kind} could not be inspected: {relativePath} ({exception.Message})";
            return false;
        }

        return true;
    }

    public static Command Create() => CliOptions.CheckVerb(
        checkDescription: "Check canonical hashes, distributed copies, and required wiring without writing; exit 1 on drift.",
        description: "Synchronize and check the maintained branding assets.",
        name: "branding",
        run: Run
    );
}
