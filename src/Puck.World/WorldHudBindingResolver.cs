using System.Globalization;
using Puck.Maths;
using Puck.Overlays;
using Puck.World.Client;
using Puck.World.Server;

namespace Puck.World;

/// <summary>
/// The render-side implementation of <see cref="IHudBindingResolver"/> for the closed <see cref="HudBindingVocabulary"/>:
/// resolves each frame's live value for <c>world.tick</c>, <c>world.fps</c>, <c>seat.&lt;n&gt;.position.{x,y,z}</c>,
/// and <c>population.active</c>. Presentation-only: every normalization here is cosmetic (which fraction of a gauge
/// fills), never simulation state, and is free to change without a determinism concern.
/// </summary>
internal sealed class WorldHudBindingResolver(WorldClient client, FrameRateMonitor frameRate, WorldPopulation population, WorldContinuum continuum) : IHudBindingResolver {
    // A generous FPS ceiling a gauge fraction normalizes against (240 covers every target hertz World boots at).
    private const float FpsNormalizerCeiling = 240f;
    // A generous symmetric world-extent a seat-position gauge fraction normalizes against — cosmetic only; a body
    // outside this range simply clamps to a full/empty gauge rather than under/overflowing.
    private const float PositionNormalizerHalfRange = 50f;
    // world.tick has no natural ceiling (it grows for the life of the session), so its gauge fraction cycles at this
    // period instead of saturating at 1 forever after the first few seconds — a visibly moving fill, which is the
    // point of binding a gauge to it at all.
    private const ulong TickCycleLength = 256UL;

    private readonly WorldClient m_client = client;
    private readonly FrameRateMonitor m_frameRate = frameRate;
    private readonly WorldPopulation m_population = population;
    private readonly WorldContinuum m_continuum = continuum;

