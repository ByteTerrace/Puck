# Product content

`Puck.World` is an engine, and the worlds it runs belong to products. A product
is a content root with a manifest. The manifest declares the product's identity,
branding, worlds and assets, release channels, and the engines it targets.
Products are often authored with outside tooling, so the manifest is the
contract, not a folder convention. The official Puck world is the first product,
and the manual draws its examples from it.

This plan separates content from the engine, introduces product roots under
`content/<id>/`, and turns the existing official build into a build that can be
pointed at any product root.

## Implementation status

Reviewed against `ef148e667`. Nothing has landed.

## Owner decisions

- **The engine ships no content.** `Puck.World` and its sibling projects ship
  engine data only: shaders, fonts, overlays, probe kinds, and document schemas,
  each owned by the project that uses it. Worlds, cartridges, music, addons, and
  art belong to a product.
- **The unit is a product, rooted at `content/<id>/`.** The official world is
  `content/puck/`. "Client" is not used, because `Puck.World.Client`, extension
  client settings, and OAuth client ids already claim it. "Distribution",
  "channel", "title", and "identity" are taken for the same reason.
- **The manifest is the contract.** The build reads `product.json` to learn what a
  product contains. It never infers content from folder names such as `games`,
  `modules`, or `shards`.
- **No product, no boot.** Launched without a product or world, `Puck.World`
  refuses and names what it needs. There is no hidden default world.
- **The official product is the example set.** Documentation cites
  `content/puck` by document name. No second product exists yet, so nothing
  outside review proves the build is product-neutral; a Puck-specific name in a
  build verb is a defect.
- **Existing pipelines are extended, not joined by a new one.**

## What exists today

The engine and the Puck game are joined at these points:

| Coupling | Where |
|---|---|
| Default boot of the Nexus island | `WorldDefinitionLoader.DefaultRelativePath` (`Assets/worlds/puck.world.json`) |
| Bare world names fall back to the shipped content tree | `WorldDocumentOrigin.TryResolveCanonicalPath` |
| Music, tune, patch, and addon paths resolve against the executable directory | `WorldAssetRowLoader`, `WorldAddonRuntime` |
| Everything under `Assets` ships with the engine | `Puck.World.csproj` `Content Include="Assets\**"` |
| The silo image copies the raw content tree | `src/Puck.World.Silo/Dockerfile` |
| The official build hard-codes the root, basis, and fragment folders | `OfficialWorldDocumentScanner` (`puck.world.json`, `standard.basis.json`, `games`, `modules`) |
| World preparation hard-codes the root world | `WorldPrepareCommand` |
| The self-update application id is fixed | `Program.cs` (`App: "puck.world"`) |
| The channel and publication URL are fixed | `BundleCommand`, `AzureWebsite`, `AzureCommand` (`stable`, `puck.byteterrace.com/official`) |

`src/Puck.World/Assets` mixes four kinds of file:

- **Engine data.** These are the generated document schemas, which only CLI
  tooling reads, and the default recording configuration.
- **Official content.** This covers the root world, basis, games, modules,
  shards, avatars, cartridges, the dormant Nexus music, `granaries` hosting
  configuration, and the moth render study.
- **Test fixtures in the shipped tree.** These are the canary music, tunes, and
  patch, the `quilt-nw-gap` shard, and the shader-pipeline demo world with its
  `ink` pipeline.
- **Examples and orphans.** The examples are the default addon binary, the Azure
  extensions example, and the probe track. The orphans are the hud-builder addon
  binary, `sdf/example.sdf.json`, and the transition stinger patch.

Thirty-four comments and generated schema descriptions say an engine
`standard.world.json` supplies default movement, HUD, host, and view rows. That
file no longer exists; those rows live in `puck.world.json` or nowhere.

Eight pipelines package, bundle, or release something:

1. `puck official`: the browser engine plus world documents and some asset rows.
2. `puck publish`: desktop binaries for self-update, content excluded by design.
3. `puck world prepare` and `puck world release`: hosted-world packages.
4. The silo Dockerfile.
5. `puck azure build` with `puck bundle`: the deployment bundle.
6. `dotnet publish src/Puck.World`: the desktop artifact, engine and content with no manifest.
7. `puck nuget`: libraries and the CLI.
8. `puck artifacts`: a CI cache of build outputs.

Pipelines 1, 3, 4, and 6 carry content. They select it in three different ways
and describe it with two manifest schemas.

## Design

### Layout

```
content/
  puck/
    product.json      puck.product.v1
    branding/         window icon, portal and site assets for this product
    worlds/           root world, basis, fragments, shards
    cartridges/       .puck sources and compiled documents
    music/  tunes/  patches/  addons/  pipelines/  hosting/
branding/             the Puck project's own brand: docs, NuGet, editor
roms/                 the engine's verification ROMs (see ROM ledger)
src/Puck.World/       no Assets directory
tests/                fixtures evicted from the shipped tree
```

A product's internal folders are its own choice. The layout above is the
official product's, not a requirement.

### The product manifest

