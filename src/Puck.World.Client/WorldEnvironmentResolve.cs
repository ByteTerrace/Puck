using System.Numerics;
using Puck.Hosting;
using Puck.SdfVm;
using Puck.SignedDistance;

namespace Puck.World.Client;

/// <summary>Resolves authored lighting, sky and studio reflections through the shared typed value resolver.
/// Only values actually read invalidate the cached environment. Rate integrals are compiled with the world;
/// presentation evaluates their active pieces at the requested tick, without retaining motion history.</summary>
public sealed partial class WorldEnvironmentResolve {
    private readonly SdfLighting[] m_output = [new(), new()];
    private readonly SdfLighting m_resolved = new();
    private readonly SdfLighting m_empty = new();
    private readonly SdfLighting m_default = SdfLighting.Default();

    private WorldValueReadSet? m_reads;
    private WorldDefinition? m_definition;
    private WorldPresentationRates? m_rates;
    private WorldSkyMotion? m_cloudMotion;
    private WorldSkyMotion? m_starMotion;
    private PresentedTick? m_motionTick;

    private int m_revision = -1;

    private int m_outputIndex;

    /// <summary>Gets the number of document-to-environment resolves, excluding cache hits and anchor poses.</summary>
    public long Resolutions { get; private set; }
    /// <summary>Gets the number of rate evaluations actually performed.</summary>
    public long RateEvaluations { get; private set; }
    /// <summary>Gets the active-piece binary-search comparisons actually performed.</summary>
    public long PieceSearches { get; private set; }
    /// <summary>Gets the active antiderivative's Bernstein blends actually performed.</summary>
    public long CoefficientBlends { get; private set; }
    /// <summary>Gets the modular doublings used to include whole elapsed periods.</summary>
    public long PeriodDoublings { get; private set; }
    /// <summary>Gets the world's shared retained coefficient storage, counted once by its compilation cache.</summary>
    public WorldRateCost CompiledRates => (m_rates?.Cost ?? default);

    /// <summary>Resolves a presented environment. The returned instance is reused every other call; a consumer
    /// holding it across frames must copy it. Anchored positional lights follow their current pose on every call.</summary>
    /// <param name="definition">The live world definition, whose state-only copies retain the revision.</param>
    /// <param name="revision">The structural definition revision.</param>
    /// <param name="mirror">The presented state and tick.</param>
    /// <param name="resolveLightAnchor">The current positional-light anchor resolver.</param>
    /// <returns>The resolved environment.</returns>
    public SdfLighting Resolve(WorldDefinition definition, int revision, WorldStateMirror mirror,
        Func<WorldAnchor, SdfAnchor?>? resolveLightAnchor = null) {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(mirror);
        var replaced = (m_revision != revision);

        if (replaced) {
            m_definition = WorldPresentationValues.Of(definition: definition).Definition;
            PrepareDomains(definition: m_definition);
            m_rates = WorldPresentationRates.Of(definition: definition);
            m_cloudMotion = m_starMotion = null;
            if (m_definition.Render.Sky?.Layers is { } layers) {
                for (var index = 0; (index < layers.Count); index++) {
                    var layer = layers[index];

                    if (m_rates.Of(layer: layer) is not { Moves: true } motion) { continue; }
                    if (layer is WorldRenderSkyLayer.Clouds) { m_cloudMotion = motion; } else if (layer is WorldRenderSkyLayer.Stars) { m_starMotion = motion; }
                }
            }
            m_revision = revision;
        }
        if ((m_reads is null) || !ReferenceEquals(objA: m_reads.Mirror, objB: mirror)) {
            m_reads = new(Mirror: mirror);
            replaced = true;
        }
        if (replaced || m_reads.Changed) {
            m_reads.Reset();
            Write(m_definition!, m_reads.Values, m_resolved);
            m_motionTick = null;
            Resolutions++;
        }
        if (((m_cloudMotion is not null) || (m_starMotion is not null)) && (m_motionTick != mirror.Presented)) {
            WriteMotion(mirror.Presented, m_resolved);
            m_motionTick = mirror.Presented;
        }
        var output = m_output[m_outputIndex];

        m_outputIndex ^= 1;
        output.CopyFrom(source: m_resolved);
        ApplyAnchors(definition: m_definition!, output: output, resolveLightAnchor: resolveLightAnchor);
        return output;
    }

