using Puck.SignedDistance;

namespace Puck.SdfVm;

/// <summary>A completed presentation-only pick of one rendered pixel. The program and mesh revision identify the
/// rendered source; the captured immutable map keeps delayed identity lookup independent of later scene changes.</summary>
/// <param name="Request">The request identity.</param>
/// <param name="X">The sampled pixel column.</param>
/// <param name="Y">The sampled pixel row.</param>
/// <param name="Width">The rendered width.</param>
/// <param name="Height">The rendered height.</param>
/// <param name="Identity">The visibility identity: kind in bits 31..30, source in bits 29..0.</param>
/// <param name="Distance">The ray parameter along the normalized camera ray.</param>
/// <param name="Material">The winning material index.</param>
/// <param name="Program">The program rendered by this request.</param>
/// <param name="MeshRevision">The mesh draw revision rendered by this request.</param>
/// <param name="Map">The immutable host identity table captured with the frame.</param>
public readonly record struct SdfPickResult(long Request, uint X, uint Y, uint Width, uint Height, uint Identity, float Distance, int Material, SdfProgram Program, long MeshRevision, ISdfPickMap? Map = null) {
    /// <summary>Gets the host identity from the table captured by this request.</summary>
    public object? Target => Map?.Resolve(identity: Identity);
    /// <summary>Gets the hit kind: 0 background, 1 SDF, 2 mesh.</summary>
    public uint Kind => (Identity >> 30);
    /// <summary>Gets the source: an SDF instance ordinal plus one, or a mesh draw ordinal.</summary>
    public uint Source => Identity & 0x3FFFFFFF;
    /// <summary>Gets whether the pixel hits geometry.</summary>
    public bool Hit => (Kind != 0);
}
/// <summary>One view instance's asynchronous presentation picker. Requests use normalized view coordinates and copy
/// only one visibility pixel. Superseded requests and answers from a replaced program, identity table or routed view are
/// discarded. All members run on the presentation thread; nothing is sustained into simulation input.</summary>
public sealed class SdfWorldPicker {
    private readonly List<SdfWorldPickReadback> m_readbacks = [];

    private long m_request;
    private bool m_pending;
    private bool m_requested;
    private bool m_awaiting;
    private SdfProgram? m_program;
    private ISdfPickMap? m_map;
    private float m_x;
    private float m_y;
    private SdfWorldView? m_view;

    /// <summary>Gets the latest completed answer for the view, retained while a newer hover is in flight, or null before one completes.</summary>
    public SdfPickResult? Result { get; private set; }
    /// <summary>Gets whether a request still needs a rendered frame.</summary>
    public bool Pending => m_pending;

    /// <summary>Requests the pixel at normalized coordinates, with the origin at the view's top left. Each coordinate
    /// must be finite and in [0, 1). Calling again supersedes every earlier result.</summary>
    /// <param name="x">The horizontal coordinate.</param>
    /// <param name="y">The vertical coordinate.</param>
    /// <returns>The request identity, echoed by the completed answer.</returns>
    /// <exception cref="ArgumentOutOfRangeException">A coordinate is outside [0, 1) or nonfinite.</exception>
    public long Request(float x, float y) {
        if (!float.IsFinite(f: x) || (x < 0) || (x >= 1)) {
            throw new ArgumentOutOfRangeException(paramName: nameof(x));
        }
        if (!float.IsFinite(f: y) || (y < 0) || (y >= 1)) {
            throw new ArgumentOutOfRangeException(paramName: nameof(y));
        }
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
    /// <returns>The request identity.</returns>
    public long Demand(float x, float y) {
        if (m_requested && m_awaiting && (m_x == x) && (m_y == y)) {
            return m_request;
        }
        var previous = Result;
        var request = Request(x: x, y: y);

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

        if (!ReferenceEquals(objA: m_program, objB: frame?.Program) || !ReferenceEquals(objA: m_map, objB: frame?.PickMap)) {
            // A queued coordinate has no captured identity until Take. Keep it for this new frame, but never
            // publish an answer copied against an earlier program or placement map.
            if (m_pending) {
                Result = null;
            } else {
                Clear();
            }
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
    internal bool Take(uint width, uint height, SdfFrame frame, out SdfPickResult request) {
        if (!m_pending) {
            request = default;
            return false;
        }
        m_pending = false;
        request = new SdfPickResult(Request: m_request,
            X: Math.Min(val1: ((uint)(m_x * width)), val2: (width - 1)), Y: Math.Min(val1: ((uint)(m_y * height)), val2: (height - 1)),
            Width: width, Height: height, Identity: 0, Distance: 0, Material: -1, Program: frame.Program, MeshRevision: frame.MeshDrawsRevision, Map: frame.PickMap);
        return true;
    }
    internal void Publish(SdfPickResult result) {
        if ((result.Request == m_request) && Current(result: result)) {
            Result = result;
            m_awaiting = false;
        }
    }

    private bool Current(SdfPickResult result) => ((m_view?.Residency.Frame is { } frame) &&
        ReferenceEquals(objA: result.Program, objB: frame.Program) && ReferenceEquals(objA: result.Map, objB: frame.PickMap));
}
