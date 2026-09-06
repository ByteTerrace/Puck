import { TokenCredential } from "@azure/identity";
import { Client, GraphRequest } from "@microsoft/microsoft-graph-client";
import { TokenCredentialAuthenticationProvider } from "@microsoft/microsoft-graph-client/authProviders/azureTokenCredentials";
import { type User } from "@microsoft/microsoft-graph-types";
import {
  ApiClient,
  RequestController,
  RequestDefinition,
} from "../../../shared/clientApi";

interface GraphRequestDefinition<TResult> {
  enableAutoRefresh?: boolean;
  endpoint: string;
  onDispose?: (result: TResult) => void;
  scopes: string[];
  sendRequest: (request: GraphRequest) => Promise<TResult>;
  staleRequestTimeout?: number;
  tokenCredential?: TokenCredential;
}

type GraphRequestMap = Record<string, GraphRequestDefinition<any>>;
type TypedGraphClient<T extends GraphRequestMap> = {
  [K in keyof T]: T[K] extends GraphRequestDefinition<infer TResult>
    ? () => RequestController<void, TResult>
    : never;
};

class GraphClientApi {
  private static buildRequest<TResult>(
    definition: GraphRequestDefinition<TResult>
  ): RequestDefinition<void, TResult> {
    return {
      enableAutoRefresh: definition.enableAutoRefresh,
      keySelector: () =>
        `${definition.endpoint}|${definition.scopes.join(",")}`,
      requestFunction: async () => {
        const client = Client.initWithMiddleware({
          authProvider: new TokenCredentialAuthenticationProvider(
            definition.tokenCredential!,
            { scopes: definition.scopes }
          ),
        });
        const response = await definition.sendRequest(
          client.api(definition.endpoint)
        );

        return {
          response,
          onDispose: definition.onDispose
            ? () => definition.onDispose!(response)
            : undefined,
        };
      },
      staleRequestTimeout: definition.staleRequestTimeout,
    };
  }

  static create<T extends GraphRequestMap>(
    requestMap: T,
    tokenCredential: TokenCredential
  ): TypedGraphClient<T> {
    const apiClient = new ApiClient();
    const client = {} as TypedGraphClient<T>;

    for (const [name, definition] of Object.entries(requestMap)) {
      const requestDefinition = GraphClientApi.buildRequest({
        tokenCredential,
        ...definition,
      });

      (client as any)[name] = () =>
        apiClient.getOrAdd(requestDefinition, undefined!);
    }

    return client;
  }
}

export const createGraphClient = (tokenCredential: TokenCredential) =>
  GraphClientApi.create(
    {
      getMe: {
        enableAutoRefresh: true,
        endpoint: "/me",
        scopes: ["User.Read"],
        sendRequest: (request) =>
          request.get() as Promise<User>,
        staleRequestTimeout: 1000 * 60 * 15,
      },
      getMyPhoto: {
        enableAutoRefresh: true,
        endpoint: "/me/photo/$value",
        onDispose: URL.revokeObjectURL,
        scopes: ["User.Read"],
        sendRequest: (request) =>
          request.get().then(URL.createObjectURL) as Promise<string>,
        staleRequestTimeout: 1000 * 60 * 15,
      },
    },
    tokenCredential
  );
