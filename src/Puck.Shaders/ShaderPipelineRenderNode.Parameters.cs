using System.Buffers.Binary;
using System.Runtime.InteropServices;

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

        return true;
    }
    /// <summary>Returns whether an installed pass declares an array in its World block.</summary>
    /// <param name="passName">The pass.</param>
    /// <param name="array">The array's name.</param>
    /// <returns><see langword="true"/> when an installed pass declares the array.</returns>
    public bool DeclaresArray(string passName, string array) =>
        TryWriteArray(
            array: array,
            passName: passName,
            values: default,
            write: false
        );
    /// <summary>Writes one array of an installed pass's World block, which the pass reads from the next time it binds
    /// (<see cref="ShaderPipelineParameterLayout.WriteArray"/>).</summary>
    /// <param name="passName">The pass.</param>
    /// <param name="array">The pass's array.</param>
    /// <param name="values">The elements; elements past them read zero.</param>
    /// <returns><see langword="true"/> when an installed pass declares the array and the values were written.</returns>
    public bool TryWriteArray(string passName, string array, ReadOnlySpan<double> values) =>
        TryWriteArray(
            array: array,
            passName: passName,
            values: values,
            write: true
        );

    private bool TryWriteArray(string passName, string array, ReadOnlySpan<double> values, bool write) {
        if (
            (m_pipeline is null) ||
            !m_ready
        ) {
            return false;
        }

        foreach (var pass in m_passes) {
            if (!string.Equals(
                a: pass.Name,
                b: passName,
                comparisonType: StringComparison.Ordinal
            )) {
                continue;
            }
            if (pass.WorldBlock is not { } block) {
                return false;
            }

            var arrays = pass.ParametersLayout.Arrays;

            for (var index = 0; (index < arrays.Count); index++) {
                if (string.Equals(
                    a: arrays[index].Name,
                    b: array,
                    comparisonType: StringComparison.Ordinal
                )) {
                    if (write) {
                        pass.ParametersLayout.WriteArray(
                            array: arrays[index],
                            block: block,
                            values: values
                        );
                    }

                    return true;
                }
            }

            return false;
        }

        return false;
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
}
