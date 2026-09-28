using Puck.SignedDistance;

namespace Puck.SdfVm;

/// <summary>One view instance's asynchronous presentation picker. Requests use normalized view coordinates and copy
/// only one visibility pixel. Superseded requests and answers from a replaced program, identity table or routed view are
/// discarded. All members run on the presentation thread; nothing is sustained into simulation input.</summary>
public sealed class SdfWorldPicker {
    private readonly List<SdfWorldPickReadback> m_readbacks = [];

    private long m_request;
    private bool m_pending;
    private bool m_requested;
    private bool m_awaiting;
    private bool m_surface;
    private long m_cut;
    private SdfProgram? m_program;
    private ISdfPickMap? m_map;
    private float m_x;
    private float m_y;
    private SdfWorldView? m_view;

    /// <summary>Gets the latest completed answer for the view, retained while a newer hover is in flight, or null before one completes.</summary>
    public SdfPickResult? Result { get; private set; }
    /// <summary>Gets the currently followed view, for presentation diagnostics of the same residency that answers picks.
    /// Null while no view is followed.</summary>
    public SdfWorldView? View => m_view;
    /// <summary>Gets whether a request still needs a rendered frame.</summary>
    public bool Pending => m_pending;

    /// <summary>Requests the pixel at normalized coordinates, with the origin at the view's top left. Each coordinate
    /// must be finite and in [0, 1). Calling again supersedes every earlier result.</summary>
    /// <param name="x">The horizontal coordinate.</param>
    /// <param name="y">The vertical coordinate.</param>
    /// <param name="surface">Whether to copy the packed surface normal and capture the rendered camera.</param>
    /// <returns>The request identity, echoed by the completed answer.</returns>
    /// <exception cref="ArgumentOutOfRangeException">A coordinate is outside [0, 1) or nonfinite.</exception>
    public long Request(float x, float y, bool surface = false) {
        if (!float.IsFinite(f: x) || (x < 0) || (x >= 1)) {
            throw new ArgumentOutOfRangeException(paramName: nameof(x));
        }
        if (!float.IsFinite(f: y) || (y < 0) || (y >= 1)) {
            throw new ArgumentOutOfRangeException(paramName: nameof(y));
        }
        m_surface = surface;
        m_requested = true;
        m_awaiting = true;
        m_x = x;
        m_y = y;
        m_pending = true;
        Result = null;
        return ++m_request;
    }
    /// <summary>Demands a live hover pixel. While an answer is in flight, equal coordinates reuse that request;
    /// after completion demand samples the next rendered frame, including camera and geometry motion beneath a
    /// stationary pointer. The last completed answer stays visible until its replacement arrives.</summary>
    /// <param name="x">The normalized horizontal coordinate.</param>
    /// <param name="y">The normalized vertical coordinate.</param>
    /// <param name="surface">Whether this consumer needs the surface normal and captured camera.</param>
    /// <returns>The request identity.</returns>
    public long Demand(float x, float y, bool surface = false) {
        if (m_requested && m_awaiting && (m_x == x) && (m_y == y) && (m_surface == surface)) {
            return m_request;
        }
        var previous = Result;
        var request = Request(surface: surface, x: x, y: y);

        Result = previous;
        return request;
    }
    /// <summary>Cancels the current request and clears its result without waiting for any submission.</summary>
    public void Clear() {
        m_request++;
        m_pending = false;
        m_requested = false;
        m_awaiting = false;
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
            // A queued coordinate has no captured identity until Take. Keep it for this new frame, but never
            // publish an answer copied against an earlier program or placement map.
            if (m_pending) {
                Result = null;
            } else {
                Clear();
            }
            m_cut = cut;
            m_program = frame?.Program;
            m_map = frame?.PickMap;
        }
        foreach (var readback in m_readbacks) {
            readback.Poll();
        }
    }
    internal void Attach(SdfWorldPickReadback readback) => m_readbacks.Add(item: readback);
    internal void Detach(SdfWorldPickReadback readback) {
        _ = m_readbacks.Remove(item: readback);
        Clear();
    }
    internal bool Take(uint width, uint height, SdfFrame frame, SdfReprojectionView sample, long cut, out SdfPickResult request) {
        if (!m_pending) {
            request = default;
            return false;
        }
        m_pending = false;
        request = new SdfPickResult(Request: m_request,
            X: Math.Min(val1: ((uint)(m_x * width)), val2: (width - 1)), Y: Math.Min(val1: ((uint)(m_y * height)), val2: (height - 1)),
            Width: width, Height: height, Identity: 0, Distance: 0, Material: -1, Program: frame.Program, MeshRevision: frame.MeshDrawsRevision, Map: frame.PickMap) { CutRevision = cut, Sample = (m_surface ? sample : null) };
        return true;
    }
    internal void Publish(SdfPickResult result) {
        if ((result.Request == m_request) && Current(result: result)) {
            Result = result;
            m_awaiting = false;
        }
    }

    private bool Current(SdfPickResult result) => ((m_view?.Residency.Frame is { } frame) &&
        (result.CutRevision == m_cut) && ReferenceEquals(objA: result.Program, objB: frame.Program) && ReferenceEquals(objA: result.Map, objB: frame.PickMap));
}
