using Puck.SignedDistance;

namespace Puck.SdfVm;

/// <summary>One view instance's asynchronous presentation picker. Requests use normalized view coordinates and copy
/// only one visibility pixel, with the frame's dispatch box. At most one copy is in flight: while it is, a newer
/// coordinate waits as the latest request and records when the copy completes, so a moving pointer gets one answer per
/// round trip and forces no render while it waits. Answers from a replaced program, identity table or routed view are
/// discarded. All members run on the presentation thread; nothing is sustained into simulation input.</summary>
public sealed class SdfWorldPicker {
    private readonly List<SdfWorldPickReadback> m_readbacks = [];

    // The latest request, and the newest identity whose answer is never published: Request and Clear raise it past
    // every earlier request, Demand leaves it, so a hover still publishes an older coordinate's answer, labelled with
    // that coordinate.
    private long m_request;
    private long m_floor;
    // The identity of the explicit request a caller waits on: a hover's Demand leaves it standing until it is answered,
    // cleared or replaced by another explicit request.
    private long m_reserved;
    // The published answer to that explicit request, kept apart from Result until its caller takes it: a hover's release
    // or a later hover answer replaces Result, and the caller reads on a different thread's schedule than the frame.
    private SdfPickResult? m_unclaimed;
    // Whether the latest request has no answer yet, and whether it still needs a copy recorded.
    private bool m_awaiting;
    private bool m_queued;
    // The readback holding the one copy in flight, or null.
    private SdfWorldPickReadback? m_flight;
    private bool m_surface;
    private long m_cut;
    private SdfProgram? m_program;
    private ISdfPickMap? m_map;
    private float m_x;
    private float m_y;
    private SdfWorldView? m_view;

    /// <summary>Gets the latest published answer, retained while a newer request is in flight, or null before one
    /// completes. Its <see cref="SdfPickResult.X"/> and <see cref="SdfPickResult.Y"/> are the pixel it answers for, which
    /// a hover's latest coordinate may already have left.</summary>
    public SdfPickResult? Result { get; private set; }
    /// <summary>Gets the currently followed view, for presentation diagnostics of the same residency that answers picks.
    /// Null while no view is followed.</summary>
    public SdfWorldView? View => m_view;
    /// <summary>Gets whether the latest request needs the next rendered frame to record its copy: false while another
    /// copy is in flight and while nothing is requested, so neither forces a render.</summary>
    public bool Pending => (m_queued && (m_flight is null));
    /// <summary>Gets whether the latest request has no answer yet, whether its copy is still to be recorded or already in
    /// flight. It turns false when the request is answered, cleared or discarded with the followed view.</summary>
    public bool Awaiting => m_awaiting;
    /// <summary>Gets whether a recorder follows this picker, so a request can ever be answered: false for a view that is
    /// registered but not rendering.</summary>
    public bool Attached => (m_readbacks.Count > 0);
    /// <summary>Gets whether an unanswered explicit <see cref="Request"/> stands, which <see cref="Demand"/> and a hover's
    /// release leave alone.</summary>
    public bool Reserved => (m_awaiting && (m_reserved == m_request));
    /// <summary>Gets whether a copy is in flight.</summary>
    public bool InFlight => (m_flight is not null);
    /// <summary>Gets the current request identity. A shared one-shot consumer cancels only while this still names
    /// its own request; a newer consumer must keep its demand.</summary>
    public long RequestIdentity => m_request;

    /// <summary>Requests the pixel at normalized coordinates, with the origin at the view's top left. Each coordinate
    /// must be finite and in [0, 1). Calling again supersedes every earlier result, and an answer to an earlier
    /// request is never published. The request stays the latest until it is answered, cleared or replaced by another
    /// request: a <see cref="Demand"/> meanwhile reuses it whatever its coordinates.</summary>
    /// <param name="x">The horizontal coordinate.</param>
    /// <param name="y">The vertical coordinate.</param>
    /// <param name="surface">Whether to copy the packed surface normal and capture the rendered camera.</param>
    /// <returns>The request identity, echoed by the completed answer.</returns>
    /// <exception cref="ArgumentOutOfRangeException">A coordinate is outside [0, 1) or nonfinite.</exception>
    public long Request(float x, float y, bool surface = false) {
        var request = Issue(surface: surface, x: x, y: y);

        m_floor = (request - 1);
        m_reserved = request;
        m_unclaimed = null;
        Result = null;
        return request;
    }
    /// <summary>Takes the published answer to an explicit <see cref="Request"/>, once. Unlike <see cref="Result"/> it
    /// survives a hover's <see cref="Release"/> and a later hover answer, until the caller that asked takes it, another
    /// explicit request replaces it, or <see cref="Clear"/> cancels it.</summary>
    /// <param name="request">The identity <see cref="Request"/> returned.</param>
    /// <returns>The answer, or null while it has not landed, was already taken or was discarded.</returns>
    public SdfPickResult? TakeAnswer(long request) {
        if (m_unclaimed is { } answer && (answer.Request == request)) {
            m_unclaimed = null;
            return answer;
        }

        return null;
    }
    /// <summary>Ends a hover's use of this picker: <see cref="Clear"/> without discarding the answer of an explicit
    /// request that its caller has not taken yet.</summary>
    public void Release() {
        var unclaimed = m_unclaimed;

        Clear();
        m_unclaimed = unclaimed;
    }
    /// <summary>Demands a live hover pixel. While the latest request is unanswered, equal coordinates reuse it; after
    /// its answer, demand samples the next rendered frame, including camera and geometry motion beneath a stationary
    /// pointer. A moved coordinate replaces the latest request, and an answer still in flight for an earlier one is
    /// published labelled with its own pixel. An unanswered explicit <see cref="Request"/> is never replaced. The last
    /// published answer stays visible until its replacement arrives.</summary>
    /// <param name="x">The normalized horizontal coordinate.</param>
    /// <param name="y">The normalized vertical coordinate.</param>
    /// <param name="surface">Whether this consumer needs the surface normal and captured camera.</param>
    /// <returns>The request identity.</returns>
    /// <exception cref="ArgumentOutOfRangeException">A coordinate is outside [0, 1) or nonfinite.</exception>
    public long Demand(float x, float y, bool surface = false) {
        if (Reserved || (m_awaiting && (m_x == x) && (m_y == y) && (m_surface == surface))) {
            return m_request;
        }

        return Issue(surface: surface, x: x, y: y);
    }
    /// <summary>Cancels the current request and clears its result without waiting for any submission. A copy still in
    /// flight completes unpublished before another records.</summary>
    public void Clear() {
        m_floor = ++m_request;
        m_queued = false;
        m_awaiting = false;
        m_unclaimed = null;
        Result = null;
    }

