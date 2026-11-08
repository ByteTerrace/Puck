using Puck.Assets;

namespace Puck.Shaders;

public sealed partial class ShaderCompiler {
    // A shipping entry is named by the plan's inputs, which exclude the toolchain, so a player with no compiler finds it;
    // its envelope keeps the compiling toolchain's cache key ahead of the bytecode as provenance.
    private const string PreparedKind = "shader-output";
    private const int PreparedKeyBytes = 32;

    /// <summary>Exports a planned output the cache holds into a player's shipping cache, where every compiler that plans
    /// the same output reads it before its own cache (<see cref="CompileOutputAsync"/>), with no toolchain needed.</summary>
    /// <param name="plan">The plan, whose output the cache holds.</param>
    /// <param name="directory">The shipping cache directory (<see cref="DerivedBytes.ShippingDirectoryName"/> beside the
    /// player).</param>
    /// <exception cref="ArgumentNullException"><paramref name="plan"/> or <paramref name="directory"/> is
    /// <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">The cache does not hold the plan's output.</exception>
    /// <exception cref="IOException">The shipping cache cannot be written.</exception>
    public void ExportPrepared(ShaderOutputPlan plan, string directory) {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(directory);

        var bytes = (ReadCached(plan: plan) ?? throw new InvalidOperationException(message: $"The cache holds no output for '{plan.Stage.Path}': compile it before exporting it."));
        var store = new ContentAddressedStore(root: directory);
        var envelope = new byte[PreparedKeyBytes + bytes.Length];

        Convert.FromHexString(s: plan.Key).CopyTo(array: envelope, index: 0);
        bytes.CopyTo(array: envelope, index: PreparedKeyBytes);
        store.SetDerived(kind: PreparedKind, inputHash: ContentPin.Parse(text: "sha256/" + plan.Inputs), outputHash: store.Put(content: envelope));
    }

    // The store verifies the whole envelope; an absent, damaged or unreadable entry is an ordinary miss, never a refusal.
    private static byte[]? ReadPrepared(ShaderOutputPlan plan) {
        foreach (var directory in DerivedBytes.ShippingDirectories()) {
            if (!Directory.Exists(path: directory)) { continue; }
            try {
                var store = new ContentAddressedStore(root: directory);

                if (store.TryResolveDerived(kind: PreparedKind, inputHash: ContentPin.Parse(text: "sha256/" + plan.Inputs), hash: out var pin) &&
                    store.TryGet(pin: pin, content: out var envelope) && (envelope.Length > PreparedKeyBytes)) {
                    return envelope[PreparedKeyBytes..];
                }
            } catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) {
            }
        }
        return null;
    }
}
