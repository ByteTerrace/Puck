using System.Text;
using Puck.Abstractions.Gpu;
using Puck.Hosting;

namespace Puck.Shaders;

/// <summary>
/// The live budget a runtime spends: what its latest schedule (<see cref="RenderGraphRuntime.Latest"/>) decided for
/// every instance, read back as text. Each instance's row names it, what the scheduler decided (rendered, waiting,
/// deferred or unread), the extent it renders at (a graph instance's quantized footprint, a source's negotiated
/// extent), its rate as a frame divisor, the passes one render records and the pass-pixels it spent this frame, for an
/// instance that renders a graph the bytes of the host-written regions its installed graph reads
/// (<see cref="RenderGraphRuntime.RegionBytes"/>), and the work its newest completed submission counted
/// (<see cref="RenderGraphRuntime.Work"/>): the passes that executed of those recorded, and their dispatches and draws.
/// Every figure is a count; nothing is timed. A reader reuses one
/// instance, which reuses one <see cref="GpuWorkSample"/>, so a steady read into a builder with room allocates nothing.
/// </summary>
public sealed class RenderGraphLiveBudget {
    private static readonly int DispatchesColumn = GpuWork.SubmissionKinds.IndexOf(value: GpuWork.Dispatches);
    private static readonly int DrawsColumn = GpuWork.SubmissionKinds.IndexOf(value: GpuWork.Draws);

    private readonly GpuWorkSample m_sample = new();

    private static string StatusOf(RenderGraphInstanceStatus status) => status switch {
        RenderGraphInstanceStatus.Rendered => "rendered",
        RenderGraphInstanceStatus.Waiting => "waiting",
        RenderGraphInstanceStatus.Deferred => "deferred",
        RenderGraphInstanceStatus.Refused => "refused (rate source, display rate unknown)",
        _ => "unread",
    };
    // Appends the work the instance's newest completed submission counted, or that it has none yet.
    private void AppendLedger(StringBuilder into, IGpuWorkSource work) {
        if (!work.TryReadCompleted(sample: m_sample)) {
            _ = into.Append(value: " ledger none");

            return;
        }

        var executed = 0;
        var dispatches = 0L;
        var draws = 0L;

        for (var pass = 0; (pass < m_sample.PassCount); pass++) {
            if (!m_sample.TryGetPassCount(
                column: DispatchesColumn,
                pass: pass,
                value: out var passDispatches
            )) {
                continue;
            }

            _ = m_sample.TryGetPassCount(
                column: DrawsColumn,
                pass: pass,
                value: out var passDraws
            );
            executed++;
            dispatches += passDispatches;
            draws += passDraws;
        }

        _ = into.Append(value: " ledger ")
            .Append(value: executed)
            .Append(value: '/')
            .Append(value: m_sample.PassCount)
            .Append(value: " passes, ")
            .Append(value: dispatches)
            .Append(value: " dispatches, ")
            .Append(value: draws)
            .Append(value: " draws");
    }

    /// <summary>Appends the runtime's live budget: one summary of its latest frame, then one row per instance in
    /// schedule order, each introduced by <c>; </c>.</summary>
    /// <param name="runtime">The runtime whose latest schedule is read.</param>
    /// <param name="into">The builder the text is appended to.</param>
    /// <exception cref="ArgumentNullException"><paramref name="runtime"/> or <paramref name="into"/> is
    /// <see langword="null"/>.</exception>
    public void Describe(RenderGraphRuntime runtime, StringBuilder into) {
        ArgumentNullException.ThrowIfNull(argument: runtime);
        ArgumentNullException.ThrowIfNull(argument: into);

        if (runtime.Latest is not { } schedule) {
            _ = into.Append(value: "live not scheduled yet");

            return;
        }

        var set = runtime.Instances;
        var rows = schedule.Instances;

        _ = into.Append(value: "live frame ")
            .Append(value: schedule.Frame)
            .Append(value: ": ")
            .Append(value: rows.Count)
            .Append(value: " instance(s), ")
            .Append(value: schedule.Renders.Count)
            .Append(value: " rendered, ")
            .Append(value: schedule.PassPixels)
            .Append(value: " pass-pixels");

        for (var index = 0; (index < rows.Count); index++) {
            var row = rows[index];

            _ = into.Append(value: "; ")
                .Append(value: row.Instance)
                .Append(value: ' ')
                .Append(value: StatusOf(status: row.Status))
                .Append(value: (row.IsRoot ? " root" : string.Empty))
                .Append(value: (set.Instances[index].IsSource ? " source" : string.Empty))
                .Append(value: ' ')
                .Append(value: row.Width)
                .Append(value: 'x')
                .Append(value: row.Height);
            _ = ((row.Divisor == 1)
                ? into.Append(value: " every frame")
                : into.Append(value: " 1/").Append(value: row.Divisor).Append(value: " frames"));
            _ = into.Append(value: " passes ")
                .Append(value: row.Passes)
                .Append(value: ' ')
                .Append(value: row.PassPixels)
                .Append(value: " pass-pixels");

            if (runtime.RegionBytes(instance: index) is { } regionBytes) {
                _ = into.Append(value: " regions ")
                    .Append(value: regionBytes)
                    .Append(value: " bytes");
            }

            AppendLedger(
                into: into,
                work: runtime.Work(instance: index)
            );
        }
    }
}
