namespace Puck.World.Server;

/// <summary>What a named machine's lease held when it was captured.</summary>
public enum WorldMachineCheckpointKind : byte {
    /// <summary>The provider's runtime, whose image the row carries.</summary>
    Running = 0,
    /// <summary>A preparation refusal, which carries no runtime image and never advanced.</summary>
    Refused = 1,
}
/// <summary>One named machine's lease kind, opaque runtime image and world-owned generation bookkeeping.</summary>
/// <param name="Name">The declared machine name.</param>
/// <param name="Engine">The provider identity.</param>
/// <param name="Kind">Whether the machine ran or was refused; a restore refuses a row whose kind differs from the live
/// lease's.</param>
/// <param name="Generation">The instance generation used to reject stale operations.</param>
/// <param name="CompletedSteps">The host's synchronous step count; zero for a refused machine.</param>
/// <param name="RuntimeState">The complete provider-owned checkpoint encoding; empty for a refused machine.</param>
public sealed record WorldMachineCheckpoint(string Name, string Engine, WorldMachineCheckpointKind Kind, ulong Generation, long CompletedSteps, byte[] RuntimeState) {
    /// <summary>Gets whether the row's image and step count fit its kind: a running row carries an image and a
    /// non-negative count, a refused row neither.</summary>
    public bool IsWellFormed => (Kind switch {
        WorldMachineCheckpointKind.Running => ((CompletedSteps >= 0) && (RuntimeState is { Length: > 0 })),
        WorldMachineCheckpointKind.Refused => ((CompletedSteps == 0) && (RuntimeState is { Length: 0 })),
        _ => false,
    });
}
/// <summary>One declared screen's slot state the host owns beside its declaration: the magazine selector a screen
/// operation moves and the cable link the screen belongs to.</summary>
/// <param name="Index">The declared screen index.</param>
/// <param name="SelectedEntry">The 0-based magazine selector; zero for a screen with no magazine.</param>
/// <param name="LinkName">The cable link the screen belongs to, one of the checkpoint's links, or
/// <see langword="null"/> for none.</param>
public sealed record WorldMachineSlotCheckpoint(int Index, int SelectedEntry, string? LinkName);
/// <summary>One cable link row the host holds: its name, member screens in cable order, whether the document declares
/// it, and why it is dormant. A live coupled link refuses capture, so every captured link is dormant.</summary>
/// <param name="Name">The link's stable name.</param>
/// <param name="Members">The member screen indices in cable order.</param>
/// <param name="Declared">Whether the document's machine sources declare the link.</param>
/// <param name="DormantReason">Why the link established no coupled transport, or <see langword="null"/> when its
/// members had no machine to couple.</param>
public sealed record WorldMachineLinkCheckpoint(string Name, IReadOnlyList<int> Members, bool Declared, string? DormantReason);
/// <summary>Durable state owned by a world's machine host, captured at an authoritative world boundary.</summary>
/// <param name="Revision">The machine inventory revision.</param>
/// <param name="NextGeneration">The next generation available to a replacement machine.</param>
/// <param name="AnyEverPumped">Whether this host has advanced any machine.</param>
/// <param name="Instances">The complete named runtime inventory.</param>
/// <param name="Slots">Every declared screen's slot state, in index order.</param>
/// <param name="Links">Every cable link row, in name order.</param>
public sealed record WorldMachineHostCheckpoint(ulong Revision, ulong NextGeneration, bool AnyEverPumped,
    IReadOnlyList<WorldMachineCheckpoint> Instances, IReadOnlyList<WorldMachineSlotCheckpoint> Slots,
    IReadOnlyList<WorldMachineLinkCheckpoint> Links) {
    /// <summary>An empty machine inventory for compositions without a machine host.</summary>
    public static WorldMachineHostCheckpoint Empty { get; } = new(
        AnyEverPumped: false,
        Instances: [],
        Links: [],
        NextGeneration: 1,
        Revision: 0,
        Slots: []
    );
}
/// <summary>Optional complete-state persistence for a world machine host. Unsupported live state refuses capture;
/// a failed restore requires disposal of the private, newly created host.</summary>
public interface IWorldMachineCheckpointHost {
    /// <summary>Captures each runtime after all its accepted steps have completed, with every screen slot's selector
    /// and link membership and every link row.</summary>
    /// <returns>The complete machine inventory, its runtime images, slots and links.</returns>
    /// <exception cref="InvalidOperationException">A machine has no durable checkpoint capability, or a link couples
    /// its members live.</exception>
    WorldMachineHostCheckpoint CaptureCheckpoint();
    /// <summary>Restores exactly the declared machine inventory, slots and links: before its first simulation step,
    /// for a restored activation, or over running machines, for a history seek or a replay drive rewinding them. Each
    /// runtime restores behind the steps it already accepted, so a rewind never races one still in flight; every link
    /// the host holds is torn down and the checkpoint's rows take their place. The restore applies completely or
    /// changes nothing: every row, slot and link, and every runtime image, is checked before anything is applied.</summary>
    /// <param name="checkpoint">The captured host and runtime state.</param>
    /// <exception cref="InvalidDataException">The checkpoint's inventory, kinds, runtime images, slots or links do not
    /// fit this host's declared machines and screens; nothing was applied.</exception>
    void RestoreCheckpoint(WorldMachineHostCheckpoint checkpoint);
}
