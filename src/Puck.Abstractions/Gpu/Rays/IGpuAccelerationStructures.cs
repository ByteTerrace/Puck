using System.Buffers.Binary;
using System.Numerics;
using System.Runtime.InteropServices;

namespace Puck.Abstractions.Gpu;

/// <summary>
/// The top-level instance record both backends share byte for byte: Direct3D 12's <c>D3D12_RAYTRACING_INSTANCE_DESC</c>
/// and Vulkan's <c>VkAccelerationStructureInstanceKHR</c> are a row-major 3×4 transform of twelve floats, then the
/// instance's 24-bit index under an 8-bit mask, a 24-bit hit-group offset under 8 bits of flags, and the bottom level's
/// 64-bit address, with flag values that agree (cull disable 1, force opaque 4).
/// </summary>
public static class GpuAccelerationStructureInstance {
    /// <summary>The bytes one record occupies.</summary>
    public const int Bytes = 64;
    /// <summary>The largest index a record carries.</summary>
    public const uint MaxIndex = ((1U << 24) - 1U);

    // TRIANGLE_CULL_DISABLE | FORCE_OPAQUE: both faces answer, and no any-hit work runs.
    private const uint Flags = (0x1U | 0x4U);

    /// <summary>Writes one record: visible to every ray (mask 0xFF), opaque, culling no face.</summary>
    /// <param name="destination">The record's bytes, at least <see cref="Bytes"/> long.</param>
    /// <param name="objectToWorld">The bottom level's transform to the world, in the row-vector convention of
    /// <see cref="Matrix4x4"/>; its fourth column is ignored.</param>
    /// <param name="index">The index a hit reads back, at most <see cref="MaxIndex"/>.</param>
    /// <param name="bottomAddress">The bottom level's device address.</param>
    /// <exception cref="ArgumentException"><paramref name="destination"/> is shorter than <see cref="Bytes"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="index"/> is above <see cref="MaxIndex"/>.</exception>
    public static void Write(Span<byte> destination, in Matrix4x4 objectToWorld, uint index, ulong bottomAddress) {
        if (destination.Length < Bytes) {
            throw new ArgumentException(message: $"An instance record is {Bytes} bytes.", paramName: nameof(destination));
        }
        ArgumentOutOfRangeException.ThrowIfGreaterThan(value: index, other: MaxIndex);

        var m = objectToWorld;
        var transform = MemoryMarshal.Cast<byte, float>(destination[..48]);

        // Row r of the column-vector 3×4 is column r of the row-vector matrix, its translation last.
        transform[0] = m.M11; transform[1] = m.M21; transform[2] = m.M31; transform[3] = m.M41;
        transform[4] = m.M12; transform[5] = m.M22; transform[6] = m.M32; transform[7] = m.M42;
        transform[8] = m.M13; transform[9] = m.M23; transform[10] = m.M33; transform[11] = m.M43;
        BinaryPrimitives.WriteUInt32LittleEndian(destination: destination[48..], value: (index | (0xFFU << 24)));
        BinaryPrimitives.WriteUInt32LittleEndian(destination: destination[52..], value: (Flags << 24));
        BinaryPrimitives.WriteUInt64LittleEndian(destination: destination[56..], value: bottomAddress);
    }
}

/// <summary>
/// One built acceleration structure: the hierarchy over triangles, or over instances of such hierarchies, that a GPU's
/// ray hardware walks. It is opaque to its device and driver, so it is always derived on the device that traces it and
/// is never stored or shared. Its owner disposes it only once no submission that builds, copies or traces it can still
/// run; disposal frees its storage at once.
/// </summary>
public interface IGpuAccelerationStructure : IDisposable {
    /// <summary>Gets the structure's level.</summary>
    GpuAccelerationStructureLevel Level { get; }
    /// <summary>Gets the bytes of storage the structure holds.</summary>
    ulong SizeBytes { get; }
    /// <summary>Gets the structure's device address, which a top level's instance record names a bottom level by.</summary>
    ulong Address { get; }
}

/// <summary>
/// A device's acceleration structures (<see cref="GpuOptionalCapabilities.RayQuery"/>): sizing a build, creating a
/// structure, writing a top level's instance records, and binding a top level to a kernel. Building, compacting and
/// copying are commands on the frame's recorder (<see cref="IGpuRecorder"/>); a build's scratch is any device-local
/// buffer of <see cref="GpuBufferUsage.Storage"/> and <see cref="GpuBufferUsage.AccelerationStructureInput"/> the size
/// <see cref="Size"/> reports, which carries any alignment slack the backend needs. Every member refuses on a device
/// created without ray queries.
/// </summary>
public interface IGpuAccelerationStructures {
    /// <summary>Gets the bytes one instance record occupies, the same on both backends (64).</summary>
    int InstanceBytes { get; }

    /// <summary>Returns what building a structure over some inputs needs, as the driver reports it. Reads the inputs'
    /// counts and formats, never their contents.</summary>
    /// <param name="inputs">The inputs; their buffers are not read.</param>
    /// <returns>The sizes.</returns>
    /// <exception cref="NotSupportedException">The device was created without ray queries.</exception>
    GpuAccelerationStructureSizes Size(in GpuAccelerationStructureInputs inputs);
    /// <summary>Creates a structure's storage, which a build or a copy then fills. It counts against the device's memory
    /// like any buffer, under the family <paramref name="name"/> names.</summary>
    /// <param name="level">The structure's level.</param>
    /// <param name="sizeBytes">The bytes of storage; positive.</param>
    /// <param name="name">The structure's name.</param>
    /// <returns>The structure, owned by the caller.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="sizeBytes"/> is zero.</exception>
    /// <exception cref="NotSupportedException">The device was created without ray queries.</exception>
    IGpuAccelerationStructure Create(GpuAccelerationStructureLevel level, ulong sizeBytes, in GpuObjectName name);

    /// <summary>Writes one top-level instance record: a bottom level under a transform, with the index a ray query reads
    /// back for a hit on it (<c>CommittedInstanceID</c>). The record is opaque, visible to every ray and culls no face.</summary>
    /// <param name="destination">The record's bytes, <see cref="InstanceBytes"/> long.</param>
    /// <param name="objectToWorld">The transform from the bottom level's frame to the world, in the row-vector convention
    /// of <see cref="Matrix4x4"/> (translation in its fourth row); its fourth column is ignored.</param>
    /// <param name="index">The instance's index, below 2^24.</param>
    /// <param name="bottom">The bottom level the record names; it must outlive every trace of a top level built from the
    /// record.</param>
    /// <exception cref="ArgumentException"><paramref name="destination"/> is shorter than <see cref="InstanceBytes"/>, or
    /// <paramref name="bottom"/> is not a bottom level.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="index"/> is 2^24 or above.</exception>
    void WriteInstance(Span<byte> destination, in Matrix4x4 objectToWorld, uint index, IGpuAccelerationStructure bottom);
    /// <summary>Points one binding of a descriptor set at a top level, which a kernel then traces
    /// (<c>RaytracingAccelerationStructure</c>).</summary>
    /// <param name="descriptorSetHandle">The descriptor set.</param>
    /// <param name="binding">The binding, of kind <see cref="GpuBindingKind.AccelerationStructure"/>.</param>
    /// <param name="structure">The top level; it must outlive every submission that reads the set.</param>
    /// <exception cref="ArgumentException"><paramref name="structure"/> is not a top level.</exception>
    /// <exception cref="NotSupportedException">The device was created without ray queries.</exception>
    void WriteDescriptor(nint descriptorSetHandle, uint binding, IGpuAccelerationStructure structure);
}
