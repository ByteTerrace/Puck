using Puck.Abstractions.Collections;
using Puck.Abstractions.Gpu;
using Puck.Abstractions.Presentation;

namespace Puck.Shaders;

// A frame's reservation: what the node will publish when its prepared frame commits, decided before any recording of the
// runtime's frame runs, so a consumer of the same frame binds it and records beside the node rather than after it.
//
// Invariant: a consumer that bound a reservation submits only if the commit published exactly what was reserved, in every
// part the consumer's recording depended on. One door enforces it, the runtime's commit (RenderGraphRuntime.Reservations.cs),
// which compares the commit against the reservation and abandons the consumers of one that did not hold. The node's side
// is that a reservation and a commit are computed by the same routines (SelectedPublication, FillBufferOutputs): a
// reservation predicts each package pass's stand-in outcome from its last, a buffer's content identity as standing, and the
// submission identity as the next one. Where the prediction can miss: a pass whose stand-in outcome flips, a pass that writes
// a buffer whose identity the reservation predicted standing (which only a consumer that stood on it depends on,
// StoodOnExternal), a failed or abandoned recording, and retained package taint that grew during the recording.
public sealed partial class ShaderPipelineRenderNode {
    private BufferOutput[] m_reservedBuffers = [];
    // The external buffer versions a pass of the open frame stood on, whose bound content identity its standing read.
    private readonly HashSet<string> m_stoodExternal = new(comparer: StringComparer.Ordinal);

    // The open frame's reserved selected surface, its forwarded binding and layout.
    internal Surface ReservedSurface { get; private set; }
    internal string? ReservedBinding { get; private set; }
    internal GpuImageLayout ReservedLayout { get; private set; }
    // The submission identity the open frame's commit will carry.
    internal long ReservedSubmission => (m_submissions + 1L);
    // The label the open frame's commit will publish: its route and everything it bound.
    internal DeliveryLabel ReservedLabel => m_open.Job.BindingLabel;
    internal ulong? ReservedStateTick => Frame.StateTick;

    // Whether a named output holds history, whose instance follows whichever pass writes it this frame, so no preparation
    // can name it before the frame records; the runtime records a consumer of such a node after its commit.
    internal bool PublishesHistory {
        get {
            if (m_pipeline is not { } pipeline) { return false; }
            if (!ReferenceEquals(objA: m_historyPublisherPlan, objB: pipeline)) {
                var outputs = pipeline.Plan.Outputs;

                m_historyPublisher = false;
                for (var index = 0; (index < outputs.Count); index++) {
                    m_historyPublisher |= (m_resourceLookup.TryGetValue(key: outputs[index], value: out var resource) && resource.History);
                }
                m_historyPublisherPlan = pipeline;
            }
            return m_historyPublisher;
        }
    }
    private bool m_historyPublisher;
    private CompiledShaderPipeline? m_historyPublisherPlan;

    // Reserves the open frame's publication, which only a frame whose record job is pending has (BeginFrame returned
    // true). Called on the frame thread before any recording of the runtime's frame runs.
    internal void ReserveOutputs() {
        if (!m_open.Pending) {
            throw new InvalidOperationException(message: $"Instance '{m_name}' reserved a frame with no pending record job.");
        }

        var slot = m_open.Job.Slot;

        ReservedSurface = SelectedPublication(binding: out var binding, layout: out var layout, name: out _, predicted: true, selected: m_open.Job.Selected, slot: slot);
        ReservedBinding = binding;
        ReservedLayout = layout;
        FillBufferOutputs(into: m_reservedBuffers, predicted: true, revision: ReservedSubmission, slot: slot);
    }
    // The reserved default output, as LatestOutputBuffer reads the published one.
    internal BufferOutput ReservedOutputBuffer() {
        var outputs = m_pipeline!.Plan.Outputs;

        for (var index = 0; (index < outputs.Count); index++) {
            if (outputs[index] == m_pipeline.Plan.DefaultOutput) { return m_reservedBuffers[index]; }
        }
        return default;
    }
    // Copies the reserved named outputs into the runtime's reusable array, as LatestOutputBuffers copies the published.
    internal BufferOutput[] ReservedOutputBuffers(BufferOutput[]? reuse) {
        var result = (((reuse is not null) && (reuse.Length == m_reservedBuffers.Length)) ? reuse : new BufferOutput[m_reservedBuffers.Length]);

        m_reservedBuffers.CopyTo(array: result, index: 0);
        return result;
    }
    // The named outputs the latest commit published, in the plan's output order, as the reservation holds them.
    internal ReadOnlySpan<BufferOutput> PublishedOutputs => m_publishedBuffers;
    // The publication a forwarded binding carries, or the node's submission identity `revision` for an image of its own.
    internal ReferenceRevision PublicationOf(string? binding, long revision, in DeliveryLatch latch) =>
        (((binding is not null) && TryGetBoundImage(image: out var input, name: binding) && input.TryRead(latch: in latch, image: out var resolved))
            ? resolved.Publication
            : new ReferenceRevision(Owner: this, Revision: revision));
    // Whether a pass of the latest recording stood on the content identity bound to an external buffer version.
    internal bool StoodOnExternal(string version) => m_stoodExternal.Contains(item: version);
    // Notes the external buffer versions a standing pass's decision read.
    private void NoteStoodOn(RuntimePass pass) {
        if (pass.Cadence is not { } cadence) { return; }
        foreach (var input in cadence.Inputs) {
            if (m_externalBuffers.ContainsKey(key: input.Name)) { _ = m_stoodExternal.Add(item: input.Name); }
        }
    }
}
