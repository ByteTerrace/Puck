using System.Globalization;
using Puck.Maths;
using Puck.Overlays;
using Puck.World.Client;
using Puck.World.Server;

namespace Puck.World;

/// <summary>
/// The render-side implementation of <see cref="IHudBindingResolver"/> for the closed <see cref="HudBindingVocabulary"/>:
/// resolves each frame's live value for <c>world.tick</c>, <c>world.fps</c>, <c>seat.&lt;n&gt;.position.{x,y,z}</c>,
/// <c>population.active</c>, and <c>state.&lt;row&gt;[.&lt;key&gt;][.$target]</c>. Each token is parsed once, on first
/// sight; a state token reads the slot the document's presentation manifest registered with the client's
/// <see cref="WorldStateMirror"/>, found afresh each frame, so it never reads a slot a document swap retired. Presentation-only:
/// every normalization here is cosmetic (which fraction of a gauge fills), never simulation state, and is free to
/// change without a determinism concern.
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
    // Every token seen so far, parsed once; an unknown token is remembered as unresolvable.
    private readonly Dictionary<string, (bool Known, HudBinding Binding)> m_tokens = new(comparer: StringComparer.Ordinal);

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
    // A state.<row> or state.<row>.<key> binding's value, read through the client's state mirror at the slot the token
    // registered. The plain token presents the eased follower of a cell carrying an easing trait, interpolated at the
    // frame's fraction; the .$target token presents the stored truth. The text shows the value the mirror read at
    // the delivered tick.
    //
    // The gauge fraction is computed from the row's own declared Min/Max envelope — cells share one envelope per row,
    // they do not carry their own — so a keyed row's gauge is exactly as meaningful as a slot's. A row/cell that does
    // not exist (validation refuses this at world scope, but a seat-scope panel can never verify existence, so the
    // render path stays honest too), a keyed row bound with the plain state.<row> form, or a row carrying no declared
    // range draws an empty gauge (fraction 0), the same "an unbound gauge draws empty" precedent every other gauge
    // follows; a bool/text row carries no range at all, so its gauge fraction is always 0.
    private void ResolveState(int slot, out float fraction, out string text) {
        fraction = 0f;
        text = string.Empty;

        var mirror = m_client.StateMirror;
        var sample = mirror.Sample(slot: slot);
        var value = sample.Value;

        if (!value.HasValue) {
            return;
        }

        switch (value.Kind) {
            case CellKind.Int:
                text = value.AsInt.ToString(provider: CultureInfo.InvariantCulture);
                fraction = Fraction(
                    fixedPoint: false,
                    max: sample.Max,
                    min: sample.Min,
                    mirror: mirror,
                    slot: slot
                );

                break;
            case CellKind.Fixed:
                text = FixedQ4816.FromRawBits(value: value.AsFixed).ToString();
                fraction = Fraction(
                    fixedPoint: true,
                    max: sample.Max,
                    min: sample.Min,
                    mirror: mirror,
                    slot: slot
                );

                break;
            case CellKind.Bool:
                text = (value.AsBool
                    ? "true"
                    : "false"
                );

                break;
            case CellKind.Text:
                text = (value.AsText ?? string.Empty);

                break;
            case CellKind.Vector:
                // A vector has no scalar reading and no envelope, so the gauge draws empty and the label names the
                // shape rather than a component: a panel bound to one is an authoring mistake an operator reads off
                // the panel, not a throw on the render path.
                text = $"vector[{value.AsVector.Length.ToString(provider: CultureInfo.InvariantCulture)}]";

                break;
            default:
                throw new ArgumentOutOfRangeException(paramName: nameof(slot));
        }
    }
    // A row declaring no range draws empty. The envelope is raw, so a Fixed row's bounds convert to the presented
    // value's units first.
    private static float Fraction(WorldStateMirror mirror, int slot, long? min, long? max, bool fixedPoint) {
        if (
            (min is not { } lo) ||
            (max is not { } hi) ||
            (hi <= lo) ||
            !mirror.TryValue(
            slot: slot,
            value: out var presented
        )
        ) {
            return 0f;
        }

        var low = (fixedPoint
            ? ((double)FixedQ4816.FromRawBits(value: lo))
            : lo
        );
        var high = (fixedPoint
            ? ((double)FixedQ4816.FromRawBits(value: hi))
            : hi
        );

        return ((float)Math.Clamp(
            max: 1d,
            min: 0d,
            value: ((presented - low) / (high - low))
        ));
    }
    private void ResolveTick(out float fraction, out string text) {
        var tick = m_client.Tick;

        fraction = (((float)(tick % TickCycleLength)) / TickCycleLength);
        text = tick.ToString(provider: CultureInfo.InvariantCulture);
    }
    private (bool Known, HudBinding Binding) Token(string binding) {
        if (m_tokens.TryGetValue(
            key: binding,
            value: out var seen
        )) {
            return seen;
        }

        var known = HudBindingVocabulary.TryParse(
            binding: out var parsed,
            token: binding
        );

        seen = (known, parsed);
        m_tokens[binding] = seen;

        return seen;
    }

    /// <inheritdoc/>
    public bool TryResolve(string binding, out float fraction, out string text) {
        fraction = 0f;
        text = string.Empty;

        var (known, parsed) = Token(binding: binding);

        if (!known) {
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
                var slot = m_client.StateMirror.SlotOf(
                    conversion: WorldStateConversion.Number,
                    token: binding
                );

                if (slot < 0) {
                    return false;
                }

                ResolveState(
                    fraction: out fraction,
                    slot: slot,
                    text: out text
                );

                return true;
            default:
                return false;
        }
    }
}
