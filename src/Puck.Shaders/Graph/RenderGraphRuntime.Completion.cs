using System.Runtime.InteropServices;
using Puck.Abstractions.Collections;
using Puck.Hosting;
using Puck.Abstractions.Presentation;

namespace Puck.Shaders;

// Completion: whether the root's image shows the frame the runtime was asked to compose. An instance the frame scheduled
// that produced nothing, or that rendered over an input older than the frame it reads, is stale for the frame, and so is
// every instance that reads it within the frame; a previous-frame read is a frame late by design, and an instance the
// frame does not schedule (its refresh, an unchanged view, a paused node) keeps the standing of its last render.
public sealed partial class RenderGraphRuntime {
    // Each instance's reason its latest render attempt does not show the frame it was asked for, or null when it does.
    private string?[] m_stale = [];
    // Whether that reason is a refusal, which only a change to what the instance was built from lifts.
    private bool[] m_staleRefused = [];
    // The watchdog: how many render attempts in a row each instance has not shown its frame. A refusal narrates at once and
    // a wait once it outlasts StallFrames attempts, on standard error, so a host that never renders names why whatever its
    // script waits on.
    private int[] m_staleFrames = [];
    // The reason each instance's standing was last narrated, by instance name: a narration lasts from its start until the
    // instance is current again, so a reconfiguration that renumbers the instances or resets their standing neither
    // repeats a refusal that still holds nor hides its clearing. Reconfiguration forgets the instances it removes. A reader
    // of a refused or waiting instance takes on that instance's reason word for word (MarkReadStale), so a reason is told
    // once while any instance holds it, and cleared once no instance does.
    private readonly Dictionary<string, string> m_narrated = new(comparer: StringComparer.Ordinal);

    /// <summary>Gets or sets where the runtime narrates an instance's refusal, a wait that outlasts
    /// <see cref="StallFrames"/>, their clearing and a failed capture, one line each; <see langword="null"/>, the
    /// default, narrates on standard error as it stands when each line is written.</summary>
    public TextWriter? Narration { get; set; }

    private void Narrate(string line) => (Narration ?? Console.Error).WriteLine(value: line);

    /// <summary>The render attempts in a row an instance may wait without showing its frame before the runtime narrates
    /// why: about thirty seconds at the simulation's default rate, far past a cold boot's pipeline builds.</summary>
    public const int StallFrames = 900;
    // Repeated producer answers reuse their named diagnostic, even after a rendered frame clears the standing.
    private string?[] m_productionReasons = [];
    private string?[] m_productionMessages = [];
    private bool[] m_historySucceeded = [];

    private RenderGraphHistory? m_historyPrior;
    private RenderGraphSchedule? m_historySchedule;

    /// <summary>Gets whether the latest produced frame's root image shows that frame:
    /// <see cref="FrameCompletion.Rendered"/> when the root rendered it over inputs current for it (or stands unchanged),
    /// <see cref="FrameCompletion.NotYetRenderable"/> while an instance it reads within the frame, or the root itself, has
    /// not rendered it yet, and <see cref="FrameCompletion.Refused"/> when one cannot until something it was built from
    /// changes; with the reason, naming the instance and its state, when it is not rendered.</summary>
    public FrameRender Render { get; private set; } = FrameRender.Waiting(reason: "the runtime has produced no frame");

    // Sparse list of currently stale instance ordinals and each ordinal's slot in that list (-1 when current),
    // maintained in MarkStale/MarkCurrent/ResetStale so steady frames with zero stale instances cost O(1).
    private readonly List<int> m_staleIndices = [];
    private int[] m_staleSlot = [];

    private ReadOnlySpan<int> StaleOrdinals => CollectionsMarshal.AsSpan(list: m_staleIndices);

