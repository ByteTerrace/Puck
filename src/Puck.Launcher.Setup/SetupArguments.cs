namespace Puck.Launcher.Setup;

/// <summary>The installer's command line: the channel's address, which channel, the install root and an optional root key
/// fingerprint to require.</summary>
/// <param name="Source">The channel tree's root (a URL or a directory), or <see langword="null"/> when the install root
/// already follows one.</param>
/// <param name="Channel">The channel's name, or <see langword="null"/> for the one the root follows or
/// <see cref="DefaultChannel"/>.</param>
/// <param name="InstallRoot">The install root's full path.</param>
/// <param name="Trust">The root key fingerprint a first install requires, or <see langword="null"/> to pin the one the
/// channel publishes.</param>
/// <param name="App">The application id the channel's manifests name.</param>
/// <param name="Help">Whether only the usage was asked for.</param>
/// <param name="Release">The content pin of one exact signed manifest to install instead of the channel's newest, or
/// <see langword="null"/> for the newest.</param>
/// <param name="ReleaseSize">The byte length of <paramref name="Release"/>'s manifest; required with it.</param>
/// <param name="Start">The executable, without an extension, the laid stub starts from the installed version.</param>
public sealed record SetupArguments(string? Source, string? Channel, string InstallRoot, string? Trust, string App, bool Help,
    Puck.Assets.ContentPin? Release = null, long ReleaseSize = 0, string Start = SetupArguments.DefaultStart) {
    /// <summary>The channel a first install follows when none is named.</summary>
    public const string DefaultChannel = "player";
    /// <summary>The application a channel's manifests name when none is given.</summary>
    public const string DefaultApp = "puck.world";
    /// <summary>The executable the stub starts when none is named.</summary>
    public const string DefaultStart = "Puck.World";

    /// <summary>The usage text.</summary>
    public const string Usage = """
        usage: Puck.Launcher.Setup [<channel address>] [--to <install root>] [--channel <name>] [--trust <root key fingerprint>] [--app <id>]
                                   [--release <manifest pin> --release-size <bytes>] [--start <executable>]

        Installs the newest release of a channel into an install root, or updates the root to it. A first install pins the
        channel's root key (or requires the one --trust names); every later run verifies against that pin. Without --to,
        the root is this executable's own directory when it follows a channel, else the per-user Puck player directory.
        --release installs the one signed manifest the channel keeps under that content pin instead of the newest, so a
        machine installs exactly the release its deployment names. --start names the executable the stub starts.
        Exit codes: 0 the root holds a current version, 1 nothing could be installed, 2 a usage error.
        """;

    /// <summary>Parses the command line.</summary>
    /// <param name="args">The arguments.</param>
    /// <returns>The parsed arguments.</returns>
    /// <exception cref="ArgumentException">An option lacks its value, or an argument is unknown.</exception>
    public static SetupArguments Parse(IReadOnlyList<string> args) {
        string? source = null, channel = null, to = null, trust = null, release = null, releaseSize = null;
        var app = DefaultApp;
        var start = DefaultStart;
        var help = false;

        for (var index = 0; (index < args.Count); index++) {
            string Value() => (((index + 1) < args.Count) ? args[++index] : throw new ArgumentException(message: $"{args[index]} needs a value."));

            switch (args[index]) {
                case "--to": to = Value(); break;
                case "--channel": channel = Value(); break;
                case "--trust": trust = Value(); break;
                case "--app": app = Value(); break;
                case "--release": release = Value(); break;
                case "--release-size": releaseSize = Value(); break;
                case "--start": start = Value(); break;
                case "--help" or "-h" or "-?": help = true; break;
                case { } argument when !argument.StartsWith(value: '-') && (source is null): source = argument; break;
                case { } argument: throw new ArgumentException(message: $"'{argument}' is not an argument this installer takes.");
            }
        }

        var own = Path.TrimEndingDirectorySeparator(path: AppContext.BaseDirectory);
        var root = (to is not null)
            ? Path.GetFullPath(path: to)
            : (File.Exists(path: Path.Combine(path1: own, path2: Puck.Launcher.Release.ChannelSubscription.FileName))
                ? own
                : Puck.Abstractions.PuckUserDirectory.Resolve(name: "player"));

        Puck.Assets.ContentPin? pin = null;
        long size = 0;

        if ((release is null) != (releaseSize is null)) { throw new ArgumentException(message: "--release and --release-size name one manifest together."); }
        if (release is not null) {
            if (!Puck.Assets.ContentPin.TryParse(pin: out var parsed, text: release)) { throw new ArgumentException(message: $"--release '{release}' is not a content pin."); }
            if (!long.TryParse(s: releaseSize, provider: System.Globalization.CultureInfo.InvariantCulture, result: out size) || (size <= 0)) {
                throw new ArgumentException(message: $"--release-size '{releaseSize}' is not a byte count.");
            }
            pin = parsed;
        }
        if (!Puck.Confinement.InstallLayout.IsPlainName(name: start)) { throw new ArgumentException(message: $"--start '{start}' is not an executable name."); }
        return new SetupArguments(App: app, Channel: channel, Help: help, InstallRoot: root, Source: source, Trust: trust, Release: pin, ReleaseSize: size, Start: start);
    }
}
