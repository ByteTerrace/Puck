namespace Puck.Abstractions.Gpu;

/// <summary>
/// The layout of one record a counted indirect draw reads (<see cref="IGpuRecorder.DrawIndirectCount"/>): six
/// consecutive <see langword="uint"/>s, the draw's identity, then Vulkan's <c>VkDrawIndexedIndirectCommand</c> (index
/// count, instance count, first index, vertex offset, first instance), which is also Direct3D 12's
/// <c>D3D12_DRAW_INDEXED_ARGUMENTS</c>. Direct3D 12 reads the identity as a root-constant argument into the pushed index;
/// Vulkan skips it and reads the five words after it. Vulkan vertex pulling reads the identity through the pushed record
/// base plus <c>DrawIndex</c>. The vertex offset and first instance stay zero: a vertex stage reads its vertex id as the
/// index the buffer holds on both backends, and first instance needs no <c>drawIndirectFirstInstance</c>.
/// </summary>
public static class GpuDrawIndirectCommand {
    /// <summary>The 32-bit words one record takes.</summary>
    public const int Words = 6;
    /// <summary>The bytes one record takes, the stride between consecutive records.</summary>
    public const int StrideBytes = (Words * sizeof(uint));
    /// <summary>The word holding the draw's identity.</summary>
    public const int IdentityWord = 0;
    /// <summary>The word holding the index count, where the native draw arguments begin.</summary>
    public const int IndexCountWord = 1;
    /// <summary>The word holding the instance count.</summary>
    public const int InstanceCountWord = 2;
    /// <summary>The word holding the first index, counted in indices from the start of the index buffer.</summary>
    public const int FirstIndexWord = 3;
    /// <summary>The word holding the vertex offset, which stays zero.</summary>
    public const int VertexOffsetWord = 4;
    /// <summary>The word holding the first instance, which stays zero.</summary>
    public const int FirstInstanceWord = 5;
}
