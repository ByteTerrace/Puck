namespace Puck.Cli;

/// <summary>Whether the baked-mesh store a World boots with already holds the meshes it needs. A cold store sends a windowed
/// World through the cold bake, which reaches a far larger peak than a boot that loads the meshes it finds, so the two are
/// measured and judged apart (<see cref="HostStartProfile"/>).</summary>
public enum StoreWarmth {
    /// <summary>The store holds the meshes, or the start is not known to bake: the ordinary boot.</summary>
    Warm,
    /// <summary>The store was empty when the World booted, or its boot reported it was baking meshes.</summary>
    Cold,
}

/// <summary>
/// What a start is, finely enough that its own measured need predicts its peak: its <see cref="Kind"/>, and for a windowed
/// World also the <see cref="Device"/> backend it boots on, whether its mesh <see cref="Store"/> is warm or cold and the
/// <see cref="Document"/> it boots, since a small world and the debug world differ several times over in what they reach.
/// The host admission (<c>HostStartTracker</c>) keeps one window of observed peaks per <see cref="Class"/>, so a warm
/// Direct3D 12 session is never judged by what the cold bakes reached, nor a gallery-only world by the whole debug world. A
/// bare <see cref="HostStartKind"/> converts to its profile implicitly, for the starts that have nothing finer to say (a
/// heavy suite, a step, a headless World).
/// </summary>
/// <param name="Kind">What the start is.</param>
/// <param name="Device">The backend token a windowed World boots on (<c>directx</c>, <c>vulkan</c>), or
/// <see langword="null"/> when the start opens no device or its document picks the backend.</param>
/// <param name="Store">Whether the World's mesh store is warm or cold.</param>
/// <param name="Document">The world document a windowed World boots, as its launch reads it from the world path
/// (<see cref="DocumentOfWorld"/>, such as <c>debug</c>), or <see langword="null"/> when the launch cannot name it;
/// <see cref="Class"/> holds it as a file-name token (<see cref="DocumentToken"/>).</param>
public readonly record struct HostStartProfile(HostStartKind Kind, string? Device = null, StoreWarmth Store = StoreWarmth.Warm, string? Document = null) {
    private const char Separator = '.';
    private const char DocumentSeparator = '@';
    private const string ColdToken = "cold";

    /// <summary>Converts a kind to the profile that says nothing finer about the start.</summary>
    /// <param name="kind">The start's kind.</param>
    public static implicit operator HostStartProfile(HostStartKind kind) => new(Kind: kind);

    /// <summary>Returns a document name as the token a profile's <see cref="Name"/> and file name carry: lower case, with
    /// every character other than an ASCII letter, digit, <c>_</c> or <c>-</c> written as <c>-</c>, so a name with a
    /// directory (<c>games/klondike</c>) stays one file name on every file system.</summary>
    /// <param name="document">The document name, or <see langword="null"/>.</param>
    /// <returns>The token, or <see langword="null"/> for a missing or blank name.</returns>
    public static string? DocumentToken(string? document) => (string.IsNullOrWhiteSpace(value: document)
        ? null
        : string.Create(length: document.Trim().Length, state: document.Trim(), action: static (span, name) => {
            for (var index = 0; (index < span.Length); index++) {
                var character = char.ToLowerInvariant(c: name[index]);

                span[index] = ((char.IsAsciiLetterLower(c: character) || char.IsAsciiDigit(c: character) || (character is '_' or '-')) ? character : '-');
            }
        }));

    /// <summary>Returns the document a World booted from a world path is measured under: the path's last name up to its
    /// first dot, as a token (<see cref="DocumentToken"/>). That is a package folder's name or a world file's stem, the same
    /// wherever the world's files lie, so a checkout's <c>worlds/debug</c>, its <c>debug.puck</c> and a player install's
    /// <c>debug.world.json</c> are one document, <c>debug</c>, and a gallery-only root world beside it is another. A file
    /// whose name starts with a dot (a run's staged copy) names none.</summary>
    /// <param name="world">The world path a boot passes as <c>--world</c>, or <see langword="null"/>.</param>
    /// <returns>The document token, or <see langword="null"/> when the path names none.</returns>
    public static string? DocumentOfWorld(string? world) {
        if (string.IsNullOrWhiteSpace(value: world)) { return null; }

        var leaf = Path.GetFileName(path: Path.TrimEndingDirectorySeparator(path: world.Trim()));
        var dot = leaf.IndexOf(value: Separator);

        return DocumentToken(document: ((dot < 0) ? leaf : leaf[..dot]));
    }
    /// <summary>Gets the profile with only the dimensions its kind measures (a device, a store and a document matter to a
    /// windowed World alone): the key a window of observations is kept under, and the profile a file name or a hold
    /// records.</summary>
    public HostStartProfile Class => ((Kind == HostStartKind.WindowedWorld)
        ? new HostStartProfile(Kind: Kind, Device: (string.IsNullOrWhiteSpace(value: Device) ? null : Device.Trim().ToLowerInvariant()), Store: Store, Document: DocumentToken(document: Document))
        : new HostStartProfile(Kind: Kind));
    /// <summary>Gets <see cref="Class"/> without its document: the profile of any World of the same kind, backend and store.
    /// Every observation of a document's start is also one of this profile, so a start of a document this machine has not
    /// measured, and a start that names no document, is judged by what such Worlds reached here.</summary>
    public HostStartProfile AnyDocument => (Class with { Document = null });
    /// <summary>Gets the name of <see cref="Class"/>: the kind, then the backend when it has one, then <c>cold</c> for a cold
    /// store, joined with dots, then <c>@</c> and the document token when it has one (<c>WindowedWorld</c>,
    /// <c>WindowedWorld.vulkan</c>, <c>WindowedWorld.directx.cold@debug</c>).</summary>
    public string Name {
        get {
            var profile = Class;

            return string.Concat(Kind.ToString(), (profile.Device is { } device) ? (Separator + device) : string.Empty, (profile.Store == StoreWarmth.Cold) ? (Separator + ColdToken) : string.Empty, (profile.Document is { } document) ? (DocumentSeparator + document) : string.Empty);
        }
    }

    /// <summary>Reads a profile's <see cref="Name"/>.</summary>
    /// <param name="name">The name.</param>
    /// <param name="profile">The profile it names.</param>
    /// <returns><see langword="true"/> when <paramref name="name"/> is a kind, then at most a backend, then optionally
    /// <c>cold</c>, then optionally <c>@</c> and a document token, spelled as <see cref="Name"/> spells it.</returns>
    public static bool TryParse(string? name, out HostStartProfile profile) {
        profile = default;

        var whole = (name ?? string.Empty);
        var at = whole.IndexOf(value: DocumentSeparator);
        var tokens = ((at < 0) ? whole : whole[..at]).Split(separator: Separator);

        if ((tokens.Length is 0 or > 3) || !Enum.TryParse<HostStartKind>(value: tokens[0], result: out var kind) || !Enum.IsDefined(value: kind)) { return false; }

        string? device = null;
        var store = StoreWarmth.Warm;

        foreach (var token in tokens.Skip(count: 1)) {
            if (token == ColdToken) { store = StoreWarmth.Cold; }
            else if ((token.Length != 0) && (device is null) && (store == StoreWarmth.Warm)) { device = token; }
            else { return false; }
        }
        profile = new HostStartProfile(Kind: kind, Device: device, Store: store, Document: ((at < 0) ? null : whole[(at + 1)..])).Class;

        return (profile.Name == name);
    }

    /// <summary>Gets this start's profile with its store marked cold.</summary>
    public HostStartProfile Cold => (this with { Store = StoreWarmth.Cold });
}
