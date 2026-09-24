# Extensions

An extension adds a capability to a Puck host without the host naming it in
code: a machine engine, a cloud storage provider, a connection authenticator, a
remote control plane. The same extension works in every host. A local
`Puck.World` on a desktop or handheld with no network, the headless
`Puck.World.Silo`, and the silo that `puck mcp --silo` runs all discover,
compose, order, start, stop and refuse extensions the same way. Remote MCP and
the Azure services are optional extensions like any other; a host that installs
none of them has no dependency on them.

## One contract, keyed contributions

Every extension implements `IPuckExtension` from `Puck.Abstractions`. It has a
`Name` and one method, `Register`, which adds *contributions* to a registry:

```csharp
public sealed class HumbleGamingBrickExtension : IPuckExtension {
    public string Name => "Puck.HumbleGamingBrick.Forge";

    public void Register(IPuckExtensionRegistry registry) {
        registry.AddMachineEngine(engine: new GamingBrickEngine(), contentProvider: new HgbCartridgeCompiler());
        registry.AddMachineEngine(engine: new TuneInstrumentEngine());
    }
}
```

A contribution is any object, added under its *kind* (the exact type the host
reads it back as) and a *key* unique within that kind. `AddMachineEngine` adds
an `IMachineEngine` keyed by engine id and an `IMachineContentProvider` keyed by
the same id. Registration performs no I/O and starts nothing; a contribution's
own factory runs later, when a host selects it.

The kinds in use today, and who reads them:

| Kind | Key | Registered with | Read by |
|---|---|---|---|
| `IMachineEngine`, `IMachineContentProvider` | engine id | `AddMachineEngine` | every host's `WorldMachineCatalog.From` |
| `WorldSiloStorageProvider` | store `type` | `AddStorage` | the silo document's `store` |
| `WorldSiloRetirementProvider` | observer `type` | `AddRetirement` | the silo document's `lifecycle.observer` |
| `WorldAuthenticationProvider` | `type` | `AddAuthentication` | silo row admission; World's `--authentication-config-file` |
| `WorldExtensionProviderType`, `WorldExtensionEmbeddingProviderType` | provider `type` | `AddOperation`, `AddEmbedding` | the extension configuration's `providers` |
| `WorldParticipantType` | participant `type` | `AddParticipant` | the extension configuration's `participants` |
| `ChatClientProvider` | provider name | `AddChatClient` | an `agent.harness` participant's `provider` setting |
| `WorldHealthCheck` | request path | `AddHealthCheck` | the silo's health listener |
| `PuckHostedService` | any | `registry.Add` | every host: started with the host, stopped and disposed with it |
| `HostedControl` | always `control` | `AddHostedControl` | a host with an `IControlSessionHost`, when a control configuration is named |
| `McpServicesProvider` | provider name | `registry.Add` | remote MCP, when its configuration names `services` |

The *extension configuration* is the host-approved `puck.world.extensions.v1`
document described in
[Compose service extensions with data](../../src/Puck.World.Server/ExtensionConfiguration.md).
A local World reads it through `--extensions-config-file`; each silo row names
its own with the row's `extensions` member. Both attach it to the world through
`WorldConfiguredExtensions.Attach`, so one configuration selects the same
providers and participants in either host.

A new kind needs no host change: the package that owns the concept defines the
type, extensions add it, and whatever reads it asks the composed set for it.
That is how `Puck.Mcp.Azure` reaches `Puck.Mcp`, and how
`Puck.World.AgentHarness.Azure` reaches `Puck.World.AgentHarness`, without a
host or the package it extends referencing Azure.

## Composition refuses conflicts by name

`PuckExtensionSet.Compose` turns a host's extensions into one immutable set. It
sorts extensions by name and registers them in that order, and it keeps each
kind's contributions in key order, so the same extensions give the same set
however a host supplies or discovers them. Nothing is decided by load order.
Instead, every conflict is refused, naming what collided:

- two extensions with one name, or a blank name;
- two extensions registering the same key for the same kind, or one extension
  registering a key twice (a second `HostedControl` is this case: every hosted
  control shares the key `control`, so a host can never start two);
- a blank key, a null contribution, or a registry used after `Register`
  returned;
- a `Register` that throws, reported as that extension failing, with the
  failure as the inner exception.

