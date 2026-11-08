using Puck.Assets;

namespace Puck.Shaders;

public sealed partial class ShaderCompiler {
    /// <summary>Exports a verified cached output into a player's ordinary derived store. The source/options identity
    /// selects it without an installed compiler; the content-addressed envelope retains the compiling toolchain's key.</summary>
    public void ExportPrepared(ShaderOutputPlan plan, string directory) {
        var bytes = ReadCached(plan) ?? throw new InvalidOperationException("Compile the output before preparing it.");
        var store = new ContentAddressedStore(directory);
        var envelope = new byte[32 + bytes.Length];
        Convert.FromHexString(plan.Key).CopyTo(envelope, 0);
        bytes.CopyTo(envelope, 32);
        store.SetDerived("shader-output", ContentPin.Parse("sha256/" + plan.Inputs), store.Put(envelope));
    }

    // Shipping cache only: live derivations and retention remain in the existing shader cache. Its CAS verifies the
    // entire envelope; an absent/corrupt output is an ordinary compiler miss, never a refusal.
    private static byte[]? ReadPrepared(ShaderOutputPlan plan) {
        foreach (var directory in new[] { Path.Combine(AppContext.BaseDirectory, "derived"), Path.Combine(Environment.CurrentDirectory, "derived") }) {
            if (!Directory.Exists(directory)) { continue; }
            try {
                var store = new ContentAddressedStore(directory);
                if (store.TryResolveDerived("shader-output", ContentPin.Parse("sha256/" + plan.Inputs), out var pin) &&
                    store.TryGet(pin, out var envelope) && envelope.Length > 32) { return envelope[32..]; }
            } catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
        }
        return null;
    }
}
