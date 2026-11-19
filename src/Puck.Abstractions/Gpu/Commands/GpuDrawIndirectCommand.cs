namespace Puck.Abstractions.Gpu;

/// <summary>
/// The layout of one record a counted indirect draw reads (<see cref="IGpuRecorder.DrawIndirectCount"/>): five
/// consecutive <see langword="uint"/>s, the draw's identity, then Vulkan's <c>VkDrawIndirectCommand</c> (vertex count,
/// instance count, first vertex, first instance), which is also Direct3D 12's <c>D3D12_DRAW_ARGUMENTS</c>. Direct3D 12
/// below execute-indirect tier 1.1 reads the identity as a root-constant argument into the pushed index. Vulkan, and
/// Direct3D 12 at tier 1.1, skip it and read the four words after it. Vulkan vertex pulling reads the identity through
/// the pushed record base plus <c>DrawIndex</c>; Direct3D 12 at tier 1.1 numbers its commands with an incrementing
/// root constant, and the number names the candidate whose first-phase record holds the identity. First instance stays
/// zero, including on devices without <c>drawIndirectFirstInstance</c>.
/// </summary>
public static class GpuDrawIndirectCommand {
    /// <summary>The 32-bit words one record takes.</summary>
    public const int Words = 5;
    /// <summary>The bytes one record takes, the stride between consecutive records.</summary>
    public const int StrideBytes = (Words * sizeof(uint));
    /// <summary>The word holding the draw's identity.</summary>
    public const int IdentityWord = 0;
    /// <summary>The word holding the vertex count, where the native draw arguments begin.</summary>
    public const int VertexCountWord = 1;
    /// <summary>The word holding the instance count.</summary>
    public const int InstanceCountWord = 2;
    /// <summary>The word holding the first vertex.</summary>
    public const int FirstVertexWord = 3;
    /// <summary>The word holding the first instance.</summary>
    public const int FirstInstanceWord = 4;
}
