using Puck.Cli;

// The game's build runs this assembly alone to compile the shipped worlds (build/WorldAssets.targets); `puck` composes
// the same verbs beside every other.
WorldsRoot.Start();
return await CliRoot.InvokeAsync(
    args: args,
    root: CliRoot.Compose(description: "The Puck world-authoring verbs.", verbs: WorldsRoot.Verbs())
);
