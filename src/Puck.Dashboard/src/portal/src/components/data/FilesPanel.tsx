import { Alert, Badge, Button, FileButton, Group, Menu, Paper, Skeleton, Stack, Table, Text, Title, VisuallyHidden } from "@mantine/core";
import {
  RiArrowDownSLine,
  RiDeleteBinLine,
  RiErrorWarningLine,
  RiFolderOpenLine,
  RiGlobalLine,
  RiShareLine,
  RiTerminalBoxLine,
  RiUpload2Line,
} from "@remixicon/react";
import { EmptyState } from "../../ui/EmptyState";
import { Kicker } from "../../ui/Kicker";
import classes from "../Page.module.css";
import { Timestamp } from "../Timestamp";
import { type BlobEntry, formatSize, readFunctionFor, SHARE_DURATIONS } from "./storage";
import { Well } from "../../ui/Well";

interface FilesPanelProps {
  blobs: BlobEntry[] | undefined;
  isUploading: boolean;
  listError: string | undefined;
  onDelete: (blobName: string) => void;
  onPublish: (blobName: string) => void;
  onQuery: (blobName: string) => void;
  onRetry: () => void;
  onShareLink: (blobName: string, hours: number) => void;
  onShareWithUser: (blobName: string) => void;
  onUpload: (file: File | null) => void;
}

const PRIVATE_PREFIX = "private/";

/** A blob name with its fixed `private/` prefix set back, so the part the user chose reads first. */
export function BlobName({ name }: { name: string }) {
  const hasPrefix = name.startsWith(PRIVATE_PREFIX);

  return (
    <Text className={`${classes.data} ${classes.path}`} component="span">
      {hasPrefix ? (
        <Text c="dimmed" component="span" inherit>
          {PRIVATE_PREFIX}
        </Text>
      ) : null}
      {hasPrefix ? name.slice(PRIVATE_PREFIX.length) : name}
    </Text>
  );
}

function FileRow({ blob, ...actions }: Omit<FilesPanelProps, "blobs" | "isUploading" | "listError" | "onRetry" | "onUpload"> & { blob: BlobEntry }) {
  return (
    <Table.Tr>
      <Table.Td>
        <BlobName name={blob.name} />
      </Table.Td>
      <Table.Td className={`${classes.data} ${classes.nowrap}`} ta="right">
        {formatSize(blob.sizeInBytes)}
      </Table.Td>
      <Table.Td className={classes.nowrap}>{blob.lastModified ? <Timestamp relative time={blob.lastModified} /> : null}</Table.Td>
      <Table.Td>
        <Group gap={4} justify="flex-end" wrap="nowrap">
          {readFunctionFor(blob.name) ? (
            <Button
              aria-label={`Query ${blob.name}`}
              leftSection={<RiTerminalBoxLine size={14} />}
              onClick={() => actions.onQuery(blob.name)}
              size="compact-xs"
              variant="subtle"
            >
              Query
            </Button>
          ) : null}
          <Menu position="bottom-end">
            <Menu.Target>
              <Button
                aria-label={`Share ${blob.name}`}
                leftSection={<RiShareLine size={14} />}
                rightSection={<RiArrowDownSLine size={14} />}
                size="compact-xs"
                variant="subtle"
              >
                Share
              </Button>
            </Menu.Target>
            <Menu.Dropdown>
              <Menu.Label>Anyone with the link, expires after</Menu.Label>
              {SHARE_DURATIONS.map((duration) => (
                <Menu.Item key={duration.hours} onClick={() => actions.onShareLink(blob.name, duration.hours)}>
                  {duration.label}
                </Menu.Item>
              ))}
              <Menu.Divider />
              <Menu.Item onClick={() => actions.onShareWithUser(blob.name)}>Specific user…</Menu.Item>
            </Menu.Dropdown>
          </Menu>
          <Button
            aria-label={`Publish ${blob.name}`}
            leftSection={<RiGlobalLine size={14} />}
            onClick={() => actions.onPublish(blob.name)}
            size="compact-xs"
            variant="subtle"
          >
            Publish
          </Button>
          <Button
            aria-label={`Delete ${blob.name}`}
            color="red"
            leftSection={<RiDeleteBinLine size={14} />}
            onClick={() => actions.onDelete(blob.name)}
            size="compact-xs"
            variant="subtle"
          >
            Delete
          </Button>
        </Group>
      </Table.Td>
    </Table.Tr>
  );
}

/** The user's private files, with the upload that adds one and each file's query, share, publish, and delete. */
export function FilesPanel({ blobs, isUploading, listError, onRetry, onUpload, ...actions }: FilesPanelProps) {
  return (
    <Paper p="md" withBorder>
      <Stack gap="md">
        <Group justify="space-between" wrap="nowrap">
          <div>
            <Kicker>private/</Kicker>
            <Group gap="xs" mt={4}>
              <Title order={2} size="h4">
                Your files
              </Title>
              {blobs && 0 < blobs.length ? <Badge color="gray">{blobs.length}</Badge> : null}
            </Group>
          </div>
          <FileButton onChange={onUpload}>
            {(props) => (
              <Button {...props} leftSection={<RiUpload2Line size={16} />} loading={isUploading} size="sm">
                Upload
              </Button>
            )}
          </FileButton>
        </Group>
        {listError ? (
          <Alert color="red" icon={<RiErrorWarningLine size={18} />} title="Could not list your files" variant="light">
            <Stack align="flex-start" gap="xs">
              <Text inherit>{listError}</Text>
              <Button color="red" onClick={onRetry} size="compact-xs" variant="light">
                Retry
              </Button>
            </Stack>
          </Alert>
        ) : undefined === blobs ? (
          <Stack aria-label="Loading your files" gap="xs" role="status">
            <Skeleton height={28} />
            <Skeleton height={28} />
            <Skeleton height={28} />
          </Stack>
        ) : 0 === blobs.length ? (
          <EmptyState icon={<RiFolderOpenLine size={22} />} title="No files found">
            Upload a file to keep it here. CSV, JSON, and Parquet files can be queried with SQL below.
          </EmptyState>
        ) : (
          <Well>
            <Table.ScrollContainer minWidth={640}>
              <Table>
                <Table.Thead>
                  <Table.Tr>
                    <Table.Th>Name</Table.Th>
                    <Table.Th ta="right">Size</Table.Th>
                    <Table.Th>Modified</Table.Th>
                    <Table.Th>
                      <VisuallyHidden>Actions</VisuallyHidden>
                    </Table.Th>
                  </Table.Tr>
                </Table.Thead>
                <Table.Tbody>
                  {blobs.map((blob) => (
                    <FileRow blob={blob} key={blob.name} {...actions} />
                  ))}
                </Table.Tbody>
              </Table>
            </Table.ScrollContainer>
          </Well>
        )}
      </Stack>
    </Paper>
  );
}
