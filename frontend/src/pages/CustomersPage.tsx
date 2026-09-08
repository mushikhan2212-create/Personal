import { useCallback, useEffect, useState } from 'react';
import {
  Alert, App as AntApp, Badge, Button, Card, Drawer, Flex, Form, Input, Select, Space, Table,
  Tag, Typography,
} from 'antd';
import type { ColumnsType } from 'antd/es/table';
import { createCustomer, listCustomers } from '../api/client';
import type { CustomerInput, CustomerListItem, CustomerStatus } from '../api/types';
import { formatUtc } from '../format';

interface Props {
  canManage: boolean;
  onOpenCustomer: (publicId: string) => void;
}

const PAGE_SIZE = 25;

/** Colour carries meaning here, so it is spent only where the status is actionable. */
const STATUS_COLOUR: Record<CustomerStatus, string | undefined> = {
  Unknown: undefined,
  Lead: 'blue',
  Active: 'green',
  Customer: 'green',
  Dormant: undefined,
  Closed: undefined,
};

const STATUSES: CustomerStatus[] = ['Lead', 'Active', 'Customer', 'Dormant', 'Closed'];

const LEAD_SOURCES = [
  'WalkIn', 'Referral', 'Website', 'WhatsApp', 'SocialMedia', 'Marketplace', 'Repeat',
] as const;