    private void ResolveFps(out float fraction, out string text) {
        var fps = m_frameRate.Summarize().AverageFps;

        fraction = Math.Clamp(
            max: 1f,
            min: 0f,
            value: (fps / FpsNormalizerCeiling)
        );
        text = fps.ToString(
            format: "F1",
            provider: CultureInfo.InvariantCulture
        );
    }
    private void ResolvePopulationActive(out float fraction, out string text) {
        var active = m_population.SimulatedCount;

        fraction = Math.Clamp(
            value: (((float)active) / m_population.PeerCapacity),
            min: 0f,
            max: 1f
        );
        text = active.ToString(provider: CultureInfo.InvariantCulture);
    }
    // Seat n (1-based) resolves through the same authority claim and frame mapping as its camera.
    private void ResolveSeatPosition(HudBindingKind kind, int seatIndex, out float fraction, out string text) {
        var slot = (seatIndex - 1);

        if (!m_continuum.TryResolveSeatPose(
            interpolationAlpha: 1f,
            orientation: out _,
            position: out var position,
            slot: slot
        )) {
            fraction = 0f;
            text = "unavailable";

            return;
        }
        var component = kind switch {
            HudBindingKind.SeatPositionX => position.X,
            HudBindingKind.SeatPositionY => position.Y,
            _ => position.Z,
        };

        fraction = Math.Clamp(
            max: 1f,
            min: 0f,
            value: ((component + PositionNormalizerHalfRange) / (PositionNormalizerHalfRange * 2f))
        );
        text = component.ToString(
            format: "F2",
            provider: CultureInfo.InvariantCulture
        );
    }
    // A state.<row> or state.<row>.<key> binding's live value, resolved through WorldStateReader — the ONE (row, key)
    // read the rule gates, the rule effects and world.state's own read-back all share, so none of them can disagree
    // about which cell a pair names. cellKey null means the plain state.<row> form (the row's own SLOT cell); cellKey
    // non-null means the state.<row>.<key> form (any named cell in ANY row shape).
    //
    // The TICK passed is m_client.Tick — the LAST DELIVERED SNAPSHOT's tick, which is snapshot time, not the server's
    // completed tick. It is what this side honestly knows: the client never runs the simulation, and a value the HUD
    // draws is a value the client was told. It IS a server tick (WorldSnapshot.Tick), so it is comparable to an
    // advancing row's epoch and a gauge bound to such a row DRAWS LIVE — lagging by delivery, never reading a
    // different clock, and never reaching past the snapshot it is drawing.
    //
    // Either way the GAUGE fraction is computed from the ROW's own declared Min/Max envelope — cells share one
    // envelope per row, they do not carry their own — so a keyed row's gauge is exactly as meaningful as a slot's. A
    // row/cell that does not exist (validation refuses this at world scope, but a seat-scope panel can never verify
    // existence, so the render path stays honest too), a keyed row bound with the plain state.<row> form, or a row
    // carrying no declared range draws an EMPTY gauge (fraction 0) — the same "an unbound gauge draws empty"
    // precedent every other gauge follows; a bool/text row carries no range at all, so its gauge fraction is always
    // 0.
    private void ResolveState(string name, string? cellKey, out float fraction, out string text) {
        fraction = 0f;
        text = string.Empty;

        if (
            !WorldStateReader.TryRead(
            definition: m_client.Definition,
            rowName: name,
            key: cellKey,
            tick: m_client.Tick,
            row: out var row,
            rawValue: out var rawValue,
            text: out var cellText
        ) ||
            (rawValue is not { } raw)
        ) {
            return;
        }

        switch (row.Kind) {
            case CellKind.Int:
                text = raw.ToString(provider: CultureInfo.InvariantCulture);

                if (
                    (row.Min is { } lo) &&
                    (row.Max is { } hi) &&
                    (hi > lo)
                ) {
                    fraction = Math.Clamp(
                        max: 1f,
                        min: 0f,
                        value: (((float)(raw - lo)) / (hi - lo))
                    );
                }

                break;
            case CellKind.Fixed:
                text = FixedQ4816.FromRawBits(value: raw).ToString();

                if (
                    (row.Min is { } floLimit) &&
                    (row.Max is { } fhiLimit) &&
                    (fhiLimit > floLimit)
                ) {
                    fraction = Math.Clamp(
                        max: 1f,
                        min: 0f,
                        value: (((float)(raw - floLimit)) / (fhiLimit - floLimit))
                    );
                }

                break;
            case CellKind.Bool:
                text = ((raw != 0)
                    ? "true"
                    : "false"
                );

                break;
            case CellKind.Text:
                text = (cellText ?? string.Empty);

                break;
        }
    }
    private void ResolveTick(out float fraction, out string text) {
        var tick = m_client.Tick;

        fraction = (((float)(tick % TickCycleLength)) / TickCycleLength);
        text = tick.ToString(provider: CultureInfo.InvariantCulture);
    }

    /// <inheritdoc/>
    public bool TryResolve(string binding, out float fraction, out string text) {
        fraction = 0f;
        text = string.Empty;

        if (!HudBindingVocabulary.TryParse(
            binding: out var parsed,
            token: binding
        )) {
            return false;
        }

        switch (parsed.Kind) {
            case HudBindingKind.WorldTick:
                ResolveTick(
                    fraction: out fraction,
                    text: out text
                );

                return true;
            case HudBindingKind.WorldFps:
                ResolveFps(
                    fraction: out fraction,
                    text: out text
                );

                return true;
            case HudBindingKind.PopulationActive:
                ResolvePopulationActive(
                    fraction: out fraction,
                    text: out text
                );

                return true;
            case HudBindingKind.SeatPositionX:
            case HudBindingKind.SeatPositionY:
            case HudBindingKind.SeatPositionZ:
                ResolveSeatPosition(
                    kind: parsed.Kind,
                    seatIndex: parsed.SeatIndex,
                    fraction: out fraction,
                    text: out text
                );

                return true;
            case HudBindingKind.StateNamed:
                ResolveState(
                    name: parsed.StateName!,
                    cellKey: parsed.StateCellKey,
                    fraction: out fraction,
                    text: out text
                );

                return true;
            default:
                return false;
        }
    }
}