    internal void Follow(SdfWorldView? view) {
        if (m_view != view) {
            Clear();
            m_view = view;
        }
        var frame = view?.Residency.Frame;
        var cut = ((frame is not null) ? frame.Views[Math.Min(val1: view!.Value.View, val2: (frame.Views.Count - 1))].CutRevision : 0);

        if ((m_cut != cut) || !ReferenceEquals(objA: m_program, objB: frame?.Program) || !ReferenceEquals(objA: m_map, objB: frame?.PickMap)) {
            // A copy recorded against the earlier program, map or cut is never published; the latest request, if
            // unanswered, records again against the new frame.
            Result = null;
            m_queued |= m_awaiting;
            m_cut = cut;
            m_program = frame?.Program;
            m_map = frame?.PickMap;
        }
    }
    internal void Attach(SdfWorldPickReadback readback) => m_readbacks.Add(item: readback);
    // A retired recorder takes its copy in flight with it: the latest request, if unanswered, records again through
    // the readback that replaces it, which attaches before the retired one detaches. Only the last one clears.
    internal void Detach(SdfWorldPickReadback readback) {
        _ = m_readbacks.Remove(item: readback);
        if (ReferenceEquals(objA: m_flight, objB: readback)) {
            m_flight = null;
            m_queued |= m_awaiting;
        }
        if (m_readbacks.Count == 0) {
            Clear();
        }
    }
    internal bool Take(SdfWorldPickReadback readback, uint width, uint height, SdfFrame frame, SdfReprojectionView sample, long cut, out SdfPickResult request) {
        if (!Pending) {
            request = default;
            return false;
        }
        m_queued = false;
        m_flight = readback;
        request = new SdfPickResult(Request: m_request,
            X: Math.Min(val1: ((uint)(m_x * width)), val2: (width - 1)), Y: Math.Min(val1: ((uint)(m_y * height)), val2: (height - 1)),
            Width: width, Height: height, Identity: 0, Distance: 0, Material: -1, Program: frame.Program, MeshRevision: frame.MeshDrawsRevision, Map: frame.PickMap) { CutRevision = cut, Sample = (m_surface ? sample : null) };
        return true;
    }
    // Ends the copy in flight: an answer once its node proved the copy's slot complete, or null when its recording was
    // never submitted.
    internal void Complete(SdfWorldPickReadback readback, SdfPickResult? result) {
        if (ReferenceEquals(objA: m_flight, objB: readback)) {
            m_flight = null;
        }
        if ((result is { } answer) && (answer.Request > m_floor) && Current(result: answer)) {
            Result = answer;
            if (answer.Request == m_reserved) { m_unclaimed = answer; }
            if (answer.Request == m_request) {
                m_awaiting = false;
                m_queued = false;
            }
            return;
        }
        m_queued |= m_awaiting;
    }

    private long Issue(float x, float y, bool surface) {
        if (!float.IsFinite(f: x) || (x < 0) || (x >= 1)) {
            throw new ArgumentOutOfRangeException(paramName: nameof(x));
        }
        if (!float.IsFinite(f: y) || (y < 0) || (y >= 1)) {
            throw new ArgumentOutOfRangeException(paramName: nameof(y));
        }
        m_surface = surface;
        m_x = x;
        m_y = y;
        m_awaiting = true;
        m_queued = true;
        return ++m_request;
    }
    private bool Current(SdfPickResult result) => ((m_view?.Residency.Frame is { } frame) &&
        (result.CutRevision == m_cut) && ReferenceEquals(objA: result.Program, objB: frame.Program) && ReferenceEquals(objA: result.Map, objB: frame.PickMap));
}
