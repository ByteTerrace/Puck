using System.Runtime.InteropServices;
using Puck.SignedDistance;

namespace Puck.SdfVm;

public sealed partial class SdfWorldTables {
    private SdfShadeProgram? m_shadeDefault;
    private SdfProgram? m_shadeDefaultProgram;
    private uint m_shadeDefaultFirst;

    // Defaults share the ring-uploaded appearance allocation, after its last word. All readers receive owned ranges.
    // Neither their code nor their parameters enter the field's words or its bake identity.
    private void PackShadeDefault(SdfFrame frame) {
        if (ReferenceEquals(m_shadeDefault, frame.ShadeDefault) && ReferenceEquals(m_shadeDefaultProgram, frame.Program)) { return; }
        var first = checked((uint)(frame.Program.Words.Length / 4));
        if (frame.ShadeDefault is { } shade) {
        var region = m_programRegion;
        EnsureProgramCapacity(frame.Program, checked(shade.Words.Length + shade.ParameterWords.Length));
        if (!ReferenceEquals(region, m_programRegion)) { WriteProgramWords(frame.Program); }
        _ = m_programRegion.Write(bytes: MemoryMarshal.AsBytes(shade.Words), offset: checked((int)first * 16));
        _ = m_programRegion.Write(bytes: MemoryMarshal.AsBytes(shade.ParameterWords), offset: checked((int)first * 16 + shade.Words.Length * 4));
        }
        m_shadeDefault = frame.ShadeDefault;
        m_shadeDefaultProgram = frame.Program;
        m_shadeDefaultFirst = first;
        m_programRevision++;
    }
}
