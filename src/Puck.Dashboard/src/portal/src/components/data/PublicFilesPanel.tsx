import { Anchor, Badge, Button, CopyButton, Group, Paper, Stack, Table, Text, Title, VisuallyHidden } from "@mantine/core";
import { RiCheckLine, RiFileCopyLine, RiLockLine } from "@remixicon/react";
import { Kicker } from "../../ui/Kicker";
import classes from "../Page.module.css";
import { formatSize, type PublicFileEntry } from "./storage";
import { Well } from "../../ui/Well";

interface PublicFilesPanelProps {
  files: PublicFileEntry[];
  onUnpublish: (blobName: string) => void;
}

/** Files the user has published: their public addresses, and the unpublish that takes each one back. */
export function PublicFilesPanel({ files, onUnpublish }: PublicFilesPanelProps) {
  return (
    <Paper p="md" withBorder>
      <Stack gap="md">
        <div>
          <Kicker>public/</Kicker>
          <Group gap="xs" mt={4}>
            <Title order={2} size="h4">
              Your public files
            </Title>
            <Badge color="gray">{files.length}</Badge>
          </Group>
          <Text c="dimmed" mt={4} size="sm">
            Anyone on the internet can download these from their addresses.
          </Text>
        </div>
        <Well>
          <Table.ScrollContainer minWidth={640}>
            <Table>
              <Table.Thead>
                <Table.Tr>
                  <Table.Th>Name</Table.Th>
                  <Table.Th ta="right">Size</Table.Th>
                  <Table.Th>Address</Table.Th>
                  <Table.Th>
                    <VisuallyHidden>Actions</VisuallyHidden>
                  </Table.Th>
                </Table.Tr>
              </Table.Thead>
              <Table.Tbody>
                {files.map((file) => (
                  <Table.Tr key={file.blobName}>
                    <Table.Td className={`${classes.data} ${classes.path}`}>{file.blobName}</Table.Td>
                    <Table.Td className={`${classes.data} ${classes.nowrap}`} ta="right">
                      {formatSize(file.sizeInBytes)}
                    </Table.Td>
                    <Table.Td>
                      <Anchor className={`${classes.data} ${classes.path}`} href={file.publicUrl} rel="noreferrer" target="_blank">
                        {file.publicUrl.replace("https://", "")}
                      </Anchor>
                    </Table.Td>
                    <Table.Td>
                      <Group gap={4} justify="flex-end" wrap="nowrap">
                        <CopyButton value={file.publicUrl}>
                          {({ copied, copy }) => (
                            <Button
                              color={copied ? "jade" : undefined}
                              leftSection={copied ? <RiCheckLine size={14} /> : <RiFileCopyLine size={14} />}
                              onClick={copy}
                              size="compact-xs"
                              variant="subtle"
                            >
                              {copied ? "Copied" : "Copy address"}
                            </Button>
                          )}
                        </CopyButton>
                        <Button
                          aria-label={`Unpublish ${file.blobName}`}
                          color="red"
                          leftSection={<RiLockLine size={14} />}
                          onClick={() => onUnpublish(file.blobName)}
                          size="compact-xs"
                          variant="subtle"
                        >
                          Unpublish
                        </Button>
                      </Group>
                    </Table.Td>
                  </Table.Tr>
                ))}
              </Table.Tbody>
            </Table>
          </Table.ScrollContainer>
        </Well>
      </Stack>
    </Paper>
  );
}
