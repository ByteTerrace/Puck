# Puck.Actors

Orleans actor tier for ByteTerrace. Hosts one `UserGrain` per user (keyed by Entra `oid`) that owns the
provisioning lifecycle with reminder-driven, verified retries. Design doc: "Orleans at ByteTerrace" —
https://claude.ai/code/artifact/aed8686b-2eff-4595-9ec0-6030489200dc

## Topology

- ASP.NET minimal API co-hosted with the Orleans silo in one container app (`bytrccap001`) inside the
  existing managed environment (`bytrccaep000`).
- Clustering, reminders, and grain state live on the **private** storage account (`bytrcstp000`:
  Azure Tables for membership/reminders, blob container `orleans-state` for grain state). Never the
  user-writable `bytrcstp001` containers — grain state must not be user-forgeable.
- The Functions edge (`bytrcfuncp000`) keeps the public `POST/GET /api/self-onboard` contract and proxies
  to this tier over the internal API below.

## Internal API (consumed by the Functions edge only)

`POST /users/{oid}/ensure-provisioned`

```json
{
    "protectedAssertion": "<DataProtection-protected user JWT, produced by the edge>",
    "tokenDiscriminator": "<base64 token discriminator, same value used as the protector sub-purpose>",
    "assertionExpiresAt": "2026-08-27T21:03:00Z"
}
```

`GET /users/{oid}/provisioning-state`

Both return `200` with:

```json
{
    "status": "NotOnboarded | Onboarding | Ready | Faulted",
    "completedSteps": ["Identity", "Storage", "Keys", "Finalized"],
    "updatedAt": "2026-08-27T21:02:31Z",
    "faultReason": null
}
```

`400` when the escrow is malformed or already expired. Idempotent and safe to call on every sign-in:
`Ready` is a grain-memory fast path. A `Faulted` grain resumes from its last completed step on the next
`ensure-provisioned` call (fresh sign-in ⇒ fresh escrow).

The escrow is protected with purpose `users.tokens` and sub-purposes `[oid, tokenDiscriminator]` — it can
only be unprotected by a host in the same DataProtection application, and only for the matching grain.

**Authentication (Entra, no secrets)**: when Azure-hosted, `/users/*` requires a bearer token for the
shared application registration (`e6a7ab9f…`) carrying the app-only role `Actors.Invoke`. The role is
defined on the app registration and assigned to the Functions edge's managed identity in Bicep; the
edge acquires the token with its identity for scope `https://api.byteterrace.com/.default`. Locally
(no Azure fabric configured), the API runs open. Note: the JwtBearer scheme must be registered AFTER
`UseOrleans` — see the comment in `Program.cs`.

## Configuration

| Key | Purpose |
| --- | --- |
| `ConfigurationStore:Endpoint` | Shared App Configuration store (also supplies `PublicStorage:*`, `OnBehalfOf:*`) |
| `ConfigurationStore:Label` | Optional label for settings and the refresh sentinel; omitted selects unlabelled production settings |
| `DataProtection:BlobUri` / `KeyUri` | Shared key ring on `bytrcstp000` + Key Vault key (same as the Functions app) |
| `DataProtection:ApplicationName` | **Must match the Functions app** (currently its identity's client id) |
| `PrivateStorage:BlobEndpoint` / `TableEndpoint` | Orleans fabric storage (`bytrcstp000`) |
| `Orleans:ClusterId` / `ServiceId` / `SiloPort` / `GatewayPort` / `GrainStateContainerName` | Optional Orleans overrides |
| `Onboarding:AllUsersGroupObjectId` (default `6997d638-…`) / `AttributeSetName` / `AttributeName` | Directory provisioning targets |
| `OVERRIDE_USE_MI_FIC_ASSERTION_CLIENTID` | Silo UAI client id, used to mint OBO assertions via the FIC on app `e6a7ab9f` |
| `Authorization:JwtBearer:*` | Entra validation for the internal API (from the shared config store); mandatory when Azure-hosted |

With no `PrivateStorage:*` endpoints configured (local dev) the silo runs localhost clustering with
in-memory reminders/state and an ephemeral DataProtection key ring — `dotnet run` just works.

## Deploy

The [Azure CI workflow](../../docs/ci.md#azure-application-staging) builds from
the repository root, tests the Linux image, and deploys staging by image digest.
The Dockerfile requires the shared build files, analyzers, and sibling Maths
project; using this project directory alone as its context cannot build it.

For an operator-driven deployment from a clean, committed checkout, with
Docker running and Azure CLI signed in, run from the repository root:

```powershell
./src/Puck.Actors/build-image.ps1
```

This builds and pushes a commit-tagged image and updates `bytrccap001` to its
digest. `-NoRestart` publishes the image without updating the app;
`-ContainerApp` selects another existing app. The registry retains its ABAC
mode and disabled ARM-audience authentication throughout.

## Networking (decided 2026-08-27: public environment + VNet integration)

The managed environment (`bytrccaep000`) is recreated **public, exactly as before, plus VNet
integration** on subnet `bytrcsnetp003` (outbound egresses the subnet → NAT gateway). vsmarketplace
and its Front Door route are unaffected. Because ACA has no per-app private inbound on a public
environment (internal ingress is environment-scoped only), the silo rides the environment's public
inbound with three stacked guards: ingress IP restrictions allowing only the VNet's NAT egress
prefix (`bytrcipprep000`, currently `13.66.80.4/31` — resolved from the resource at deploy time),
an Entra app-only bearer token carrying the `Actors.Invoke` role (assigned to the edge's managed
identity only), and the DataProtection-bound escrow, which is unforgeable without the shared
DataProtection key ring. The Functions edge is already VNet-integrated
(`bytrcsnetp001`, all-traffic outbound), so its calls originate from that NAT prefix.

Setting `resources.actors.privateNetworking = true` later flips the environment's inbound to an
internal, VNet-scoped load balancer instead (and provisions the required private DNS zone for the
environment's default domain); the IP restrictions drop away automatically. Either flip is an
immutable network change: delete + redeploy `bytrccaep000` (CanNotDelete locks on it and
`bytrccap000` must be removed first).

## Open items

- ACA internal TCP port exposure for silo-to-silo gossip is unverified; scale is pinned to a single
  replica until it is (see `scaleSettings` in `main.bicep`).
- `DataProtection:ApplicationName` should become a deliberate shared name for both hosts (small
  migration of existing protected payloads; coordinate with Web.API).
- After deploy, set App Config `Onboarding:ActorsBaseUrl` to `https://bytrccap001.<env-default-domain>`
  (the `actorsEndpoint` deployment output) to flip the edge over.