`product.json` (`puck.product.v1`) declares:

- `id`, which becomes the self-update application id, and a display `name`;
- the product's `branding` assets;
- its root worlds;
- every document and asset it ships, each with a family and a path relative to
  the manifest;
- the release `channels` it publishes to, and the `engines` it targets (browser,
  desktop, silo);
- the world runtime extensions and extension configuration its hosted worlds
  require.

The asset families extend the ones the official manifest already defines (music,
table, tune, patch, plus the reserved audio, synth, and font) with `cartridge`,
`addon`, and `pipeline`. The build refuses a file under the product root that the
manifest does not name, and a manifest entry whose file is missing or whose
canonical hash disagrees.

### Resolution

A product root is the unit of resolution. `Puck.World --product <root>` reads the
manifest and resolves every world and asset path against that root, whether the
root is a checkout folder or a built product tree. Nothing resolves content
against the executable's directory or the repository layout. That replaces the
executable-relative convention in `WorldAssetRowLoader` and `WorldAddonRuntime`,
the shipped-worlds fallback, and the default boot.

`--world <path>` stays for a single document outside any product, and resolves
relative to that document.

### Build

`puck official build` becomes `puck product build <root>`, keeping its
content-addressed output, verification, and serve verbs. The manifest supplies
the documents, assets, identity, and allowed channels that are hard-coded today.
Output is scoped per product (`<out>/<id>/<channel>/manifest.json`) over a shared
`objects/sha256` store, which also stops product manifests from colliding with
`puck publish` manifests at the same channel path. The manifest schema is renamed
to match.

`puck world prepare`, `puck world release prepare`, `puck azure build`, and the
silo image take a product root instead of the content tree. Desktop and silo
engine builds ship no content; a product arrives beside them.

## Slices

Each slice deletes what it replaces in the same squash and lists the conditions
that complete it.

### 1. Evict what is not content

Move the test fixtures to the tests that use them, delete the orphans, and move
the schemas and default recording to the projects that own them. Delete the
stale `standard.world.json` claims from source comments and regenerate the
schemas that repeat them. Decide each example's home (`docs/examples` or the
official product) as it moves.

Done when every file left under `src/Puck.World/Assets` is official content, no
test reads a fixture from that tree, and `puck search -M 0 standard.world.json`
finds nothing outside Git history.

### 2. Product root and resolution

Create `content/puck` and its manifest, and move the official content there. Add
`--product`, move all content resolution onto the product root, remove the
default boot and the shipped-worlds fallback, and remove the `Assets\**` content
item. Update the run recipes that assume a default world, including the
verification command in `CLAUDE.md`, the World README, the puck-world skill, and
the canaries' `Assets/worlds/...` paths.

Done when
`dotnet run --project src/Puck.World -c Release -- --product content/puck --exit-after-seconds 2`
boots the official world, the same command without `--product` refuses with a
message naming it, `puck landing` passes, and `src/Puck.World` has no `Assets`
directory.

### 3. Product build

Rename `puck official` to `puck product`, drive it from `product.json`, add the
cartridge, addon, and pipeline families, scope output per product, and take the
application id, channels, and publication base from the manifest. Point
`puck azure build` and the dashboard's official client at the result.

Done when `puck product build content/puck` followed by `puck product verify`
passes, the dashboard boots the official world from the built tree, and
`puck azure build` produces a deployable bundle with no hard-coded product name
or channel.

### 4. Hosted and desktop engines

Point `puck world prepare`, `puck world release prepare`, and the silo image at a
product root, and have the release manifest pin the product's content and required
extensions. Make the desktop artifact engine-only.

Done when the silo image contains no copy of a content tree, a world release
package pins the product manifest's content, and the desktop artifact boots
`content/puck` given `--product`.

### 5. Documentation

Point the manual, project map, package READMEs, and skills at `content/puck` by
document name. Document `puck.product.v1` where schemas are documented, and write
the procedure for authoring a product outside this repository.

Done when no documentation or skill cites `src/Puck.World/Assets`, and the
product procedure has been followed once from an empty folder to a booting world.

## Related plans

- [ROM ledger](rom-ledger.md): cartridges are product content; the ROM ledger
  covers only the engine's verification ROMs.
- [World release management](world-release-management.md): slice 4 supplies the
  "required assets and extensions" a release is meant to name.
- [Compiled worlds](compiled-worlds.md): its container would carry a product's
  derived data; which of the two is the packaged unit is unresolved.
- [README consistency and branding](readme-and-branding.md): `branding/` stays
  the Puck project's brand.

## Open questions

- **Window title and icon.** They are authored per world document today. Decide
  whether product branding supplies them and a world may override.
- **The official product's branding.** Decide whether `content/puck/branding`
  holds its own assets or synchronized copies from `branding/` through the
  branding manifest.
- **Where document schemas ship.** Only CLI tooling reads them, and the product
  build already embeds a schema bundle. Decide between the CLI package and a
  standalone schema artifact.
- **Signing and upload.** Neither exists for the official tree today; both stay
  outside this plan.
