using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using Puck.Abstractions.Machines;
using Puck.Hosting;

namespace Puck.GamingBricks;

internal sealed record QueuedMachineCheckpoint(string Identity, byte[] CoreState, ulong CycleRemainder,
    long CompletedSteps, int FastForwardFactor, int RunaheadFrames) {
    private const string Format = "puck.queued-machine.v1";
    private const int MaximumBytes = 128 * 1024 * 1024;

    public byte[] Encode(in MachinePadState input) {
        using var buffer = new MemoryStream();
        using (var writer = new BinaryWriter(buffer, Encoding.UTF8, leaveOpen: true)) {
            writer.Write(Format);
            writer.Write(Identity);
            writer.Write(CycleRemainder);
            writer.Write(CompletedSteps);
            writer.Write(FastForwardFactor);
            writer.Write(RunaheadFrames);
            writer.Write((uint)input.Buttons);
            writer.Write(input.LeftStick.X); writer.Write(input.LeftStick.Y);
            writer.Write(input.RightStick.X); writer.Write(input.RightStick.Y);
            writer.Write(input.LeftTrigger); writer.Write(input.RightTrigger);
            writer.Write(input.Tilt.X); writer.Write(input.Tilt.Y);
            writer.Write(input.LightLevel);
            writer.Write(CoreState.Length);
            writer.Write(CoreState);
        }
        if (buffer.Length > MaximumBytes - 32) { throw new InvalidDataException("machine checkpoint exceeds the supported size"); }
        var hash = SHA256.HashData(buffer.GetBuffer().AsSpan(0, (int)buffer.Length));
        buffer.Write(hash);
        return buffer.ToArray();
    }

    public static (QueuedMachineCheckpoint Checkpoint, MachinePadState Input) Decode(ReadOnlyMemory<byte> bytes) {
        if (bytes.Length is < 32 or > MaximumBytes ||
            !SHA256.HashData(bytes.Span[..^32]).AsSpan().SequenceEqual(bytes.Span[^32..])) {
            throw new InvalidDataException("machine checkpoint is truncated or its content hash does not match");
        }
        using var stream = new MemoryStream(bytes[..^32].ToArray(), writable: false);
        using var reader = new BinaryReader(stream, Encoding.UTF8);
        if (reader.ReadString() != Format) { throw new InvalidDataException("machine checkpoint format is unsupported"); }
        var identity = reader.ReadString();
        var remainder = reader.ReadUInt64();
        var completed = reader.ReadInt64();
        var factor = reader.ReadInt32();
        var runahead = reader.ReadInt32();
        var buttons = (MachineButtons)reader.ReadUInt32();
        var left = new Vector2(reader.ReadSingle(), reader.ReadSingle());
        var right = new Vector2(reader.ReadSingle(), reader.ReadSingle());
        var leftTrigger = reader.ReadSingle(); var rightTrigger = reader.ReadSingle();
        var tilt = new Vector2(reader.ReadSingle(), reader.ReadSingle());
        var light = reader.ReadByte();
        var input = new MachinePadState(buttons, left, right, leftTrigger, rightTrigger, tilt, light);
        var count = reader.ReadInt32();
        if (string.IsNullOrWhiteSpace(identity) || identity.Length > 256 || remainder >= EngineTicks.PerSecond || completed < 0 ||
            factor is < 1 or > MachineTimeTravel<MachinePadState>.MaxFastForwardFactor ||
            runahead is < 0 or > MachineTimeTravel<MachinePadState>.MaxRunaheadFrames || count <= 0 || count != stream.Length - stream.Position) {
            throw new InvalidDataException("machine checkpoint has invalid identity, pacing, or payload bounds");
        }
        return (new(identity, reader.ReadBytes(count), remainder, completed, factor, runahead), input);
    }
}
