using System.Text.Json;

namespace Puck.Abstractions.Machines;

/// <summary>An asset resolved and pinned by the host before machine construction.</summary>
/// <param name="Path">The logical authored path, used for diagnostics rather than runtime file reads.</param>
/// <param name="Image">The native bytes to consume, after content preparation when required.</param>
/// <param name="ContentHash">The digest of the exact source bytes read by the host.</param>
/// <param name="PreparedContent">Optional canonical source identity and symbols produced by a content provider.</param>
public sealed record PreparedMachineAsset(
    string Path, ReadOnlyMemory<byte> Image, string ContentHash, PreparedMachineContent? PreparedContent = null
);

/// <summary>A complete construction request. Configuration paths refer to the supplied immutable asset set;
/// providers do not reread authored paths during construction.</summary>
/// <param name="Configuration">The provider's versioned structured configuration.</param>
/// <param name="Assets">Resolved assets keyed by dotted configuration field path, such as content.path.</param>
/// <param name="AudioSampleRate">The desired presentation audio rate, or zero for no audio synthesis.</param>
/// <param name="SavePath">The host-owned save destination, or null for in-memory persistence.</param>
public sealed record MachineCreationRequest(
    JsonElement Configuration,
    IReadOnlyDictionary<string, PreparedMachineAsset> Assets,
    int AudioSampleRate = 0,
    string? SavePath = null
) {
    /// <summary>Returns a supplied asset, refusing an incomplete preparation before construction mutates anything.</summary>
    /// <param name="fieldPath">The dotted descriptor path.</param>
    /// <returns>The prepared asset.</returns>
    /// <exception cref="ArgumentException">The host did not supply the named asset.</exception>
    public PreparedMachineAsset RequireAsset(string fieldPath) => Assets.TryGetValue(fieldPath, out var asset)
        ? asset : throw new ArgumentException($"Configuration asset '{fieldPath}' was not prepared.", nameof(Assets));
}
