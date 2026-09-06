import { createInstance, registerRemotes } from "@module-federation/runtime";
import React from "react";
import ReactDom from "react-dom";

declare const __LOCKED_VERSIONS__: Record<string, string>;

interface Manifest {
  id: string;
  metaData: ManifestMetaData;
  name: string;
}
interface ManifestMetaData {
  buildInfo: ManifestMetaDataBuildInfo;
  name: string;
  remoteEntry: ManifestMetaDataRemoteEntry;
}
interface ManifestMetaDataBuildInfo {
  buildVersion: string;
  buildName: string;
}
interface ManifestMetaDataRemoteEntry {
  name: string;
  path: string;
  type: string;
}

export interface RemoteConfig {
  baseUrl: string;
  name: string;
  requestId?: string;
}

export async function initializeModuleFederation(
  remotes: RemoteConfig[],
): Promise<void> {
  createInstance({
    name: "host",
    remotes: [],
    shared: {
      "@azure/identity": {
        scope: "default",
        shareConfig: {
          eager: true,
          requiredVersion: __LOCKED_VERSIONS__["@azure/identity"],
          singleton: true,
          strictVersion: true,
        },
        version: __LOCKED_VERSIONS__["@azure/identity"],
      },
      "@azure/msal-browser": {
        scope: "default",
        shareConfig: {
          eager: true,
          requiredVersion: __LOCKED_VERSIONS__["@azure/msal-browser"],
          singleton: true,
          strictVersion: true,
        },
        version: __LOCKED_VERSIONS__["@azure/msal-browser"],
      },
      "@azure/msal-react": {
        scope: "default",
        shareConfig: {
          eager: true,
          requiredVersion: __LOCKED_VERSIONS__["@azure/msal-react"],
          singleton: true,
          strictVersion: true,
        },
        version: __LOCKED_VERSIONS__["@azure/msal-react"],
      },
      "@reduxjs/toolkit": {
        scope: "default",
        shareConfig: {
          eager: true,
          requiredVersion: __LOCKED_VERSIONS__["@reduxjs/toolkit"],
          singleton: true,
          strictVersion: true,
        },
        version: __LOCKED_VERSIONS__["@reduxjs/toolkit"],
      },
      "@uidotdev/usehooks": {
        scope: "default",
        shareConfig: {
          eager: false,
          requiredVersion: __LOCKED_VERSIONS__["@uidotdev/usehooks"],
          singleton: true,
          strictVersion: true,
        },
        version: __LOCKED_VERSIONS__["@uidotdev/usehooks"],
      },
      react: {
        lib: () => React,
        scope: "default",
        shareConfig: {
          eager: true,
          requiredVersion: __LOCKED_VERSIONS__["react"],
          singleton: true,
          strictVersion: true,
        },
        version: __LOCKED_VERSIONS__["react"],
      },
      "react-dom": {
        lib: () => ReactDom,
        scope: "default",
        shareConfig: {
          eager: true,
          requiredVersion: __LOCKED_VERSIONS__["react-dom"],
          singleton: true,
          strictVersion: true,
        },
        version: __LOCKED_VERSIONS__["react-dom"],
      },
      "react-redux": {
        scope: "default",
        shareConfig: {
          eager: false,
          requiredVersion: __LOCKED_VERSIONS__["react-redux"],
          singleton: true,
          strictVersion: true,
        },
        version: __LOCKED_VERSIONS__["react-redux"],
      },
      rxjs: {
        scope: "default",
        shareConfig: {
          eager: false,
          requiredVersion: __LOCKED_VERSIONS__["rxjs"],
          singleton: true,
          strictVersion: true,
        },
        version: __LOCKED_VERSIONS__["rxjs"],
      },
    },
    shareStrategy: "version-first",
  });

  const manifestPromises = remotes.map(async (remote) => {
    const manifestUrl = `${remote.baseUrl}/mf-manifest.json`;
    const response = await fetch(manifestUrl);
    const manifest: Manifest = await response.json();
    const entryPath =
      manifest.metaData.remoteEntry.path ||
      `/${manifest.metaData.remoteEntry.name}`;
    const requestIdParam = remote.requestId
      ? `?request-id=${remote.requestId}`
      : "";

    return {
      alias: remote.name,
      entry: `${remote.baseUrl}${entryPath}${requestIdParam}`,
      name: remote.name,
      type: manifest.metaData.remoteEntry.type,
    };
  });
  const remoteConfigs = await Promise.all(manifestPromises);

  registerRemotes(remoteConfigs);
}
