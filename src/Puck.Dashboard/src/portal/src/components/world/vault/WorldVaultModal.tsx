import React, { useState, useEffect } from "react";
import {
  Modal,
  Tabs,
  Group,
  Stack,
  Text,
  Badge,
  Button,
  Paper,
  SimpleGrid,
  ScrollArea,
  ActionIcon,
  Tooltip,
  Alert,
  CopyButton,
} from "@mantine/core";
import {
  RiFolderShield2Line,
  RiGlobalLine,
  RiGitBranchLine,
  RiTimeLine,
  RiDeleteBinLine,
  RiShareLine,
  RiCheckLine,
  RiPlayCircleLine,
  RiAddLine,
  RiRefreshLine,
} from "@remixicon/react";
import { useActorRef, useSelector } from "@xstate/react";
import {
  WorldStorageClient,
  WorldVaultItem,
} from "../../../clients/worldStorageClient";
import {
  worldStorageMachine,
  selectPrivateWorlds,
  selectPublicWorlds,
  selectCheckpoints,
  selectShareUrl,
  selectStorageError,
  selectIsRefreshing,
} from "../../../machines/worldStorageMachine";

export interface WorldVaultModalProps {
  opened: boolean;
  onClose: () => void;
  storageClient: WorldStorageClient;
  activeWorldId: string;
  onLoadWorld: (worldData: any, metadata: any) => void;
  onOpenSaveModal: () => void;
}