export function CustomersPage({ canManage, onOpenCustomer }: Props) {
  const { message } = AntApp.useApp();

  const [items, setItems] = useState<CustomerListItem[]>([]);
  const [total, setTotal] = useState(0);
  const [page, setPage] = useState(1);
  const [query, setQuery] = useState('');
  const [status, setStatus] = useState<CustomerStatus | undefined>();
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);
  const [drawerOpen, setDrawerOpen] = useState(false);
  const [saving, setSaving] = useState(false);
  const [form] = Form.useForm<CustomerInput>();

  const load = useCallback(async (
    nextPage: number, q: string, s: CustomerStatus | undefined,
  ): Promise<void> => {
    setLoading(true);
    setError(null);

    try {
      const result = await listCustomers(q, s, nextPage, PAGE_SIZE);
      setItems(result.items);
      setTotal(result.totalCount);
    } catch (e) {
      setError(e instanceof Error ? e.message : 'Could not load customers.');
    } finally {
      setLoading(false);
    }
  }, []);

  useEffect(() => {
    void load(1, '', undefined);
  }, [load]);

  const submit = async (): Promise<void> => {
    setSaving(true);

    try {
      const created = await createCustomer(await form.validateFields());
      setDrawerOpen(false);
      form.resetFields();
      void message.success('Customer added.');

      // Straight into the detail view: the next thing anyone does after adding a customer is
      // record what they are looking for.
      onOpenCustomer(created.publicId);
    } catch (e) {
      if (e instanceof Error) message.error(e.message);
    } finally {
      setSaving(false);
    }
  };

  const columns: ColumnsType<CustomerListItem> = [
    {
      title: 'Name',
      key: 'name',
      render: (_, c) => {
        const name = [c.firstName, c.lastName].filter(Boolean).join(' ');

        return (
          <Flex vertical gap={2}>
            <Typography.Text strong>{name || '(no name)'}</Typography.Text>
            <Typography.Text type="secondary" style={{ fontSize: 12 }}>
              {[c.city, c.countryCode].filter(Boolean).join(', ') || '—'}
            </Typography.Text>
          </Flex>
        );
      },
    },
    {
      title: 'Contact',
      key: 'contact',
      width: 240,
      render: (_, c) => (
        <Flex vertical gap={2}>
          <Typography.Text style={{ fontSize: 13 }}>{c.phone ?? '—'}</Typography.Text>
          <Typography.Text type="secondary" style={{ fontSize: 12 }} ellipsis>
            {c.email ?? ''}
          </Typography.Text>
        </Flex>
      ),
    },
    {
      title: 'Status',
      key: 'status',
      width: 120,
      render: (_, c) => (
        <Tag color={STATUS_COLOUR[c.status]} style={{ marginInlineEnd: 0 }}>{c.status}</Tag>
      ),
    },
    {
      title: 'Looking for',
      key: 'requirements',
      width: 130,
      align: 'right',
      render: (_, c) => (c.openRequirements === 0
        ? <Typography.Text type="secondary" style={{ fontSize: 12 }}>nothing open</Typography.Text>
        : <Badge count={c.openRequirements} color="#2563eb" />),
    },
    {
      title: 'Updated',
      key: 'updated',
      width: 170,
      align: 'right',
      render: (_, c) => (
        <Typography.Text type="secondary" style={{ fontSize: 12 }}>
          {formatUtc(c.updatedAtUtc)}
        </Typography.Text>
      ),
    },
  ];

  return (
    <Space direction="vertical" size={16} style={{ width: '100%' }}>
      <Flex justify="space-between" align="center" wrap gap={12}>
        <Typography.Title level={4} style={{ margin: 0 }}>Customers</Typography.Title>

        <Typography.Text type="secondary">
          <strong>{total.toLocaleString()}</strong>{total === 1 ? ' customer' : ' customers'}
        </Typography.Text>
      </Flex>

      <Card size="small" styles={{ body: { padding: 12 } }}>
        <Flex gap={8} wrap>
          <Input.Search
            placeholder="Name, phone or email"
            value={query}
            onChange={(e) => setQuery(e.target.value)}
            onSearch={() => { setPage(1); void load(1, query, status); }}
            loading={loading}
            allowClear
            style={{ flex: '1 1 260px', minWidth: 200 }}
          />

          <Select
            allowClear
            placeholder="Any status"
            value={status}
            onChange={(v) => { setStatus(v); setPage(1); void load(1, query, v); }}
            style={{ width: 160 }}
            options={STATUSES.map((s) => ({ value: s, label: s }))}
          />

          {canManage && (
            <Button type="primary" onClick={() => setDrawerOpen(true)}>Add customer</Button>
          )}
        </Flex>
      </Card>

      {error && <Alert type="error" showIcon message={error} />}

      <Card size="small" styles={{ body: { padding: 0 } }}>
        <Table<CustomerListItem>
          rowKey="publicId"
          columns={columns}
          dataSource={items}
          loading={loading}
          size="middle"
          locale={{
            emptyText: query || status
              ? 'No customers match that.'
              : 'No customers yet.',
          }}
          onRow={(c) => ({
            onClick: () => onOpenCustomer(c.publicId),
            style: { cursor: 'pointer' },
          })}
          pagination={total > PAGE_SIZE
            ? {
              current: page,
              total,
              pageSize: PAGE_SIZE,
              showSizeChanger: false,
              onChange: (p) => { setPage(p); void load(p, query, status); },
            }
            : false}
        />
      </Card>

      <Drawer
        title="Add customer"
        open={drawerOpen}
        onClose={() => setDrawerOpen(false)}
        width={380}
        footer={
          <Flex gap={8} justify="flex-end">
            <Button onClick={() => setDrawerOpen(false)}>Cancel</Button>
            <Button type="primary" loading={saving} onClick={() => void submit()}>Save</Button>
          </Flex>
        }
      >
        <Form form={form} layout="vertical">
          <Flex gap={12}>
            <Form.Item name="firstName" label="First name" style={{ flex: 1 }}>
              <Input />
            </Form.Item>
            <Form.Item name="lastName" label="Last name" style={{ flex: 1 }}>
              <Input />
            </Form.Item>
          </Flex>

          <Form.Item name="phone" label="Phone" help="Usually the WhatsApp number too.">
            <Input />
          </Form.Item>

          <Form.Item name="email" label="Email" style={{ marginTop: 16 }}>
            <Input />
          </Form.Item>

          <Flex gap={12}>
            <Form.Item name="city" label="City" style={{ flex: 1 }}>
              <Input />
            </Form.Item>
            <Form.Item name="countryCode" label="Country" style={{ width: 110 }}>
              <Input placeholder="PK" maxLength={2} />
            </Form.Item>
          </Flex>

          <Flex gap={12}>
            <Form.Item name="status" label="Status" initialValue="Lead" style={{ flex: 1 }}>
              <Select options={STATUSES.map((s) => ({ value: s, label: s }))} />
            </Form.Item>
            <Form.Item name="leadSource" label="Came from" style={{ flex: 1 }}>
              <Select
                allowClear
                placeholder="Unknown"
                options={LEAD_SOURCES.map((s) => ({ value: s, label: s }))}
              />
            </Form.Item>
          </Flex>

          <Form.Item name="notes" label="Notes">
            <Input.TextArea rows={3} />
          </Form.Item>

          {/* The API requires one of four. Said here so the 400 is never a surprise. */}
          <Typography.Text type="secondary" style={{ fontSize: 12 }}>
            A name or a way to contact them is required — everything else can follow.
          </Typography.Text>
        </Form>
      </Drawer>
    </Space>
  );
}
