using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Puck.DirectX.Interop;
using Windows.Win32;
using Windows.Win32.Graphics.Direct3D;
using Windows.Win32.Graphics.Direct3D12;

namespace Puck.DirectX;

/// <summary>Creates Direct3D 12 root signatures from version 1 descriptions.</summary>
[SupportedOSPlatform("windows10.0.10240")]
public static unsafe class DirectXRootSignatures {
    /// <summary>Builds the clamp-addressed static-sampler description every texture-sampling root signature
    /// binds: CLAMP addressing on all axes, opaque-black border, no comparison, no anisotropy, and the full
    /// mip range.</summary>
    /// <param name="filter">The sampler filter.</param>
    /// <param name="shaderRegister">The sampler register (<c>s#</c>) the entry binds.</param>
    /// <param name="shaderVisibility">The shader stages that can see the sampler.</param>
    /// <returns>The static-sampler description.</returns>
    public static D3D12_STATIC_SAMPLER_DESC ClampStaticSampler(
        D3D12_FILTER filter,
        uint shaderRegister,
        D3D12_SHADER_VISIBILITY shaderVisibility
    ) {
        return new D3D12_STATIC_SAMPLER_DESC {
            AddressU = D3D12_TEXTURE_ADDRESS_MODE.D3D12_TEXTURE_ADDRESS_MODE_CLAMP,
            AddressV = D3D12_TEXTURE_ADDRESS_MODE.D3D12_TEXTURE_ADDRESS_MODE_CLAMP,
            AddressW = D3D12_TEXTURE_ADDRESS_MODE.D3D12_TEXTURE_ADDRESS_MODE_CLAMP,
            BorderColor = D3D12_STATIC_BORDER_COLOR.D3D12_STATIC_BORDER_COLOR_OPAQUE_BLACK,
            ComparisonFunc = D3D12_COMPARISON_FUNC.D3D12_COMPARISON_FUNC_NEVER,
            Filter = filter,
            MaxAnisotropy = 0,
            MaxLOD = float.MaxValue,
            MinLOD = 0f,
            MipLODBias = 0f,
            RegisterSpace = 0,
            ShaderRegister = shaderRegister,
            ShaderVisibility = shaderVisibility,
        };
    }
    /// <summary>Serializes a root-signature description and creates it on the given device.</summary>
    /// <param name="device">The device that owns the root signature.</param>
    /// <param name="description">The root-signature description to serialize.</param>
    /// <param name="serialized">The serialized root signature: part of a pipeline's identity in the device's
    /// <see cref="Interop.DirectXPipelineLibrary"/>.</param>
    /// <returns>The created root-signature pointer.</returns>
    public static nint Create(ID3D12Device* device, in D3D12_ROOT_SIGNATURE_DESC description, out byte[] serialized) {
        serialized = Serialize(description: in description);

        return Create(
            device: device,
            serialized: serialized
        );
    }
    /// <summary>Creates a root signature on the given device from its serialized form.</summary>
    /// <param name="device">The device that owns the root signature.</param>
    /// <param name="serialized">The serialized root signature, from <see cref="Serialize(in D3D12_ROOT_SIGNATURE_DESC)"/>
    /// or <see cref="Serialize(DirectXRootLayout, D3D12_ROOT_SIGNATURE_FLAGS)"/>.</param>
    /// <returns>The created root-signature pointer.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="serialized"/> is <see langword="null"/>.</exception>
    public static nint Create(ID3D12Device* device, byte[] serialized) {
        ArgumentNullException.ThrowIfNull(argument: serialized);

        void* rootSig;
        var rootSigIid = ID3D12RootSignature.IID_Guid;

        fixed (byte* bytes = serialized) {
            device->CreateRootSignature(
                0,
                bytes,
                ((nuint)serialized.Length),
                in rootSigIid,
                out rootSig
            );
        }

        return ((nint)rootSig);
    }
    /// <summary>Serializes a version 1 root-signature description. Serializing reaches no device.</summary>
    /// <param name="description">The root-signature description to serialize.</param>
    /// <returns>The serialized root signature.</returns>
    public static byte[] Serialize(in D3D12_ROOT_SIGNATURE_DESC description) {
        ID3DBlob* sigBlob = null;
        ID3DBlob* errBlob = null;

        try {
            PInvoke.D3D12SerializeRootSignature(
                Version: D3D_ROOT_SIGNATURE_VERSION.D3D_ROOT_SIGNATURE_VERSION_1,
                pRootSignature: in description,
                ppBlob: &sigBlob,
                ppErrorBlob: &errBlob
            ).ThrowIfFailed(operation: "D3D12SerializeRootSignature");

            return new ReadOnlySpan<byte>(
                length: checked((int)sigBlob->GetBufferSize()),
                pointer: sigBlob->GetBufferPointer()
            ).ToArray();
        } finally {
            if (sigBlob is not null) {
                _ = sigBlob->Release();
            }

            if (errBlob is not null) {
                _ = errBlob->Release();
            }
        }
    }
    /// <summary>Serializes the root signature a planned root layout describes, with no static sampler: each table
    /// parameter is a descriptor table of its ranges at their registers, spaces and table offsets, the pushed index is
    /// one 32-bit root constant, and every parameter takes the plan's visibility. Serializing reaches no device.</summary>
    /// <param name="layout">The planned root layout.</param>
    /// <param name="flags">The root signature's flags: <c>ALLOW_INPUT_ASSEMBLER_INPUT_LAYOUT</c> for a graphics
    /// pipeline, none for a compute pipeline.</param>
    /// <returns>The serialized root signature.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="layout"/> is <see langword="null"/>.</exception>
    public static byte[] Serialize(DirectXRootLayout layout, D3D12_ROOT_SIGNATURE_FLAGS flags) {
        ArgumentNullException.ThrowIfNull(argument: layout);

        var rangeCount = layout.Parameters.Sum(selector: static parameter => parameter.Ranges.Count);
        var ranges = new D3D12_DESCRIPTOR_RANGE[Math.Max(
            val1: rangeCount,
            val2: 1
        )];
        var parameters = new D3D12_ROOT_PARAMETER[Math.Max(
            val1: layout.Parameters.Count,
            val2: 1
        )];

        fixed (D3D12_DESCRIPTOR_RANGE* rangeBase = ranges)
        fixed (D3D12_ROOT_PARAMETER* parameterBase = parameters) {
            var next = 0;

            for (var index = 0; (index < layout.Parameters.Count); index++) {
                var planned = layout.Parameters[index];
                var parameter = new D3D12_ROOT_PARAMETER { ShaderVisibility = planned.Visibility, };

                if (planned.Constants is { } constants) {
                    parameter.ParameterType = D3D12_ROOT_PARAMETER_TYPE.D3D12_ROOT_PARAMETER_TYPE_32BIT_CONSTANTS;
                    parameter.Anonymous.Constants = new D3D12_ROOT_CONSTANTS {
                        Num32BitValues = constants.ValueCount,
                        RegisterSpace = constants.RegisterSpace,
                        ShaderRegister = constants.ShaderRegister,
                    };
                } else {
                    for (var rangeIndex = 0; (rangeIndex < planned.Ranges.Count); rangeIndex++) {
                        var range = planned.Ranges[rangeIndex];

                        rangeBase[(next + rangeIndex)] = new D3D12_DESCRIPTOR_RANGE {
                            BaseShaderRegister = range.BaseRegister,
                            NumDescriptors = range.Count,
                            OffsetInDescriptorsFromTableStart = range.TableOffset,
                            RangeType = range.Type,
                            RegisterSpace = range.Space,
                        };
                    }

                    parameter.ParameterType = D3D12_ROOT_PARAMETER_TYPE.D3D12_ROOT_PARAMETER_TYPE_DESCRIPTOR_TABLE;
                    parameter.Anonymous.DescriptorTable = new D3D12_ROOT_DESCRIPTOR_TABLE {
                        NumDescriptorRanges = ((uint)planned.Ranges.Count),
                        pDescriptorRanges = (rangeBase + next),
                    };
                    next += planned.Ranges.Count;
                }

                parameterBase[index] = parameter;
            }

            var description = new D3D12_ROOT_SIGNATURE_DESC {
                Flags = flags,
                NumParameters = ((uint)layout.Parameters.Count),
                NumStaticSamplers = 0,
                pParameters = ((layout.Parameters.Count > 0)
                    ? parameterBase
                    : null),
                pStaticSamplers = null,
            };

            return Serialize(description: in description);
        }
    }
    /// <summary>Creates the root signature and group layouts a pipeline created from a
    /// <see cref="GpuPipelineLayoutDescription"/> binds through: the root signature
    /// <see cref="Serialize(DirectXRootLayout, D3D12_ROOT_SIGNATURE_FLAGS)"/> writes from
    /// <see cref="DirectXRootLayout.Plan"/>, one <see cref="DirectXGroupLayout"/> per group, and the pushed index as the
    /// layout's root constants. The caller creates the pipeline state and owns the layout.</summary>
    /// <param name="device">The device that owns the root signature.</param>
    /// <param name="description">The neutral pipeline layout.</param>
    /// <returns>The layout, with no pipeline state yet.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="description"/> is <see langword="null"/>.</exception>
    public static DirectXPipelineLayout CreateLayout(ID3D12Device* device, GpuPipelineLayoutDescription description) {
        ArgumentNullException.ThrowIfNull(argument: description);

        var plan = DirectXRootLayout.Plan(description: description);
        var serialized = Serialize(
            flags: ((description.Stages == GpuShaderStage.Compute)
                ? D3D12_ROOT_SIGNATURE_FLAGS.D3D12_ROOT_SIGNATURE_FLAG_NONE
                : D3D12_ROOT_SIGNATURE_FLAGS.D3D12_ROOT_SIGNATURE_FLAG_ALLOW_INPUT_ASSEMBLER_INPUT_LAYOUT),
            layout: plan
        );
        var groups = new nint[((description.Groups.Count == 0)
            ? 0
            : (description.Groups[^1].Ordinal + 1))];
        var layout = new DirectXPipelineLayout {
            GroupHandles = groups,
            RootConstantsCount = ((plan.PushIndex is null)
                ? 0U
                : (GpuPipelineLayoutDescription.PushIndexBytes / sizeof(uint))),
            RootConstantsParamIndex = ((plan.PushIndex is { } push)
                ? ((int)push.Index)
                : -1),
            RootSignatureBlob = serialized,
        };

        foreach (var group in description.Groups) {
            groups[group.Ordinal] = GCHandle.ToIntPtr(value: GCHandle.Alloc(value: new DirectXGroupLayout(
                group: group,
                layout: plan
            )));
        }

        try {
            layout.RootSignatureHandle = Create(
                device: device,
                serialized: serialized
            );
        } catch {
            layout.Dispose();
            throw;
        }

        return layout;
    }
}
