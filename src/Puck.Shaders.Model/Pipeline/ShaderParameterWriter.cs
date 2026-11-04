using System.Buffers.Binary;
using System.Numerics;

namespace Puck.Shaders;

/// <summary>Writes shader parameter scalars and vectors at byte offsets in a little-endian block.</summary>
public static class ShaderParameterWriter {
    /// <summary>Writes one 32-bit floating-point value.</summary>
    /// <param name="block">The parameter block.</param>
    /// <param name="offset">The byte offset, representable as a span index.</param>
    /// <param name="value">The value.</param>
    public static void WriteSingle(Span<byte> block, long offset, float value) =>
        BinaryPrimitives.WriteSingleLittleEndian(destination: block[checked((int)offset)..], value: value);

    /// <summary>Writes one unsigned 32-bit integer.</summary>
    /// <param name="block">The parameter block.</param>
    /// <param name="offset">The byte offset, representable as a span index.</param>
    /// <param name="value">The value.</param>
    public static void WriteUInt32(Span<byte> block, long offset, uint value) =>
        BinaryPrimitives.WriteUInt32LittleEndian(destination: block[checked((int)offset)..], value: value);

    /// <summary>Writes three 32-bit floating-point components without padding.</summary>
    /// <param name="block">The parameter block.</param>
    /// <param name="offset">The byte offset of the first component.</param>
    /// <param name="value">The value.</param>
    public static void WriteVector3(Span<byte> block, long offset, Vector3 value) {
        WriteSingle(block, offset, value.X);
        WriteSingle(block, offset + sizeof(float), value.Y);
        WriteSingle(block, offset + (2 * sizeof(float)), value.Z);
    }

    /// <summary>Writes four 32-bit floating-point components without padding.</summary>
    /// <param name="block">The parameter block.</param>
    /// <param name="offset">The byte offset of the first component.</param>
    /// <param name="value">The value.</param>
    public static void WriteVector4(Span<byte> block, long offset, Vector4 value) {
        WriteVector3(block, offset, new Vector3(value.X, value.Y, value.Z));
        WriteSingle(block, offset + (3 * sizeof(float)), value.W);
    }
}
