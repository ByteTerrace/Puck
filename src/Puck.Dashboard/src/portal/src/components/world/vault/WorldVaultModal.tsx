import { useStudioConfirmation } from "../StudioConfirmation";
import React, { useEffect, useState } from "react";
import { Alert, Button, Group, Modal, Paper, Stack, Tabs, Text } from "@mantine/core";
import { WorldStorageClient, WorldVaultItem, LocalCheckpoint } from "../../../clients/worldStorageClient";
export interface WorldVaultModalProps {
  opened: boolean;
  onClose: () => void;
  storageClient: WorldStorageClient;
  activeWorldId: string;
  onLoadWorld: (world: any, metadata: any) => void;
  onOpenSaveModal: () => void;
}
export const WorldVaultModal: React.FC<WorldVaultModalProps> = ({ opened, onClose, storageClient, activeWorldId, onLoadWorld, onOpenSaveModal }) => {
  const confirm = useStudioConfirmation();
  const [items, setItems] = useState<WorldVaultItem[]>([]);
  const [history, setHistory] = useState<LocalCheckpoint[]>([]);
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  const refresh = async () => {
    try {
      setItems(await storageClient.listPrivateWorlds());
      setHistory(storageClient.listCheckpoints(activeWorldId));
      setError(null);
    }
    catch(e) {
      setError((e as Error).message);
    }
  };
  useEffect(() => {
    if(opened)
      void refresh();
  }, [opened, activeWorldId, storageClient]);
  const open = (item: WorldVaultItem) => { onLoadWorld(item.world, item.metadata); onClose(); };
  const cards = (list: WorldVaultItem[]) => list.length ? list.map(item => <Paper
    key={item.metadata.id}
    withBorder
    p="md"
    radius="md">
    <Group
      justify="space-between"><div><Text
        fw={600}>{item.metadata.name}</Text><Text
          size="sm"
          c="var(--ink-soft)">{item.metadata.description}</Text><Text
            size="xs"
            c="var(--ink-soft)">{item.isPreset ? "Bundled example" : "Saved in this browser · Revision " + item.metadata.revision}</Text></div>
      <Group
        gap="xs"><Button
          variant="default"
          onClick={() => open(item)}>Open {item.metadata.id}</Button>
        {!item.isPreset && <Button
          variant="subtle"
          color="red"
          disabled={busy}
          onClick={async () => {
            if(!await confirm("Delete local document '" + item.metadata.name + "' and its saved revisions?"))
              return;
            setBusy(true);
            try {
              await storageClient.deleteWorld(item.metadata.id);
              await refresh();
            }
            catch(e) {
              setError((e as Error).message);
            }
            finally {
              setBusy(false);
            }
          }}>Delete {item.metadata.id}</Button>}</Group></Group>
  </Paper>) : <Text
    size="sm">No saved documents yet. Save your applied document to start a local library.</Text>;
  return <Modal
    closeButtonProps={{ "aria-label": "Close local library" }}
    opened={opened}
    onClose={onClose}
    title="Local document library"
    size="xl"><Stack>
      <Text
        size="sm"
        c="var(--ink-soft)">Saved on this browser and device. Export JSON for a portable copy. No cloud upload or public publishing occurs.</Text>
      {error && <Alert
        color="red"
        role="alert">{error}</Alert>}
      <Group><Button
        onClick={onOpenSaveModal}>Save current document</Button><Button
          variant="default"
          onClick={refresh}>Refresh</Button></Group>
      <Tabs
        defaultValue="local"
        keepMounted={false}><Tabs.List><Tabs.Tab
          value="local">My documents</Tabs.Tab><Tabs.Tab
            value="examples">Examples</Tabs.Tab><Tabs.Tab
              value="history">Saved revisions</Tabs.Tab></Tabs.List>
        <Tabs.Panel
          value="local"
          pt="md"><Stack>{cards(items)}</Stack></Tabs.Panel>
        <Tabs.Panel
          value="examples"
          pt="md"><Stack>{cards(storageClient.listPublicWorlds())}</Stack></Tabs.Panel>
        <Tabs.Panel
          value="history"
          pt="md"><Stack><Text
            size="sm">Up to ten saved revisions of {activeWorldId}. Opening a revision lets you review it before saving again.</Text>
            {history.length ? history.map(entry => <Paper
              key={entry.revision}
              p="sm"
              withBorder><Group
                justify="space-between"><Text
                  size="sm">Revision {entry.revision} · {new Date(entry.timestamp).toLocaleString()}</Text><Button
                    variant="default"
                    disabled={!entry.world}
                    onClick={() => { onLoadWorld(entry.world, items.find(i => i.metadata.id === activeWorldId)?.metadata); onClose(); }}>Open revision {entry.revision}</Button></Group></Paper>) : <Text
                      size="sm">No recoverable revisions saved yet.</Text>}
          </Stack></Tabs.Panel>
      </Tabs>
    </Stack></Modal>;
};
export default WorldVaultModal;
