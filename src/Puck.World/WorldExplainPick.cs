using Puck.Commands;
using Puck.SdfVm;

namespace Puck.World;

/// <summary>Owns one explanation request on the pointer's existing presentation picker. It holds the ordinary
/// hover demand until that exact request completes, and settles every cancellation before releasing the picker.</summary>
public sealed class WorldExplainPick : IDisposable {
    private sealed record Pending(int Slot, SdfWorldPicker Picker, SdfWorldView View, long Request,
        CommandSettlement Settlement, Func<SdfPickResult, CommandResult> Describe);

    private Pending? m_pending;
    private bool m_closed;

    /// <summary>The last completed explanation's immutable pixel, retained independently of later hover answers.</summary>
    public SdfPickResult? Captured { get; private set; }
    /// <summary>The exact residency that rendered <see cref="Captured"/>, never a later routed seat.</summary>
    public SdfWorldResidency? CapturedResidency { get; private set; }
    /// <summary>The view that rendered the retained answer, including its ordinal within that residency.</summary>
    public SdfWorldView? CapturedView { get; private set; }
    /// <summary>The seat that requested the retained explanation, or null before any completes.</summary>
    public int? CapturedSlot { get; private set; }

    /// <summary>Whether this exact picker is reserved by an unfinished explanation.</summary>
    /// <param name="picker">The ordinary pointer picker.</param>
    /// <returns>Whether a pending one-shot request owns this picker.</returns>
    public bool Holds(SdfWorldPicker picker) => ReferenceEquals(objA: m_pending?.Picker, objB: picker);
    /// <summary>Continues ordinary hover only when this picker has no outstanding explanation fence.</summary>
    /// <param name="picker">The shared pointer picker.</param>
    /// <param name="x">The ordinary hover's normalized horizontal coordinate.</param>
    /// <param name="y">The ordinary hover's normalized vertical coordinate.</param>
    /// <param name="surface">Whether the ordinary consumer requires the captured normal and camera.</param>
    /// <exception cref="ArgumentNullException">The picker is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">An admitted hover coordinate is outside [0, 1) or nonfinite.</exception>
    public void Demand(SdfWorldPicker picker, float x, float y, bool surface) {
        ArgumentNullException.ThrowIfNull(picker);
        if (!Holds(picker: picker)) { _ = picker.Demand(surface: surface, x: x, y: y); }
    }
    /// <summary>Requests one surfaced pixel and returns the eventual verdict without blocking the frame owner.</summary>
    /// <param name="slot">The acting seat.</param>
    /// <param name="picker">The existing picker resolved by the shared pointer/pane route.</param>
    /// <param name="x">The shared route's normalized horizontal coordinate.</param>
    /// <param name="y">The shared route's normalized vertical coordinate.</param>
    /// <param name="describe">Shapes and evaluates the captured answer once, after its readback fence.</param>
    /// <returns>A named refusal, or the settlement belonging to exactly this request.</returns>
    /// <exception cref="ArgumentNullException">The answer callback is null.</exception>
    public CommandResult Request(int slot, SdfWorldPicker? picker, float x, float y,
        Func<SdfPickResult, CommandResult> describe) {
        ArgumentNullException.ThrowIfNull(describe);
        if (m_closed) { return Refuse(reason: "the presentation owner was closed"); }
        if (m_pending is not null) { return Refuse(reason: "an explanation is already pending"); }
        if (picker?.View is not { } view) { return Refuse(reason: "no rendered pixel at the acting seat or pane"); }
        if (!float.IsFinite(f: x) || !float.IsFinite(f: y) || (x < 0) || (x >= 1) || (y < 0) || (y >= 1)) {
            return Refuse(reason: "the pointer is outside the acting seat or pane");
        }
        var settlement = new CommandSettlement();
        var request = picker.Request(x, y, surface: true);

        m_pending = new Pending(Describe: describe, Picker: picker, Request: request, Settlement: settlement, Slot: slot, View: view);
        return CommandResult.Settling(settlement);
    }
    /// <summary>Consumes only the requested answer, or refuses it when the acting seat, pane or view goes away.</summary>
    /// <param name="slot">The seat selected by the shared pointer route this frame.</param>
    /// <param name="picker">Its selected picker, or null outside the presented view.</param>
    public void Poll(int slot, SdfWorldPicker? picker) {
        if (m_pending is not { } pending) { return; }
        if (pending.Picker.RequestIdentity != pending.Request) {
            Cancel(reason: "the pixel request was superseded");
            return;
        }
        if ((picker is null) || (slot != pending.Slot) || !ReferenceEquals(objA: picker, objB: pending.Picker) || (picker.View != pending.View)) {
            Cancel(reason: "the acting seat or pane changed before the pixel completed");
            return;
        }
        if (picker.Result is { } answer) {
            if (answer.Request != pending.Request) {
                Cancel(reason: "the pixel request was superseded");
                return;
            }
            m_pending = null;
            Captured = answer;
            CapturedResidency = pending.View.Residency;
            CapturedView = pending.View;
            CapturedSlot = pending.Slot;
            var verdict = Refuse(reason: "the captured answer could not be evaluated");

            try { verdict = pending.Describe(answer); } catch (Exception error) when ((error is InvalidOperationException or ArgumentException)) {
                verdict = Refuse(reason: error.Message);
            } finally { pending.Settlement.Settle(result: verdict); }
        } else if (!picker.Pending && !picker.InFlight) {
            Cancel(reason: "the pixel request was cancelled before its readback");
        }
    }

    private void Cancel(string reason) {
        if (m_pending is not { } pending) { return; }
        m_pending = null;
        if (pending.Picker.RequestIdentity == pending.Request) { pending.Picker.Clear(); }
        pending.Settlement.Settle(result: Refuse(reason: reason));
    }
    private static CommandResult Refuse(string reason) => CommandResult.Error(output: $"[world.explain: {reason}]");

    /// <summary>Refuses an unfinished request so a settling command session cannot wait on a retired renderer.</summary>
    public void Dispose() {
        m_closed = true;
        Cancel(reason: "the presentation owner was closed");
    }
}
