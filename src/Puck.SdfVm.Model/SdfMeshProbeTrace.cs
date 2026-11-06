namespace Puck.SdfVm;

/// <summary>The CPU and compute trace's shared hit words and uploaded scene layout.</summary>
public static class SdfMeshProbeTrace {
    /// <summary>One closest-hit ray per angular texel.</summary>
    public const int HitCount = SdfMeshIrradianceProbe.DepthSize * SdfMeshIrradianceProbe.DepthSize;
    /// <summary>FP32 signed distance, then material and packed normal, per ray.</summary>
    public const int HitWords = 2 * HitCount;
    /// <summary>The reserved material for a hit outside the packed material range.</summary>
    public const int UnknownMaterial = ushort.MaxValue;
    /// <summary>Low bits of a node link holding its leaf count; zero names two children.</summary>
    public const int NodeCountBits = 3;
    /// <summary>A node's low and high bounds, then its packed link.</summary>
    public const int NodeWords = 7;
    /// <summary>One instance address and one geometry subtree address per top-level leaf.</summary>
    public const int EntryWords = 2;
    /// <summary>Inverse matrix, normal matrix, then geometry and material addresses.</summary>
    public const int InstanceWords = 32;
    /// <summary>First word of the instance's winding-aware normal matrix.</summary>
    public const int NormalWord = 16;
    /// <summary>First word of the instance's mesh index, vertex, material and base fields.</summary>
    public const int GeometryWord = 25;
    /// <summary>Whether the instance's transform is exactly identity.</summary>
    public const int IdentityWord = 29;
    /// <summary>The scene instance ordinal, for hit identity diagnostics.</summary>
    public const int InstanceIndexWord = 30;
    /// <summary>Traversal stack capacity shared with the CPU walk.</summary>
    public const int StackDepth = 64;
    /// <summary>The most node visits in one ray, across both hierarchy levels; corrupt links cannot cycle forever.</summary>
    public const int MaxWalkSteps = 65536;
    /// <summary>Threads per trace group; the two per-thread stacks occupy 16 KiB.</summary>
    public const int GroupSize = 32;
}
