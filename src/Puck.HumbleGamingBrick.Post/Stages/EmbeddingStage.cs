namespace Puck.HumbleGamingBrick.Post;

/// <summary>Checks default factory composition and the public core used by an external synchronous host.</summary>
internal sealed class EmbeddingStage : IPostStage<PostContext> {
    /// <inheritdoc/>
    public string Name => "embedding";
    /// <inheritdoc/>
    public PostTier Tier => PostTier.A;
    /// <inheritdoc/>
    public bool IsConcurrent => true;

    /// <inheritdoc/>
    public PostStageOutcome Run(PostContext context) {
        var configuration = new MachineConfiguration(model: ConsoleModel.DmgC, cartridgeRom: SyntheticRom.Create());
        using var instance = MachineFactory.Create(configuration: configuration);
        using var core = new HumbleGamingBrickCore(configuration: configuration);
        if (!instance.Machine.Snapshot().Data.SequenceEqual(other: core.Instance.Machine.Snapshot().Data)) {
            return PostStageOutcome.Fail(detail: "default factory and public core booted differently");
        }
        using var fork = instance.Fork();
        if (fork.Machine.Snapshot().Identity != instance.Machine.Snapshot().Identity) {
            return PostStageOutcome.Fail(detail: "default factory fork lost configuration");
        }
        return CoreEmbeddingProbe.Verify(core: core, cycleBudget: 70_224, framebufferLength: 160 * 144);
    }
}
