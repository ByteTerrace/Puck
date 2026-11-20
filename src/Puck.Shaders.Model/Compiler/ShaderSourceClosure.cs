using System.Collections.ObjectModel;
using System.Text;
using System.Text.RegularExpressions;
using Puck.Abstractions;
using Puck.Assets;

namespace Puck.Shaders;

/// <summary>The limits a shader source closure must fit before anything compiles. Every compile checks its own closure
/// against <see cref="Default"/>; a package checks its whole closure against the limits it is built or loaded with, which
/// may only tighten the defaults.</summary>
/// <param name="MaxExpandedBytes">The most UTF-8 bytes the distinct files of a closure may hold together: every stage
/// source and every include, each counted once, which is the most text an include-guarded compile expands.</param>
/// <param name="MaxFileBytes">The most UTF-8 bytes any one file of the closure may hold.</param>
/// <param name="MaxDependencies">The most include files a closure may reach.</param>
/// <param name="MaxIncludeDepth">The deepest an include may nest: a stage source's own include is depth one.</param>
/// <param name="MaxCompileSteps">The most native tool runs a package's passes may need together
/// (<see cref="ShaderCompileIdentity.StepCount"/>).</param>
public sealed record ShaderSourceLimits(
    long MaxExpandedBytes = ((16L * 1024) * 1024),
    long MaxFileBytes = ((4L * 1024) * 1024),
    int MaxDependencies = 256,
    int MaxIncludeDepth = 32,
    int MaxCompileSteps = 512
) {
    /// <summary>Gets the limits every compile enforces.</summary>
    public static ShaderSourceLimits Default { get; } = new();
}
/// <summary>A shader source closure or package refused before anything compiled, named by its code.</summary>
public sealed class ShaderClosureRefusedException : Exception {
    /// <summary>A stage source includes a file that does not exist.</summary>
    public const string IncludeMissing = "SHADERSRC_INCLUDE_MISSING";
    /// <summary>A stage source is also an include of the same compile, so the compiler could not snapshot the two apart.</summary>
    public const string SourceIncluded = "SHADERSRC_SOURCE_INCLUDED";
    /// <summary>A stage source or include lies outside the closure's root directory.</summary>
    public const string OutsideClosure = "SHADERSRC_OUTSIDE_CLOSURE";
    /// <summary>The closure's distinct files hold more bytes than <see cref="ShaderSourceLimits.MaxExpandedBytes"/>.</summary>
    public const string ExpandedBytes = "SHADERSRC_EXPANDED_BYTES";
    /// <summary>One file holds more bytes than <see cref="ShaderSourceLimits.MaxFileBytes"/>.</summary>
    public const string FileBytes = "SHADERSRC_FILE_BYTES";
    /// <summary>The closure reaches more includes than <see cref="ShaderSourceLimits.MaxDependencies"/>.</summary>
    public const string DependencyCount = "SHADERSRC_DEPENDENCY_COUNT";
    /// <summary>An include nests deeper than <see cref="ShaderSourceLimits.MaxIncludeDepth"/>.</summary>
    public const string IncludeDepth = "SHADERSRC_INCLUDE_DEPTH";
    /// <summary>The passes need more native tool runs than <see cref="ShaderSourceLimits.MaxCompileSteps"/>.</summary>
    public const string CompileSteps = "SHADERSRC_COMPILE_STEPS";
    /// <summary>A package manifest is not a well-formed <c>puck.shader.package.v1</c> document.</summary>
    public const string PackageMalformed = "SHADERPKG_MALFORMED";
    /// <summary>A file a package manifest lists is absent from the package.</summary>
    public const string PackageFileMissing = "SHADERPKG_FILE_MISSING";
    /// <summary>A package file's content does not match its pin.</summary>
    public const string PackageFilePin = "SHADERPKG_FILE_PIN";
    /// <summary>A package's files are not exactly the closure its document reaches.</summary>
    public const string PackageClosure = "SHADERPKG_CLOSURE";
    /// <summary>A native tool reports no version for a package build to record.</summary>
    public const string PackageCompiler = "SHADERPKG_COMPILER";
    /// <summary>A pass reads an interface, or declarations generated from it, other than the ones the package was built
    /// for, or a built binary or echo pass reads its frame block somewhere its interface does not lay it out.</summary>
    public const string PackageInterface = "SHADERPKG_INTERFACE";
    /// <summary>The package's recorded backend capabilities differ from the ones its plan requires.</summary>
    public const string PackageCapabilities = "SHADERPKG_CAPABILITIES";
    /// <summary>The package's output directory holds something other than a package.</summary>
    public const string PackageOutput = "SHADERPKG_OUTPUT";
    /// <summary>A pipeline source has no package in the build's store, and no compiler can compile it where it
    /// loads.</summary>
    public const string PackageAbsent = "SHADERPKG_ABSENT";

