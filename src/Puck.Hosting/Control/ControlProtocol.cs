using Puck.Networking;
using System.Text.Json.Serialization;

namespace Puck.Hosting;

/// <summary>Private local-control wire limits. These bound transport memory, not simulation values.</summary>
public static class ControlLimits {
    /// <summary>Maximum completed PNG file size, in bytes.</summary>
    public const int ImageBytes = ((16 * 1024) * 1024);
    /// <summary>Maximum console output length, in UTF-16 code units. Output up to this length is delivered whole; longer
    /// output is cut to it with a truncation marker (<see cref="ControlResponse.Bounded"/>). JSON escapes a code unit to
    /// at most six bytes, so the largest output encodes to 6 MiB, inside <see cref="ResponseBytes"/>.</summary>
    public const int OutputCharacters = (1024 * 1024);
    /// <summary>Maximum UTF-8 request JSON payload, in bytes, excluding the shared frame prefix.</summary>
    public const int RequestBytes = (16 * 1024);
    /// <summary>Maximum UTF-8 response JSON payload, including base64 image data but excluding the shared frame prefix.</summary>
    public const int ResponseBytes = ((24 * 1024) * 1024);
    /// <summary>Maximum request deadline in wall-clock milliseconds.</summary>
    public const int TimeoutMilliseconds = 120_000;
}
/// <summary>A single request. IDs increase strictly within one connection; operations are exec or capture.</summary>
/// <param name="Id">Positive decimal sequence, starting at one.</param>
/// <param name="Operation">The operation discriminator.</param>
/// <param name="Command">One console line for exec; null for capture.</param>
/// <param name="TimeoutMilliseconds">Deadline on the serving host's clock, from one through the transport ceiling.</param>
/// <param name="Capture">How a capture reads the frame, or <see langword="null"/> for one settled frame; always
/// <see langword="null"/> for exec.</param>
public sealed record ControlRequest(long Id, string Operation, string? Command, int TimeoutMilliseconds,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] ControlCapture? Capture = null);
/// <summary>How a capture request reads the render chain's frames.</summary>
/// <param name="Mode"><c>settled</c>: one still, holding presentation while temporal views converge over it.
/// <c>presented</c>: the frames the display shows next, read as presented, holding and settling nothing.</param>
/// <param name="Frames">The frames a presented capture reads, 1 through
/// <see cref="Abstractions.Presentation.FrameCaptureRequest.MaxPresentedFrames"/>; one for a settled capture. A burst
/// of more than one is written to files on the serving host and answered as a report, with no image.</param>
/// <param name="Region">The region a presented capture measures each frame's difference over, as <c>x y width
/// height</c> fractions of the frame, or <see langword="null"/> for the whole frame.</param>
public sealed record ControlCapture(string Mode, int Frames = 1,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] double[]? Region = null) {
    /// <summary>The settled mode's name.</summary>
    public const string Settled = "settled";
    /// <summary>The presented mode's name.</summary>
    public const string Presented = "presented";

    /// <summary>Gets whether the capture is a presented burst of more than one frame, answered as a report with no
    /// image.</summary>
    [JsonIgnore]
    public bool IsBurst => (Frames > 1);

