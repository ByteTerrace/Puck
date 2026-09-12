using System.Text.Json;
using Puck.Abstractions.Machines;
using Puck.Networking;
using Puck.World.Protocol;
using Puck.World.Server;
using Xunit;

namespace Puck.World.Tests;

/// <summary>Laws for generic machine operation ordering, ownership, and typed completion framing.</summary>
public sealed class MachineOperationProtocolLawTests {
    [Fact]
    public void OperationPayloadIsDetachedAndRoundTripsNonAscii() {
        WorldMachineOperation operation;
        using (var source = JsonDocument.Parse("""{"schema":"puck.machine.operation.v1","label":"機械"}""")) {
            operation = new WorldMachineOperation("cabinet", 7, "device.model", source.RootElement);
        }

        Assert.Equal("機械", operation.Payload.GetProperty("label").GetString());
        var payload = new WorldSubmissionPayload.Operation(operation);
        Assert.True(WorldFrameCodec.TryEncode(payload, out var frame, out var encodeFailure), encodeFailure.ToString());
        Assert.True(WorldFrameCodec.TryDecode(frame, out var decoded, out var decodeFailure), decodeFailure.ToString());

        var decodedOperation = Assert.IsType<WorldSubmissionPayload.Operation>(decoded).Value;
        Assert.Equal(operation.Instance, decodedOperation.Instance);
        Assert.Equal(operation.ExpectedGeneration, decodedOperation.ExpectedGeneration);
        Assert.Equal(operation.OperationId, decodedOperation.OperationId);
        Assert.Equal(operation.Payload.GetRawText(), decodedOperation.Payload.GetRawText());
    }

    [Fact]
    public void OperationLeafAcceptsExactCanonicalBoundaryAndRejectsAnOversizedLeaf() {
        var cap = WorldFrameCodec.MaxPayloadBytes(WorldSubmissionKind.Operation);
        var exact = FindOperationAtLeafLength(cap);

        Assert.True(WorldFrameCodec.TryEncode(
            new WorldSubmissionPayload.Operation(exact),
            out var frame,
            out var exactFailure
        ), exactFailure.ToString());
        Assert.True(FrameCodec.TrySplit(frame, maxPayloadBytes: cap, out _, out var leaf, out _));
        Assert.Equal(cap, leaf.Length);

        using var source = JsonDocument.Parse("""{"value":""}""");
        var oversizedText = new string('a', cap);
        using var oversizedDocument = JsonDocument.Parse($"{{\"value\":\"{oversizedText}\"}}");
        var oversized = new WorldMachineOperation("cabinet", 7, "device.model", oversizedDocument.RootElement);
        Assert.False(WorldFrameCodec.TryEncode(
            new WorldSubmissionPayload.Operation(oversized),
            out _,
            out var oversizedFailure
        ));
        Assert.Equal(WorldCodecRefusal.PayloadTooLarge, oversizedFailure.Refusal);
    }

    [Fact]
    public async Task MachineOperationCompletionIsTypedAndDetached() {
        JsonElement value;
        using (var source = JsonDocument.Parse("""{"model":"cgb","accepted":true}""")) {
            value = source.RootElement.Clone();
        }

        var result = new WorldSubmissionResult.MachineOperation(
            new MachineOperationResult(
                MachineOperationStatus.Applied,
                value,
                "model switched"
            )
        );
        await using var stream = new MemoryStream();
        await WorldPeerWireFormat.WriteResultAsync(stream, result, CancellationToken.None);
        stream.Position = 0;

        var frame = await WorldPeerWireFormat.TryReadDownstreamAsync(stream, CancellationToken.None);
        Assert.NotNull(frame);
        Assert.Equal(WorldPeerWireFormat.DownstreamKind.MachineOperationOutcome, frame.Value.Kind);
        Assert.True(WorldPeerWireFormat.TryReadResult(frame.Value.Kind, frame.Value.Body.Span, out var decoded, out var reason), reason);

        var completion = Assert.IsType<WorldSubmissionResult.MachineOperation>(decoded);
        Assert.Equal(MachineOperationStatus.Applied, completion.Result.Status);
        Assert.Equal("model switched", completion.Result.Reason);
        Assert.Equal("""{"model":"cgb","accepted":true}""", completion.Result.Value!.Value.GetRawText());
    }

