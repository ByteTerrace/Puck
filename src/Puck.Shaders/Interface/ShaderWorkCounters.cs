using System.Globalization;
using System.Text;
using Puck.Abstractions.Gpu;

namespace Puck.Shaders;

/// <summary>
/// The members a pass interface declares when its kernels count their own work (<see cref="GpuWork.KernelKinds"/>) into
/// their node's kernel counters (<see cref="GpuKernelCounters"/>): the pass's row, a pass-block value, and the frame
/// slot's counter buffer, bound read-write in the pass group. Every generated include (<see cref="ShaderInterfaceHlsl"/>)
/// declares the counting functions: <c>puckCountWork(steps, texels)</c>, which sums a wave's counts and adds them with its
/// first active lane, and <c>puckCountFragmentWork(steps, texels)</c>, which does the same for a fragment stage over the
/// wave's lanes that are not helper lanes, since a helper lane's atomics have no effect. An interface that declares both
/// members gets their counting bodies, laid out as <see cref="GpuKernelCounters"/> reads the row back from the constants
/// generated beside them; any other interface, a document pass's among them, gets them empty. So a kernel counts
/// unguarded, and a package's kernel compiles as a document pass naming its source.
/// </summary>
public static class ShaderWorkCounters {
    /// <summary>The pass-block value holding the pass's row: its index in its node's planned passes
    /// (<see cref="GpuKernelCounterRow.Row"/>) (<c>uint</c>).</summary>
    public const string Row = "workCounterRow";
    /// <summary>The frame slot's counter buffer, a read-write <c>uint</c> buffer the pass adds to.</summary>
    public const string Buffer = "workCounters";

    /// <summary>Gets the pass-block value <see cref="Row"/>, which a pass block laid out value by value lists among its
    /// values.</summary>
    public static ShaderInterfaceMember RowMember { get; } = ShaderInterfaceMember.Value(
        group: ShaderInterfaceGroup.Pass,
        name: Row,
        type: ShaderValueType.Uint
    );
    /// <summary>Gets the read-write buffer <see cref="Buffer"/>.</summary>
    public static ShaderInterfaceMember BufferMember { get; } = ShaderInterfaceMember.ReadWriteBuffer(
        element: ShaderValueType.Uint,
        group: ShaderInterfaceGroup.Pass,
        name: Buffer
    );
    /// <summary>Gets the two members a counting pass declares in its pass group: <see cref="RowMember"/> and
    /// <see cref="BufferMember"/>.</summary>
    public static IReadOnlyList<ShaderInterfaceMember> Members { get; } = [RowMember, BufferMember];

    /// <summary>Indicates whether an interface declares the work counters: both of <see cref="Members"/>.</summary>
    /// <param name="shaderInterface">The interface.</param>
    /// <returns><see langword="true"/> when the interface's kernels count their own work.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="shaderInterface"/> is <see langword="null"/>.</exception>
    public static bool IsDeclaredBy(ShaderInterface shaderInterface) {
        ArgumentNullException.ThrowIfNull(argument: shaderInterface);

        return IsDeclaredBy(members: shaderInterface.Members);
    }
    /// <summary>Indicates whether a list of members declares the work counters: both of <see cref="Members"/>.</summary>
    /// <param name="members">The members, such as a package's (<see cref="RenderGraphPackage.Members"/>).</param>
    /// <returns><see langword="true"/> when the members hold both.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="members"/> is <see langword="null"/>.</exception>
    public static bool IsDeclaredBy(IReadOnlyList<ShaderInterfaceMember> members) {
        ArgumentNullException.ThrowIfNull(argument: members);

        return Members.All(predicate: member => members.Any(predicate: declared => (declared == member)));
    }

