using Puck.DirectX;
using Puck.Hosting;

namespace Puck.Recursive.Nodes;

/// <summary>
/// Shared pieces for the Direct3D 12 nodes that draw a textured pass: the HLSL that samples a single texture,
/// and the mapping from a backend-neutral <see cref="SurfaceFormat"/> to the Direct3D pixel format a sampled
/// surface was produced in.
/// </summary>
internal static class DirectXTextured {
    /// <summary>The HLSL for a textured pass: a pass-through vertex stage and a single-texture sampling pixel stage.</summary>
    public const string ShaderSource =
        """
        struct VertexInput {
            float2 position : POSITION;
            float2 uv : TEXCOORD;
        };
        struct PixelInput {
            float4 position : SV_POSITION;
            float2 uv : TEXCOORD;
        };
        Texture2D g_texture : register(t0);
        SamplerState g_sampler : register(s0);
        PixelInput VSMain(VertexInput input) {
            PixelInput output;
            output.position = float4(input.position, 0.0, 1.0);
            output.uv = input.uv;
            return output;
        }
        float4 PSMain(PixelInput input) : SV_TARGET {
            return g_texture.Sample(g_sampler, input.uv);
        }
        """;

    /// <summary>Maps a neutral surface format to the Direct3D pixel format its bytes are laid out in.</summary>
    /// <param name="format">The neutral surface format.</param>
    /// <returns>The matching <see cref="DirectXPixelFormat"/>.</returns>
    public static DirectXPixelFormat ToPixelFormat(SurfaceFormat format) {
        return ((format == SurfaceFormat.B8G8R8A8Unorm)
            ? DirectXPixelFormat.B8G8R8A8Unorm
            : DirectXPixelFormat.R8G8B8A8Unorm);
    }
}