    /// <summary>Initializes a refusal.</summary>
    /// <param name="code">The refusal's code, one of this type's constants.</param>
    /// <param name="message">What was refused and why.</param>
    public ShaderClosureRefusedException(string code, string message)
        : base(message: $"[{code}] {message}") {
        Code = code;
    }

    /// <summary>Gets the refusal's code.</summary>
    public string Code { get; }
}
/// <summary>
/// The files one compile or one package reads: the stage sources it is handed and every file their <c>#include</c>
/// lines reach, each with its content hash, checked against <see cref="ShaderSourceLimits"/> and, when a root is given,
/// confined to that root. It is the one include walk: <see cref="ShaderCompiler"/> collects every compile's closure
/// through it before the cache is consulted, so a missing include is refused by name even when a warm cache holds the
/// bytecode it once produced.
/// <para>Every <c>#include "…"</c> or <c>#include &lt;…&gt;</c> line resolves against the directory of the file that
/// holds it and then, in order, against each include directory the compile names (<see cref="ShaderIncludeDirectories"/>),
/// the first that holds the file winning, as DXC searches its <c>-I</c> directories; this includes a line inside an
/// inactive preprocessor branch: the closure is the set of files a source names, not the set one configuration happens
/// to read. An include the caller generates, such as a pass's interface declarations, is read from the text handed in
/// for its path rather than from disk, whether or not a file holds that path. The closure records where each directive
/// resolved (<see cref="WithIncludes"/>), so nothing resolves an include a second way.</para>
/// </summary>
public sealed partial class ShaderSourceClosure {
    // Where each directive resolved: by the including file's full path, then by the path the directive names.
    private readonly IReadOnlyDictionary<string, Dictionary<string, ShaderIncludeResolution>> m_resolutions;

    private ShaderSourceClosure(
        string? root,
        IReadOnlyList<ShaderSourceDependency> sources,
        IReadOnlyList<ShaderSourceDependency> includes,
        IReadOnlyDictionary<string, string> contents,
        IReadOnlyList<string> includeDirectories,
        IReadOnlyDictionary<string, Dictionary<string, ShaderIncludeResolution>> resolutions,
        long expandedBytes,
        int depth
    ) {
        Root = root;
        Sources = sources;
        Includes = includes;
        Contents = contents;
        IncludeDirectories = includeDirectories;
        m_resolutions = resolutions;
        ExpandedBytes = expandedBytes;
        Depth = depth;
    }

    /// <summary>Gets the text of every include, by full path.</summary>
    public IReadOnlyDictionary<string, string> Contents { get; }
    /// <summary>Gets the deepest include nesting the closure reached.</summary>
    public int Depth { get; }
    /// <summary>Gets the UTF-8 bytes of every distinct file in the closure, stage sources included.</summary>
    public long ExpandedBytes { get; }
    /// <summary>Gets the include directories at least one include resolved through, as full paths, in search
    /// order.</summary>
    public IReadOnlyList<string> IncludeDirectories { get; }
    /// <summary>Gets every include the stage sources reach, in discovery order, by full path.</summary>
    public IReadOnlyList<ShaderSourceDependency> Includes { get; }
    /// <summary>Gets the directory every file must lie within, or <see langword="null"/> for an unconfined closure.</summary>
    public string? Root { get; }
    /// <summary>Gets the stage sources, in the order they were handed in, by full path.</summary>
    public IReadOnlyList<ShaderSourceDependency> Sources { get; }

