using System.Diagnostics;
using System.Globalization;
using Puck.Shaders;

namespace Puck.World;

internal sealed partial class WorldProbes {
    // Deep-validates and resolves one declared parameter binding row into its reusable template: its (probe,
    // channel) reference, and its target — a composed render.extensions entry (already proven at document load)
    // whose manifest declares the named field as a scalar float, since FullscreenPassNode.TrySetConfig only accepts
    // one; or another declared probe whose kind declares the named field as a scalar float. The target probe row is
    // resolved here (m_rowIndexById is complete by the time this runs); the target instance is resolved fresh every
    // ServiceParameters pass — if the target row is seat-relative, against this binding's own instance seat; if
    // single, its one instance — so a target that has not instanced yet (or has retired) is never baked in.
    private ParameterBindingTemplate BuildParameterTemplate(WorldProbeBinding.Parameter parameter, string path, ProbeRowInfo rowInfo) {
        var channel = ResolveChannel(
            channel: parameter.Channel,
            manifest: rowInfo.Manifest,
            path: path
        );

        if (parameter.Target is WorldProbeParameterTarget.Probe target) {
            if (!m_rowIndexById.TryGetValue(key: target.Id, value: out var targetIndex)) {
                throw new InvalidOperationException(message: $"{path}.target.id '{target.Id}' names no declared probe.");
            }

            var targetRow = m_rows[targetIndex];
            var targetManifest = targetRow.Manifest;

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

            return new ParameterBindingTemplate {
                Channel = channel,
                ConstantOffset = offset,
                MaxAgeTicks = (long)(parameter.MaxAgeSeconds * Stopwatch.Frequency),
                Row = parameter,
                TargetRowInfo = targetRow,
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

        return new ParameterBindingTemplate {
            Channel = channel,
            ExtensionField = extension.Field,
            ExtensionId = extension.Id,
            MaxAgeTicks = (long)(parameter.MaxAgeSeconds * Stopwatch.Frequency),
            Row = parameter,
        };
    }
    // Builds one instance's live parameter binding state from its row's already-validated template — cheap, never
    // re-validating.
    private static ParameterState BuildParameter(ProbeInstance instance, ParameterBindingTemplate template) {
        return new ParameterState {
            Channel = template.Channel,
            ConstantOffset = template.ConstantOffset,
            ExtensionField = template.ExtensionField,
            ExtensionId = template.ExtensionId,
            Instance = instance,
            MaxAgeTicks = template.MaxAgeTicks,
            Row = template.Row,
            TargetRowInfo = template.TargetRowInfo,
        };
    }
    // Writes every declared parameter binding's conditioned value into its target — a composed extension pass, or
    // another probe's instance (resolved fresh here, at this binding's own instance seat, and patched in place —
    // handed to its running kernel, which adopts them on its next cycle) — skipping a stale reading (older than the
    // binding's own maxAgeSeconds — the write simply stops, never forcing the target back to some other value), a
    // target that is not currently live (a seat-relative target with no instance at this seat), and an unchanged
    // write. A boot shape that never composed presentation (no pass registered under the extension's id) leaves
    // every binding's Writes at zero — a harmless, honestly-reported no-op, never a fault.
    private void ServiceParameters() {
        var nowTimestamp = Stopwatch.GetTimestamp();

        foreach (var instance in m_liveInstances) {
            foreach (var parameter in instance.ParameterBindings) {
                if (!instance.Ring.TryReadLatest(reading: out var reading)) {
                    continue;
                }
                if ((nowTimestamp - reading.CaptureTimestamp) > parameter.MaxAgeTicks) {
                    continue;
                }

                var spec = instance.RowInfo.Manifest.Channels[parameter.Channel];
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

                if (parameter.TargetRowInfo is { } targetRow) {
                    if (ResolveInstance(target: targetRow, contextSeat: instance.Seat) is not { } targetInstance) {
                        continue;
                    }

                    WriteConstant(offset: parameter.ConstantOffset, target: targetInstance, value: value);
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
    }
    /// <summary>Patches one float config field of a declared probe's kind live — the same write
    /// <see cref="ServiceParameters"/> performs for a <c>probe</c>-target parameter binding, driven instead by the
    /// <c>probe.set</c> verb. Bound only by the field's own declared range, never a binding's authored one; a
    /// <c>parameter</c> binding targeting the same field overwrites this write on its own next changed reading. With
    /// no <c>@seat</c> suffix, a seat-relative probe's every live instance is written.</summary>
    /// <param name="probeRef">The declared probe id, optionally suffixed <c>@&lt;seat&gt;</c> to name one instance
    /// of a seat-relative row.</param>
    /// <param name="field">The probe kind's float config field name.</param>
    /// <param name="value">The value to write.</param>
    /// <param name="reason">Why the write was refused, set only when this returns <see langword="false"/>.</param>
    /// <returns><see langword="true"/> when the field was written to at least one live instance.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="probeRef"/> or <paramref name="field"/> is
    /// <see langword="null"/>.</exception>
    public bool TrySetField(string probeRef, string field, float value, out string? reason) {
        ArgumentNullException.ThrowIfNull(argument: probeRef);
        ArgumentNullException.ThrowIfNull(argument: field);

        ParseInstanceRef(probeRef: probeRef, baseId: out var baseId, seat: out var seat);

        if (!m_rowIndexById.TryGetValue(
            key: baseId,
            value: out var rowIndex
        )) {
            reason = $"no probe '{baseId}'";

            return false;
        }

        var rowInfo = m_rows[rowIndex];
        var manifest = rowInfo.Manifest;

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

        if (seat is { } explicitSeat) {
            if (ResolveInstance(target: rowInfo, contextSeat: explicitSeat) is not { } instance) {
                reason = $"no live instance '{baseId}@{explicitSeat}'{DescribeKnownInstances(rowInfo: rowInfo)}";

                return false;
            }

            WriteConstant(offset: offset, target: instance, value: value);
            reason = null;

            return true;
        }

        if (!rowInfo.IsSeatRelative) {
            if (rowInfo.SingleInstance is not { } single) {
                reason = $"probe '{baseId}' has no live instance";

                return false;
            }

            WriteConstant(offset: offset, target: single, value: value);
            reason = null;

            return true;
        }

        var wroteAny = false;

        foreach (var instance in rowInfo.InstancesBySeat!.Values) {
            WriteConstant(offset: offset, target: instance, value: value);
            wroteAny = true;
        }

        if (!wroteAny) {
            reason = $"probe '{baseId}' has no live instances";

            return false;
        }

        reason = null;

        return true;
    }
}
