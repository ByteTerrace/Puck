# Initial Setup Checklist
- [ ] clone repository
- [ ] create branch
- [ ] create resource group
- [ ] create managed identity
- [ ] assign resource group roles
- [ ] create service connection
- [ ] create federated credential
- [ ] assign API permissions
- [ ] create deployment pipeline
- [ ] run deployment pipeline

# Official Content Publishing (`puck official`) and the Docs Workflow
`bytrcidpzzz` (`userAssignedIdentityPublishing` in main.bicep) is a template-managed resource with
a federated identity credential for
`repo:ByteTerrace@18753984/Puck@1271519029:environment:Puck`. This matches the
repository's immutable OIDC subject and the existing credential. For a new environment:
- [ ] set the repo variable `AZURE_CLIENT_ID` (in the `Puck` GitHub environment) to the template
      output `publishingIdentityClientId`.
- [ ] set the repo variables `AZURE_TENANT_ID` and `AZURE_SUBSCRIPTION_ID` for the same
      environment. Nothing else needs setting by hand — `docs.yml`'s `azure/login` step exchanges
      these for an Azure token over OIDC.
- [ ] `officialContentContainerName` (a template output) is a GUID, not a fixed name — it is the
      Front Door identity's own principal id, matching the container
      `avm-temp/ptn/platform/public-flex-api/main.bicep` already provisions for platform-owned
      content (favicon.ico's own container). The publisher must resolve it from this output,
      never hard-code it.
- [ ] `officialContentBaseUrl` is `https://puck.byteterrace.com/official` — `puck.byteterrace.com`
      is carried on the `api`, `blob-private`, `blob-public`, and `portal` routes' `customDomains`
      alongside the apex, `portal.`, `www.`, and `blob.`. Its Front Door custom domain, Azure DNS
      zone (with automatic NS delegation from the `byteterrace.com` zone this same template
      creates), and TLS certificate are provisioned the same way as every other custom domain here
      — nothing to create by hand.
- [ ] cache behavior for `/official/*`: the `blob-public` route's `cacheConfiguration` (in
      `main.bicepparam`) sets `queryStringCachingBehavior` but never `cacheBehavior`, so it
      defaults to `HonorOrigin` — Front Door honors each blob's own `Cache-Control` header rather
      than overriding it. Confirm this once a fetch runs through the live edge; it was not
      exercised against a real deployment here.