    // Sizes the per-instance standing to a set of instances, every one current until it renders.
    private void ResetStale(int count) {
        m_stale = new string?[count];
        m_staleRefused = new bool[count];
        m_staleFrames = new int[count];
        m_staleIndices.Clear();
        m_staleSlot = new int[count];
        Array.Fill(array: m_staleSlot, value: -1);
        m_productionReasons = new string?[count];
        m_productionMessages = new string?[count];
        m_historySucceeded = new bool[count];
    }
    // Scheduling predicts writes for same-frame reads. Keep only writers that actually succeeded, including when a
    // later instance throws; a failed frame must not advance unattempted writers or keep a speculative allocation.
    private void FinishHistory() {
        if ((m_historySchedule is not { } schedule) || (m_historyPrior is not { } prior)) { return; }
        for (var position = 0; (position < schedule.Renders.Count); position++) {
            var index = schedule.Renders[position];

            if (!m_historySucceeded[index]) { schedule.Next.Withdraw(index: index, previous: prior); }
            m_historySucceeded[index] = false;
        }
        m_historySchedule = null;
        m_historyPrior = null;
    }
    private void RememberOutput(int index, ShaderPipelineRenderNode node, RenderGraphSchedule schedule, in Surface surface) {
        NotePackageTaint(index: index);
        SettleWithdrawal(index: index);
        var publication = (((node.PublishedBinding is { } binding) && node.TryGetBoundImage(image: out var input, name: binding))
            ? input.Publication
            : new ReferenceRevision(Owner: node, Revision: node.SubmissionCount));

        var buffer = node.LatestOutputBuffer();
        var buffers = node.LatestOutputBuffers(reuse: m_previous[index].Buffers);
        var standsFor = StandingOf(index: index, node: node, schedule: schedule, surface: in surface);

        m_previous[index] = m_current[index];
        m_current[index] = new Output(
            Buffer: buffer.Buffer, Frame: schedule.Frame, Image: surface, Layout: node.PublishedLayout,
            Buffers: buffers,
            BufferPublication: buffer.Publication,
            StateTick: node.PublishedStateTick,
            Publication: publication,
            StandsFor: standsFor,
            Tainted: (m_taintedReads[index] is not null));
        if (!standsFor.IsOwn) {
            TrackStandingCandidate(index: index);
        }
    }
    private void MarkCurrent(int index) {
        var slot = m_staleSlot[index];

        if (slot >= 0) {
            var last = (m_staleIndices.Count - 1);
            var moved = m_staleIndices[last];

            m_staleIndices[slot] = moved;
            m_staleSlot[moved] = slot;
            m_staleIndices.RemoveAt(index: last);
            m_staleSlot[index] = -1;
        }

        m_stale[index] = null;
        m_staleRefused[index] = false;
        m_staleFrames[index] = 0;

        if (
            (m_narrated.Count != 0) &&
            m_narrated.Remove(
                key: m_set.Instances[index].Name,
                value: out var cleared
            ) &&
            !m_narrated.ContainsValue(value: cleared)
        ) {
            Narrate(line: $"[render-graph] cleared: {cleared}");
        }
    }
    // Drops the narrations of instances the set no longer holds; nothing renders them, so nothing would clear them.
    private void ForgetRemovedNarrations() {
        if (m_narrated.Count == 0) { return; }

        foreach (var name in m_narrated.Keys.ToArray()) {
            if (m_set.IndexOf(name: name) < 0) { _ = m_narrated.Remove(key: name); }
        }
    }
    private void MarkStale(int index, string reason, bool refused = false) {
        if (m_staleSlot[index] < 0) {
            m_staleSlot[index] = m_staleIndices.Count;
            m_staleIndices.Add(item: index);
        }

        m_stale[index] = reason;
        m_staleRefused[index] = refused;

        var name = m_set.Instances[index].Name;

        if (
            (refused || (++m_staleFrames[index] > StallFrames)) &&
            !(m_narrated.TryGetValue(key: name, value: out var narrated) && string.Equals(a: reason, b: narrated, comparisonType: StringComparison.Ordinal))
        ) {
            var told = m_narrated.ContainsValue(value: reason);

            m_narrated[name] = reason;
            if (!told) { Narrate(line: $"[render-graph] {(refused ? "refused" : $"not rendered for {m_staleFrames[index]} attempts")}: {reason}"); }
        }
    }
    // A paused node presents its exact previous image on purpose. Its readers consume that standing publication, not
    // an absent render at the latest scheduled frame.
    private bool Stands(int index) => (m_nodes[index] is { Paused: true });
    // An uploaded source whose producer is not running (IRenderGraphSourceUpload.IsAbsent). Its absence is a defined
    // outcome, not a refusal or a wait: the instance is current, it has no output, and a reader draws its stand-in, so
    // neither the source nor any reader holds the frame incomplete.
    private bool IsAbsent(int index) => (m_sources[index] is { IsAbsent: true });
    // A scheduled graph instance whose node produced nothing this frame. A paused node presents its last image on purpose.
    // This is the one place a graph instance's refusal becomes Refused: a package's refusal of the instance
    // (IRenderGraphPackageFactory.RefusalOf, such as an SDF residency's refused tables), the node's refused build or a
    // missing graph; otherwise the instance is waiting. A source write that waits also passes through here, since binding
    // its region advances the build. A producer's or an upload's own three-way answer is taken as it is (MarkProduction).
    private void MarkUnproduced(int index, ShaderPipelineRenderNode node, string? waiting = null) {
        var name = m_set.Instances[index].Name;

        if (Stands(index: index)) {
            MarkCurrent(index: index);
        } else if (node.LastRecordError is { } failure) {
            // A failed recording may fail the same way on every frame, so no wait is known to end it.
            MarkStale(
                index: index,
                reason: $"the instance '{name}' cannot render: {failure}",
                refused: true
            );
        } else if ((PackageReasonOf(index: index, kind: PackageReason.Refusal) ?? m_sources[index]?.Upload.Fault) is { } refusal) {
            MarkStale(
                index: index,
                reason: $"the instance '{name}' cannot render: {refusal}",
                refused: true
            );
        } else if (
            (node.LastSwapError is { } error) &&
            !node.IsBuildingCandidate &&
            !node.HasPendingCandidate
        ) {
            MarkStale(
                index: index,
                reason: $"the instance '{name}' cannot render: {error.Message}",
                refused: true
            );
        } else if (m_graphs[index] is null) {
            MarkStale(
                index: index,
                reason: $"the instance '{name}' cannot render: {(m_sources[index]?.Fault ?? "no graph is installed")}",
                refused: true
            );
        } else if (!node.IsReady) {
            MarkStale(
                index: index,
                reason: (waiting ?? $"the instance '{name}' has no installed graph yet: its pipelines are building")
            );
        } else {
            MarkStale(
                index: index,
                reason: (waiting ?? $"the instance '{name}' kept an earlier frame's image while its graph rebuilds")
            );
        }
    }
    // What a package is asked of an instance: its refusal, why its rendered image is incomplete, or why it records nothing.
    private enum PackageReason { Refusal, Waiting, Withheld }

