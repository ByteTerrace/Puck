import { createBlobClient } from "./blobClientApi";
import { TokenCredential } from "@azure/identity";
import { BlobServiceClient } from "@azure/storage-blob";

interface UseBlobClientOptions {
  blobPath: string;
  containerName: string;
  endpoint: string;
  tokenCredential: TokenCredential;
}

export function useBlobClient(options: UseBlobClientOptions) {
  const { blobPath, containerName, endpoint, tokenCredential } = options;

  return createBlobClient(
    blobPath,
    containerName,
    new BlobServiceClient(endpoint, tokenCredential)
  );
}
