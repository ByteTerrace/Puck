using System.Text.RegularExpressions;

namespace Puck.Cli.Canary;

internal static partial class CanaryCommand {
    /// <summary>The <c>audio-output</c> capability token: a canary that reads <c>audio.state</c>'s <c>peak</c> (or any
    /// other device-dependent fact) and asserts the mix actually produced signal declares it, so a machine with no
    /// usable render device reports UNSUPPORTED instead of a failed signal assertion.</summary>
    internal const string AudioOutputRequirement = "audio-output";

    // The audio.state echo (WorldAudioCommandModule): "[audio.state: device=<token> ... fault=<none|reason>]". Only
    // the device token and the trailing fault matter here; every field between them is free to grow.
    [GeneratedRegex(pattern: @"^\[audio\.state: device=(?<device>\S+) .*fault=(?<fault>.*)\]$")]
    private static partial Regex AudioState();

    // WasapiAudioRenderDevice's own wording when Windows has no default render endpoint to open — the deterministic
    // "no audio hardware" signature, distinct from a mid-stream loss (device=rebinding, a real defect once a device
    // was already open) and from any OTHER open failure a real device could still throw.
    private const string AudioEndpointUnavailablePrefix = "render endpoint could not be opened:";

    /// <summary>Names why a leg requiring <see cref="AudioOutputRequirement"/> could not exercise its environment, or
    /// returns <see langword="null"/> when it could. <c>device=unsupported</c> (no platform render backend at all —
    /// WorldAudioRenderService's non-Windows posture) and <c>device=silent</c> naming
    /// <see cref="AudioEndpointUnavailablePrefix"/> (a platform backend exists but found no default render endpoint
    /// to open) are both the machine categorically lacking a usable audio device, read from the World's own
    /// <c>audio.state</c> echo rather than a separate host probe. A device that opened but produced no signal reports
    /// <c>device=playing</c> and is never treated as unsupported — that is a real defect.</summary>
    /// <param name="manifest">The leg's manifest — only one declaring <see cref="AudioOutputRequirement"/> is ever
    /// checked.</param>
    /// <param name="transcript">The leg's captured streams.</param>
    /// <returns>The <c>audio.state</c> line naming the unsupported device, or <see langword="null"/>.</returns>
    internal static string? AudioUnsupportedReason(CanaryManifest manifest, CanaryTranscript transcript) {
        if (!manifest.Requirements.Contains(
            value: AudioOutputRequirement,
            comparer: StringComparer.Ordinal
        )) {
            return null;
        }

        foreach (var line in transcript.Stdout) {
            if (AudioState().Match(input: line) is not { Success: true } match) {
                continue;
            }

            var device = match.Groups["device"].Value;

            if (string.Equals(
                a: device,
                b: "unsupported",
                comparisonType: StringComparison.Ordinal
            )) {
                return line;
            }
            if (
                string.Equals(a: device, b: "silent", comparisonType: StringComparison.Ordinal) &&
                match.Groups["fault"].Value.StartsWith(
                    comparisonType: StringComparison.Ordinal,
                    value: AudioEndpointUnavailablePrefix
                )
            ) {
                return line;
            }
        }

        return null;
    }
}