    /// <summary>Computes the content hash every closure, cache key, and package manifest records for a file: the
    /// SHA-256 of its UTF-8 text, the bytes the compiler reads, as a <see cref="ContentPin"/>'s digits.</summary>
    /// <param name="text">The file's text.</param>
    /// <returns>64 lowercase hexadecimal digits.</returns>
    public static string HashOf(string text) {
        ArgumentNullException.ThrowIfNull(argument: text);

        return ContentPin.OfText(text: text).Hex;
    }
    /// <summary>Collects the closure of <paramref name="sources"/>.</summary>
    /// <param name="sources">Each stage source's path and the text the compile reads for it. A source may name a
    /// path no file holds, such as a stage the loader synthesizes; its includes resolve beside that path.</param>
    /// <param name="limits">The limits the closure must fit.</param>
    /// <param name="root">The directory every file must lie within, or <see langword="null"/> for none.</param>
    /// <param name="generated">The text of each generated include by full path, or <see langword="null"/> for none.</param>
    /// <param name="readInclude">Reads an include's text by full path, <see langword="null"/> when no file holds it, in
    /// place of the file system, for a caller reading another tree than the working one; or <see langword="null"/> to read
    /// the file system.</param>
    /// <param name="includeDirectories">The directories, in search order, an include that does not lie beside the file
    /// naming it resolves through (<see cref="ShaderStageSource.IncludeDirectories"/>), or <see langword="null"/> for
    /// none.</param>
    /// <returns>The closure.</returns>
    /// <exception cref="ShaderClosureRefusedException">An include is missing, a file lies outside
    /// <paramref name="root"/>, or a limit is exceeded; the code names which.</exception>
    public static ShaderSourceClosure Collect(IReadOnlyList<(string Path, string Text)> sources, ShaderSourceLimits limits, string? root = null, IReadOnlyDictionary<string, string>? generated = null, Func<string, string?>? readInclude = null, IReadOnlyList<string>? includeDirectories = null) {
        ArgumentNullException.ThrowIfNull(argument: sources);
        ArgumentNullException.ThrowIfNull(argument: limits);

        var fullRoot = ((root is null)
            ? null
            : Path.GetFullPath(path: root)
        );
        var searched = (includeDirectories ?? []).Select(selector: static directory => Path.TrimEndingDirectorySeparator(path: Path.GetFullPath(path: directory))).Distinct(comparer: PuckPaths.Comparer).ToArray();
        var used = new bool[searched.Length];
        var roots = new List<ShaderSourceDependency>(capacity: sources.Count);
        var includes = new List<ShaderSourceDependency>();
        var contents = new Dictionary<string, string>(comparer: PuckPaths.Comparer);
        var resolutions = new Dictionary<string, Dictionary<string, ShaderIncludeResolution>>(comparer: PuckPaths.Comparer);
        var visited = new HashSet<string>(comparer: PuckPaths.Comparer);
        var rootPaths = new HashSet<string>(comparer: PuckPaths.Comparer);
        var expanded = 0L;
        var deepest = 0;

        // An include's text by full path, or null when nothing holds it: generated text first, then the caller's reader or
        // the file system. A file larger than one file may hold is refused before it is read.
        string? Read(string includePath) {
            if ((generated is not null) && generated.TryGetValue(key: includePath, value: out var text)) {
                return text;
            }

            if (readInclude is not null) {
                return readInclude(arg: includePath);
            }

            var info = new FileInfo(fileName: includePath);

            if (!info.Exists) {
                return null;
            }

            // A file's UTF-8 text is never longer than its bytes, so an oversized file is refused before it is read.
            if (info.Length > limits.MaxFileBytes) {
                throw new ShaderClosureRefusedException(
                    code: ShaderClosureRefusedException.FileBytes,
                    message: $"'{includePath}' holds {info.Length} bytes; one file may hold at most {limits.MaxFileBytes}."
                );
            }

            return File.ReadAllText(path: includePath);
        }

        void Admit(string path, string text) {
            if (
                (fullRoot is not null) &&
                !IsWithin(
                    directory: fullRoot,
                    path: path
                )
            ) {
                throw new ShaderClosureRefusedException(
                    code: ShaderClosureRefusedException.OutsideClosure,
                    message: $"'{path}' lies outside the closure rooted at '{fullRoot}'."
                );
            }

            var bytes = Encoding.UTF8.GetByteCount(s: text);

            if (bytes > limits.MaxFileBytes) {
                throw new ShaderClosureRefusedException(
                    code: ShaderClosureRefusedException.FileBytes,
                    message: $"'{path}' holds {bytes} bytes; one file may hold at most {limits.MaxFileBytes}."
                );
            }

            expanded += bytes;

            if (expanded > limits.MaxExpandedBytes) {
                throw new ShaderClosureRefusedException(
                    code: ShaderClosureRefusedException.ExpandedBytes,
                    message: $"the closure holds more than {limits.MaxExpandedBytes} bytes once '{path}' is read."
                );
            }
        }
        void Walk(string path, string text, int depth) {
            var directory = (Path.GetDirectoryName(path: path) ?? Environment.CurrentDirectory);

            foreach (Match match in IncludePattern().Matches(input: text)) {
                var written = match.Groups[1].Value;
                string? includePath = null;
                string? included = null;
                var through = -1;

                // The including file's directory, then each include directory in order: the first that holds the file wins.
                for (var candidate = -1; ((candidate < searched.Length) && (includePath is null)); candidate++) {
                    var candidatePath = Path.GetFullPath(path: Path.Combine(
                        path1: ((candidate < 0) ? directory : searched[candidate]),
                        path2: written
                    ));

                    if (rootPaths.Contains(item: candidatePath)) {
                        throw new ShaderClosureRefusedException(
                            code: ShaderClosureRefusedException.SourceIncluded,
                            message: $"'{path}' includes the stage source '{candidatePath}' of the same compile."
                        );
                    }

                    if (visited.Contains(item: candidatePath)) {
                        (includePath, through) = (candidatePath, candidate);
                    } else if (Read(includePath: candidatePath) is { } read) {
                        (includePath, included, through) = (candidatePath, read, candidate);
                    }
                }

                if (includePath is null) {
                    var beside = Path.GetFullPath(path: Path.Combine(path1: directory, path2: written));

                    throw new ShaderClosureRefusedException(
                        code: ShaderClosureRefusedException.IncludeMissing,
                        message: ((searched.Length == 0)
                            ? $"'{path}' includes '{written}', and no file exists at '{beside}'."
                            : $"'{path}' includes '{written}', and no file exists at '{beside}' or below the include directories '{string.Join(separator: "', '", value: searched)}'.")
                    );
                }

                if (!resolutions.TryGetValue(key: path, value: out var directives)) {
                    directives = new Dictionary<string, ShaderIncludeResolution>(comparer: StringComparer.Ordinal);
                    resolutions[path] = directives;
                }

                directives[written] = new ShaderIncludeResolution(
                    Directory: ((through < 0) ? null : searched[through]),
                    Path: includePath
                );

                if (through >= 0) {
                    used[through] = true;
                }

                if (included is null) {
                    continue;
                }

                visited.Add(item: includePath);

                var includeDepth = (depth + 1);

                if (includeDepth > limits.MaxIncludeDepth) {
                    throw new ShaderClosureRefusedException(
                        code: ShaderClosureRefusedException.IncludeDepth,
                        message: $"'{includePath}' is included at depth {includeDepth}; includes may nest at most {limits.MaxIncludeDepth} deep."
                    );
                }

                if ((includes.Count + 1) > limits.MaxDependencies) {
                    throw new ShaderClosureRefusedException(
                        code: ShaderClosureRefusedException.DependencyCount,
                        message: $"the closure reaches more than {limits.MaxDependencies} includes at '{includePath}'."
                    );
                }

                Admit(
                    path: includePath,
                    text: included
                );
                contents[includePath] = included;
                includes.Add(item: new ShaderSourceDependency(
                    ContentHash: HashOf(text: included),
                    Path: includePath
                ));
                deepest = Math.Max(
                    val1: deepest,
                    val2: includeDepth
                );
                Walk(
                    depth: includeDepth,
                    path: includePath,
                    text: included
                );
            }
        }

        foreach (var (path, text) in sources) {
            ArgumentException.ThrowIfNullOrWhiteSpace(argument: path);
            ArgumentNullException.ThrowIfNull(argument: text);

            var fullPath = Path.GetFullPath(path: path);

            if (rootPaths.Add(item: fullPath)) {
                Admit(
                    path: fullPath,
                    text: text
                );
            }

            roots.Add(item: new ShaderSourceDependency(
                ContentHash: HashOf(text: text),
                Path: fullPath
            ));
        }

        foreach (var (path, text) in sources) {
            Walk(
                depth: 0,
                path: Path.GetFullPath(path: path),
                text: text
            );
        }

        return new ShaderSourceClosure(
            contents: new ReadOnlyDictionary<string, string>(dictionary: contents),
            depth: deepest,
            expandedBytes: expanded,
            includeDirectories: [.. searched.Where(predicate: (_, index) => used[index])],
            includes: includes.AsReadOnly(),
            resolutions: resolutions,
            root: fullRoot,
            sources: roots.AsReadOnly()
        );
    }
    /// <summary>Indicates whether <paramref name="path"/> lies within <paramref name="directory"/>.</summary>
    /// <param name="path">A full path.</param>
    /// <param name="directory">A full directory path.</param>
    /// <returns><see langword="true"/> when <paramref name="path"/> is <paramref name="directory"/> or lies below it.</returns>
    public static bool IsWithin(string path, string directory) {
        var relative = Path.GetRelativePath(
            path: path,
            relativeTo: directory
        );

        return !(Path.IsPathRooted(path: relative) || (relative == "..") || relative.StartsWith(
            comparisonType: StringComparison.Ordinal,
            value: (".." + Path.DirectorySeparatorChar)
        ));
    }
    /// <summary>Returns the text of a file of this closure with each include directive naming the path
    /// <paramref name="map"/> gives its include instead, the include as this closure resolved it and every other character
    /// kept, so no line moves. A directive this closure did not resolve keeps its text.</summary>
    /// <param name="text">The file's text, as this closure read it.</param>
    /// <param name="path">The file's path: a stage source or an include of this closure.</param>
    /// <param name="map">Maps where a directive resolved and the path it names to the path the directive names instead,
    /// which may be the same.</param>
    /// <returns>The text.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="text"/>, <paramref name="path"/> or
    /// <paramref name="map"/> is <see langword="null"/>.</exception>
    public string WithIncludes(string text, string path, Func<ShaderIncludeResolution, string, string> map) {
        ArgumentNullException.ThrowIfNull(argument: text);
        ArgumentNullException.ThrowIfNull(argument: path);
        ArgumentNullException.ThrowIfNull(argument: map);

        var includer = Path.GetFullPath(path: path);

        return IncludePattern().Replace(
            evaluator: match => {
                var include = match.Groups[1];

                if (!m_resolutions.TryGetValue(key: includer, value: out var directives) || !directives.TryGetValue(key: include.Value, value: out var resolution)) {
                    return match.Value;
                }

                var start = (include.Index - match.Index);

                return string.Concat(
                    str0: match.Value.AsSpan(length: start, start: 0),
                    str1: map(
                        arg1: resolution,
                        arg2: include.Value
                    ),
                    str2: match.Value.AsSpan(start: (start + include.Length))
                );
            },
            input: text
        );
    }

    // Multiline: every include line is found, not only one that opens the file.
    [GeneratedRegex(pattern: @"^[ \t]*#[ \t]*include[ \t]*[<""]([^>""\r\n]+)[>""]", options: RegexOptions.Multiline)]
    private static partial Regex IncludePattern();
}
