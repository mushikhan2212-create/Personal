import { useCallback, useEffect, useState } from 'react';
import {
  Alert, App as AntApp, Button, Card, Col, Empty, Flex, Modal, Row, Skeleton, Space, Switch,
  Tag, Tooltip, Typography,
} from 'antd';
import { deleteSource, listMySources, setMySource, syncSource } from '../api/client';
import type { MySource } from '../api/types';
import { formatUtc } from '../format';

interface Props {
  onChanged: () => void;
  /**
   * Whether this user holds vehicles.sync. It gates the destructive half of the screen only -
   * the switches are everyone's.
   */
  canManage: boolean;
}

/**
 * The sources screen: which ones feed this person's searches, and - for an administrator - the
 * actions that decide which exist at all.
 *
 * Two concerns on one page because they are about the same list of things, and a user asked to
 * think about sources should not have to find two screens. They are not the same in weight,
 * though: the switch is a private view preference that changes nothing for anyone else, while
 * sync and delete write the shared catalogue. So the switches need no permission and are shown
 * to everyone, and the buttons need vehicles.sync and appear only for Admin and Tenant Owner.
 *
 * A card each rather than list rows. A source carries five facts - name, code, size, freshness
 * and whether the last attempt failed - and on a list row they collapsed into one grey line of
 * text with the actions crowded against the right margin.
 */
export function MySourcesPage({ onChanged, canManage }: Props) {
  const { message } = AntApp.useApp();

  const [sources, setSources] = useState<MySource[]>([]);
  const [loading, setLoading] = useState(true);
  const [saving, setSaving] = useState<string | null>(null);
  const [syncing, setSyncing] = useState<string | null>(null);
  const [error, setError] = useState<string | null>(null);

  const refresh = useCallback(async (): Promise<void> => {
    try {
      setSources(await listMySources());
    } catch (e) {
      setError(e instanceof Error ? e.message : 'Could not load your sources.');
    } finally {
      setLoading(false);
    }
  }, []);

  useEffect(() => {
    void refresh();
  }, [refresh]);

  const toggle = async (source: MySource, isEnabled: boolean): Promise<void> => {
    setSaving(source.code);

    // Optimistic, because a switch that lags feels broken. Reverted below if the call fails.
    setSources((all) => all.map((s) => (s.code === source.code ? { ...s, isEnabled } : s)));

    try {
      await setMySource(source.code, isEnabled);
      onChanged();
    } catch (e) {
      setSources((all) => all.map((s) => (s.code === source.code ? { ...s, isEnabled: !isEnabled } : s)));
      message.error(e instanceof Error ? e.message : 'Could not save that change.');
    } finally {
      setSaving(null);
    }
  };

  const confirmDelete = (source: MySource): void => {
    Modal.confirm({
      title: `Delete ${source.name}?`,
      okText: 'Delete permanently',
      okButtonProps: { danger: true },
      width: 520,
      content: (
        <Space direction="vertical" size={8}>
          <Typography.Text>
            This removes the source, its {source.vehicleCount.toLocaleString()} listing(s) and
            its sync history — for everyone, not just you. To hide it from your own searches,
            use the switch instead.
          </Typography.Text>

          {/* Said plainly, because the alternative is someone discovering it afterwards. */}
          <Typography.Text>
            A car another source also lists is kept. Only cars nothing else offers are deleted,
            along with their photos and any prices you have set on them.
          </Typography.Text>

          <Typography.Text type="danger">This cannot be undone.</Typography.Text>
        </Space>
      ),
      onOk: async () => {
        const outcome = await deleteSource(source.code);

        message.success(
          `${source.code} deleted: ${outcome.listingsDeleted} listing(s) and `
          + `${outcome.vehiclesDeleted} vehicle(s) removed, `
          + `${outcome.vehiclesKept} kept because another source still lists them.`,
          10,
        );

        await refresh();
        onChanged();
      },
    });
  };

  const runSync = async (code: string, fetchDetail: boolean): Promise<void> => {
    setSyncing(code);

    try {
      const result = await syncSource(code, 2, fetchDetail);

      message.success(
        `${code}: ${result.created} created, ${result.updated} updated, `
        + `${result.autoMerged} merged, ${result.failed} failed — `
        + `${result.requestCount} request(s) in ${result.elapsedMs}ms. `
        + `${result.withoutStrongIdentifier} record(s) had no strong identifier.`,
        10,
      );

      await refresh();
      onChanged();
    } catch (e) {
      message.error(e instanceof Error ? e.message : 'Sync failed.', 8);
    } finally {
      setSyncing(null);
    }
  };

  const enabled = sources.filter((s) => s.isEnabled).length;
  const listings = sources
    .filter((s) => s.isEnabled)
    .reduce((sum, s) => sum + s.vehicleCount, 0);

  return (
    <Space direction="vertical" size={16} style={{ width: '100%' }}>
      <Flex justify="space-between" align="center" wrap gap={12}>
        <Typography.Title level={4} style={{ margin: 0 }}>My sources</Typography.Title>

        {!loading && sources.length > 0 && (
          // The consequence of the switches, in one line: what this person's searches can
          // currently see. It is the question the screen exists to answer.
          <Typography.Text type="secondary">
            <strong>{enabled}</strong> of {sources.length} on ·{' '}
            <strong>{listings.toLocaleString()}</strong> listing{listings === 1 ? '' : 's'} in
            your searches
          </Typography.Text>
        )}
      </Flex>

      <Card size="small" styles={{ body: { padding: 14 } }}>
        <Typography.Text type="secondary">
          Choose which sources appear in your searches. This affects only you — your colleagues
          keep their own choices, and nothing is removed from the catalogue. Sources are added
          by an administrator; a new one is on for everyone until you turn it off.
          {canManage && (
            <>
              {' '}As an administrator you can also sync or delete a source, which changes the
              catalogue <strong>for everyone</strong>.
            </>
          )}
        </Typography.Text>
      </Card>

      {error && <Alert type="error" showIcon message={error} />}

      {loading
        ? (
          <Row gutter={[16, 16]}>
            {Array.from({ length: 3 }, (_, i) => (
              <Col key={i} xs={24} md={12} xxl={8}>
                <Card><Skeleton active paragraph={{ rows: 2 }} /></Card>
              </Col>
            ))}
          </Row>
        )
        : sources.length === 0
          ? <Card><Empty description="No sources have been registered yet." /></Card>
          : (
            <Row gutter={[16, 16]}>
              {sources.map((s) => (
                <Col key={s.code} xs={24} md={12} xxl={8}>
                  <SourceCard
                    source={s}
                    canManage={canManage}
                    saving={saving === s.code}
                    syncing={syncing === s.code}
                    onToggle={(checked) => void toggle(s, checked)}
                    onSync={(detail) => void runSync(s.code, detail)}
                    onDelete={() => confirmDelete(s)}
                  />
                </Col>
              ))}
            </Row>
          )}

      {/* Turning everything off is allowed - it is your view - but an empty search screen with
          no explanation looks like a broken catalogue rather than a choice you made. */}
      {!loading && sources.length > 0 && enabled === 0 && (
        <Alert
          type="warning"
          showIcon
          message="Every source is switched off"
          description="Your searches will return nothing until you switch at least one back on."
        />
      )}
    </Space>
  );
}

