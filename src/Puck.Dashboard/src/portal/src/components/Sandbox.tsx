import { TokenCredential } from "@azure/identity";
import { useEffect } from "react";
import { useBlobClient } from "../clients/BlobClientProvider";

export default function Sandbox({
  tokenCredential,
}: {
  tokenCredential: TokenCredential;
}) {
  const blobClient = useBlobClient({
    blobPath: "private/message.txt",
    containerName: "38eb40ba-c8c0-4ca9-8358-37f9395c64be",
    endpoint: "https://bytrcstp001.blob.core.windows.net",
    tokenCredential: tokenCredential,
  });

  useEffect(() => {
    blobClient.getProperties().subscribe(console.log);
  }, [blobClient]);

  return <></>;
}
