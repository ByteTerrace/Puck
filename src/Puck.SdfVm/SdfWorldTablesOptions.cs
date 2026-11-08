using Puck.Abstractions.Gpu;
using Puck.SignedDistance;

namespace Puck.SdfVm;

/// <summary>Construction options for <see cref="SdfWorldTables"/>.</summary>
/// <param name="Program">The scene program, uploaded at construction (the "program uploaded once" seam the
/// dynamic-transform channel rides). A frame source whose scene later changes hands another one, which
/// <see cref="SdfWorldTables.UploadProgram"/> uploads, growing the program and instance tables when necessary.</param>
/// <param name="DynamicTransformCapacity">The number of dynamic entity-transform slots to allocate (at least one slot
/// is always bound so the binding stays valid for a static scene). The tables raise this floor to the program's
/// <see cref="SdfProgram.RequiredDynamicTransformCapacity"/>. Each slot costs 48 bytes of the dynamic-transform
/// region, and a frame uploads only the words that changed. A later program or frame requiring more slots grows
/// the region and its motion-history buffer.</param>
/// <param name="ProgramWordCapacity">The packed words the tables are provisioned for, which
/// <see cref="SdfWorldTables.ProgramWordCapacity"/> reports while no program exceeds it. It allocates nothing: the
/// program region holds the live program and <see cref="SdfWorldTables.UploadProgram"/> grows it by half again when a
/// larger one arrives.</param>
/// <param name="InstanceCapacity">An optional initial instance reserve for the bake's instance masks a view's scratch is
/// counted by. The tables provision at least the initial program's instance count and grow with later
/// uploads.</param>
/// <param name="WorkLedger">The ledger the tables count their uploads into, owned by the caller so that submission
/// identities keep increasing when the caller rebuilds the tables (after a device loss, say); created with
/// <see cref="SdfWorldTables.FrameRingSize"/> frames in flight. The caller invalidates it when it drops the tables.
/// When <see langword="null"/>, the tables create their own.</param>
/// <param name="SkyDetails">The detail rows the sky's layers count in, shared by every residency of a composition
/// (<see cref="SdfWorldPipelineCatalog.SkyDetails"/>) so a view's rows never move when it follows another residency.
/// When <see langword="null"/>, the tables create their own.</param>
/// <param name="ShadeDefault">The default shade program the first frame packs after <paramref name="Program"/>'s words
/// (<see cref="SdfFrame.ShadeDefault"/>), which the program region is sized to hold from construction; <see langword="null"/>
/// when the first frame has none.</param>
public sealed record SdfWorldTablesOptions(
    SdfProgram Program,
    int DynamicTransformCapacity = 1,
    int ProgramWordCapacity = 0,
    int InstanceCapacity = 0,
    GpuWorkLedger? WorkLedger = null,
    SdfSkyDetails? SkyDetails = null,
    SdfShadeProgram? ShadeDefault = null
);
