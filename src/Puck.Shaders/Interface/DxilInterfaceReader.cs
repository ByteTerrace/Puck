using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Puck.Abstractions.Gpu;

namespace Puck.Shaders;

/// <summary>
/// Reads the descriptor bindings a DXIL container declares, in the neutral <see cref="ShaderInterfaceBinding"/> shape,
/// through DXC's documented reflection interface: <c>IDxcUtils::CreateReflection</c> returns an
/// <c>ID3D12ShaderReflection</c> over the container, whose <c>GetResourceBindingDesc</c> gives each binding's register
/// space and number and whose constant-buffer reflection gives each block member's offset. It parses no container
/// part itself.
/// <para>The reader loads <c>dxcompiler.dll</c> from beside the <c>dxc</c> executable a <see cref="ShaderToolchain"/>
/// resolves, and calls it through its COM vtables with the layouts <c>dxcapi.h</c> and <c>d3d12shader.h</c> declare.
/// Those layouts are the Windows ones: DXC's non-Windows <c>IUnknown</c> carries a virtual destructor, which moves
/// every slot, so the reader is Windows-only.</para>
/// </summary>
[SupportedOSPlatform("windows")]
public sealed unsafe class DxilInterfaceReader : IDisposable {
    private const int DxcUtilsCreateReflection = 13;
    private const int UnknownRelease = 2;
    private const int ReflectionGetDesc = 3;
    private const int ReflectionGetConstantBufferByName = 5;
    private const int ReflectionGetResourceBindingDesc = 6;
    private const int ConstantBufferGetDesc = 0;
    private const int ConstantBufferGetVariableByIndex = 1;
    private const int VariableGetDesc = 0;
    private const int VariableGetType = 1;
    private const int TypeGetDesc = 0;
    private const int TypeGetMemberTypeByIndex = 1;
    private const int TypeGetMemberTypeName = 3;
    // D3D_SHADER_INPUT_TYPE.
    private const uint InputConstantBuffer = 0;
    private const uint InputTextureBuffer = 1;
    private const uint InputTexture = 2;
    private const uint InputSampler = 3;
    private const uint InputReadWriteTyped = 4;
    private const uint InputStructured = 5;
    private const uint InputReadWriteStructured = 6;
    private const uint InputByteAddress = 7;
    private const uint InputReadWriteByteAddress = 8;
    private const uint InputAppendStructured = 9;
    private const uint InputConsumeStructured = 10;
    private const uint InputReadWriteStructuredWithCounter = 11;
    // D3D_SRV_DIMENSION_BUFFER.
    private const uint DimensionBuffer = 1;
    // D3D_SHADER_VARIABLE_CLASS and D3D_SHADER_VARIABLE_TYPE.
    private const uint ClassScalar = 0;
    private const uint ClassVector = 1;
    private const uint ClassStruct = 5;
    private const uint TypeInt = 2;
    private const uint TypeFloat = 3;
    private const uint TypeUint = 19;

    private static readonly Guid DxcUtilsClassId = new(g: "6245d6af-66e0-48fd-80b4-4d271796748c");
    private static readonly Guid DxcUtilsInterfaceId = new(g: "4605c4cb-2019-492a-ada4-65f20bb7d67f");
    private static readonly Guid ShaderReflectionInterfaceId = new(g: "5a58797d-a72c-478d-8ba2-efc6b0efe88e");

    private nint m_library;
    private void* m_utils;

