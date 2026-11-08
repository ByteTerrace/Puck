using Puck.Abstractions;
using Puck.Hosting;

namespace Puck.Cli;

/// <summary>The CLI process's installed extensions, shared by its verbs and nested invocations.</summary>
public static class CliExtensions {
    private static readonly Lock Gate = new();
    private static PuckExtensionSet? Extensions;

    static CliExtensions() => AppDomain.CurrentDomain.ProcessExit += (_, _) => Extensions?.DisposeAsync().AsTask().GetAwaiter().GetResult();

    /// <summary>Gets the immutable installation for this process; registration performs no provider I/O.</summary>
    public static PuckExtensionSet Installed {
        get {
            lock (Gate) {
                return Extensions ??= PuckExtensionDiscovery.Compose(
                    builtIns: [],
                    directories: PuckExtensionDiscovery.DefaultDirectories()
                );
            }
        }
    }

    /// <summary>Installs an explicitly composed set before the process's first verb, for an embedding host or a test.
    /// The CLI owns the supplied set after success and disposes it at process exit.</summary>
    public static void Install(PuckExtensionSet extensions) {
        ArgumentNullException.ThrowIfNull(argument: extensions);

        lock (Gate) {
            if (Extensions is not null) { throw new InvalidOperationException(message: "The CLI's extensions are already installed."); }
            Extensions = extensions;
        }
    }
}
