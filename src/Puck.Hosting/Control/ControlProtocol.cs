using Puck.Networking;
using System.Text.Json.Serialization;

namespace Puck.Hosting;

/// <summary>Private local-control wire limits. These bound transport memory, not simulation values.</summary>
public static class ControlLimits {
    /// <summary>Maximum UTF-8 request JSON payload, in bytes, excluding the shared frame prefix.</summary>
    public const int RequestBytes = 16 * 1024;
    /// <summary>Maximum UTF-8 response JSON payload, including base64 image data but excluding the shared frame prefix.</summary>
    public const int ResponseBytes = 24 * 1024 * 1024;
    /// <summary>Maximum completed PNG file size, in bytes.</summary>
    public const int ImageBytes = 16 * 1024 * 1024;
    /// <summary>Maximum console output length, in UTF-16 code units.</summary>
    public const int OutputCharacters = 64 * 1024;
    /// <summary>Maximum request deadline in wall-clock milliseconds.</summary>
    public const int TimeoutMilliseconds = 120_000;
}

/// <summary>A single request. IDs increase strictly within one connection; operations are exec or capture.</summary>
/// <param name="Id">Positive decimal sequence, starting at one.</param>
/// <param name="Operation">The operation discriminator.</param>
/// <param name="Command">One console line for exec; null for capture.</param>
/// <param name="TimeoutMilliseconds">Wall-clock deadline, from one through the transport ceiling.</param>
public sealed record ControlRequest(long Id, string Operation, string? Command, int TimeoutMilliseconds);

/// <summary>A correlated console result or completed PNG. Completion is never an authoritative mutation receipt.</summary>
/// <param name="Id">The host request sequence, or zero for a local admission refusal that sent no request.</param>
/// <param name="Status">completed, submitted, refused, or unknown (effects may already have occurred).</param>
/// <param name="Output">Console output or refusal detail.</param>
/// <param name="IsError">Whether the operation reports an error.</param>
/// <param name="ClearTranscript">Whether the console handler requested transcript clearing.</param>
/// <param name="Png">Completed PNG bytes, or null. Absent images are omitted from the wire.</param>
public sealed record ControlResponse(long Id, string Status, string Output, bool IsError = false, bool ClearTranscript = false,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] byte[]? Png = null) {
    /// <summary>Checks correlation, outcome semantics and bounded image/output data before exposing a host result.</summary>
    /// <param name="request">The admitted request that produced this response.</param>
    /// <returns>Whether the response satisfies the control contract.</returns>
    public bool IsValidFor(ControlRequest request) => Id == request.Id && Output is not null && Output.Length <= ControlLimits.OutputCharacters &&
        (IsError ? Status is "refused" or "unknown" : Status is "completed" or "submitted") &&
        (Png is null
            ? request.Operation != "capture" || IsError
            : request.Operation == "capture" && !IsError && Status == "completed" && !ClearTranscript && Png.Length is > 0 and <= ControlLimits.ImageBytes);
}

/// <summary>One authenticated connection's host operations. Calls are serial; disposal closes only this ingress.</summary>
public interface IControlSession : IDisposable {
    /// <summary>Runs one bounded request. Cancellation may leave already-dispatched work with an unknown outcome.</summary>
    /// <param name="request">The admitted request.</param>
    /// <param name="cancellationToken">The connection and request deadline.</param>
    /// <returns>The operation's correlated result.</returns>
    Task<ControlResponse> ExecuteAsync(ControlRequest request, CancellationToken cancellationToken);
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, MaxDepth = 8, RespectNullableAnnotations = true, RespectRequiredConstructorParameters = true, AllowDuplicateProperties = false)]
[JsonSerializable(typeof(ControlRequest))]
[JsonSerializable(typeof(ControlResponse))]
internal sealed partial class ControlJson : JsonSerializerContext;

// Host operation dialect over the shared Networking grammar. Caps below count JSON payload bytes.
internal static class ControlWire {
    internal const byte RequestKind = 1;
    internal const byte ResponseKind = 2;

    internal static async Task<ReadOnlyMemory<byte>?> ReadAsync(Stream stream, byte kind, int maximumBytes, CancellationToken token) {
        var frame = await WireFrame.ReadAsync(stream, checked(maximumBytes + WireFrame.PrefixBytes), token).ConfigureAwait(false);
        if (frame.Failure.Refusal == WireRefusal.ConnectionClosed) { return null; }
        if (frame.Failure.IsRefusal || frame.Kind != kind || frame.Body.IsEmpty) { throw new InvalidDataException("Invalid control frame."); }
        return frame.Body;
    }

    internal static Task WriteAsync(Stream stream, byte kind, ReadOnlyMemory<byte> payload, int maximumBytes, CancellationToken token) {
        if (payload.IsEmpty || payload.Length > maximumBytes) { throw new InvalidDataException("Control frame exceeds its size limit."); }
        return WireFrame.WriteAsync(stream, kind, payload, token);
    }
}
