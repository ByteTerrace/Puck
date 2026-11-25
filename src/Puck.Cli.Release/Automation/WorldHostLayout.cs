namespace Puck.Cli.Automation;

/// <summary>The layout of a host release (<c>puck world host build</c>) and of the hosting machine that installs one.
/// A host release is an install payload like a player build's: the CLI, which carries the silo, published with its own
/// runtime at the version's root, its launcher in <c>launcher/</c>, the composed hosted worlds in
/// <see cref="WorldsDirectory"/> and its world extensions in <see cref="ExtensionsDirectory"/>. Every path a step names
/// inside an installed version comes from here.</summary>
public static class WorldHostLayout {
    /// <summary>The release channel a host release is published on.</summary>
    public const string Channel = "server";
    /// <summary>The application id a host release's manifest names.</summary>
    public const string App = "puck.world.host";
    /// <summary>The CLI executable at a version's root, without an extension: the one program a hosting machine runs.</summary>
    public const string Executable = "puck";
    /// <summary>The directory, relative to an installed version, holding the composed hosted documents inside the authored
    /// asset layout they resolve against: every silo document a host runs names it as its <c>documentDirectory</c>,
    /// resolved against the version directory, the service's working directory.</summary>
    public const string WorldsDirectory = "worlds";
    /// <summary>The directory, relative to an installed version, holding its world extensions, one directory each. The
    /// CLI and the silo discover it beside their own assemblies (<c>PuckExtensionDiscovery.DefaultDirectories</c>).</summary>
    public const string ExtensionsDirectory = "extensions";
    /// <summary>The TLS front's executable, relative to an installed version, when the release carries one.</summary>
    public const string TlsFrontExecutable = "tls/caddy";

    /// <summary>The install root a hosting machine installs its host release into.</summary>
    public const string InstallRoot = "/opt/puck/world";
    /// <summary>The root-only directory holding the machine's host configuration and the files its services load as
    /// systemd credentials.</summary>
    public const string ConfigurationDirectory = "/etc/puck";
    /// <summary>The system account every Puck service on a hosting machine runs as; it owns the install root.</summary>
    public const string Account = "puck";
    /// <summary>The silo's state directory, a systemd <c>StateDirectory</c>.</summary>
    public const string SiloStateDirectory = "/var/lib/puck-world";
    /// <summary>The TLS front's and its state keeper's shared state directory, a systemd <c>StateDirectory</c>.</summary>
    public const string TlsStateDirectory = "/var/lib/puck-tls";
    /// <summary>The lock every delayed effect on a hosting machine holds across its guard and the effect.</summary>
    public const string ReleaseLock = "/run/puck-world-release.lock";
    /// <summary>The silo's systemd unit.</summary>
    public const string SiloUnit = "puck-world.service";
    /// <summary>The TLS front's systemd unit.</summary>
    public const string TlsUnit = "puck-tls.service";
    /// <summary>The TLS state keeper's systemd unit.</summary>
    public const string TlsKeeperUnit = "puck-tls-keeper.service";
    /// <summary>The stable path of the installed CLI, a link to the current version's <see cref="Executable"/>, which
    /// the units and a controller's guest commands run.</summary>
    public const string HostExecutable = "/usr/local/bin/puck";

    /// <summary>Returns the directory systemd exposes a unit's credentials in.</summary>
    /// <param name="unit">The unit's name.</param>
    /// <returns>The credentials directory.</returns>
    public static string CredentialsDirectory(string unit) => $"/run/credentials/{unit}";
}
