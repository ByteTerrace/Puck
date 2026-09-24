using Puck.Commands;
using System.Text.Json;
using Puck.Abstractions.Machines;
using Puck.Networking;
using Puck.World.Protocol;
using Puck.World.Server;
using Xunit;

namespace Puck.World.Tests;

/// <summary>Laws for generic machine operation ordering, ownership, and typed completion framing.</summary>
public sealed class MachineOperationProtocolLawTests {
    private static WorldMachineOperation FindOperationAtLeafLength(int target) {
        var low = 0;
        var high = target;

        while (low <= high) {
            var length = (low + ((high - low) / 2));
            var leafLength = OperationLeafLength(valueLength: length);

            if (leafLength == target) {
                using var document = JsonDocument.Parse($"{{\"value\":\"{new string(
                    c: 'a',
                    count: length
                )}\"}}");

                return new WorldMachineOperation(
                    "cabinet",
                    7,
                    "device.model",
                    document.RootElement
                );
            }
            if (leafLength < target) {
                low = (length + 1);
            } else {
                high = (length - 1);
            }
        }
        throw new Xunit.Sdk.XunitException(userMessage: $"No operation payload reached exact leaf cap {target}.");
    }
    private static int OperationLeafLength(int valueLength) {
        using var document = JsonDocument.Parse($"{{\"value\":\"{new string(
            c: 'a',
            count: valueLength
        )}\"}}");
        var operation = new WorldMachineOperation(
            "cabinet",
            7,
            "device.model",
            document.RootElement
        );

        Assert.True(
            condition: WorldSubmissionCodec.TryEncode(
                new WorldSubmissionPayload.Operation(Value: operation),
                out _,
                out var leaf,
                out var failure
            ),
            userMessage: failure.ToString()
        );
        return leaf.Length;
    }

    [Fact]
    public void DefaultMachineResultIsRefusedAndAppliedCannotBePreparationRefusal() {
        Assert.Equal(
            MachineOperationStatus.Refused,
            default(MachineOperationResult).Status
        );
        Assert.Throws<ArgumentException>(testCode: () => new MachineOperationPreparation.Refusal(result: new MachineOperationResult(MachineOperationStatus.Applied)));
    }
    [Fact]
    public async Task MachineOperationCompletionIsTypedAndDetached() {
        JsonElement value;

        using (var source = JsonDocument.Parse("""{"model":"cgb","accepted":true}""")) {
            value = source.RootElement.Clone();
        }

        var result = new WorldSubmissionResult.MachineOperation(Result: new MachineOperationResult(
            reason: "model switched",
            status: MachineOperationStatus.Applied,
            value: value
        ));
        await using var stream = new MemoryStream();

        await WorldPeerWireFormat.WriteResultAsync(
            stream,
            result,
            CancellationToken.None
        );
        stream.Position = 0;

        var frame = await WorldPeerWireFormat.TryReadDownstreamAsync(
            stream,
            CancellationToken.None
        );

        Assert.NotNull(value: frame);
        Assert.Equal(
            WorldPeerWireFormat.DownstreamKind.MachineOperationOutcome,
            frame.Value.Kind
        );
        Assert.True(
            condition: WorldPeerWireFormat.TryReadResult(
                frame.Value.Kind,
                frame.Value.Body.Span,
                out var decoded,
                out var reason
            ),
            userMessage: reason
        );

        var completion = Assert.IsType<WorldSubmissionResult.MachineOperation>(@object: decoded);

        Assert.Equal(
            MachineOperationStatus.Applied,
            completion.Result.Status
        );
        Assert.Equal(
            "model switched",
            completion.Result.Reason
        );
        Assert.Equal(
            """{"model":"cgb","accepted":true}""",
            completion.Result.Value!.Value.GetRawText()
        );
    }
    [Fact]
    public void MachineSubjectIsNamedControlAuthorityWithWildcardSemantics() {
        Assert.True(condition: GrantSubject.TryParse(
            subject: out var parsed,
            token: "machine:cabinet"
        ));
        Assert.Equal(
            GrantSubject.Machine(name: "cabinet"),
            parsed
        );
        Assert.Equal(
            "machine:cabinet",
            parsed.Describe()
        );

        var grants = new WorldGrants(
            population: 8,
            routeTransition: static (_, _, _) => { },
            seatCount: 4
        );
        var addon = Principal.Addon(name: "tool");

        Assert.False(condition: grants.Allows(
            addon,
            WorldCapability.Control,
            GrantSubject.Machine(name: "cabinet")
        ).IsAllowed);
        Assert.True(
            condition: grants.TryGrant(
                grant: new WorldGrant(
                    addon,
                    WorldCapability.Control,
                    GrantSubject.Machine(name: "cabinet"),
                    Exclusive: false
                ),
                reason: out var reason
            ),
            userMessage: reason
        );
        Assert.True(condition: grants.Allows(
            addon,
            WorldCapability.Control,
            GrantSubject.Machine(name: "cabinet")
        ).IsAllowed);
        Assert.False(condition: grants.Allows(
            addon,
            WorldCapability.Control,
            GrantSubject.Machine(name: "other")
        ).IsAllowed);
    }
    [Fact]
    public void OperationLeafAcceptsExactCanonicalBoundaryAndRejectsAnOversizedLeaf() {
        var cap = WorldFrameCodec.MaxPayloadBytes(kind: WorldSubmissionKind.Operation);
        var exact = FindOperationAtLeafLength(target: cap);

        Assert.True(
            condition: WorldFrameCodec.TryEncode(
                new WorldSubmissionPayload.Operation(Value: exact),
                out var frame,
                out var exactFailure
            ),
            userMessage: exactFailure.ToString()
        );
        Assert.True(condition: FrameCodec.TrySplit(
            frame,
            maxPayloadBytes: cap,
            out _,
            out var leaf,
            out _
        ));
        Assert.Equal(
            cap,
            leaf.Length
        );

        using var source = JsonDocument.Parse("""{"value":""}""");
        var oversizedText = new string(
            c: 'a',
            count: cap
        );
        using var oversizedDocument = JsonDocument.Parse($"{{\"value\":\"{oversizedText}\"}}");
        var oversized = new WorldMachineOperation(
            "cabinet",
            7,
            "device.model",
            oversizedDocument.RootElement
        );

        Assert.False(condition: WorldFrameCodec.TryEncode(
            new WorldSubmissionPayload.Operation(Value: oversized),
            out _,
            out var oversizedFailure
        ));
        Assert.Equal(
            WorldCodecRefusal.PayloadTooLarge,
            oversizedFailure.Refusal
        );
    }
    [Fact]
    public void OperationPayloadIsDetachedAndRoundTripsNonAscii() {
        WorldMachineOperation operation;

        using (var source = JsonDocument.Parse("""{"schema":"puck.machine.operation.v1","label":"機械"}""")) {
            operation = new WorldMachineOperation(
                "cabinet",
                7,
                "device.model",
                source.RootElement
            );
        }

        Assert.Equal(
            "機械",
            operation.Payload.GetProperty(propertyName: "label").GetString()
        );
        var payload = new WorldSubmissionPayload.Operation(Value: operation);

        Assert.True(
            condition: WorldFrameCodec.TryEncode(
                failure: out var encodeFailure,
                frame: out var frame,
                payload: payload
            ),
            userMessage: encodeFailure.ToString()
        );
        Assert.True(
            condition: WorldFrameCodec.TryDecode(
                failure: out var decodeFailure,
                frame: frame,
                payload: out var decoded
            ),
            userMessage: decodeFailure.ToString()
        );

        var decodedOperation = Assert.IsType<WorldSubmissionPayload.Operation>(@object: decoded).Value;

        Assert.Equal(
            operation.Instance,
            decodedOperation.Instance
        );
        Assert.Equal(
            operation.ExpectedGeneration,
            decodedOperation.ExpectedGeneration
        );
        Assert.Equal(
            operation.OperationId,
            decodedOperation.OperationId
        );
        Assert.Equal(
            operation.Payload.GetRawText(),
            decodedOperation.Payload.GetRawText()
        );
    }
    [Fact]
    public async Task OversizedAppliedValuePreservesStatusAndFitsDownstreamCap() {
        var raw = (("{\"value\":\"" + new string(
            c: ((char)120),
            count: WorldFrameCodec.MaxPayloadBytes(kind: WorldSubmissionKind.Operation)
        )) + "\"}");
        using var document = JsonDocument.Parse(raw);
        var result = new WorldSubmissionResult.MachineOperation(Result: new MachineOperationResult(
            MachineOperationStatus.Applied,
            document.RootElement,
            "hardware completed"
        ));
        await using var stream = new MemoryStream();

        await WorldPeerWireFormat.WriteResultAsync(
            stream,
            result,
            CancellationToken.None
        );
        stream.Position = 0;
        var frame = await WorldPeerWireFormat.TryReadDownstreamAsync(
            stream,
            CancellationToken.None
        );

        Assert.NotNull(value: frame);
        Assert.True(condition: (frame.Value.Body.Length <= (((64 * 1024) - sizeof(uint)) - sizeof(byte))));
        Assert.True(
            condition: WorldPeerWireFormat.TryReadResult(
                frame.Value.Kind,
                frame.Value.Body.Span,
                out var decoded,
                out var reason
            ),
            userMessage: reason
        );
        var completion = Assert.IsType<WorldSubmissionResult.MachineOperation>(@object: decoded);

        Assert.Equal(
            MachineOperationStatus.Applied,
            completion.Result.Status
        );
        Assert.Null(value: completion.Result.Value);
        Assert.Contains(
            "omitted",
            completion.Result.Reason
        );
    }
    [Fact]
    public void TruncatedMachineOperationCompletionRefuses() {
        Assert.False(condition: WorldPeerWireFormat.TryReadResult(
            body: new byte[] { ((byte)MachineOperationStatus.Applied), 0 },
            kind: WorldPeerWireFormat.DownstreamKind.MachineOperationOutcome,
            reason: out var reason,
            result: out _
        ));
        Assert.NotEmpty(collection: reason);
    }
}