    // The first reason of one kind a package of the instance's graph states for it.
    private string? PackageReasonOf(int index, PackageReason kind) {
        var factories = m_instanceFactories[index];

        if (factories.Length == 0) {
            return null;
        }

        var name = m_set.Instances[index].Name;

        for (var position = 0; (position < factories.Length); position++) {
            var factory = factories[position];

            if (
                ((kind switch {
                    PackageReason.Refusal => factory.RefusalOf(instance: name),
                    PackageReason.Waiting => factory.WaitingOf(instance: name),
                    _ => factory.WithheldOf(instance: name),
                }) is { } reason)
            ) {
                return reason;
            }
        }

        return null;
    }
    // A producer's or an upload's own answer that it produced nothing: refused or waiting, as it says, naming the
    // instance and its reason.
    private void MarkProduction(int index, FrameRender production) {
        if ((m_productionMessages[index] is null) || (m_productionReasons[index] != production.Reason)) {
            m_productionReasons[index] = production.Reason;
            m_productionMessages[index] = $"the instance '{m_set.Instances[index].Name}' produced no output: {production.Reason}";
        }

        MarkStale(
            index: index,
            reason: m_productionMessages[index]!,
            refused: (production.Completion == FrameCompletion.Refused)
        );
    }
    // A graph instance that rendered: stale when an input it reads within the frame is, when it bound a stand-in, or when
    // the output it bound is older than the frame the schedule has it read.
    private void MarkRendered(int index, RenderGraphSchedule schedule) {
        if (!MarkReadStale(index: index, schedule: schedule)) {
            if (PackageReasonOf(index: index, kind: PackageReason.Waiting) is { } waiting) {
                MarkStale(index: index, reason: waiting);
            } else {
                MarkCurrent(index: index);
            }
        }
    }
    // Only reads the schedule shows can affect completion. External producers and screen-sampling packages read
    // edges without graph input bindings; an unshown bound image has no bearing on the displayed frame.
    private bool MarkReadStale(int index, RenderGraphSchedule schedule) {
        schedule.ConsumerReads(consumer: index, count: out var readCount, start: out var readStart);
        if (readCount == 0) {
            return false;
        }

        var name = m_set.Instances[index].Name;
        string? waiting = null;
        var reads = schedule.Reads;
        var readEnd = (readStart + readCount);

        for (var position = readStart; (position < readEnd); position++) {
            var read = reads[position];

            if (read.PreviousFrame) {
                continue;
            }
            var producer = schedule.ReadProducer(readIndex: position);

            if ((m_producers[index] is null) &&
                !BindsProducer(index: index, producer: producer) &&
                !SamplesReads(index: index)) {
                continue;
            }
            if (m_stale[producer] is { } reason) {
                if (m_staleRefused[producer]) {
                    MarkStale(index: index, reason: reason, refused: true);

                    return true;
                }
                waiting ??= reason;

                continue;
            }
            if (m_standInReads[index] == read.Producer) {
                waiting ??= $"the instance '{name}' rendered over a stand-in for '{read.Producer}', which has no output for the frame";

                continue;
            }
            if (
                (m_producers[producer] is null) &&
                !IsAbsent(index: producer) &&
                !Stands(index: producer) &&
                (read.Frame >= 0) &&
                (RecordedAt(
                    frame: read.Frame,
                    producer: producer
                ).Frame < read.Frame)
            ) {
                waiting ??= $"the instance '{name}' read an earlier frame's image of '{read.Producer}'";
            }
        }

        if (waiting is null) {
            return false;
        }

        MarkStale(index: index, reason: waiting);

        return true;
    }
    // Decides the frame's completion from the root's standing.
    private void Complete() {
        // A capture waiting here has not reached a node's readback. A refused dependency cannot become current by
        // composing another frame, so report its refusal instead of keeping the capture's host clock held.
        if ((m_capture.PendingPath is not null) && m_staleRefused[m_captureInstance] &&
            (m_stale[m_captureInstance] is { } refusal)) {
            m_capture.Refuse(error: new InvalidOperationException(message: refusal));
        }
        if ((PresentedPath is not null) && m_staleRefused[m_captureInstance] &&
            (m_stale[m_captureInstance] is { } presentedRefusal)) {
            RefusePresented(error: new InvalidOperationException(message: presentedRefusal));
        }
        if (m_stale[m_root] is { } reason) {
            Render = (m_staleRefused[m_root]
                ? FrameRender.Refused(reason: reason)
                : FrameRender.Waiting(reason: reason));
        } else if (
            (m_producers[m_root] is null) &&
            (m_current[m_root].Frame < 0)
        ) {
            Render = FrameRender.Waiting(reason: $"the root '{m_set.Instances[m_root].Name}' has produced no output");
        } else {
            Render = FrameRender.Rendered;
        }
    }
}
