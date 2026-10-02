using System.Numerics;
using Puck.Abstractions.Presentation;
using Puck.Hosting;
using Puck.Shaders;
using Puck.SignedDistance;

namespace Puck.World.Client;

public sealed partial class WorldEnvironmentResolve {
    private SkyRow[] m_skyRows = [];
    private SdfSkySnapshot? m_sky;
    private SdfSkyParameterTable<SdfSkyLayerData>? m_skyCommon;
    private Quaternion m_skyRotation = Quaternion.Identity;

    private sealed class SkyRow(WorldRenderSkyLayer authored, WorldSkyKind kind, WorldValueDomainGroup domain,
        SkyWriter writer, WorldSkyMotion? motion, int authoredIndex, int parameterIndex, WorldClock? clock) {
        public WorldRenderSkyLayer Authored { get; } = authored;
        public WorldSkyKind Kind { get; } = kind;
        public WorldValueDomainGroup Domain { get; } = domain;
        public SkyWriter Writer { get; } = writer;
        public WorldSkyMotion? Motion { get; } = motion;
        public int AuthoredIndex { get; } = authoredIndex;
        public int ParameterIndex { get; } = parameterIndex;
        public WorldClock? Clock { get; } = clock;
        public float Yaw { get; set; }
        public float Pitch { get; set; }
        public float Roll { get; set; }
    }

    private void PrepareSky(WorldDefinition definition) {
        m_sky = null;
        m_skyCommon = null;
        m_skyRows = [];
        m_skyRotation = Quaternion.Identity;
        if (definition.Render.Sky?.Layers is not { } layers) { return; }
        var counts = new int[WorldSkyKinds.All.Count];
        var rowCount = 0;
        var stopCount = 0;
        foreach (var layer in layers) {
            if (WorldSkyKinds.Of(layer) is not { } kind) { continue; }
            counts[kind.Tag]++;
            rowCount++;
            if (layer is WorldRenderSkyLayer.Gradient gradient) { stopCount = checked(stopCount + (gradient.Stops?.Count ?? 0)); }
        }
        if (rowCount == 0) { return; }
        var tables = new ISdfSkyParameterTable?[counts.Length];
        var present = new List<ISdfSkyParameterTable>();
        foreach (var kind in WorldSkyKinds.All) {
            if (counts[kind.Tag] == 0) { continue; }
            tables[kind.Tag] = CreateSkyKindTable(kind, counts[kind.Tag]);
            present.Add(tables[kind.Tag]!);
        }
        m_skyRows = new SkyRow[rowCount];
        m_skyCommon = new("layers", rowCount);
        var stops = new SdfSkyParameterTable<SdfSkyStopData>("stops", stopCount);
        var facts = new SdfSkyLayerInfo[rowCount];
        var next = new int[counts.Length];
        var firstStop = 0;
        var rowIndex = 0;
        var initial = new WorldValueResolver(definition, default);
        for (var authoredIndex = 0; authoredIndex < layers.Count; authoredIndex++) {
            var layer = layers[authoredIndex];
            if (WorldSkyKinds.Of(layer) is not { } kind) { continue; }
            var parameterIndex = next[kind.Tag]++;
            var domain = m_skyDomains[authoredIndex];
            var motion = m_rates!.Of(layer);
            var context = new SkyWriterContext(this, definition, domain, tables[kind.Tag]!, parameterIndex,
                motion, stops, firstStop, $"render.sky.layers[{layer.Name}]");
            m_skyRows[rowIndex] = new(layer, kind, domain, CreateSkyWriter(layer, context), motion,
                authoredIndex, parameterIndex, layer.Clock is { } clock ? initial.Clock(clock) : null);
            facts[rowIndex] = new(layer.Name!, kind.Name, kind.Class, layer.Blend ?? kind.Blend,
                layer.Visibility ?? kind.Visibility, layer.Tier ?? QualityTier.Low, 0f, false, layer.Clock, 0d);
            if (layer is WorldRenderSkyLayer.Gradient gradient) { firstStop += gradient.Stops?.Count ?? 0; }
            rowIndex++;
        }
        m_sky = new(facts, definition.Render.Sky.Quality ?? QualityTier.High, present.ToArray(), m_skyCommon,
            stopCount == 0 ? null : stops);
    }