    /// <summary>Returns why the options are not a capture's, or <see langword="null"/> when they are.</summary>
    /// <returns>The refusal, or <see langword="null"/>.</returns>
    public string? RefusalOf() {
        if (Mode is not (Settled or Presented)) {
            return "Capture mode must be settled or presented.";
        }
        if ((Frames < 1) || (Frames > Abstractions.Presentation.FrameCaptureRequest.MaxPresentedFrames)) {
            return $"Capture frames must be 1..{Abstractions.Presentation.FrameCaptureRequest.MaxPresentedFrames}.";
        }
        if ((Mode == Settled) && ((Frames != 1) || (Region is not null))) {
            return "A settled capture is one frame and takes no region; frames and region are presented options.";
        }
        if (Region is { } region) {
            return ((region.Length != 4)
                ? "Capture region must be four numbers: x, y, width, height."
                : (Abstractions.Presentation.FrameCaptureRegion.RefusalOf(height: region[3], width: region[2], x: region[0], y: region[1]) is { } refusal)
                    ? $"Capture region: {refusal}."
                    : null);
        }

        return null;
    }
}
/// <summary>A correlated console result or completed PNG. Completion is never an authoritative mutation receipt.</summary>
/// <param name="Id">The host request sequence, or zero for a local admission refusal that sent no request.</param>
/// <param name="Status">completed, submitted, refused, or unknown (effects may already have occurred).</param>
/// <param name="Output">Console output or refusal detail.</param>
/// <param name="IsError">Whether the operation reports an error.</param>
/// <param name="ClearTranscript">Whether the console handler requested transcript clearing.</param>
/// <param name="Png">Completed PNG bytes, or null. Absent images are omitted from the wire.</param>
/// <param name="Truncated">Whether <paramref name="Output"/> is the head of a longer console output, cut to
/// <see cref="ControlLimits.OutputCharacters"/> and ending in a marker that names the full length. The status still
/// reports the command's own outcome.</param>
public sealed record ControlResponse(long Id, string Status, string Output, bool IsError = false, bool ClearTranscript = false,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] byte[]? Png = null, bool Truncated = false) {
    /// <summary>Returns this response with output that fits <see cref="ControlLimits.OutputCharacters"/>: the response
    /// itself when its output fits, otherwise a copy whose output is the longest head that fits beside a marker naming
    /// the full length, marked <see cref="Truncated"/>. A long result is a completed read, never an unknown outcome, so
    /// the status, error flag and transcript request are kept.</summary>
    /// <returns>A response whose output is within the limit.</returns>
    public ControlResponse Bounded() {
        if (
            (Output is null) ||
            (Output.Length <= ControlLimits.OutputCharacters)
        ) {
            return this;
        }

        var marker = string.Create(
            provider: System.Globalization.CultureInfo.InvariantCulture,
            handler: $"\n[control: output truncated; its {Output.Length} characters exceed the {ControlLimits.OutputCharacters}-character limit. Narrow the command to read the rest, such as help <prefix> or a counter source.]"
        );
        var kept = (ControlLimits.OutputCharacters - marker.Length);

        // A cut between the halves of a surrogate pair would leave an unpaired surrogate that JSON cannot carry.
        if (char.IsHighSurrogate(c: Output[(kept - 1)])) {
            kept--;
        }

        return (this with { Output = string.Concat(str0: Output.AsSpan(length: kept, start: 0), str1: marker), Truncated = true });
    }
    /// <summary>Checks correlation, outcome semantics and bounded image/output data before exposing a host result.</summary>
    /// <param name="request">The admitted request that produced this response.</param>
    /// <returns>Whether the response satisfies the control contract.</returns>
    public bool IsValidFor(ControlRequest request) => ((Id == request.Id) && (Output is not null) && (Output.Length <= ControlLimits.OutputCharacters) &&
        (IsError
        ? (Status is "refused" or "unknown")
        : (Status is "completed" or "submitted")) &&
        ((Png is null)
        ? ((request.Operation != "capture") || IsError || (request.Capture is { IsBurst: true }))
        : ((request.Operation == "capture") && (request.Capture is not { IsBurst: true }) && !IsError && (Status == "completed") && !ClearTranscript && !Truncated && (Png.Length is > 0 and <= ControlLimits.ImageBytes))));
}
/// <summary>One authenticated connection's host operations. Calls are serial; disposal closes only this ingress.</summary>
public interface IControlSession : IDisposable {
    /// <summary>Runs one bounded request. Cancellation may leave already-dispatched work with an unknown outcome.</summary>
    /// <param name="request">The admitted request.</param>
    /// <param name="cancellationToken">The connection and request deadline.</param>
    /// <returns>The operation's correlated result.</returns>
    Task<ControlResponse> ExecuteAsync(ControlRequest request, CancellationToken cancellationToken);
}

[JsonSerializable(typeof(ControlRequest))]
[JsonSerializable(typeof(ControlResponse))]
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, MaxDepth = 8, RespectNullableAnnotations = true, RespectRequiredConstructorParameters = true, AllowDuplicateProperties = false)]
internal sealed partial class ControlJson : JsonSerializerContext;
// Host operation dialect over the shared Networking grammar. Caps below count JSON payload bytes.
internal static class ControlWire {
    internal const byte RequestKind = 1;
    internal const byte ResponseKind = 2;

    internal static async Task<ReadOnlyMemory<byte>?> ReadAsync(Stream stream, byte kind, int maximumBytes, CancellationToken token) {
        var frame = await WireFrame.ReadAsync(
            ct: token,
            maxFrameBytes: checked((maximumBytes + WireFrame.PrefixBytes)),
            stream: stream
        ).ConfigureAwait(continueOnCapturedContext: false);

        if (frame.Failure.Refusal == WireRefusal.ConnectionClosed) { return null; }
        if (
            frame.Failure.IsRefusal ||
            (frame.Kind != kind) ||
            frame.Body.IsEmpty
        ) { throw new InvalidDataException(message: "Invalid control frame."); }
        return frame.Body;
    }
    internal static Task WriteAsync(Stream stream, byte kind, ReadOnlyMemory<byte> payload, int maximumBytes, CancellationToken token) {
        if (
            payload.IsEmpty ||
            (payload.Length > maximumBytes)
        ) { throw new InvalidDataException(message: "Control frame exceeds its size limit."); }
        return WireFrame.WriteAsync(
            body: payload,
            ct: token,
            kind: kind,
            stream: stream
        );
    }
}