The set owns the extensions it received, whatever the outcome. Disposing it
disposes each extension that implements `IDisposable` or `IAsyncDisposable`
once, in reverse name order; a refused composition does the same before the
refusal propagates.

Selection refuses by name too, and every host selects through one method,
`PuckExtensionSet.Select`. A configuration that names a key gets that
contribution or a refusal listing the installed keys: the silo document's
`store.type`, a provider row's `type`, a participant's `type`. A configuration
that names none gets the kind's one installed contribution, and is refused when
none or several are installed, naming each: a remote MCP configuration with
`services`, or an agent participant that names no `provider`.

## Discovery

`PuckExtensionDiscovery.Compose` (in `Puck.Hosting`) composes a host's built-in
extensions with the ones installed in its extensions directories. By default a
host searches `extensions` under the working directory, then `extensions` beside
its own assemblies; the silo's `--extensions-dir` names one directory instead.

Each installed extension is a subdirectory named after its assembly, holding
that assembly beside its dependency manifest, which is what `dotnet publish -o
extensions/<name>` produces:

```text
extensions/
  Puck.Mcp/Puck.Mcp.dll            (+ Puck.Mcp.deps.json and private dependencies)
  Puck.Mcp.Azure/Puck.Mcp.Azure.dll
  Puck.World.Azure/Puck.World.Azure.dll
```

The assembly names its entry types with `[assembly: PuckExtension(typeof(...))]`;
discovery reads only that attribute and never scans for types. An installation
it cannot use is refused by path rather than skipped: an assembly sitting
directly in an extensions directory, a subdirectory without its `<name>.dll`,
the same name installed twice, an assembly that cannot load or declares no
entry type, and an entry type that is not a constructible `IPuckExtension`.
Installing a copy of a host's built-in extension is a duplicate name. Built-ins
and every extension activated before a refusal are disposed.

Each installed extension loads into its own non-collectible
`PuckExtensionLoadContext`, which resolves an assembly in this order:

1. another installed extension's own assembly, from that extension's context,
   so an extension built on another (`Puck.Mcp.Azure` on `Puck.Mcp`) shares its
   types;
2. an assembly the host carries at a compatible version, so every contract the
   host and its extensions exchange has one identity;
3. a dependency an upstream installed extension resolves;
4. the extension's own published dependency graph.

Contexts live for the process. Discovery happens once, while a host composes,
and never on a simulation or frame path.

## Hosts

| Host | Built-ins | Installs the set with |
|---|---|---|
| `Puck.World` | both Gaming Brick forges, `WorldServerExtension` | `AddPuckExtensions` |
| `Puck.World.Silo` and `puck mcp --silo` | `WorldServerExtension` | `AddPuckExtensions`, with `--mcp` naming the control configuration |
| CLI tooling verbs | both Gaming Brick forges | the machine catalog only |

`WorldServerExtension` contributes the `directory` storage provider.
`AddPuckExtensions` (in `Puck.Launcher`) registers the set, every
`PuckHostedService` contribution, the hosted control when a control
configuration is named (refused when none is installed), and the
`world.extensions.catalog` verb. A contributed service that already implements
.NET's `IHostedService` keeps its background-task supervision; any other is
adapted so the host starts, stops and disposes it. Services stop and dispose
before the set that owns their extensions.

`world.extensions.catalog` prints each composed extension and its contributions
by kind and key, for example
`Puck.World.Server: WorldSiloStorageProvider directory`. It is how a running
World or silo shows what it composed.

## Writing an extension

1. Reference the packages that define the kinds you contribute:
   `Puck.Abstractions` for the contract and machine kinds, `Puck.World.Server`
   for the world-hosting providers, `Puck.World.Protocol` for
   `WorldParticipantType`, `Puck.Hosting` for `HostedControl`, `Puck.Mcp` for
   `McpServicesProvider`, `Puck.World.AgentHarness` for `ChatClientProvider`.
2. Implement `IPuckExtension` with a public parameterless constructor, name it
   after its assembly, and declare it with `[assembly: PuckExtension(...)]`.
3. Set `<EnableDynamicLoading>true</EnableDynamicLoading>` and publish into
   `extensions/<AssemblyName>`.
4. Run `world.extensions.catalog` in the host to check the composed result.

A host may also compose an extension it references directly by passing it as a
built-in; the contract and the refusals are the same.
