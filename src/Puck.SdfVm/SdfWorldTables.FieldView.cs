using System.Numerics;
using System.Runtime.InteropServices;

using Puck.Abstractions.Gpu;

namespace Puck.SdfVm;

/// <summary>What a pass block carries of the field views (<c>fieldPlane</c>, <c>fieldStyle</c>, <c>fieldScope</c>): all
/// zero while no view shows one.</summary>
/// <param name="Plane">Where the slicing plane lies, read by its kind (<c>SdfWorldPackage.FieldPlane</c>).</param>
/// <param name="Style">The plane's band spacing, opacity, kind and panel (<c>SdfWorldPackage.FieldStyle</c>).</param>
/// <param name="Scope">How many field entries the table holds, and the pointer's position as a share of the view's width and
/// height, negative for none (<c>SdfWorldPackage.FieldScope</c>).</param>
public readonly record struct SdfFieldValues(Vector4 Plane, Vector4 Style, Vector4 Scope);

// The field view's table (sdfFieldEntries): the entries the field pass walks (SdfFieldEntries), written only while a view of
// the residency shows a field view, so a residency whose views show none stages, uploads and draws nothing for it. The
// region keeps the words it last held; its write owes only the words that moved, so a still world's entries owe nothing.
public sealed partial class SdfWorldTables {
    private const int FieldEntriesRegionIndex = 12;

    private readonly GpuRegion m_fieldEntriesRegion;
    private readonly SdfFieldEntries m_fieldEntries = new();
    private SdfFieldValues m_fieldValues;
    // One more each time what a field view shows changes (its entries, its plane or the pointer): the field pass's and the
    // composite's signatures fold it, so a still view renders the change.
    private ulong m_fieldViewRevision;

    /// <summary>Stages the field views for the frame: while <paramref name="shows"/>, writes the frame's field entries and
    /// carries the plane, its style and the entries' scope into the pass block; otherwise leaves the table as it is and
    /// makes the pass block show nothing. Runs in the residency's preparation, before the tables' signature is
    /// updated.</summary>
    /// <param name="view">The residency's field views' settings.</param>
    /// <param name="frame">The frame the tables hold.</param>
    /// <param name="shows">Whether a view of the residency shows a field view (<see cref="SdfWorldResidency.ShowsField"/>).</param>
    public void StageFieldView(SdfFieldView view, SdfFrame frame, bool shows) {
        ArgumentNullException.ThrowIfNull(argument: view);
        ArgumentNullException.ThrowIfNull(argument: frame);

        if (!shows) {
            if (m_fieldValues != default) {
                m_fieldValues = default;
                m_fieldViewRevision++;
            }
            return;
        }
        m_fieldEntries.Build(frame: frame);

        var words = m_fieldEntries.Words;
        var bytes = (words.Length * sizeof(uint));
        var moved = false;

        if (bytes > m_fieldEntriesRegion.ShadowBytes) {
            Retire(replaced: m_fieldEntriesRegion.Grow(byteCount: bytes), carryingRegion: m_fieldEntriesRegion);
            m_bindingRevision++;
            moved = true;
        }
        if (bytes > 0) {
            moved |= m_fieldEntriesRegion.Write(bytes: MemoryMarshal.AsBytes(span: words), offset: 0);
        }

        var plane = view.Plane;
        var pointer = (((plane?.Kind ?? SdfFieldPlaneKind.None) == SdfFieldPlaneKind.Pointer) ? view.ReadPointer() : null);
        var values = new SdfFieldValues(
            Plane: ((plane is null) ? Vector4.Zero : new Vector4(value: plane.Normal, w: plane.Offset)),
            Scope: new Vector4(x: m_fieldEntries.Count, y: (pointer?.X ?? -1f), z: (pointer?.Y ?? -1f), w: 0f),
            Style: ((plane is null) ? Vector4.Zero : new Vector4(x: plane.Band, y: Math.Clamp(value: plane.Opacity, min: 0f, max: 1f), z: (float)plane.Kind, w: (float)plane.Panel))
        );

        if (moved || (values != m_fieldValues)) {
            m_fieldValues = values;
            m_fieldViewRevision++;
        }
    }
}