function SourceCard({ source: s, canManage, saving, syncing, onToggle, onSync, onDelete }: {
  source: MySource;
  canManage: boolean;
  saving: boolean;
  syncing: boolean;
  onToggle: (checked: boolean) => void;
  onSync: (fetchDetail: boolean) => void;
  onDelete: () => void;
}) {
  return (
    <Card
      style={{
        height: '100%',
        // A source switched off is still readable, just visibly not in play. Hiding it would
        // make turning it back on impossible.
        opacity: s.isEnabled ? 1 : 0.62,
      }}
      styles={{ body: { padding: 18 } }}
    >
      <Flex vertical gap={14} style={{ height: '100%' }}>
        <Flex justify="space-between" align="flex-start" gap={12}>
          <Flex vertical gap={4} style={{ minWidth: 0 }}>
            <Typography.Text strong style={{ fontSize: 15 }} ellipsis={{ tooltip: s.name }}>
              {s.name}
            </Typography.Text>

            <Flex gap={6} wrap>
              <Tag style={{ marginInlineEnd: 0 }}>{s.code}</Tag>
              {!s.isShared && (
                <Tag color="blue" style={{ marginInlineEnd: 0 }}>private to your tenant</Tag>
              )}
            </Flex>
          </Flex>

          <Tooltip title={s.isEnabled ? 'Hide from your searches' : 'Show in your searches'}>
            <Switch checked={s.isEnabled} loading={saving} onChange={onToggle} />
          </Tooltip>
        </Flex>

        <Flex gap={24} wrap>
          <Flex vertical gap={1}>
            <Typography.Text type="secondary" style={{ fontSize: 11 }}>Listings</Typography.Text>
            <Typography.Text strong style={{ fontSize: 20, lineHeight: 1.2 }}>
              {s.vehicleCount.toLocaleString()}
            </Typography.Text>
          </Flex>

          <Flex vertical gap={1}>
            <Typography.Text type="secondary" style={{ fontSize: 11 }}>Last sync</Typography.Text>
            <Typography.Text style={{ fontSize: 13 }}>
              {s.lastSyncAtUtc ? formatUtc(s.lastSyncAtUtc) : 'never'}
            </Typography.Text>
          </Flex>
        </Flex>

        {/* A failed run is called a failure. Without this a source whose every attempt has
            failed is indistinguishable from one nobody has tried. */}
        {s.lastAttemptStatus === 'Failed' && (
          <Alert
            type="error"
            showIcon
            style={{ padding: '6px 10px' }}
            message={
              <Typography.Text style={{ fontSize: 12 }}>Last sync attempt failed</Typography.Text>
            }
          />
        )}

        {!s.isEnabled && (
          <Typography.Text type="secondary" style={{ fontSize: 12 }}>
            Hidden from your searches.
          </Typography.Text>
        )}

        {canManage && (
          <Flex
            gap={8}
            wrap
            style={{
              marginTop: 'auto',
              paddingTop: 12,
              borderTop: '1px solid var(--app-stroke)',
            }}
          >
            <Button size="small" loading={syncing} onClick={() => onSync(false)}>Sync</Button>

            {/* The expensive path, labelled as such: one request per vehicle instead of one
                per page, in exchange for VINs and source prices. */}
            <Tooltip title="Fetches each vehicle's detail record. Costs one request per vehicle, and is what makes deduplication and pricing work.">
              <Button size="small" loading={syncing} onClick={() => onSync(true)}>
                Sync + detail
              </Button>
            </Tooltip>

            <Button size="small" danger onClick={onDelete} style={{ marginInlineStart: 'auto' }}>
              Delete
            </Button>
          </Flex>
        )}
      </Flex>
    </Card>
  );
}
