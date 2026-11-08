using Puck.Abstractions;
using Puck.Hosting;

namespace Puck.Cli;

/// <summary>The CLI process's installed extensions, shared by its verbs and nested invocations.</summary>
public static class CliExtensions {
    private static readonly AsyncLocal<PuckExtensionSet?> Current = new();
    private static readonly Lazy<PuckExtensionSet> Extensions = new(valueFactory: () => {
        var extensions = PuckExtensionDiscovery.Compose(
            builtIns: [],
            directories: PuckExtensionDiscovery.DefaultDirectories()
        );

        AppDomain.CurrentDomain.ProcessExit += (_, _) => extensions.DisposeAsync().AsTask().GetAwaiter().GetResult();
        return extensions;
    });

    /// <summary>Gets the immutable installation for this process; registration performs no provider I/O.</summary>
    public static PuckExtensionSet Installed => (Current.Value ?? Extensions.Value);

    /// <summary>Uses a caller-owned extension set for this invocation and its nested verbs. Disposing the scope
    /// restores the previous selection; the caller retains ownership of the set.</summary>
    public static IDisposable Use(PuckExtensionSet extensions) {
        ArgumentNullException.ThrowIfNull(argument: extensions);
        var previous = Current.Value;
        Current.Value = extensions;
        return new Selection(previous: previous);
    }

    private sealed class Selection(PuckExtensionSet? previous) : IDisposable {
        public void Dispose() => Current.Value = previous;
    }
}
