using System.Runtime.Versioning;
using Puck.DirectX.Interop;
using Puck.Maths;
using Windows.Win32.Graphics.Direct3D12;
using Windows.Win32.Graphics.Dxgi.Common;
using Windows.Win32.System.Com;

namespace Puck.DirectX;

/// <summary>
/// Implements <see cref="IGpuAccelerationStructures"/> for Direct3D 12 on a device at raytracing tier 1.1: a structure
/// is a default-heap buffer created in <c>RAYTRACING_ACCELERATION_STRUCTURE</c>, which it never leaves, and every
/// address a build reads is its buffer's virtual address. The build, compaction and copy commands
/// <see cref="DirectXGpuRecorder"/> records describe their inputs through <see cref="Describe"/>.
/// </summary>
/// <param name="deviceContext">The device context every structure is created on.</param>
[SupportedOSPlatform("windows10.0.10240")]
public sealed unsafe class DirectXGpuAccelerationStructures(DirectXDeviceContext deviceContext) : IGpuAccelerationStructures {
    // D3D12_RAYTRACING_ACCELERATION_STRUCTURE_BYTE_ALIGNMENT: a structure's and a scratch's address. A placed buffer's
    // address is aligned to its 64 KiB placement, a committed one's to its 64 KiB resource alignment.
    private const ulong StructureAlignment = 256UL;


    /// <inheritdoc/>
    public GpuAccelerationStructureSizes Size(in GpuAccelerationStructureInputs inputs) {
        // ID3D12Device5 and its prebuild query arrived with Windows 10 1809, which raytracing tier 1.1 implies.
        if (!OperatingSystem.IsWindowsVersionAtLeast(major: 10, minor: 0, build: 17763)) {
            throw new NotSupportedException(message: "Direct3D 12 acceleration structures need Windows 10 1809 or later.");
        }
        RequireRayQuery();

        // The prebuild query sees the description the build records, addresses included: a null index or vertex address
        // describes other geometry than the indexed triangles built.
        var geometry = default(D3D12_RAYTRACING_GEOMETRY_DESC);
        var description = Describe(geometry: &geometry, inputs: in inputs);

        if (((IUnknown*)Device)->QueryInterface(ppvObject: out ID3D12Device5* device5).Failed) {
            throw new NotSupportedException(message: "This Direct3D 12 device has no ID3D12Device5.");
        }

        try {
            var info = default(D3D12_RAYTRACING_ACCELERATION_STRUCTURE_PREBUILD_INFO);

            device5->GetRaytracingAccelerationStructurePrebuildInfo(&description, &info);

            return new GpuAccelerationStructureSizes(
                ScratchBytes: AlignUp(value: info.ScratchDataSizeInBytes),
                StructureBytes: AlignUp(value: info.ResultDataMaxSizeInBytes)
            );
        } finally {
            _ = ((IUnknown*)device5)->Release();
        }
    }
    /// <inheritdoc/>
    public IGpuAccelerationStructure Create(GpuAccelerationStructureLevel level, ulong sizeBytes, in GpuObjectName name) {
        ArgumentOutOfRangeException.ThrowIfZero(value: sizeBytes);
        RequireRayQuery();

        var buffer = DirectXBuffers.Create(
            arena: deviceContext.HeapArena,
            device: Device,
            family: name.MemoryFamily(),
            flags: D3D12_RESOURCE_FLAGS.D3D12_RESOURCE_FLAG_ALLOW_UNORDERED_ACCESS,
            heapType: D3D12_HEAP_TYPE.D3D12_HEAP_TYPE_DEFAULT,
            initialState: D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_RAYTRACING_ACCELERATION_STRUCTURE,
            memory: deviceContext.Memory,
            sizeBytes: AlignUp(value: sizeBytes)
        );
        var storage = new DirectXGpuDeviceBuffer(bufferHandle: ((nint)buffer), memory: deviceContext.Memory, sizeBytes: AlignUp(value: sizeBytes));

        deviceContext.Services.Naming.Name(handle: ((nint)buffer), kind: GpuObjectKind.Buffer, name: in name);

        return new Structure(level: level, storage: storage, address: buffer->GetGPUVirtualAddress());
    }


    /// <inheritdoc/>
    public void WriteDescriptor(nint descriptorSetHandle, uint binding, IGpuAccelerationStructure structure) {
        var top = Of(structure: structure);
        RequireRayQuery();
        if (top.Level != GpuAccelerationStructureLevel.Top) {
            throw new ArgumentException(message: "A kernel traces a top level.", paramName: nameof(structure));
        }

        deviceContext.DescriptorBindings.WriteAccelerationStructure(address: top.Address, binding: binding, descriptorSetHandle: descriptorSetHandle);
    }

