import type { TokenCredential } from "@azure/identity";
import { Code, Stack } from "@mantine/core";
import { useState } from "react";
import { ConfirmDialog, PublishDialog, ShareWithUserDialog } from "./data/Dialogs";
import { FilesPanel } from "./data/FilesPanel";
import { ActionError, IncomingShare, ShareNoticeAlert } from "./data/Notices";
import { PublicFilesPanel } from "./data/PublicFilesPanel";
import { QueryPanel } from "./data/QueryPanel";
import {
  blobUrl,
  buildQuerySql,
  downloadSharedFile,
  errorMessage,
  publishFile,
  readFunctionFor,
  requestShareLink,
  requestUserShare,
  type ShareNotice,
  tryCopy,
  unpublishFile,
} from "./data/storage";
import { useStorageAccount } from "./data/useStorageAccount";
import { usePendingShare, useStorageQuery } from "./data/useStorageQuery";
import { PageHeader } from "./PageHeader";

type PendingConfirmation = { blobName: string; kind: "delete" | "unpublish" };

/**
 * Cloud Storage: the signed-in user's private files, their published files, files shared with them, and SQL over
 * any of those, run in the browser.
 */
export default function DataExplorer({
  tokenCredential,
  userObjectId,
}: {
  tokenCredential: TokenCredential;
  userObjectId: string;
}) {
  const account = useStorageAccount(tokenCredential, userObjectId);
  const query = useStorageQuery(tokenCredential);
  const incoming = usePendingShare(query.replaceAndRun);
  const [actionError, setActionError] = useState<string | undefined>();
  // The confirmation outlives its dialog so the closing dialog keeps its own words.
  const [confirmation, setConfirmation] = useState<PendingConfirmation | undefined>();
  const [isConfirmOpen, setIsConfirmOpen] = useState(false);
  const [isConfirming, setIsConfirming] = useState(false);
  const [isDownloading, setIsDownloading] = useState(false);
  const [isPublishing, setIsPublishing] = useState(false);
  const [isSharing, setIsSharing] = useState(false);
  const [isUploading, setIsUploading] = useState(false);
  const [publishTarget, setPublishTarget] = useState<string | undefined>();
  const [share, setShare] = useState<ShareNotice | undefined>();
  const [shareTarget, setShareTarget] = useState<string | undefined>();

  // Every file action clears the last failure first and reports its own, by name, if it fails.
  const attempt = async (action: () => Promise<void>) => {
    setActionError(undefined);

    try {
      await action();
    } catch (e) {
      setActionError(errorMessage(e));
    }
  };

  const shareLink = (blobName: string, hours: number) =>
    attempt(async () => {
      setShare(undefined);

      const uri = await requestShareLink(tokenCredential, userObjectId, blobName, hours);

      setShare({
        note: "Anyone with this link can read the file until it expires.",
        title: `Share link for ${blobName} (copied to clipboard)`,
        uri: uri,
      });
      await tryCopy(uri);
    });
  const shareWithUser = async (recipient: string, hours: number) => {
    if (!shareTarget) {
      return;
    }

    setIsSharing(true);
    await attempt(async () => {
      const expiresOn = new Date(Date.now() + hours * 3600000);
      const { recipientName, uri } = await requestUserShare(tokenCredential, shareTarget, recipient, expiresOn);
      const portalLink = `${location.origin}/data#share=${encodeURIComponent(uri)}`;

      setShare({
        note: `Send this link to ${recipientName} — it opens the file right here in the portal after they sign in, and it only works for them. It expires ${expiresOn.toLocaleString()} and cannot be revoked early.`,
        title: `Share link for ${shareTarget} (copied to clipboard)`,
        uri: portalLink,
      });
      setShareTarget(undefined);
      await tryCopy(portalLink);
    });
    setIsSharing(false);
  };
  const publish = async () => {
    if (!publishTarget) {
      return;
    }

    setIsPublishing(true);
    await attempt(async () => {
      const publicUrl = await publishFile(tokenCredential, publishTarget);

      setShare({
        note: "Anyone on the internet can download it from this address. Unpublishing moves it back to your private files, though cached copies may stay reachable for a short while.",
        title: `${publishTarget} is now public (address copied)`,
        uri: publicUrl,
      });
      setPublishTarget(undefined);
      await tryCopy(publicUrl);
      await account.reload(true);
    });
    setIsPublishing(false);
  };
  const confirm = async () => {
    if (!confirmation) {
      return;
    }

    const { blobName, kind } = confirmation;

    setIsConfirming(true);
    await attempt(async () => {
      if ("delete" === kind) {
        await account.containerClient().deleteBlob(blobName);
        await account.reload();
      } else {
        await unpublishFile(tokenCredential, blobName);
        setShare({
          note: "It is back under your files. Copies cached at the edge may stay reachable for a short while.",
          title: `${blobName} is private again`,
          uri: "",
        });
        await account.reload(true);
      }
    });
    setIsConfirming(false);
    setIsConfirmOpen(false);
  };
  const upload = async (file: File | null) => {
    if (!file) {
      return;
    }

    setIsUploading(true);
    await attempt(async () => {
      await account
        .containerClient()
        .getBlockBlobClient(`private/${file.name}`)
        .uploadData(file, { blobHTTPHeaders: { blobContentType: file.type || "application/octet-stream" } });
      await account.reload();
    });
    setIsUploading(false);
  };
  const download = async (url: string) => {
    setIsDownloading(true);
    await attempt(() => downloadSharedFile(tokenCredential, url));
    setIsDownloading(false);
  };
  const askToConfirm = (kind: PendingConfirmation["kind"], blobName: string) => {
    setConfirmation({ blobName: blobName, kind: kind });
    setIsConfirmOpen(true);
  };
  const insertQueryFor = (blobName: string) => {
    const readFunction = readFunctionFor(blobName);

    if (readFunction) {
      query.setSql(buildQuerySql(readFunction, blobUrl(account.storageEndpoint, userObjectId, blobName)));
    }
  };

  return (
    <Stack gap="lg">
      <PageHeader kicker="Account" title="Cloud Storage">
        Files under <Code>private/</Code> in your storage container. Queries run entirely in your browser.
      </PageHeader>
      {actionError ? <ActionError message={actionError} onClose={() => setActionError(undefined)} /> : null}
      {share ? <ShareNoticeAlert notice={share} onClose={() => setShare(undefined)} /> : null}
      {incoming.pendingShare ? (
        <IncomingShare
          isDownloading={isDownloading}
          isRunning={query.isRunning}
          onClose={incoming.dismiss}
          onDownload={() => void download(incoming.pendingShare!)}
          onQuery={(readFunction) => query.replaceAndRun(buildQuerySql(readFunction, incoming.pendingShare!))}
          uri={incoming.pendingShare}
        />
      ) : null}
      <FilesPanel
        blobs={account.blobs}
        isUploading={isUploading}
        listError={account.listError}
        onDelete={(blobName) => askToConfirm("delete", blobName)}
        onPublish={setPublishTarget}
        onQuery={insertQueryFor}
        onRetry={() => void account.reload(true)}
        onShareLink={(blobName, hours) => void shareLink(blobName, hours)}
        onShareWithUser={setShareTarget}
        onUpload={(file) => void upload(file)}
      />
      {0 < account.publicFiles.length ? (
        <PublicFilesPanel
          files={account.publicFiles}
          onUnpublish={(blobName) => askToConfirm("unpublish", blobName)}
        />
      ) : null}
      <QueryPanel
        files={(account.blobs ?? []).map((blob) => ({
          label: blob.name,
          url: blobUrl(account.storageEndpoint, userObjectId, blob.name),
        }))}
        isRunning={query.isRunning}
        onChange={query.setSql}
        onRun={() => void query.run(query.sql)}
        output={query.output}
        queryError={query.queryError}
        sql={query.sql}
      />
      <PublishDialog
        blobName={publishTarget}
        isPublishing={isPublishing}
        onCancel={() => setPublishTarget(undefined)}
        onPublish={() => void publish()}
        userObjectId={userObjectId}
      />
      <ShareWithUserDialog
        blobName={shareTarget}
        isSharing={isSharing}
        onCancel={() => setShareTarget(undefined)}
        onShare={(recipient, hours) => void shareWithUser(recipient, hours)}
      />
      <ConfirmDialog
        confirmLabel={"delete" === confirmation?.kind ? "Delete" : "Unpublish"}
        isWorking={isConfirming}
        onCancel={() => setIsConfirmOpen(false)}
        onConfirm={() => void confirm()}
        opened={isConfirmOpen}
        title={"delete" === confirmation?.kind ? `Delete ${confirmation.blobName}?` : `Unpublish ${confirmation?.blobName ?? ""}?`}
      >
        {"delete" === confirmation?.kind
          ? "This removes the file from your storage. It cannot be undone."
          : "Its public address stops working and the file moves back under your private files. Anyone you gave the address to loses access."}
      </ConfirmDialog>
    </Stack>
  );
}
