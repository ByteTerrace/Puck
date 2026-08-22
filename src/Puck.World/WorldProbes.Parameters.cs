using System.Diagnostics;
using System.Globalization;
using Puck.Shaders;

namespace Puck.World;

internal sealed partial class WorldProbes {
    // Resolves and deep-validates one declared parameter binding row: its (probe, channel) reference, and its
    // target — a composed render.extensions entry (already proven at document load) whose manifest declares the
    // named field as a scalar float, since FullscreenPassNode.TrySetConfig only accepts one; or another declared
    // probe whose kind declares the named field as a scalar float, patched into that probe's packed constants.
    private ParameterState BuildParameter(int probeIndex, WorldProbeBinding.Parameter parameter, string path) {
        var channel = ResolveChannel(
            channel: parameter.Channel,
            path: path,
            probeIndex: probeIndex
        );

        if (parameter.Target is WorldProbeParameterTarget.Probe target) {
            if (!m_probeIndexById.TryGetValue(key: target.Id, value: out var targetIndex)) {
                throw new InvalidOperationException(message: $"{path}.target.id '{target.Id}' names no declared probe.");
            }

            var targetManifest = m_probes[targetIndex].Manifest;

            if (
                !targetManifest.TryGetConstantOffset(field: target.Field, offset: out var offset, type: out var type) ||
                (type != ShaderValueType.Float) ||
                (targetManifest.Config is not { } targetConfig) ||
                !targetConfig.TryGetValue(key: target.Field, value: out var targetField)
            ) {
                throw new InvalidOperationException(message: $"{path}.target.field '{target.Field}' names no float config field of probe kind '{targetManifest.Name}'.");
            }
            if (
                !ShaderConfigBinding.InRange(field: targetField, value: parameter.Range.X) ||
                !ShaderConfigBinding.InRange(field: targetField, value: parameter.Range.Y)
            ) {
                throw new InvalidOperationException(message: $"{path}.range [{parameter.Range.X}, {parameter.Range.Y}] leaves the declared range of '{targetManifest.Name}.{target.Field}' [{targetField.Min}, {targetField.Max}].");
            }

            return new ParameterState {
                ProbeIndex = probeIndex,
                Channel = channel,
                ConstantOffset = offset,
                MaxAgeTicks = (long)(parameter.MaxAgeSeconds * Stopwatch.Frequency),
                Row = parameter,
                TargetProbeIndex = targetIndex,
            };
        }

        if (parameter.Target is not WorldProbeParameterTarget.Extension extension) {
            throw new InvalidOperationException(message: $"{path}.target is required.");
        }

        ShaderSetManifest extensionManifest;

        try {
            extensionManifest = WorldPostRenderExtensions.Shipped.Load(id: extension.Id);
        } catch (Exception exception) {
            throw new InvalidOperationException(message: $"{path}.target.id '{extension.Id}' failed to load: {exception.Message}", innerException: exception);
        }

        if (
            (extensionManifest.Config is not { } config) ||
            !config.TryGetValue(key: extension.Field, value: out var field) ||
            (field.Type != ShaderValueType.Float)
        ) {
            throw new InvalidOperationException(message: $"{path}.target.field '{extension.Field}' names no float config field of extension '{extension.Id}'.");
        }
        if (
            !ShaderConfigBinding.InRange(field: field, value: parameter.Range.X) ||
            !ShaderConfigBinding.InRange(field: field, value: parameter.Range.Y)
        ) {
            throw new InvalidOperationException(message: $"{path}.range [{parameter.Range.X}, {parameter.Range.Y}] leaves the declared range of '{extension.Id}.{extension.Field}' [{field.Min}, {field.Max}].");
        }

        return new ParameterState {
            ProbeIndex = probeIndex,
            Channel = channel,
            ExtensionField = extension.Field,
            ExtensionId = extension.Id,
            MaxAgeTicks = (long)(parameter.MaxAgeSeconds * Stopwatch.Frequency),
            Row = parameter,
        };
    }
    // Writes every declared parameter binding's conditioned value into its target — a composed extension pass, or
    // another probe's constants (patched in place and handed to its running kernel, which adopts them on its next
    // cycle) — skipping a stale reading (older than the binding's own maxAgeSeconds — the write simply stops, never
    // forcing the target back to some other value) and an unchanged write. A boot shape that never composed
    // presentation (no pass registered under the extension's id) leaves every binding's Writes at zero — a harmless,
    // honestly-reported no-op, never a fault.
    private void ServiceParameters() {
        var nowTimestamp = Stopwatch.GetTimestamp();

        foreach (var parameter in m_parameterBindings) {
            var probe = m_probes[parameter.ProbeIndex];

            if (!probe.Ring.TryReadLatest(reading: out var reading)) {
                continue;
            }
            if ((nowTimestamp - reading.CaptureTimestamp) > parameter.MaxAgeTicks) {
                continue;
            }

            var spec = probe.Manifest.Channels[parameter.Channel];
            var normalized = NormalizeChannel(
                raw: (double)reading[parameter.Channel],
                min: spec.Min,
                max: spec.Max,
                neutral: spec.Neutral
            );
            var unitInterval = ((normalized + 1.0) / 2.0);
            var range = parameter.Row.Range;
            var value = (float)(range.X + (unitInterval * (range.Y - range.X)));

            if (value == parameter.LastValue) {
                continue;
            }

            if (parameter.TargetProbeIndex >= 0) {
                WriteConstant(offset: parameter.ConstantOffset, target: m_probes[parameter.TargetProbeIndex], value: value);
            } else if (
                !m_passes.TryGet(id: parameter.ExtensionId!, pass: out var pass) ||
                !pass.TrySetConfig(field: parameter.ExtensionField!, value: value)
            ) {
                continue;
            }

            parameter.LastValue = value;
            parameter.Writes++;
        }
    }
    /// <summary>Patches one float config field of a declared probe's kind live — the same write
    /// <see cref="ServiceParameters"/> performs for a <c>probe</c>-target parameter binding, driven instead by the
    /// <c>probe.set</c> verb. Bound only by the field's own declared range, never a binding's authored one; a
    /// <c>parameter</c> binding targeting the same field overwrites this write on its own next changed reading.
    /// </summary>
    /// <param name="probeId">The declared probe id.</param>
    /// <param name="field">The probe kind's float config field name.</param>
    /// <param name="value">The value to write.</param>
    /// <param name="reason">Why the write was refused, set only when this returns <see langword="false"/>.</param>
    /// <returns><see langword="true"/> when the field was written.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="probeId"/> or <paramref name="field"/> is
    /// <see langword="null"/>.</exception>
    public bool TrySetField(string probeId, string field, float value, out string? reason) {
        ArgumentNullException.ThrowIfNull(argument: probeId);
        ArgumentNullException.ThrowIfNull(argument: field);

        if (!m_probeIndexById.TryGetValue(
            key: probeId,
            value: out var probeIndex
        )) {
            reason = $"no probe '{probeId}'";

            return false;
        }

        var probe = m_probes[probeIndex];
        var manifest = probe.Manifest;

        if (
            !manifest.TryGetConstantOffset(field: field, offset: out var offset, type: out var type) ||
            (type != ShaderValueType.Float) ||
            (manifest.Config is not { } config) ||
            !config.TryGetValue(key: field, value: out var configField)
        ) {
            reason = $"'{field}' names no float config field of probe kind '{manifest.Name}'";

            return false;
        }
        if (!ShaderConfigBinding.InRange(field: configField, value: value)) {
            reason = $"{value.ToString(format: "0.0000", provider: CultureInfo.InvariantCulture)} is outside the declared range of '{manifest.Name}.{field}' [{configField.Min}, {configField.Max}]";

            return false;
        }

        WriteConstant(offset: offset, target: probe, value: value);
        reason = null;

        return true;
    }
    // The one place a probe's packed constants are patched and handed to its running kernel — a parameter
    // binding's target write and probe.set's live write share this so the two paths can never drift.
    private static void WriteConstant(int offset, ProbeState target, float value) {
        BitConverter.TryWriteBytes(destination: target.Constants.AsSpan(start: offset), value: value);
        target.Run?.SetConstants(constants: target.Constants);
    }
}
