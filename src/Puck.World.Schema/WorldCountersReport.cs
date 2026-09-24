using Puck.Abstractions.Counting;
using Puck.Abstractions.Gpu;

namespace Puck.World;

/// <summary>
/// A <c>puck.counters.report.v1</c> document: what <c>puck counters</c> read from one authored workload booted
/// offscreen once per backend, every count tagged with the <see cref="WorkClass"/> its owner declared. Two reports of
/// the same workload at the same code agree on every deterministic count; <c>puck counters compare</c> says where they
/// do not.
/// </summary>
/// <param name="Revision">The source the World that produced the counts was built from.</param>
/// <param name="Workload">The workload's world document, repository-relative with forward slashes.</param>
/// <param name="Script">The workload's console script, repository-relative with forward slashes.</param>
/// <param name="Runs">One run per backend, in the order they ran.</param>
public sealed record WorldCountersReport(
    WorldCountersRevision Revision,
    string Workload,
    string Script,
    IReadOnlyList<WorldCountersRun> Runs
) {
    /// <summary>The document schema tag every well-formed <c>puck.counters.report.v1</c> document carries.</summary>
    public const string SchemaVersion = "puck.counters.report.v1";

    /// <summary>Gets the document schema tag — <see cref="SchemaVersion"/> for a well-formed document.</summary>
    public string Schema { get; init; } = SchemaVersion;
}
/// <summary>The source a report's counts were produced from.</summary>
/// <param name="Commit">The checkout's <c>HEAD</c> commit.</param>
/// <param name="SourceState">The key of the World build that ran: a hash of the World's source closure including every
/// uncommitted change, so two reports with equal keys ran the same code.</param>
public sealed record WorldCountersRevision(string Commit, string SourceState);
/// <summary>One backend's run of the workload.</summary>
/// <param name="Backend">The backend the World was booted on: <c>vulkan</c> or <c>directx</c>.</param>
/// <param name="Device">The device the render nodes ran on, as the backend reported it at creation.</param>
/// <param name="Width">The offscreen presentation's width in pixels.</param>
/// <param name="Height">The offscreen presentation's height in pixels.</param>
/// <param name="Compiler">The shader toolchain's identity: the hash the shader cache keys on, over each tool's path,
/// timestamp and version.</param>
/// <param name="GcMode">The World process's GC mode, which an allocation reading is taken under.</param>
/// <param name="Counts">Every count the run read, in the order the World reported them.</param>
/// <param name="Passes">Every render node's passes in its newest completed submission, in pass order.</param>
public sealed record WorldCountersRun(
    string Backend,
    GpuDeviceIdentity Device,
    int Width,
    int Height,
    string Compiler,
    string GcMode,
    IReadOnlyList<WorldCount> Counts,
    IReadOnlyList<WorldCountersPass> Passes
);
/// <summary>One count a run read, named by where it was read and tagged with its class.</summary>
/// <param name="Source">The section the count was read from: a counter source's name, <c>gpu</c>, or
/// <c>allocation</c>.</param>
/// <param name="Node">The render node, for a count of the <c>gpu</c> section; otherwise <see langword="null"/>.</param>
/// <param name="Pass">The pass, for a GPU count recorded inside one; otherwise <see langword="null"/>.</param>
/// <param name="Kind">The kind's dotted name, or for an allocation reading the window's name.</param>
/// <param name="Class">What two readings of the count may be expected to agree on.</param>
/// <param name="Value">The count; for an allocation reading, the fewest bytes a run of the window allocated.</param>
public sealed record WorldCount(string Source, string? Node, string? Pass, string Kind, WorkClass Class, long Value);
/// <summary>What one pass of a render node did in its newest completed submission. A pass's state is deterministic:
/// the same inputs execute, skip, or leave unreached the same passes on every backend.</summary>
/// <param name="Node">The render node.</param>
/// <param name="Label">The pass's label.</param>
/// <param name="State">What the pass did.</param>
public sealed record WorldCountersPass(string Node, string Label, GpuPassState State);