    private void WriteSkyStack(WorldValueResolver values, SdfLighting into) {
        into.Sky = m_sky;
        if (m_sky is null) { return; }
        var frameResolved = false;
        for (var index = 0; index < m_skyRows.Length; index++) {
            var row = m_skyRows[index];
            var layer = row.Authored;
            var opacity = row.Domain.Scalar("opacity", layer.Opacity, values, 1f, WorldValueDomain.Unit);
            var enabled = opacity > 0f;
            var common = new SdfSkyLayerData {
                InverseRotation = new(0f, 0f, 0f, 1f), Opacity = opacity,
                Kind = row.Kind.Tag, ParameterIndex = (uint)row.ParameterIndex,
                Blend = (uint)(layer.Blend ?? row.Kind.Blend), Visibility = (uint)(layer.Visibility ?? row.Kind.Visibility),
                MinimumTier = (uint)(layer.Tier ?? QualityTier.Low), AuthoredIndex = (uint)row.AuthoredIndex,
            };
            var phase = 0d;
            if (enabled) {
                enabled = row.Writer.Write(values);
                if (enabled) {
                    if (!frameResolved) {
                        var up = m_skyFrameDomain!.Direction("up", m_definition!.Render.Sky?.Frame?.Up, values, Vector3.UnitY);
                        m_skyRotation = WorldSeatCameraResolver.AlignUp(up);
                        frameResolved = true;
                    }
                    row.Yaw = row.Domain.Angle("transform.yaw", layer.Transform?.Yaw, values, 0f, WorldValueDomain.Finite);
                    row.Pitch = row.Domain.Angle("transform.pitch", layer.Transform?.Pitch, values, 0f, WorldValueDomain.Finite);
                    row.Roll = row.Domain.Angle("transform.roll", layer.Transform?.Roll, values, 0f, WorldValueDomain.Finite);
                    common.InverseRotation = Rotation(row, 0f);
                    WriteMask(row, values, ref common);
                    if (row.Clock is { } clock) { phase = values.Phase(clock); }
                }
            }
            common.Enabled = enabled ? 1u : 0u;
            m_skyCommon!.Rows[index] = common;
            m_sky.SetLayer(index, m_sky.Layer(index) with { Opacity = opacity, Enabled = enabled, Phase = phase });
        }
    }

    private static void WriteMask(SkyRow row, WorldValueResolver values, ref SdfSkyLayerData common) {
        if (row.Authored.Mask?.Elevation is { Count: 2 } band) {
            var (low, high) = row.Domain.AngleBand("mask.elevation", band[0], band[1], values, 0f, 0f,
                new(-MathF.PI / 2f, MathF.PI / 2f));
            common.Mask = (uint)SdfSkyMask.Elevation;
            common.Elevation = new(MathF.Sin(low), MathF.Sin(high));
        } else if (row.Authored.Mask?.Cone is { } cone) {
            common.Mask = (uint)SdfSkyMask.Cone;
            common.ConeDirection = row.Domain.Direction("mask.cone.toward", cone.Toward, values, Vector3.UnitY);
            common.ConeCosine = MathF.Cos(row.Domain.Angle("mask.cone.radius", cone.Radius, values,
                MathF.PI, new(0d, MathF.PI, MinimumOpen: true)));
        }
    }

    private Vector4 Rotation(SkyRow row, float motion) {
        var local = Quaternion.CreateFromYawPitchRoll(row.Yaw + motion, row.Pitch, row.Roll);
        var inverse = Quaternion.Conjugate(Quaternion.Concatenate(local, m_skyRotation));
        return new(inverse.X, inverse.Y, inverse.Z, inverse.W);
    }

    private void WriteSkyMotion(PresentedTick tick) {
        for (var index = 0; index < m_skyRows.Length; index++) {
            if (m_skyCommon!.Rows[index].Enabled == 0u) { continue; }
            var row = m_skyRows[index];
            if (row.Writer.Moves) { row.Writer.Move(tick); }
            if (row.Motion?.Yaw is { } yaw) {
                m_skyCommon.Rows[index].InverseRotation = Rotation(row, Evaluate(yaw, tick, Math.Tau));
            }
        }
    }
}
