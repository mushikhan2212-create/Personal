import { useEffect, useState } from 'react';
import {
  Alert, Card, Flex, List, Space, Spin, Switch, Tag, Typography, Button, App as AntApp,
} from 'antd';
import { listMySources, setMySource } from '../api/client';
import type { MySource } from '../api/types';

interface Props {
  onBack: () => void;
  onChanged: () => void;
}

/**
 * Which sources feed this person's searches.
 *
 * Deliberately separate from the admin Sources panel on the search screen. That one is about
 * what exists - registering, importing, deleting - and needs a permission. This one is about
 * what you personally want to look at, and needs none, because switching a source off here
 * changes nothing for anyone else.
 */
export function MySourcesPage({ onBack, onChanged }: Props) {
  const { message } = AntApp.useApp();

  const [sources, setSources] = useState<MySource[]>([]);
  const [loading, setLoading] = useState(true);
  const [saving, setSaving] = useState<string | null>(null);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    listMySources()
      .then(setSources)
      .catch((e: unknown) => setError(e instanceof Error ? e.message : 'Could not load your sources.'))
      .finally(() => setLoading(false));
  }, []);

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

  const enabled = sources.filter((s) => s.isEnabled).length;

  return (
    <Space direction="vertical" size={16} style={{ width: '100%', maxWidth: 820 }}>
      <Flex justify="space-between" align="center">
        <Typography.Title level={3} style={{ margin: 0 }}>My sources</Typography.Title>
        <Button onClick={onBack}>Back to search</Button>
      </Flex>

      <Typography.Paragraph type="secondary" style={{ marginBottom: 0 }}>
        Choose which sources appear in your searches. This affects only you — your colleagues
        keep their own choices, and nothing is removed from the catalogue. Sources are added by
        an administrator; a new one is on for everyone until you turn it off.
      </Typography.Paragraph>

      {error && <Alert type="error" showIcon message={error} />}

      <Spin spinning={loading}>
        <Card size="small">
          <List
            dataSource={sources}
            locale={{ emptyText: 'No sources have been registered yet.' }}
            renderItem={(s) => (
              <List.Item
                actions={[
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
                    <Typography.Text type="secondary" style={{ fontSize: 12 }}>
                      {s.vehicleCount.toLocaleString()} listing(s)
                      {!s.isEnabled && ' — hidden from your searches'}
                    </Typography.Text>
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
