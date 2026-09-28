using System.Globalization;
using System.Text;
using Puck.Abstractions.Gpu;

namespace Puck.Shaders;

/// <summary>
/// The members a pass interface declares when its kernels count their own work (<see cref="GpuWork.KernelKinds"/>) into
/// their node's kernel counters (<see cref="GpuKernelCounters"/>): the pass's row, a pass-block value, and the frame
/// slot's counter buffer, bound read-write in the pass group. An interface that declares both gets the counting functions
/// in its generated include (<see cref="ShaderInterfaceHlsl"/>): <c>puckCountWork(steps, texels)</c>, which sums a wave's
/// counts and adds them with its first active lane, and <c>puckCountWorkEach(steps, texels)</c>, which adds one
/// invocation's own counts for a stage whose helper lanes must not count, since their atomics have no effect. Both lay
/// the row out as <see cref="GpuKernelCounters"/> reads it back, from the constants generated beside them.
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

        return Members.All(predicate: member => shaderInterface.Members.Any(predicate: declared => (declared == member)));
    }

    // The counting functions an interface declaring the work counters generates, after its declarations.
    internal static void AppendHlsl(StringBuilder text) {
        var number = static (int value) => value.ToString(provider: CultureInfo.InvariantCulture);

        _ = text.Append(value: $$"""

            // The pass's own work, added to its row of the node's kernel counters (GpuKernelCounters, which reads the rows
            // back): each counted kind in GpuWork.KernelKinds order, march steps then texels written, as a 64-bit count in
            // two words, low word first.
            static const uint PuckWorkRowWords = {{number(GpuKernelCounters.RowWords)}}u;
            static const uint PuckWorkStepsWord = 0u;
            static const uint PuckWorkTexelsWord = {{number(GpuKernelCounters.CountWords)}}u;
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
            // Adds one invocation's own march steps and texels written to its pass's row, with no wave sum: for a fragment
            // stage, whose helper lanes' atomics have no effect.
            void puckCountWorkEach(uint steps, uint texels) {
                uint row = (passGroup.{{Row}} * PuckWorkRowWords);

                puckAddWork((row + PuckWorkStepsWord), steps);
                puckAddWork((row + PuckWorkTexelsWord), texels);
            }

            """);
    }
}