    /// <summary>Returns a structure's address and the storage it lives in, refusing one another device created.</summary>
    /// <param name="structure">The structure.</param>
    /// <returns>Its address.</returns>
    /// <exception cref="ArgumentException"><paramref name="structure"/> was not created by a Direct3D 12 device.</exception>
    public static ulong AddressOf(IGpuAccelerationStructure structure) => Of(structure: structure).Address;
    /// <summary>Fills the build inputs Direct3D 12 describes a build with: one opaque triangle geometry of float3
    /// positions and 32-bit indices for a bottom level, an array of instance records for a top level, each built for fast
    /// tracing and a bottom level for compaction.</summary>
    /// <param name="geometry">Where a bottom level's one geometry description is written; it must outlive the use of
    /// the returned inputs.</param>
    /// <param name="inputs">The neutral inputs, whose source buffer's address every input address is read from, for a
    /// prebuild query as for the build.</param>
    /// <returns>The native inputs.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="inputs"/> names no source buffer.</exception>
    public static D3D12_BUILD_RAYTRACING_ACCELERATION_STRUCTURE_INPUTS Describe(D3D12_RAYTRACING_GEOMETRY_DESC* geometry, in GpuAccelerationStructureInputs inputs) {
        ArgumentNullException.ThrowIfNull(argument: inputs.Source, paramName: nameof(inputs));
        var source = ((ID3D12Resource*)inputs.Source.BufferHandle)->GetGPUVirtualAddress();
        var description = new D3D12_BUILD_RAYTRACING_ACCELERATION_STRUCTURE_INPUTS {
            DescsLayout = D3D12_ELEMENTS_LAYOUT.D3D12_ELEMENTS_LAYOUT_ARRAY,
            NumDescs = ((inputs.Level == GpuAccelerationStructureLevel.Top) ? inputs.Count : 1U),
        };

        if (inputs.Level == GpuAccelerationStructureLevel.Top) {
            description.Type = D3D12_RAYTRACING_ACCELERATION_STRUCTURE_TYPE.D3D12_RAYTRACING_ACCELERATION_STRUCTURE_TYPE_TOP_LEVEL;
            description.Flags = D3D12_RAYTRACING_ACCELERATION_STRUCTURE_BUILD_FLAGS.D3D12_RAYTRACING_ACCELERATION_STRUCTURE_BUILD_FLAG_PREFER_FAST_TRACE;
            description.Anonymous.InstanceDescs = (source + inputs.InstanceOffsetBytes);

            return description;
        }

        *geometry = new D3D12_RAYTRACING_GEOMETRY_DESC {
            Flags = D3D12_RAYTRACING_GEOMETRY_FLAGS.D3D12_RAYTRACING_GEOMETRY_FLAG_OPAQUE,
            Type = D3D12_RAYTRACING_GEOMETRY_TYPE.D3D12_RAYTRACING_GEOMETRY_TYPE_TRIANGLES,
        };
        geometry->Anonymous.Triangles = new D3D12_RAYTRACING_GEOMETRY_TRIANGLES_DESC {
            IndexBuffer = (source + inputs.IndexOffsetBytes),
            IndexCount = checked(inputs.Count * 3U),
            IndexFormat = DXGI_FORMAT.DXGI_FORMAT_R32_UINT,
            VertexBuffer = new D3D12_GPU_VIRTUAL_ADDRESS_AND_STRIDE {
                StartAddress = (source + inputs.VertexOffsetBytes),
                StrideInBytes = inputs.VertexStrideBytes,
            },
            VertexCount = inputs.VertexCount,
            VertexFormat = DXGI_FORMAT.DXGI_FORMAT_R32G32B32_FLOAT,
        };
        description.Type = D3D12_RAYTRACING_ACCELERATION_STRUCTURE_TYPE.D3D12_RAYTRACING_ACCELERATION_STRUCTURE_TYPE_BOTTOM_LEVEL;
        description.Flags = (D3D12_RAYTRACING_ACCELERATION_STRUCTURE_BUILD_FLAGS.D3D12_RAYTRACING_ACCELERATION_STRUCTURE_BUILD_FLAG_PREFER_FAST_TRACE |
            D3D12_RAYTRACING_ACCELERATION_STRUCTURE_BUILD_FLAGS.D3D12_RAYTRACING_ACCELERATION_STRUCTURE_BUILD_FLAG_ALLOW_COMPACTION);
        description.Anonymous.pGeometryDescs = geometry;

        return description;
    }

    private ID3D12Device* Device => ((ID3D12Device*)deviceContext.Device.Handle);

    private void RequireRayQuery() {
        if (!(deviceContext.Capabilities?.Has(required: GpuOptionalCapabilities.RayQuery) ?? false)) {
            throw new NotSupportedException(message: "This Direct3D 12 device was created without ray queries (GpuOptionalCapabilities.RayQuery).");
        }
    }

    private static ulong AlignUp(ulong value) => value.AlignUp(alignment: StructureAlignment);
    private static Structure Of(IGpuAccelerationStructure structure) {
        ArgumentNullException.ThrowIfNull(argument: structure);

        return ((structure as Structure) ?? throw new ArgumentException(message: "The structure was not created by this Direct3D 12 device.", paramName: nameof(structure)));
    }

    // One structure: its storage buffer and that buffer's address. Disposal releases the buffer.
    private sealed class Structure(GpuAccelerationStructureLevel level, DirectXGpuDeviceBuffer storage, ulong address) : IGpuAccelerationStructure {
        public GpuAccelerationStructureLevel Level { get; } = level;
        public ulong SizeBytes => storage.SizeBytes;
        public ulong Address { get; } = address;
        public void Dispose() => storage.Dispose();
    }
}
