using System.Globalization;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Text;
using Puck.Abstractions;

namespace Puck.Assets;

/// <summary>
/// The identity of the code reachable from a set of roots in a deployed build (<see cref="CodeClosure"/>), remembered
/// across processes: the walk reads every reached assembly's IL and costs seconds on a large closure, so a build walks
/// each set of roots once and every later process of the same build reads the hash back. What it remembers is the hash
/// and the module version identity of every assembly the walk read; an entry stands while each of those assemblies still
/// carries the identity it was walked under, so an edit to code the closure never read keeps the entry, and an edit to
/// an assembly it read walks again. The walk itself runs only when the hash is not remembered or a caller asks what the
/// closure reads (<see cref="Reads"/>, which a hot-reloaded process asks to fold <see cref="CodeRevision.AppliedTo"/>).
/// The entries are derived products in a content-addressed store (<see cref="DerivedBytes.DefaultDirectory"/>, else the
/// per-user derived directory); an absent, damaged or stale entry is an ordinary miss.
/// </summary>
public sealed class CodeIdentity {
    // The kind an entry is set under in the store.
    private const string Kind = "code-identity";

    private readonly string m_directory;
    private readonly IReadOnlyList<(string Assembly, string Type, string? Method)> m_roots;
    private readonly IReadOnlySet<string> m_stopTypes;
    private readonly Lazy<CodeClosure> m_closure;
    private readonly Lazy<UInt128> m_hash;

    private CodeIdentity(string directory, IReadOnlyList<(string Assembly, string Type, string? Method)> roots, IReadOnlySet<string> stopTypes) {
        m_directory = Path.GetFullPath(path: directory);
        m_roots = roots;
        m_stopTypes = stopTypes;
        m_closure = new Lazy<CodeClosure>(valueFactory: Walk);
        m_hash = new Lazy<UInt128>(valueFactory: Remembered);
    }

    /// <summary>Returns the remembered identity of the code reachable from roots in a deployed build. Nothing is walked
    /// or read until the identity is asked for.</summary>
    /// <param name="directory">The build directory.</param>
    /// <param name="roots">Assembly, full type name and method name (all overloads), or null for the whole type.</param>
    /// <param name="stopTypes">Full names of the types whose called bodies are transport or cache boundaries
    /// (<see cref="CodeClosure.Of"/>).</param>
    /// <returns>The identity.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="directory"/> or <paramref name="roots"/> is
    /// <see langword="null"/>.</exception>
    public static CodeIdentity Of(string directory, IReadOnlyList<(string Assembly, string Type, string? Method)> roots, IReadOnlySet<string>? stopTypes = null) {
        ArgumentNullException.ThrowIfNull(argument: directory);
        ArgumentNullException.ThrowIfNull(argument: roots);

        return new CodeIdentity(directory: directory, roots: roots, stopTypes: (stopTypes ?? new HashSet<string>(comparer: StringComparer.Ordinal)));
    }

    /// <summary>Gets the identity of the reachable code and layouts (<see cref="CodeClosure.Hash"/>), remembered for the
    /// build when an earlier process walked it.</summary>
    public UInt128 Hash => m_hash.Value;
    /// <summary>Gets the walked closure, walking it on first use.</summary>
    public CodeClosure Closure => m_closure.Value;

    /// <summary>Returns whether an applied metadata update to a type invalidates this identity (<see cref="CodeClosure.Reads"/>);
    /// walks the closure on first use.</summary>
    /// <param name="type">The updated type.</param>
    /// <returns>Whether the closure reads it.</returns>
    public bool Reads(Type type) => Closure.Reads(type: type);
    /// <summary>Returns the identity with every applied update to code it reads folded in
    /// (<see cref="CodeRevision.AppliedTo"/>): the hash as 32 lower-case hex digits while none applies, otherwise the hash
    /// and the update's token.</summary>
    /// <returns>The identity text.</returns>
    public string Applied() {
        var hash = Hash.ToString(format: "x32", provider: CultureInfo.InvariantCulture);
        var applied = CodeRevision.AppliedTo(reads: Reads);

        return ((applied.Length == 0) ? hash : $"{hash}.{applied}");
    }

