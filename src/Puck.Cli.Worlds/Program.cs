using Puck.Cli;

// A verb that compiles a tree in a process of its own runs this assembly alone; `puck` composes the same verbs beside
// every other.
WorldsRoot.Start();
return await CliRoot.InvokeAsync(
    args: args,
    root: CliRoot.Compose(description: "The Puck world-authoring verbs.", verbs: WorldsRoot.Verbs())
);
