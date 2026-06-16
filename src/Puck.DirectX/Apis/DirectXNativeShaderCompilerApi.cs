using System.Runtime.Versioning;
using System.Text;
using Puck.DirectX.Interfaces;
using Puck.DirectX.Interop;
using Puck.DirectX.Messages;
using Windows.Win32;
using Windows.Win32.Graphics.Direct3D;

namespace Puck.DirectX.Apis;

/// <summary>
/// The native implementation of <see cref="IDirectXShaderCompilerApi"/>, marshaling to <c>D3DCompile</c>.
/// </summary>
[SupportedOSPlatform("windows8.1")]
public sealed unsafe class DirectXNativeShaderCompilerApi : IDirectXShaderCompilerApi {
    /// <inheritdoc/>
    public DirectXShaderBytecode Compile(DirectXShaderCompileRequest request) {
        ArgumentException.ThrowIfNullOrEmpty(argument: request.HlslSource);

        var sourceBytes = Encoding.ASCII.GetBytes(s: request.HlslSource);

        ID3DBlob* code = null;
        ID3DBlob* errors = null;

        fixed (byte* source = sourceBytes) {
            var result = PInvoke.D3DCompile(
                pSrcData: source,
                SrcDataSize: (nuint)sourceBytes.Length,
                pSourceName: request.SourceName,
                pDefines: (D3D_SHADER_MACRO?)null,
                pInclude: null,
                pEntrypoint: request.EntryPoint,
                pTarget: request.Target,
                Flags1: 0,
                Flags2: 0,
                ppCode: &code,
                ppErrorMsgs: &errors
            );

            try {
                if (result.Failed) {
                    var diagnostics = ReadBlobString(blob: errors);

                    throw new DirectXException(
                        operation: $"D3DCompile({request.SourceName}){((diagnostics is null)
                            ? ""
                            : $": {diagnostics}")}",
                        result: result.Value
                    );
                }
            } finally {
                if (errors is not null) {
                    _ = errors->Release();
                }
            }
        }

        return new DirectXShaderBytecode(blobHandle: (nint)code);
    }

    private static string? ReadBlobString(ID3DBlob* blob) {
        if (blob is null) {
            return null;
        }

        var pointer = blob->GetBufferPointer();
        var length = (int)blob->GetBufferSize();

        if (
            (pointer is null) ||
            (0 == length)
        ) {
            return null;
        }

        return Encoding.ASCII.GetString(
            bytes: (byte*)pointer,
            byteCount: length
        ).TrimEnd(
            '\0',
            '\n',
            '\r'
        );
    }
}