    [Fact]
    public async Task OversizedAppliedValuePreservesStatusAndFitsDownstreamCap() {
        var raw = "{\"value\":\"" + new string((char)120, WorldFrameCodec.MaxPayloadBytes(WorldSubmissionKind.Operation)) + "\"}";
        using var document = JsonDocument.Parse(raw);
        var result = new WorldSubmissionResult.MachineOperation(new MachineOperationResult(
            MachineOperationStatus.Applied,
            document.RootElement,
            "hardware completed"
        ));
        await using var stream = new MemoryStream();
        await WorldPeerWireFormat.WriteResultAsync(stream, result, CancellationToken.None);
        stream.Position = 0;
        var frame = await WorldPeerWireFormat.TryReadDownstreamAsync(stream, CancellationToken.None);
        Assert.NotNull(frame);
        Assert.True(frame.Value.Body.Length <= 64 * 1024 - sizeof(uint) - sizeof(byte));
        Assert.True(WorldPeerWireFormat.TryReadResult(frame.Value.Kind, frame.Value.Body.Span, out var decoded, out var reason), reason);
        var completion = Assert.IsType<WorldSubmissionResult.MachineOperation>(decoded);
        Assert.Equal(MachineOperationStatus.Applied, completion.Result.Status);
        Assert.Null(completion.Result.Value);
        Assert.Contains("omitted", completion.Result.Reason);
    }

    [Fact]
    public void TruncatedMachineOperationCompletionRefuses() {
        Assert.False(WorldPeerWireFormat.TryReadResult(
            WorldPeerWireFormat.DownstreamKind.MachineOperationOutcome,
            new byte[] { (byte)MachineOperationStatus.Applied, 0 },
            out _,
            out var reason
        ));
        Assert.NotEmpty(reason);
    }
    [Fact]
    public void MachineSubjectIsNamedControlAuthorityWithWildcardSemantics() {
        Assert.True(GrantSubject.TryParse("machine:cabinet", out var parsed));
        Assert.Equal(GrantSubject.Machine("cabinet"), parsed);
        Assert.Equal("machine:cabinet", parsed.Describe());

        var grants = new WorldGrants(4, 8, static (_, _, _) => { });
        var addon = WorldPrincipal.Addon("tool");
        Assert.False(grants.Allows(addon, WorldCapability.Control, GrantSubject.Machine("cabinet")).IsAllowed);
        Assert.True(grants.TryGrant(
            new WorldGrant(addon, WorldCapability.Control, GrantSubject.Machine("cabinet"), Exclusive: false),
            out var reason
        ), reason);
        Assert.True(grants.Allows(addon, WorldCapability.Control, GrantSubject.Machine("cabinet")).IsAllowed);
        Assert.False(grants.Allows(addon, WorldCapability.Control, GrantSubject.Machine("other")).IsAllowed);
    }

    [Fact]
    public void DefaultMachineResultIsRefusedAndAppliedCannotBePreparationRefusal() {
        Assert.Equal(MachineOperationStatus.Refused, default(MachineOperationResult).Status);
        Assert.Throws<ArgumentException>(() => new MachineOperationPreparation.Refusal(
            new MachineOperationResult(MachineOperationStatus.Applied)
        ));
    }

    private static WorldMachineOperation FindOperationAtLeafLength(int target) {
        var low = 0;
        var high = target;
        while (low <= high) {
            var length = low + ((high - low) / 2);
            var leafLength = OperationLeafLength(length);
            if (leafLength == target) {
                using var document = JsonDocument.Parse($"{{\"value\":\"{new string('a', length)}\"}}");
                return new WorldMachineOperation("cabinet", 7, "device.model", document.RootElement);
            }
            if (leafLength < target) {
                low = length + 1;
            } else {
                high = length - 1;
            }
        }
        throw new Xunit.Sdk.XunitException($"No operation payload reached exact leaf cap {target}.");
    }

    private static int OperationLeafLength(int valueLength) {
        using var document = JsonDocument.Parse($"{{\"value\":\"{new string('a', valueLength)}\"}}");
        var operation = new WorldMachineOperation("cabinet", 7, "device.model", document.RootElement);
        Assert.True(WorldSubmissionCodec.TryEncode(new WorldSubmissionPayload.Operation(operation), out _, out var leaf, out var failure), failure.ToString());
        return leaf.Length;
    }
}
