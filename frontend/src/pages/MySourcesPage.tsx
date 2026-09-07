import { useCallback, useEffect, useState } from 'react';
import {
  Alert, Card, List, Modal, Space, Spin, Switch, Tag, Typography, Button, App as AntApp,
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

  return (
    <Space direction="vertical" size={16} style={{ width: '100%', maxWidth: 820 }}>
      <Typography.Title level={4} style={{ margin: 0 }}>My sources</Typography.Title>

      <Typography.Paragraph type="secondary" style={{ marginBottom: 0 }}>
        Choose which sources appear in your searches. This affects only you — your colleagues
        keep their own choices, and nothing is removed from the catalogue. Sources are added by
        an administrator; a new one is on for everyone until you turn it off.
      </Typography.Paragraph>

      {canManage && (
        <Typography.Paragraph type="secondary" style={{ marginBottom: 0 }}>
          As an administrator you can also sync a source or delete it. Those change the
          catalogue <strong>for everyone</strong> — the switch above only changes your own view.
        </Typography.Paragraph>
      )}

      {error && <Alert type="error" showIcon message={error} />}

      <Spin spinning={loading}>
        <Card size="small">
          <List
            dataSource={sources}
            locale={{ emptyText: 'No sources have been registered yet.' }}
            renderItem={(s) => (
              <List.Item
                actions={[
                  ...(canManage
                    ? [
                        <Button
                          key="sync"
                          size="small"
                          loading={syncing === s.code}
                          onClick={() => void runSync(s.code, false)}
                        >
                          Sync
                        </Button>,

                        // The expensive path, labelled as such: one request per vehicle
                        // instead of one per page, in exchange for VINs and source prices.
                        <Button
                          key="sync-detail"
                          size="small"
                          loading={syncing === s.code}
                          onClick={() => void runSync(s.code, true)}
                          title="Fetches each vehicle's detail record. Costs one request per vehicle, and is what makes deduplication and pricing work."
                        >
                          Sync + detail
                        </Button>,

                        <Button key="delete" size="small" danger onClick={() => confirmDelete(s)}>
                          Delete
                        </Button>,
                      ]
                    : []),
                  <Switch
                    key="toggle"
                    checked={s.isEnabled}
                    loading={saving === s.code}
                    onChange={(checked) => void toggle(s, checked)}
                  />,
                ]}
              >
                <List.Item.Meta
                  title={
                    <Space>
                      <Typography.Text strong={s.isEnabled} type={s.isEnabled ? undefined : 'secondary'}>
                        {s.name}
                      </Typography.Text>
                      <Tag>{s.code}</Tag>
                      {!s.isShared && <Tag color="blue">private to your tenant</Tag>}
                    </Space>
                  }
                  description={
                    <Space direction="vertical" size={0}>
                      <Typography.Text type="secondary" style={{ fontSize: 12 }}>
                        {s.vehicleCount.toLocaleString()} listing(s)
                        {s.lastSyncAtUtc && ` · last sync ${formatUtc(s.lastSyncAtUtc)}`}
                        {!s.isEnabled && ' — hidden from your searches'}
                      </Typography.Text>

                      {/* A failed run is called a failure. Without this a source whose every
                          attempt has failed is indistinguishable from one nobody has tried. */}
                      {s.lastAttemptStatus === 'Failed' && (
                        <Typography.Text type="danger" style={{ fontSize: 12 }}>
                          Last sync attempt failed
                        </Typography.Text>
                      )}
                    </Space>
                  }
                />
              </List.Item>
            )}
          />
        </Card>
      </Spin>

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