export const WorldVaultModal: React.FC<WorldVaultModalProps> = ({
  opened,
  onClose,
  storageClient,
  activeWorldId,
  onLoadWorld,
  onOpenSaveModal,
}) => {
  // Formal XState Storage Substrate Actor
  const storageActor = useActorRef(worldStorageMachine);

  // Fine-grained Selectors
  const privateWorlds = useSelector(storageActor, selectPrivateWorlds);
  const publicWorlds = useSelector(storageActor, selectPublicWorlds);
  const checkpoints = useSelector(storageActor, selectCheckpoints);
  const shareUrl = useSelector(storageActor, selectShareUrl);
  const storageError = useSelector(storageActor, selectStorageError);
  const isRefreshing = useSelector(storageActor, selectIsRefreshing);
  const [activeTab, setActiveTab] = useState<string | null>("private");

  useEffect(() => {
    if (opened) {
      storageActor.send({ type: "SET_ACTIVE_WORLD", id: activeWorldId });
      storageActor.send({ type: "REFRESH_VAULT" });
    }
  }, [opened, activeWorldId, storageActor]);

  const handleSelectWorld = async (item: WorldVaultItem) => {
    if (!item.world) {
      const full = await storageClient.loadWorld(item.metadata.id);
      if (full && full.world) {
        onLoadWorld(full.world, full.metadata);
      }
    } else {
      onLoadWorld(item.world, item.metadata);
    }
    onClose();
  };

  const handleDeleteWorld = async (id: string) => {
    storageActor.send({ type: "DELETE_WORLD", id });
  };

  const handleForkWorld = async (sourceItem: WorldVaultItem) => {
    const newId = `${sourceItem.metadata.id}-fork-${Date.now().toString().slice(-4)}`;
    await storageClient.forkWorld(sourceItem.metadata.id, newId, `${sourceItem.metadata.name} (Fork)`);
    storageActor.send({ type: "REFRESH_VAULT" });
    setActiveTab("private");
  };

  const handleGenerateShare = async (_id: string) => {
    storageActor.send({ type: "GENERATE_SHARE", durationHours: 24 });
  };

  return (
    <Modal
      opened={opened}
      onClose={onClose}
      title={
        <Group gap="xs">
          <RiFolderShield2Line size={20} color="var(--accent)" />
          <Text fw={600} size="md" style={{ fontFamily: '"Lora", Georgia, serif' }}>
            Cloud World Vault & Storage Substrate
          </Text>
        </Group>
      }
      size="xl"
      styles={{
        content: { background: "var(--paper-2)", color: "var(--ink)", border: "1px solid var(--rule)" },
        header: { background: "var(--paper-2)", color: "var(--ink)" },
      }}
    >
      <Stack gap="xs">
        {/* Substrate Error Alert */}
        {storageError && (
          <Alert
            color="red"
            variant="light"
            withCloseButton
            onClose={() => storageActor.send({ type: "CLEAR_ERROR" })}
            title="Storage Substrate Error"
          >
            {storageError}
          </Alert>
        )}

        {/* SAS Share Link Notification Banner */}
        {shareUrl && (
          <Alert
            icon={<RiShareLine size={16} />}
            color="teal"
            variant="light"
            withCloseButton
            onClose={() => storageActor.send({ type: "CLEAR_SHARE_URL" })}
            title="Time-Bounded Share Link (24 Hours)"
          >
            <Group justify="space-between" align="center" wrap="nowrap">
              <Text size="xs" style={{ fontFamily: '"JetBrains Mono", monospace', wordBreak: "break-all" }}>
                {shareUrl}
              </Text>
              <CopyButton value={shareUrl}>
                {({ copied, copy }) => (
                  <Button
                    size="compact-xs"
                    color={copied ? "teal" : "coral"}
                    variant="filled"
                    onClick={copy}
                    leftSection={copied ? <RiCheckLine size={12} /> : <RiShareLine size={12} />}
                  >
                    {copied ? "Copied!" : "Copy"}
                  </Button>
                )}
              </CopyButton>
            </Group>
          </Alert>
        )}

        <Tabs value={activeTab} onChange={setActiveTab} variant="outline">
          <Group justify="space-between" mb="xs">
            <Tabs.List>
              <Tabs.Tab value="private" leftSection={<RiFolderShield2Line size={15} />}>
                My Private Vault ({privateWorlds.length})
              </Tabs.Tab>
              <Tabs.Tab value="public" leftSection={<RiGlobalLine size={15} />}>
                Public Gallery ({publicWorlds.length})
              </Tabs.Tab>
              <Tabs.Tab value="checkpoints" leftSection={<RiTimeLine size={15} />}>
                Checkpoint Revisions ({checkpoints.length})
              </Tabs.Tab>
            </Tabs.List>

            <Group gap="xs">
              <Button
                size="xs"
                variant="subtle"
                color="gray"
                leftSection={<RiRefreshLine size={14} />}
                loading={isRefreshing}
                onClick={() => storageActor.send({ type: "REFRESH_VAULT" })}
              >
                Sync Vault
              </Button>
              <Button
                size="xs"
                color="coral"
                leftSection={<RiAddLine size={14} />}
                onClick={() => {
                  onClose();
                  onOpenSaveModal();
                }}
              >
                Save Active World As...
              </Button>
            </Group>
          </Group>

          {/* Tab 1: My Private Vault */}
          <Tabs.Panel value="private">
            <ScrollArea h={380} offsetScrollbars>
              {privateWorlds.length === 0 ? (
                <Paper p="xl" radius="sm" style={{ background: "var(--code-bg)", textAlign: "center" }}>
                  <RiFolderShield2Line size={36} color="var(--ink-faint)" style={{ margin: "0 auto 8px auto" }} />
                  <Text size="sm" fw={600} c="var(--ink-soft)">
                    No private worlds in your cloud vault yet.
                  </Text>
                  <Text size="xs" c="dimmed" mt={4}>
                    Create a new world in the Studio or fork a game from the Public Gallery.
                  </Text>
                </Paper>
              ) : (
                <SimpleGrid cols={{ base: 1, sm: 2 }} spacing="xs">
                  {privateWorlds.map((item: WorldVaultItem) => (
                    <Paper
                      key={item.metadata.id}
                      p="xs"
                      radius="sm"
                      style={{
                        background: "var(--code-bg)",
                        border: "1px solid var(--rule)",
                        transition: "all 0.15s ease",
                      }}
                    >
                      <Group justify="space-between" mb={4}>
                        <Group gap={6}>
                          <Text fw={600} size="sm" style={{ fontFamily: '"Lora", Georgia, serif', color: "var(--ink)" }}>
                            {item.metadata.name}
                          </Text>
                          <Badge size="xs" variant="light" color="coral">
                            {item.metadata.topologyType.toUpperCase()}
                          </Badge>
                        </Group>

                        <Badge size="xs" variant="outline" color="gray">
                          v{item.metadata.version}
                        </Badge>
                      </Group>

                      <Text size="xs" c="var(--ink-soft)" lineClamp={2} style={{ fontSize: 11, minHeight: 32, marginBottom: 8 }}>
                        {item.metadata.description}
                      </Text>

                      <Group justify="space-between" align="center">
                        <Group gap={4}>
                          <Badge size="xs" color="jade" variant="light" style={{ fontSize: 9 }}>
                            {item.metadata.rulesCount} RULES
                          </Badge>
                          <Text size="xs" c="dimmed" style={{ fontSize: 9, fontFamily: '"JetBrains Mono", monospace' }}>
                            {item.metadata.id}
                          </Text>
                        </Group>

                        <Group gap={4}>
                          <Tooltip label="Share time-bounded SAS link">
                            <ActionIcon
                              size="xs"
                              variant="subtle"
                              color="jade"
                              onClick={() => handleGenerateShare(item.metadata.id)}
                            >
                              <RiShareLine size={13} />
                            </ActionIcon>
                          </Tooltip>

                          <Tooltip label="Fork to new private copy">
                            <ActionIcon
                              size="xs"
                              variant="subtle"
                              color="blue"
                              onClick={() => handleForkWorld(item)}
                            >
                              <RiGitBranchLine size={13} />
                            </ActionIcon>
                          </Tooltip>

                          <Tooltip label="Delete from vault">
                            <ActionIcon
                              size="xs"
                              variant="subtle"
                              color="red"
                              onClick={() => handleDeleteWorld(item.metadata.id)}
                            >
                              <RiDeleteBinLine size={13} />
                            </ActionIcon>
                          </Tooltip>

                          <Button
                            size="compact-xs"
                            color="coral"
                            variant="filled"
                            leftSection={<RiPlayCircleLine size={12} />}
                            onClick={() => handleSelectWorld(item)}
                          >
                            Load
                          </Button>
                        </Group>
                      </Group>
                    </Paper>
                  ))}
                </SimpleGrid>
              )}
            </ScrollArea>
          </Tabs.Panel>

          {/* Tab 2: Public Community Gallery */}
          <Tabs.Panel value="public">
            <ScrollArea h={380} offsetScrollbars>
              <SimpleGrid cols={{ base: 1, sm: 2 }} spacing="xs">
                {publicWorlds.map((item: WorldVaultItem) => (
                  <Paper
                    key={item.metadata.id}
                    p="xs"
                    radius="sm"
                    style={{
                      background: "var(--code-bg)",
                      border: "1px solid var(--rule)",
                    }}
                  >
                    <Group justify="space-between" mb={4}>
                      <Group gap={6}>
                        <Text fw={600} size="sm" style={{ fontFamily: '"Lora", Georgia, serif', color: "var(--ink)" }}>
                          {item.metadata.name}
                        </Text>
                        <Badge size="xs" variant="light" color="jade">
                          {item.metadata.category}
                        </Badge>
                      </Group>

                      {item.isPreset && (
                        <Badge size="xs" color="teal" variant="filled" style={{ fontSize: 9 }}>
                          OFFICIAL PRESET
                        </Badge>
                      )}
                    </Group>

                    <Text size="xs" c="var(--ink-soft)" lineClamp={2} style={{ fontSize: 11, minHeight: 32, marginBottom: 8 }}>
                      {item.metadata.description}
                    </Text>

                    <Group justify="space-between" align="center">
                      <Group gap={4}>
                        <Badge size="xs" color="coral" variant="light" style={{ fontSize: 9 }}>
                          {item.metadata.topologyType.toUpperCase()}
                        </Badge>
                        <Text size="xs" c="dimmed" style={{ fontSize: 10 }}>
                          by {item.metadata.author}
                        </Text>
                      </Group>

                      <Group gap={4}>
                        <Button
                          size="compact-xs"
                          color="jade"
                          variant="outline"
                          leftSection={<RiGitBranchLine size={12} />}
                          onClick={() => handleForkWorld(item)}
                        >
                          Fork to My Vault
                        </Button>

                        <Button
                          size="compact-xs"
                          color="coral"
                          variant="filled"
                          leftSection={<RiPlayCircleLine size={12} />}
                          onClick={() => handleSelectWorld(item)}
                        >
                          Play
                        </Button>
                      </Group>
                    </Group>
                  </Paper>
                ))}
              </SimpleGrid>
            </ScrollArea>
          </Tabs.Panel>

          {/* Tab 3: Checkpoint Revisions */}
          <Tabs.Panel value="checkpoints">
            <ScrollArea h={380} offsetScrollbars>
              {checkpoints.length === 0 ? (
                <Paper p="xl" radius="sm" style={{ background: "var(--code-bg)", textAlign: "center" }}>
                  <RiTimeLine size={36} color="var(--ink-faint)" style={{ margin: "0 auto 8px auto" }} />
                  <Text size="sm" fw={600} c="var(--ink-soft)">
                    No historical checkpoints recorded for '{activeWorldId}'.
                  </Text>
                  <Text size="xs" c="dimmed" mt={4}>
                    Checkpoints are written create-only to the storage substrate whenever a world revision is saved.
                  </Text>
                </Paper>
              ) : (
                <Stack gap="xs">
                  {checkpoints.map((chk: { revision: number; hash: string; timestamp: string }, idx: number) => (
                    <Paper key={idx} p="xs" radius="xs" style={{ background: "var(--code-bg)", border: "1px solid var(--rule)" }}>
                      <Group justify="space-between">
                        <Group gap="xs">
                          <Badge size="xs" color="coral" variant="filled">
                            Rev #{chk.revision}
                          </Badge>
                          <Text size="xs" style={{ fontFamily: '"JetBrains Mono", monospace', color: "var(--ink)" }}>
                            Pin: {chk.hash}
                          </Text>
                        </Group>

                        <Group gap="xs">
                          <Text size="xs" c="dimmed" style={{ fontSize: 10 }}>
                            {new Date(chk.timestamp).toLocaleString()}
                          </Text>
                        </Group>
                      </Group>
                    </Paper>
                  ))}
                </Stack>
              )}
            </ScrollArea>
          </Tabs.Panel>
        </Tabs>
      </Stack>
    </Modal>
  );
};

export default WorldVaultModal;