    // The counting functions every generated interface declares, after its declarations: an interface declaring the work
    // counters adds to them, and any other, a document pass's among them, declares the same counting functions empty. A kernel
    // therefore counts unguarded, and compiles alike as its package's pass and as a document pass naming its source.
    internal static void AppendHlsl(StringBuilder text, bool counts) {
        if (!counts) {
            _ = text.Append(value: """

                // This interface declares no work counters, so its passes count nothing: the counting functions a kernel calls
                // are declared empty, and a kernel written for a counting package compiles here unchanged.
                void puckCountWork(uint steps, uint texels) {
                }
                void puckCountFragmentWork(uint steps, uint texels) {
                }
                void puckCountDetailWork(uint row, uint steps, uint evaluations, uint hashes, uint textureLoads) {
                }

                """);

            return;
        }

        var number = static (int value) => value.ToString(provider: CultureInfo.InvariantCulture);

        _ = text.Append(value: $$"""

            // The pass's own work, added to its row of the node's kernel counters (GpuKernelCounters, which reads the rows
            // back): each counted kind in GpuWork.KernelKinds order, march steps, texels, sky evaluations, hashes and texture loads, as a 64-bit count in
            // two words, low word first. An interface declaring no work counters declares the same counting functions empty.
            static const uint PuckWorkRowWords = {{number(GpuKernelCounters.RowWords)}}u;
            static const uint PuckWorkStepsWord = 0u;
            static const uint PuckWorkTexelsWord = {{number(GpuKernelCounters.CountWords)}}u;
            static const uint PuckWorkSkyEvaluationsWord = {{number(GpuKernelCounters.CountWords * 2)}}u;
            static const uint PuckWorkSkyHashesWord = {{number(GpuKernelCounters.CountWords * 3)}}u;
            static const uint PuckWorkSkyTextureLoadsWord = {{number(GpuKernelCounters.CountWords * 4)}}u;
            // Adds to one count: the low word atomically, then the high word by one when that addition carries.
            void puckAddWork(uint word, uint amount) {
                if (amount == 0u) {
                    return;
                }

                uint before;

                InterlockedAdd({{Buffer}}[word], amount, before);

                if (before > (0xFFFFFFFFu - amount)) {
                    InterlockedAdd({{Buffer}}[word + 1u], 1u);
                }
            }
            // Counts a compute invocation's named detail. Equal active-lane rows share wave sums; mixed rows add
            // independently, so divergent layer/body selection never attributes another lane's work to the first row.
            // Detail rows never contain output texels: those belong to the ordinary pass/run row.
            void puckCountDetailWork(uint row, uint steps, uint evaluations, uint hashes, uint textureLoads) {
                bool sharedRow = WaveActiveAllEqual(row);
                if (sharedRow) {
                    steps = WaveActiveSum(steps);
                    evaluations = WaveActiveSum(evaluations);
                    hashes = WaveActiveSum(hashes);
                    textureLoads = WaveActiveSum(textureLoads);
                }
                if (!sharedRow || WaveIsFirstLane()) {
                    uint word = row * PuckWorkRowWords;
                    puckAddWork(word + PuckWorkStepsWord, steps);
                    puckAddWork(word + PuckWorkSkyEvaluationsWord, evaluations);
                    puckAddWork(word + PuckWorkSkyHashesWord, hashes);
                    puckAddWork(word + PuckWorkSkyTextureLoadsWord, textureLoads);
                }
            }
            // Adds an invocation's march steps and texels written to its pass's row: the wave sums both, and its first active
            // lane adds each sum. Every lane that did work reaches the call, since a lane that returned before it counts nothing.
            void puckCountWork(uint steps, uint texels) {
                uint waveSteps = WaveActiveSum(steps);
                uint waveTexels = WaveActiveSum(texels);

                if (WaveIsFirstLane()) {
                    uint row = (passGroup.{{Row}} * PuckWorkRowWords);

                    puckAddWork((row + PuckWorkStepsWord), waveSteps);
                    puckAddWork((row + PuckWorkTexelsWord), waveTexels);
                }
            }
            // Adds a fragment's march steps and texels written to its pass's row: the wave sums its lanes that are not helper
            // lanes, and the first of them adds each sum. A helper lane counts nothing and never adds, whether or not the
            // backend lets it take part in wave operations, since its atomics have no effect.
            void puckCountFragmentWork(uint steps, uint texels) {
                bool counting = !IsHelperLane();
                uint waveSteps = WaveActiveSum(counting ? steps : 0u);
                uint waveTexels = WaveActiveSum(counting ? texels : 0u);

                if (counting && (WavePrefixCountBits(counting) == 0u)) {
                    uint row = (passGroup.{{Row}} * PuckWorkRowWords);

                    puckAddWork((row + PuckWorkStepsWord), waveSteps);
                    puckAddWork((row + PuckWorkTexelsWord), waveTexels);
                }
            }

            """);
    }
}
