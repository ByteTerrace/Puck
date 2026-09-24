using Puck.Abstractions.Counting;

namespace Puck.Shaders;

public sealed partial record ShaderSetManifest {
    /// <summary>The name a counters report heads <see cref="LoadWork"/>'s section with.</summary>
    public const string LoadWorkSourceName = "shaders.set-manifest";

    /// <summary>Gets the kind counting manifest loads: one per <see cref="Load(string, WorkCounterSet)"/> of a file
    /// that exists, whether or not it validates.</summary>
    public static WorkKind Loads { get; } = new(name: "shaders.set-manifest.loads", unit: "count", workClass: WorkClass.PerBackendDeterministic);
    /// <summary>Gets the kind counting the bytecode bytes a load read to validate its stages' <c>.spv</c> and
    /// <c>.dxil</c> files.</summary>
    public static WorkKind BytecodeBytes { get; } = new(name: "shaders.set-manifest.bytecode-bytes", unit: "bytes", workClass: WorkClass.PerBackendDeterministic);

    /// <summary>Gets the process's manifest-load counts, which <see cref="Load(string)"/> counts into and a host
    /// registers as its <see cref="LoadWorkSourceName"/> source. Counts only go up, and any thread may load.</summary>
    public static WorkCounterSet LoadWork =>
        LoadCounts.Process;

    // A nested holder initializes after every kind above, whatever order the members are declared in.
    private static class LoadCounts {
        internal static readonly WorkCounterSet Process = new(
            kinds: [Loads, BytecodeBytes],
            name: LoadWorkSourceName
        );
    }
}
