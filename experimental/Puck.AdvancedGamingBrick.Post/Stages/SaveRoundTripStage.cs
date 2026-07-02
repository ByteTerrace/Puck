namespace Puck.AdvancedGamingBrick.Post;

/// <summary>
/// Tier-A stage: the cartridge save-persistence API round-trips. Writes known bytes to a synthesised SRAM-tagged
/// cartridge, exports the backup (as a host would to a <c>.sav</c> file), reloads it into a fresh cartridge, and
/// confirms the bytes survive — plus the dirty-flag transitions and the wrong-size rejection. Self-contained (no machine,
/// no external ROM); proves the save layer a host relies on to persist progress across launches.
/// </summary>
internal sealed class SaveRoundTripStage : IPostStage {
    /// <inheritdoc/>
    public string Name =>
        "save-round-trip";

    /// <inheritdoc/>
    public PostTier Tier =>
        PostTier.A;

    /// <inheritdoc/>
    public PostStageOutcome Run(PostContext context) {
        var (pass, detail) = SaveRoundTripProbe.Run();

        return pass
            ? PostStageOutcome.Pass(detail: detail)
            : PostStageOutcome.Fail(detail: detail);
    }
}
