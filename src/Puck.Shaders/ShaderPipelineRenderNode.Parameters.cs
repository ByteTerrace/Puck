using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace Puck.Shaders;

// A bound parameter's value: one scalar config field a host writes into its pass's parameter block between frames,
// which the pass block copies at the field's offset when the pass next binds. The block is the array TryBind made for the
// installed graph, owned by the node alone, so a write lands in place and allocates nothing; a rebind of the pass's
// config (TrySetConfig, an install) replaces the block and returns every field to its bound value until the host writes
// again.
public sealed partial class ShaderPipelineRenderNode {
    /// <summary>Writes one scalar config field of an installed pass: a float field takes the value as a float, an int
    /// or uint field takes it rounded to the nearest integer and clamped to the field's range. The value reaches the GPU
    /// the next time the pass binds.</summary>
    /// <param name="passName">The pass.</param>
    /// <param name="field">The pass's config field, which must be a scalar.</param>
    /// <param name="value">The value.</param>
    /// <returns><see langword="true"/> when an installed pass declares the scalar field and the value was written;
    /// <see langword="false"/> before an install, for an unknown pass or field, a vector field, or a value that is not
    /// finite.</returns>
    public bool TryWriteParameter(string passName, string field, double value) {
        if (
            !double.IsFinite(d: value) ||
            !TryFindScalarSlot(
                field: field,
                pass: out var pass,
                passName: passName,
                slot: out var slot
            )
        ) {
            return false;
        }

        var block = MemoryMarshal.AsMemory(memory: pass.Parameters.Bytes).Span[((int)slot.Offset)..];

        var previous = BinaryPrimitives.ReadUInt32LittleEndian(source: block);

        switch (slot.Type) {
            case ShaderValueType.Float:
                BinaryPrimitives.WriteSingleLittleEndian(destination: block, value: ((float)value));

                break;
            case ShaderValueType.Int:
                BinaryPrimitives.WriteInt32LittleEndian(destination: block, value: ((int)Math.Clamp(
                    max: int.MaxValue,
                    min: int.MinValue,
                    value: Math.Round(mode: MidpointRounding.ToEven, value: value)
                )));

                break;
            default:
                BinaryPrimitives.WriteUInt32LittleEndian(destination: block, value: ((uint)Math.Clamp(
                    max: uint.MaxValue,
                    min: 0d,
                    value: Math.Round(mode: MidpointRounding.ToEven, value: value)
                )));

                break;
        }

        if ((previous != BinaryPrimitives.ReadUInt32LittleEndian(source: block)) && (pass.Cadence is { } cadence)) { cadence.Signature = null; }
        return true;
    }
    /// <summary>Reads one scalar config field of an installed pass as the pass block holds it.</summary>
    /// <param name="passName">The pass.</param>
    /// <param name="field">The pass's config field, which must be a scalar.</param>
    /// <param name="value">The field's value, or zero when this returns <see langword="false"/>.</param>
    /// <returns><see langword="true"/> when an installed pass declares the scalar field.</returns>
    public bool TryReadParameter(string passName, string field, out double value) {
        if (!TryFindScalarSlot(
            field: field,
            pass: out var pass,
            passName: passName,
            slot: out var slot
        )) {
            value = 0d;

            return false;
        }

        var block = pass.Parameters.Bytes.Span[((int)slot.Offset)..];

        value = (slot.Type switch {
            ShaderValueType.Float => BinaryPrimitives.ReadSingleLittleEndian(source: block),
            ShaderValueType.Int => BinaryPrimitives.ReadInt32LittleEndian(source: block),
            _ => BinaryPrimitives.ReadUInt32LittleEndian(source: block),
        });

        return true;
    }

    private bool TryFindScalarSlot(string passName, string field, out RuntimePass pass, out ShaderPipelineParameterSlot slot) {
        pass = null!;
        slot = null!;

        if (
            (m_pipeline is null) ||
            !m_ready
        ) {
            return false;
        }

        foreach (var candidate in m_passes) {
            if (!string.Equals(
                a: candidate.Name,
                b: passName,
                comparisonType: StringComparison.Ordinal
            )) {
                continue;
            }

            var slots = candidate.ParametersLayout.Slots;

            for (var index = 0; (index < slots.Count); index++) {
                var declared = slots[index];

                if (
                    string.Equals(
                    a: declared.Name,
                    b: field,
                    comparisonType: StringComparison.Ordinal
                ) &&
                    (declared.Type.ComponentCount() == 1)
                ) {
                    pass = candidate;
                    slot = declared;

                    return true;
                }
            }

            return false;
        }

        return false;
    }

    /// <summary>Copies one pass's live packed parameter block for inspection or persistence.</summary>
    public bool TryGetConfigSnapshot(string passName, out byte[] bytes) {
        ArgumentException.ThrowIfNullOrWhiteSpace(passName);
        var pass = m_passes.FirstOrDefault(predicate: item => (item.Name == passName));

        if (pass is null) {
            bytes = [];
            return false;
        }
        bytes = pass.Parameters.Bytes.ToArray();
        return true;
    }
    /// <summary>Rebinds a complete JSON object to one pass's authored parameter schema.</summary>
    public bool TrySetConfig(string passName, JsonElement? config, out string reason) {
        ArgumentException.ThrowIfNullOrWhiteSpace(passName);
        if (
            (m_pipeline is null) ||
            !m_ready
        ) {
            reason = "The shader pipeline has not allocated its GPU resources yet.";
            return false;
        }
        var pass = m_passes.FirstOrDefault(predicate: item => (item.Name == passName));

        if (pass is null) {
            reason = $"Unknown shader pass '{passName}'.";
            return false;
        }
        if (!pass.ParametersLayout.TryBind(
            config: config,
            reason: out reason,
            values: out var values
        )) {
            return false;
        }
        if (!pass.Parameters.Bytes.Span.SequenceEqual(other: values.Bytes.Span) && (pass.Cadence is { } cadence)) { cadence.Signature = null; }
        pass.Parameters = values;
        return true;
    }
}