    private static void ApplyAnchors(SdfLighting output, WorldDefinition definition,
        Func<WorldAnchor, SdfAnchor?>? resolveLightAnchor) {
        if (definition.Render.Lighting?.Lights is not { } lights) { return; }
        for (var index = 0; (index < Math.Min(val1: lights.Count, val2: output.LightCount)); index++) {
            var anchor = lights[index] switch {
                WorldRenderLight.Point point => point.Anchor,
                WorldRenderLight.Occluder occluder => occluder.Anchor,
                _ => null,
            };

            if (anchor is null) { continue; }
            var light = output.GetLight(index: index);

            output.SetLight(index: index, light: ((resolveLightAnchor?.Invoke(anchor) is { } frame)
                ? light with { DynamicSlot = -1, Direction = (frame.Position + Vector3.Transform(light.Direction, frame.Orientation)) }
                : light with { DynamicSlot = -1, Weight = 0f }));
        }
    }
    private void WriteMotion(PresentedTick tick, SdfLighting into) {
        if ((m_cloudMotion is { } clouds) && (into.CloudCoverage > 0f)) {
            into.CloudOffset = new(x: Evaluate(clouds.DriftX, tick, SdfVolume.NoisePeriodCells),
                y: Evaluate(clouds.DriftY, tick, SdfVolume.NoisePeriodCells));
            into.CloudShearOffset = new(x: Evaluate(clouds.ShearX, tick, SdfVolume.NoisePeriodCells),
                y: Evaluate(clouds.ShearY, tick, SdfVolume.NoisePeriodCells));
            into.CloudSpinAngle = Evaluate(clouds.Spin, tick, Math.Tau);
        }
        if ((m_starMotion is { } stars) && (into.StarBrightness > 0f) && (into.StarDensity > 0f)
            && (into.TwinkleShare > 0f) && (into.TwinkleDepth > 0f)) {
            var phase = Evaluate(stars.Twinkle, tick, 1d);

            into.TwinklePhase = ((phase < 0f) ? (phase + 1f) : phase);
        }
    }
    private float Evaluate(WorldRateIntegral? rate, PresentedTick tick, double modulus) {
        if (rate is null) { return 0f; }
        var resolved = rate.At(modulus: modulus, tick: tick, work: out var work);

        RateEvaluations++;
        PieceSearches += work.PieceSearches;
        CoefficientBlends += work.CoefficientBlends;
        PeriodDoublings += work.PeriodDoublings;
        return ((float)resolved);
    }
    private static float Scalar(WorldValueResolver values, BindableScalar? value, float fallback) =>
        ((value is { } scalar) ? (float)values.Scalar(fallback: fallback, value: scalar) : fallback);
    private static float Angle(WorldValueResolver values, BindableAngle? value, float fallback) =>
        ((value is { } angle) ? (float)values.Angle(fallback: fallback, value: angle) : fallback);
    private static Vector3 Direction(WorldValueResolver values, BindableDirection? value, Vector3 fallback) =>
        ((value is { } direction) ? values.Direction(fallback: fallback, value: direction) : fallback);
    private static Vector3 Position(WorldValueResolver values, BindableVector3? value, Vector3 fallback) =>
        ((value is { } position) ? values.Vector(fallback: fallback, value: position) : fallback);
    // Environment color lanes are RGB; alpha is resolved by the shared grammar and discarded at this seam.
    private static Vector3 Rgb(WorldValueResolver values, BindableColor? value, Vector3 fallback) {
        if (value is not { } color) { return fallback; }
        var resolved = values.Color(color, new Vector4(value: fallback, w: 1f));

        return new(x: resolved.X, y: resolved.Y, z: resolved.Z);
    }
}
