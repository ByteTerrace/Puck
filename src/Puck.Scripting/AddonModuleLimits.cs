namespace Puck.Scripting;

/// <summary>Representation and allocation ceilings for one self-contained addon module. Execution fuel is
/// separately selected by the document; these ceilings do not impose a native compiler CPU deadline.</summary>
public static class AddonModuleLimits {
    /// <summary>Four simultaneous source loads/conversions/native compilations process-wide keep hostile distinct
    /// modules from multiplying compiler memory and CPU; cache size alone cannot bound in-flight work.</summary>
    public const int ConcurrentCompilations = 4;
    /// <summary>Maximum source bytes, checked before WAT conversion or binary compilation.</summary>
    public const int SourceBytes = ((16 * 1024) * 1024);
    /// <summary>Maximum binary bytes, including the result of WAT conversion.</summary>
    public const int BinaryBytes = ((16 * 1024) * 1024);
    /// <summary>Maximum bytes in the guest's one linear memory: 256 standard WebAssembly pages.</summary>
    public const long MemoryBytes = (256L * 65536);
    /// <summary>Maximum linear memories, including unexported memories.</summary>
    public const int Memories = 1;
    /// <summary>Maximum tables, including unexported tables.</summary>
    public const int Tables = 1;
    /// <summary>Maximum elements in the guest's optional table, at creation and after growth.</summary>
    public const uint TableElements = 65536;
}
