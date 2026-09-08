import {
  BlobClient,
  BlobDownloadOptions,
  BlobGetPropertiesOptions,
  BlobServiceClient,
  BlobSetTierOptions,
} from "@azure/storage-blob";
import {
  ApiClient,
  RequestController,
  RequestDefinition,
} from "../../../shared/clientApi";

interface BlobRequestDefinition<TResult, TArgs = void> {
  blobClient?: BlobClient;
  onDispose?: (result: TResult) => void;
  sendRequest: (client: BlobClient, args: TArgs) => Promise<TResult>;
}

type BlobRequestMap = Record<string, BlobRequestDefinition<any, any>>;
type TypedBlobClient<T extends BlobRequestMap> = {
  [K in keyof T]: T[K] extends BlobRequestDefinition<infer TResult, infer TArgs>
    ? (args?: TArgs) => RequestController<TArgs, TResult>
    : never;
};

class BlobClientApi {
  private static buildRequest<TArgs, TResult>(
    definition: BlobRequestDefinition<TResult, TArgs>,
  ): RequestDefinition<any, TResult> {
    const blobClient = definition.blobClient!;
    const { url } = blobClient;

    return {
      enableAutoRefresh: false,
      keySelector: () => url,
      requestFunction: async (args) => {
        const response = await definition.sendRequest(blobClient, args);

        return {
          response,
          onDispose: definition.onDispose
            ? () => definition.onDispose!(response)
            : undefined,
        };
      },
      staleRequestTimeout: undefined,
    };
  }

  static create<T extends BlobRequestMap>(
    blobClient: BlobClient,
    requestMap: T,
  ): TypedBlobClient<T> {
    const apiClient = new ApiClient();
    const client = {} as TypedBlobClient<T>;

    for (const [name, definition] of Object.entries(requestMap)) {
      const requestDefinition = BlobClientApi.buildRequest({
        blobClient,
        ...definition,
      });

      (client as any)[name] = (args: any) =>
        apiClient.getOrAdd(requestDefinition, args);
    }

    return client;
  }
}

export const createBlobClient = (
  blobPath: string,
  containerName: string,
  serviceClient: BlobServiceClient,
) => {
  const containerClient = serviceClient.getContainerClient(containerName);
  const blobClient = containerClient.getBlobClient(blobPath);

  return BlobClientApi.create(blobClient, {
    download: {
      sendRequest: (
        blobClient,
        options?: {
          count?: number;
          offset?: number;
          options?: BlobDownloadOptions;
        },
      ) => {
        const { count, offset, options: downloadOptions } = options || {};

        return blobClient.download(offset, count, downloadOptions);
      },
    },
    getProperties: {
      sendRequest: (blobClient, options?: BlobGetPropertiesOptions) =>
        blobClient.getProperties(options),
    },
    setAccessTier: {
      sendRequest: (
        blobClient,
        options: {
          options?: BlobSetTierOptions;
          tier: "Archive" | "Cool" | "Hot";
        },
      ) => blobClient.setAccessTier(options.tier, options.options),
    },
  });
};
