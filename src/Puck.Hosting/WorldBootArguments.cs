using System.Globalization;

namespace Puck.World;

/// <summary>The arguments every World boot carries and the one place that spells them: <c>--world</c>, <c>--devices</c>,
/// <c>--headless</c>, <c>--backend</c>, <c>--state-dir</c> and <c>--exit-after-seconds</c>. The CLI's World launch and a
/// test batch's in-process boots both build their command line from it, so the two never spell a shared flag apart.</summary>
/// <param name="World">The world the boot loads (<c>--world</c>), or <see langword="null"/> when the caller's own
/// arguments name what it runs.</param>
public sealed record WorldBootArguments(string? World = null) {
    /// <summary>Gets whether the World opens the machine's physical input devices (<c>--devices all</c>), which only a
    /// World the owner asked to play in does; every other World passes <c>--devices none</c>.</summary>
    public bool OwnerDevices { get; init; }
    /// <summary>Gets whether the World boots headless (<c>--headless true</c>) or windowed (<c>--headless false</c>), or
    /// <see langword="null"/> to pass no <c>--headless</c> and let the world document's presentation decide.</summary>
    public bool? Headless { get; init; }
    /// <summary>Gets the backend the World boots on (<c>--backend</c>), or <see langword="null"/> to let the document
    /// decide.</summary>
    public WorldBackendPreference? Backend { get; init; }
    /// <summary>Gets the World's state directory (<c>--state-dir</c>), or <see langword="null"/>.</summary>
    public string? StateDirectory { get; init; }
    /// <summary>Gets the World's own exit backstop in seconds (<c>--exit-after-seconds</c>; zero disables it), or
    /// <see langword="null"/> to let the document decide.</summary>
    public int? ExitAfterSeconds { get; init; }

    /// <summary>Builds the arguments, in the order the flags are listed above.</summary>
    /// <returns>The arguments, ahead of any the caller adds.</returns>
    public IReadOnlyList<string> Build() {
        var arguments = new List<string>();

        if (World is { } world) { arguments.AddRange(collection: ["--world", world]); }
        arguments.AddRange(collection: ["--devices", (OwnerDevices ? "all" : "none")]);
        if (Headless is { } headless) { arguments.AddRange(collection: ["--headless", (headless ? "true" : "false")]); }
        if (Backend is { } backend) { arguments.AddRange(collection: ["--backend", WorldBackendTokens.Token(backend: backend)]); }
        if (StateDirectory is { } state) { arguments.AddRange(collection: ["--state-dir", state]); }
        if (ExitAfterSeconds is { } seconds) { arguments.AddRange(collection: ["--exit-after-seconds", seconds.ToString(provider: CultureInfo.InvariantCulture)]); }

        return arguments;
    }
}
