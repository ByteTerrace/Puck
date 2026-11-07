# Puck.Storage.Azure

The Azure Blob storage provider for [Puck.Storage](../Puck.Storage/README.md):
`AzureBlobObjectStorageTarget` and the backend it names, the ambient-credential
access probe, and the storage-account partitioning the Azure deployment routes
users with. A World or silo never references this project; it reaches it
through `Puck.World.Azure`'s `azure.blob` storage provider
([settings and edge shape](../Puck.World.Azure/README.md#azure-blob-storage)).
The Azure deployment's own hosts (`Puck.Actors`, `Puck.Azure.Functions`) and the
CLI's Azure verbs reference it directly.

## Route an oid to its storage account

`IPartitionResolver` answers which storage account owns a user's container.
`DefaultPartitionResolver` runs `Puck.Maths`' `MonotonicPartitioner` over
`PartitioningOptions`, so the silo, the edge, and the browser's WASM build all
land on the same account without coordinating; the monotonic invariant means
raising `Count` only migrates the users who fall into a new bucket.

`PartitioningOptions.AnchorPartition` is the exception to routing: a user's
published `public/` content lives in their oid-named container on that partition
whatever their home partition is, so published URLs survive a migration.

The resolver reports where a user *should* live for the current partition count.
During a migration the recorded home and the computed partition differ and the
data is still in the home, so a caller that must reach real bytes resolves
through the owning grain rather than through this.

## Documentation

📚 [Engine overview](https://github.com/ByteTerrace/Puck/blob/main/docs/overview.md) · 🛠️ [Contributing to Puck](https://github.com/ByteTerrace/Puck/blob/main/docs/development/contributing.md)
