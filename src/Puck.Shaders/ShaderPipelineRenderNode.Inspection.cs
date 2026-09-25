using System.Text;
using Puck.Abstractions.Gpu;

namespace Puck.Shaders;

// The node's own account of its installed graph, the text pipeline.inspect answers with, so a console verb and a law
// replaying a canary read the same lines.
public sealed partial class ShaderPipelineRenderNode {
    /// <summary>Writes the inspection of the installed graph: every byte the node owns (<see cref="OwnedBytes"/>,
    /// which adds replaced objects and held images still waiting to retire to the installed graph), the installed
    /// graph's <see cref="InstalledAccount"/> (its steady-state bytes and the peak a reload of it would reach) and
    /// <see cref="BudgetBytes"/>, the GPU work the newest completed submission recorded with the node's lifetime counts,
    /// then each ordered pass, planned resource, allocated resource and named output on its own line.</summary>
    /// <param name="builder">The builder the inspection is appended to, as one bracketed record.</param>
    /// <param name="name">The instance name the record carries.</param>
    /// <returns><see langword="false"/>, having appended nothing, when no graph is installed.</returns>
    public bool TryAppendInspection(StringBuilder builder, string name) {
        ArgumentNullException.ThrowIfNull(builder);

        if (Plan is not { } plan) { return false; }
        var account = InstalledAccount;

        builder.Append(handler: $"[pipeline.inspect: {name}; owned={OwnedBytes} bytes; steady={account.SteadyBytes} bytes; peak={account.PeakBytes} bytes; budget={account.BudgetBytes} bytes; ");

        // The work block leads, so its submission line shares the record's first line; it ends in a line feed, which
        // the next line's own separator replaces.
        GpuWorkReport.AppendLifetime(
            builder: GpuWorkReport.AppendCompleted(
                builder: builder,
                sample: new GpuWorkSample(),
                source: this
            ),
            source: this
        ).Length--;

        foreach (var pass in plan.Passes) {
            builder.Append(handler: $"\n  {pass.Name}: {pass.Kind}; reads={string.Join(
                separator: ",",
                values: pass.Declaration.InputReferences.Select(selector: input => (input.Name + (input.PreviousFrame
                ? "@previous"
                : string.Empty)))
            )}; writes={string.Join(
                separator: ",",
                values: pass.Declaration.OutputReferences.Select(selector: output => output.Name)
            )}");
        }
        foreach (var resource in plan.Resources) {
            builder.Append(handler: $"\n  resource {resource.Name}: {resource.Declaration.Kind} {resource.Declaration.Format}; history={resource.Declaration.History}; contents={resource.Contents}; storage={plan.Storages[resource.Storage].Name}; lifetime={resource.FirstUsePassIndex}..{resource.LastUsePassIndex}");
        }
        foreach (var resource in ResourceStatus) {
            builder.Append(handler: $"\n  allocated {resource.Name}: {resource.Width}x{resource.Height}; bytes={resource.AllocationBytes}; external={resource.External}");
        }
        foreach (var output in plan.Outputs) { builder.Append(handler: $"\n  output {output}"); }
        builder.Append(value: ']');
        return true;
    }
}