    private CodeClosure Walk() => CodeClosure.Of(directory: m_directory, roots: m_roots, stopTypes: m_stopTypes);

    // The hash a remembered entry holds while every assembly it names still carries the version it was walked under,
    // else the walk's, which is remembered for the next process.
    private UInt128 Remembered() {
        var input = InputPin();
        ContentAddressedStore? store = null;

        try {
            store = new ContentAddressedStore(root: Path.Combine(path1: (DerivedBytes.DefaultDirectory ?? PuckUserDirectory.Resolve(name: "derived")), path2: "code"), retention: new CacheBound(MaxBytes: 4L << 20, MaxEntries: 4096));
            if (store.TryResolveDerived(kind: Kind, inputHash: input, hash: out var pin) && store.TryGet(pin: pin, content: out var entry) && TryStand(entry: entry, hash: out var held)) {
                return held;
            }
        } catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) {
            store = null;
        }

        var closure = Closure;

        if (store is not null) {
            try {
                store.SetDerived(inputHash: input, kind: Kind, outputHash: store.Put(content: Encoding.UTF8.GetBytes(s: Entry(closure: closure))));
            } catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) {
                // A refused write only costs the next process a walk.
            }
        }
        return closure.Hash;
    }

    // What the walk is asked: the roots and the stop types, in order, each delimited by its length.
    private ContentPin InputPin() {
        var text = new StringBuilder();

        foreach (var (assembly, type, method) in m_roots) {
            _ = text.Append(provider: CultureInfo.InvariantCulture, handler: $"r{assembly.Length}:{assembly}{type.Length}:{type}{(method?.Length ?? -1)}:{method}");
        }
        foreach (var stop in m_stopTypes.Order(comparer: StringComparer.Ordinal)) {
            _ = text.Append(provider: CultureInfo.InvariantCulture, handler: $"s{stop.Length}:{stop}");
        }
        return ContentPin.OfText(text: text);
    }

    // An entry: the hash, then each assembly the walk read and its module version, one a line.
    private static string Entry(CodeClosure closure) {
        var text = new StringBuilder();

        _ = text.Append(provider: CultureInfo.InvariantCulture, handler: $"{closure.Hash:x32}\n");
        foreach (var (assembly, version) in closure.Assemblies) {
            _ = text.Append(provider: CultureInfo.InvariantCulture, handler: $"{assembly} {version:N}\n");
        }
        return text.ToString();
    }

    // Whether an entry stands in this build: every assembly it names carries the module version it names, or is still
    // absent when it names none.
    private bool TryStand(byte[] entry, out UInt128 hash) {
        hash = default;

        var lines = Encoding.UTF8.GetString(bytes: entry).Split(separator: '\n', options: StringSplitOptions.RemoveEmptyEntries);

        if ((lines.Length == 0) || !UInt128.TryParse(s: lines[0], style: NumberStyles.HexNumber, provider: CultureInfo.InvariantCulture, result: out var held)) { return false; }
        for (var index = 1; (index < lines.Length); index++) {
            var parts = lines[index].Split(separator: ' ');

            if ((parts.Length != 2) || !Guid.TryParseExact(input: parts[1], format: "N", result: out var version) || (VersionOf(assembly: parts[0]) != version)) { return false; }
        }
        hash = held;
        return true;
    }

    // An assembly's module version identity in the build, or the empty identity when the build holds no such assembly.
    private Guid VersionOf(string assembly) {
        var path = Path.Combine(path1: m_directory, path2: (assembly + ".dll"));

        if (!File.Exists(path: path)) { return Guid.Empty; }
        using var stream = File.OpenRead(path: path);
        using var pe = new PEReader(peStream: stream);
        var metadata = pe.GetMetadataReader();

        return metadata.GetGuid(handle: metadata.GetModuleDefinition().Mvid);
    }
}
