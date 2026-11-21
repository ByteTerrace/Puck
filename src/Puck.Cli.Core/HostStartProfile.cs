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
/// World also the <see cref="Device"/> backend it boots on and whether its mesh <see cref="Store"/> is warm or cold. The host
/// admission (<c>HostStartTracker</c>) keeps one window of observed peaks per <see cref="Class"/>, so a warm Direct3D 12
/// session is never judged by what the cold bakes reached. A bare <see cref="HostStartKind"/> converts to its profile
/// implicitly, for the starts that have nothing finer to say (a heavy suite, a step, a headless World).
/// </summary>
/// <param name="Kind">What the start is.</param>
/// <param name="Device">The backend token a windowed World boots on (<c>directx</c>, <c>vulkan</c>), or
/// <see langword="null"/> when the start opens no device or its document picks the backend.</param>
/// <param name="Store">Whether the World's mesh store is warm or cold.</param>
public readonly record struct HostStartProfile(HostStartKind Kind, string? Device = null, StoreWarmth Store = StoreWarmth.Warm) {
    private const char Separator = '.';
    private const string ColdToken = "cold";

    /// <summary>Converts a kind to the profile that says nothing finer about the start.</summary>
    /// <param name="kind">The start's kind.</param>
    public static implicit operator HostStartProfile(HostStartKind kind) => new(Kind: kind);

    /// <summary>Gets the profile with only the dimensions its kind measures (a device and a store matter to a windowed World
    /// alone): the key a window of observations is kept under, and the profile a file name or a hold records.</summary>
    public HostStartProfile Class => ((Kind == HostStartKind.WindowedWorld)
        ? new HostStartProfile(Kind: Kind, Device: (string.IsNullOrWhiteSpace(value: Device) ? null : Device.Trim().ToLowerInvariant()), Store: Store)
        : new HostStartProfile(Kind: Kind));
    /// <summary>Gets the name of <see cref="Class"/>: the kind, then the backend when it has one, then <c>cold</c> for a cold
    /// store, joined with dots (<c>WindowedWorld</c>, <c>WindowedWorld.vulkan</c>, <c>WindowedWorld.directx.cold</c>).</summary>
    public string Name {
        get {
            var profile = Class;

            return string.Concat(Kind.ToString(), (profile.Device is { } device) ? (Separator + device) : string.Empty, (profile.Store == StoreWarmth.Cold) ? (Separator + ColdToken) : string.Empty);
        }
    }

    /// <summary>Reads a profile's <see cref="Name"/>.</summary>
    /// <param name="name">The name.</param>
    /// <param name="profile">The profile it names.</param>
    /// <returns><see langword="true"/> when <paramref name="name"/> is a kind, then at most a backend, then optionally
    /// <c>cold</c>.</returns>
    public static bool TryParse(string? name, out HostStartProfile profile) {
        profile = default;

        var tokens = (name ?? string.Empty).Split(separator: Separator);

        if ((tokens.Length is 0 or > 3) || !Enum.TryParse<HostStartKind>(value: tokens[0], result: out var kind) || !Enum.IsDefined(value: kind)) { return false; }

        string? device = null;
        var store = StoreWarmth.Warm;

        foreach (var token in tokens.Skip(count: 1)) {
            if (token == ColdToken) { store = StoreWarmth.Cold; }
            else if ((token.Length != 0) && (device is null) && (store == StoreWarmth.Warm)) { device = token; }
            else { return false; }
        }
        profile = new HostStartProfile(Kind: kind, Device: device, Store: store).Class;

        return (profile.Name == name);
    }

    /// <summary>Gets this start's profile with its store marked cold.</summary>
    public HostStartProfile Cold => (this with { Store = StoreWarmth.Cold });
}