    [StructLayout(LayoutKind.Sequential)]
    private struct DxcBuffer {
        public void* Pointer;
        public nuint Size;
        public uint Encoding;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct ShaderDesc {
        public uint Version;
        public nint Creator;
        public uint Flags;
        public uint ConstantBuffers;
        public uint BoundResources;
        public fixed uint Counts[33];
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct InputBindDesc {
        public nint Name;
        public uint Type;
        public uint BindPoint;
        public uint BindCount;
        public uint Flags;
        public uint ReturnType;
        public uint Dimension;
        public uint NumSamples;
        public uint Space;
        public uint Id;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct BufferDesc {
        public nint Name;
        public uint Type;
        public uint Variables;
        public uint Size;
        public uint Flags;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct VariableDesc {
        public nint Name;
        public uint StartOffset;
        public uint Size;
        public uint Flags;
        public nint DefaultValue;
        public uint StartTexture;
        public uint TextureSize;
        public uint StartSampler;
        public uint SamplerSize;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct TypeDesc {
        public uint Class;
        public uint Type;
        public uint Rows;
        public uint Columns;
        public uint Elements;
        public uint Members;
        public uint Offset;
        public nint Name;
    }

    /// <summary>Initializes a new instance of the <see cref="DxilInterfaceReader"/> class over one
    /// <c>dxcompiler</c> library.</summary>
    /// <param name="libraryPath">The full path of <c>dxcompiler.dll</c>; the library stays loaded until the reader is
    /// disposed.</param>
    /// <exception cref="DllNotFoundException">The library cannot be loaded.</exception>
    /// <exception cref="EntryPointNotFoundException">The library exports no <c>DxcCreateInstance</c>.</exception>
    /// <exception cref="InvalidDataException"><c>DxcCreateInstance</c> refuses to create <c>IDxcUtils</c>.</exception>
    public DxilInterfaceReader(string libraryPath) {
        ArgumentException.ThrowIfNullOrWhiteSpace(argument: libraryPath);

        m_library = NativeLibrary.Load(libraryPath: libraryPath);

        try {
            var create = ((delegate* unmanaged<Guid*, Guid*, void**, int>)NativeLibrary.GetExport(
                handle: m_library,
                name: "DxcCreateInstance"
            ));
            var classId = DxcUtilsClassId;
            var interfaceId = DxcUtilsInterfaceId;
            void* utils = null;

            Check(
                operation: "DxcCreateInstance(CLSID_DxcUtils)",
                result: create(&classId, &interfaceId, &utils)
            );
            m_utils = utils;
        } catch {
            NativeLibrary.Free(handle: m_library);
            m_library = 0;

            throw;
        }
    }

    /// <summary>Creates a reader over the <c>dxcompiler.dll</c> beside the <c>dxc</c> executable
    /// <paramref name="toolchain"/> resolves.</summary>
    /// <param name="toolchain">The toolchain whose <c>dxc</c> to use.</param>
    /// <returns>The reader, which the caller disposes.</returns>
    /// <exception cref="ShaderToolMissingException">The toolchain resolves no <c>dxc</c>, or no <c>dxcompiler.dll</c>
    /// sits beside it.</exception>
    public static DxilInterfaceReader Load(ShaderToolchain toolchain) {
        ArgumentNullException.ThrowIfNull(argument: toolchain);

        var dxc = (toolchain.Locate(name: "dxc") ?? throw new ShaderToolMissingException(
            directory: toolchain.Directory,
            tool: "dxc"
        ));
        var library = Path.Combine(
            path1: Path.GetDirectoryName(path: dxc)!,
            path2: "dxcompiler.dll"
        );

        return (File.Exists(path: library)
            ? new DxilInterfaceReader(libraryPath: library)
            : throw new ShaderToolMissingException(
                directory: Path.GetDirectoryName(path: dxc),
                tool: "dxcompiler.dll"
            ));
    }
    /// <summary>Reads every binding the container's reflection reports, ordered by register space and then register
    /// number.</summary>
    /// <param name="container">The DXIL container's bytes, as DXC wrote them.</param>
    /// <returns>The bindings.</returns>
    /// <exception cref="ObjectDisposedException">The reader is disposed.</exception>
    /// <exception cref="InvalidDataException">DXC cannot reflect the container, which includes a container compiled with
    /// its reflection stripped, or a binding or block member has a type the neutral shape has no kind for.</exception>
    public IReadOnlyList<ShaderInterfaceBinding> Read(ReadOnlySpan<byte> container) {
        ObjectDisposedException.ThrowIf(condition: (m_utils is null), instance: this);

        var bindings = new List<ShaderInterfaceBinding>();

        fixed (byte* bytes = container) {
            var buffer = new DxcBuffer {
                Encoding = 0,
                Pointer = bytes,
                Size = ((nuint)container.Length),
            };
            var interfaceId = ShaderReflectionInterfaceId;
            void* reflection = null;

            Check(
                operation: "IDxcUtils::CreateReflection",
                result: ((delegate* unmanaged<void*, DxcBuffer*, Guid*, void**, int>)Slot(
                    instance: m_utils,
                    slot: DxcUtilsCreateReflection
                ))(m_utils, &buffer, &interfaceId, &reflection)
            );

            try {
                ShaderDesc desc;

                Check(
                    operation: "ID3D12ShaderReflection::GetDesc",
                    result: ((delegate* unmanaged<void*, ShaderDesc*, int>)Slot(
                        instance: reflection,
                        slot: ReflectionGetDesc
                    ))(reflection, &desc)
                );

                for (var index = 0u; (index < desc.BoundResources); index++) {
                    InputBindDesc bind;

                    Check(
                        operation: "ID3D12ShaderReflection::GetResourceBindingDesc",
                        result: ((delegate* unmanaged<void*, uint, InputBindDesc*, int>)Slot(
                            instance: reflection,
                            slot: ReflectionGetResourceBindingDesc
                        ))(reflection, index, &bind)
                    );

                    var name = (Marshal.PtrToStringUTF8(ptr: bind.Name) ?? "");
                    var kind = Kind(
                        bind: bind,
                        name: name
                    );

                    bindings.Add(item: new ShaderInterfaceBinding(
                        Binding: bind.BindPoint,
                        Kind: kind,
                        Members: ((kind == GpuBindingKind.ConstantBuffer)
                            ? BlockMembers(
                                name: bind.Name,
                                reflection: reflection
                            )
                            : []),
                        Name: name,
                        Set: bind.Space
                    ));
                }
            } finally {
                Release(instance: reflection);
            }
        }

        bindings.Sort(comparison: static (a, b) => ((a.Set != b.Set)
            ? a.Set.CompareTo(value: b.Set)
            : a.Binding.CompareTo(value: b.Binding)));

        return bindings.AsReadOnly();
    }
    /// <summary>Releases <c>IDxcUtils</c> and unloads the library.</summary>
    public void Dispose() {
        if (m_utils is not null) {
            Release(instance: m_utils);
            m_utils = null;
        }
        if (m_library != 0) {
            NativeLibrary.Free(handle: m_library);
            m_library = 0;
        }
    }

    private static IReadOnlyList<ShaderInterfaceBlockMember> BlockMembers(void* reflection, nint name) {
        var constantBuffer = ((delegate* unmanaged<void*, nint, void*>)Slot(
            instance: reflection,
            slot: ReflectionGetConstantBufferByName
        ))(reflection, name);
        BufferDesc bufferDesc;

        Check(
            operation: "ID3D12ShaderReflectionConstantBuffer::GetDesc",
            result: ((delegate* unmanaged<void*, BufferDesc*, int>)Slot(
                instance: constantBuffer,
                slot: ConstantBufferGetDesc
            ))(constantBuffer, &bufferDesc)
        );

        var members = new List<ShaderInterfaceBlockMember>();

        for (var index = 0u; (index < bufferDesc.Variables); index++) {
            var variable = ((delegate* unmanaged<void*, uint, void*>)Slot(
                instance: constantBuffer,
                slot: ConstantBufferGetVariableByIndex
            ))(constantBuffer, index);
            VariableDesc variableDesc;

            Check(
                operation: "ID3D12ShaderReflectionVariable::GetDesc",
                result: ((delegate* unmanaged<void*, VariableDesc*, int>)Slot(
                    instance: variable,
                    slot: VariableGetDesc
                ))(variable, &variableDesc)
            );

            var type = ((delegate* unmanaged<void*, void*>)Slot(
                instance: variable,
                slot: VariableGetType
            ))(variable);
            var typeDesc = Describe(type: type);

            // A ConstantBuffer<T> reflects as one variable of struct type T; a cbuffer reflects its members directly.
            if (
                (bufferDesc.Variables == 1) &&
                (typeDesc.Class == ClassStruct)
            ) {
                for (var member = 0u; (member < typeDesc.Members); member++) {
                    var memberType = ((delegate* unmanaged<void*, uint, void*>)Slot(
                        instance: type,
                        slot: TypeGetMemberTypeByIndex
                    ))(type, member);
                    var memberDesc = Describe(type: memberType);
                    var memberName = ((delegate* unmanaged<void*, uint, nint>)Slot(
                        instance: type,
                        slot: TypeGetMemberTypeName
                    ))(type, member);

                    members.Add(item: Member(
                        desc: memberDesc,
                        name: (Marshal.PtrToStringUTF8(ptr: memberName) ?? ""),
                        offset: (variableDesc.StartOffset + memberDesc.Offset)
                    ));
                }
            } else {
                members.Add(item: Member(
                    desc: typeDesc,
                    name: (Marshal.PtrToStringUTF8(ptr: variableDesc.Name) ?? ""),
                    offset: variableDesc.StartOffset
                ));
            }
        }

        members.Sort(comparison: static (a, b) => a.Offset.CompareTo(value: b.Offset));

        return members.AsReadOnly();
    }
    private static void Check(int result, string operation) {
        if (result < 0) {
            throw new InvalidDataException(message: $"{operation} failed with HRESULT 0x{result:X8}.");
        }
    }
    private static TypeDesc Describe(void* type) {
        TypeDesc desc;

        Check(
            operation: "ID3D12ShaderReflectionType::GetDesc",
            result: ((delegate* unmanaged<void*, TypeDesc*, int>)Slot(
                instance: type,
                slot: TypeGetDesc
            ))(type, &desc)
        );

        return desc;
    }
    private static GpuBindingKind Kind(InputBindDesc bind, string name) =>
        bind.Type switch {
            InputConstantBuffer => GpuBindingKind.ConstantBuffer,
            (InputTexture or InputReadWriteTyped) when (bind.Dimension == DimensionBuffer) => throw ShaderInterfaceBinding.TypedBuffer(
                name: name,
                reader: "DXIL"
            ),
            InputTexture => GpuBindingKind.SampledImage,
            InputSampler => GpuBindingKind.Sampler,
            InputReadWriteTyped => GpuBindingKind.StorageImage,
            InputTextureBuffer or InputStructured or InputByteAddress => GpuBindingKind.ReadOnlyBuffer,
            InputReadWriteStructured or InputReadWriteByteAddress or InputAppendStructured or InputConsumeStructured or InputReadWriteStructuredWithCounter => GpuBindingKind.ReadWriteBuffer,
            _ => throw new InvalidDataException(message: $"DXIL binding '{name}' has input type {bind.Type}, which is no binding kind."),
        };
    private static ShaderInterfaceBlockMember Member(TypeDesc desc, string name, uint offset) {
        var scalar = desc.Type switch {
            TypeFloat => ShaderScalarKind.Float,
            TypeUint => ShaderScalarKind.Uint,
            TypeInt => ShaderScalarKind.Int,
            _ => throw new InvalidDataException(message: $"DXIL block member '{name}' has variable type {desc.Type}, which is not a 32-bit scalar kind."),
        };

        if (
            (desc.Class is not (ClassScalar or ClassVector)) ||
            (desc.Rows != 1) ||
            (desc.Columns is < 1 or > 4)
        ) {
            throw new InvalidDataException(message: $"DXIL block member '{name}' is not a scalar or vector.");
        }

        return new ShaderInterfaceBlockMember(
            Length: desc.Elements,
            Name: name,
            Offset: offset,
            Type: ShaderValueTypes.FromComponents(
                count: desc.Columns,
                kind: scalar
            )
        );
    }
    private static void Release(void* instance) =>
        _ = ((delegate* unmanaged<void*, uint>)Slot(
            instance: instance,
            slot: UnknownRelease
        ))(instance);
    private static void* Slot(void* instance, int slot) =>
        (*((void***)instance))[slot];
}
