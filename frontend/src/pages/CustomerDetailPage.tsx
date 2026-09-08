import { useCallback, useEffect, useState } from 'react';
import {
  Alert, App as AntApp, Button, Card, Collapse, Descriptions, Drawer, Empty, Flex, Form,
  Input, InputNumber, Pagination, Select, Space, Spin, Tag, Typography,
} from 'antd';
import {
  addRequirement, deleteCustomer, deleteRequirement, getCustomer, getMatches,
} from '../api/client';
import type {
  CustomerDetail, Requirement, RequirementInput, RequirementMatches,
} from '../api/types';
import { VehicleTable } from '../components/VehicleTable';
import { formatUtc } from '../format';

interface Props {
  publicId: string;
  canManage: boolean;
  onBack: () => void;
  onOpenVehicle: (id: string) => void;
}

/** Matches per page. Enough that scrolling beats paging for most requirements. */
const PAGE_SIZE = 25;

const STATUS_COLOUR: Record<string, string | undefined> = {
  Open: 'blue',
  OnHold: undefined,
  Fulfilled: 'green',
  Cancelled: undefined,
};

export function CustomerDetailPage({ publicId, canManage, onBack, onOpenVehicle }: Props) {
  const { message, modal } = AntApp.useApp();

  const [customer, setCustomer] = useState<CustomerDetail | null>(null);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);
  const [drawerOpen, setDrawerOpen] = useState(false);
  const [saving, setSaving] = useState(false);
  const [form] = Form.useForm<RequirementInput>();

  const load = useCallback(async (): Promise<void> => {
    setLoading(true);

    try {
      setCustomer(await getCustomer(publicId));
      setError(null);
    } catch (e) {
      setError(e instanceof Error ? e.message : 'Could not load this customer.');
    } finally {
      setLoading(false);
    }
  }, [publicId]);

  useEffect(() => {
    void load();
  }, [load]);

  const addOne = async (): Promise<void> => {
    setSaving(true);

    try {
      await addRequirement(publicId, await form.validateFields());
      setDrawerOpen(false);
      form.resetFields();
      await load();
      void message.success('Requirement added.');
    } catch (e) {
      if (e instanceof Error) message.error(e.message);
    } finally {
      setSaving(false);
    }
  };

  const removeRequirement = (r: Requirement): void => {
    modal.confirm({
      title: `Delete "${r.name ?? 'this requirement'}"?`,
      okText: 'Delete',
      okButtonProps: { danger: true },
      onOk: async () => {
        await deleteRequirement(publicId, r.id);
        await load();
      },
    });
  };

  const removeCustomer = (): void => {
    const name = [customer?.firstName, customer?.lastName].filter(Boolean).join(' ') || 'this customer';

    modal.confirm({
      title: `Delete ${name}?`,
      okText: 'Delete permanently',
      okButtonProps: { danger: true },
      width: 480,
      content: (
        <Space direction="vertical" size={8}>
          <Typography.Text>
            This removes the customer and every requirement recorded against them.
          </Typography.Text>

          {/* Said plainly because it is unusual, and deliberate: there is no soft delete, so
              that "we deleted it" stays true on the day someone asks. */}
          <Typography.Text type="danger">
            There is no undo and no archive — the record is gone.
          </Typography.Text>
        </Space>
      ),
      onOk: async () => {
        await deleteCustomer(publicId);
        void message.success(`${name} deleted.`);
        onBack();
      },
    });
  };

  if (loading && !customer) {
    return <Spin style={{ margin: 48 }} />;
  }

  if (error) {
    return (
      <Space direction="vertical" size={16} style={{ width: '100%' }}>
        <Alert type="error" showIcon message={error} />
        <Button onClick={onBack}>Back to customers</Button>
      </Space>
    );
  }

  if (!customer) return null;

  const name = [customer.firstName, customer.lastName].filter(Boolean).join(' ') || '(no name)';

  return (
    <Space direction="vertical" size={16} style={{ width: '100%' }}>
      <Flex justify="space-between" align="flex-start" wrap gap={12}>
        <Flex vertical gap={2}>
          <Typography.Title level={4} style={{ margin: 0 }}>{name}</Typography.Title>
          <Typography.Text type="secondary">
            {[customer.city, customer.countryCode].filter(Boolean).join(', ') || '—'}
          </Typography.Text>
        </Flex>

        <Space>
          <Button onClick={onBack}>Back to customers</Button>
          {canManage && <Button danger onClick={removeCustomer}>Delete</Button>}
        </Space>
      </Flex>

      {/* Details across the top rather than in a side column, so the match table below gets
          the whole width. Beside it the table fell under the 900px it needs and every vehicle
          name ellipsed to "TOYOTA Corolla …" - twenty-five identical-looking rows is not a
          shortlist a salesperson can work from. */}
      <Card size="small" title="Details">
        <Descriptions column={{ xs: 1, sm: 2, lg: 3, xl: 6 }} size="small">
          <Descriptions.Item label="Phone">{customer.phone ?? '—'}</Descriptions.Item>
          <Descriptions.Item label="Email">{customer.email ?? '—'}</Descriptions.Item>
          <Descriptions.Item label="Language">
            {customer.preferredLanguage ?? '—'}
          </Descriptions.Item>
          <Descriptions.Item label="Status">
            <Tag style={{ marginInlineEnd: 0 }}>{customer.status}</Tag>
          </Descriptions.Item>
          <Descriptions.Item label="Came from">{customer.leadSource}</Descriptions.Item>
          <Descriptions.Item label="Added">
            {formatUtc(customer.createdAtUtc)}
          </Descriptions.Item>
        </Descriptions>

        {customer.notes && (
          <>
            <Typography.Text type="secondary" style={{ fontSize: 12 }}>Notes</Typography.Text>
            <Typography.Paragraph style={{ marginTop: 4, marginBottom: 0 }}>
              {customer.notes}
            </Typography.Paragraph>
          </>
        )}
      </Card>

      <Card
        size="small"
        title={`Looking for (${customer.requirements.length})`}
        extra={canManage && (
          <Button size="small" type="primary" onClick={() => setDrawerOpen(true)}>
            Add requirement
          </Button>
        )}
        styles={{ body: { padding: customer.requirements.length === 0 ? 24 : 0 } }}
      >
        {customer.requirements.length === 0
          ? <Empty description="Nothing recorded yet." image={Empty.PRESENTED_IMAGE_SIMPLE} />
          : (
            <Collapse
              ghost
              items={customer.requirements.map((r) => ({
                key: String(r.id),
                label: (
                  <Flex justify="space-between" align="center" gap={12}>
                    <Flex vertical gap={2} style={{ minWidth: 0 }}>
                      <Typography.Text strong>
                        {r.name ?? ([r.make, r.model].filter(Boolean).join(' ') || 'Requirement')}
                      </Typography.Text>
                      <Typography.Text type="secondary" style={{ fontSize: 12 }}>
                        {summarise(r)}
                      </Typography.Text>
                    </Flex>

                    <Tag color={STATUS_COLOUR[r.status]} style={{ marginInlineEnd: 0 }}>
                      {r.status}
                    </Tag>
                  </Flex>
                ),
                children: (
                  <RequirementMatchesPanel
                    publicId={publicId}
                    requirement={r}
                    canManage={canManage}
                    onOpenVehicle={onOpenVehicle}
                    onDelete={() => removeRequirement(r)}
                  />
                ),
              }))}
            />
          )}
      </Card>

      <Drawer
        title="What are they looking for?"
        open={drawerOpen}
        onClose={() => setDrawerOpen(false)}
        width={380}
        footer={
          <Flex gap={8} justify="flex-end">
            <Button onClick={() => setDrawerOpen(false)}>Cancel</Button>
            <Button type="primary" loading={saving} onClick={() => void addOne()}>Save</Button>
          </Flex>
        }
      >
        <Form form={form} layout="vertical">
          <Form.Item name="name" label="Call it" help="How you refer to it: “Hiace for the shop”.">
            <Input />
          </Form.Item>

          <Flex gap={12} style={{ marginTop: 16 }}>
            <Form.Item name="make" label="Make" style={{ flex: 1 }}>
              <Input placeholder="Toyota" />
            </Form.Item>
            <Form.Item name="model" label="Model" style={{ flex: 1 }}>
              <Input placeholder="Corolla" />
            </Form.Item>
          </Flex>

          <Flex gap={12}>
            <Form.Item name="minYear" label="Year from" style={{ flex: 1 }}>
              <InputNumber style={{ width: '100%' }} min={1950} max={2100} />
            </Form.Item>
            <Form.Item name="maxYear" label="Year to" style={{ flex: 1 }}>
              <InputNumber style={{ width: '100%' }} min={1950} max={2100} />
            </Form.Item>
          </Flex>

          <Form.Item name="maxMileage" label="Max mileage (km)">
            <InputNumber style={{ width: '100%' }} min={0} step={10_000} />
          </Form.Item>

          <Flex gap={12}>
            <Form.Item name="fuelType" label="Fuel" style={{ flex: 1 }}>
              <Select
                allowClear
                placeholder="Any"
                options={['Petrol', 'Diesel', 'Hybrid', 'Electric'].map((v) => ({ value: v, label: v }))}
              />
            </Form.Item>
            <Form.Item name="transmission" label="Gearbox" style={{ flex: 1 }}>
              <Select
                allowClear
                placeholder="Any"
                options={['Automatic', 'Manual'].map((v) => ({ value: v, label: v }))}
              />
            </Form.Item>
          </Flex>

          <Flex gap={12}>
            <Form.Item name="minPrice" label="Budget from" style={{ flex: 1 }}>
              <InputNumber style={{ width: '100%' }} min={0} step={1000} />
            </Form.Item>
            <Form.Item name="maxPrice" label="Budget to" style={{ flex: 1 }}>
              <InputNumber style={{ width: '100%' }} min={0} step={1000} />
            </Form.Item>
          </Flex>

          <Form.Item name="destinationCountryCode" label="Destination">
            <Input placeholder="PK" maxLength={2} />
          </Form.Item>

          <Form.Item name="rawRequirementText" label="What they actually said">
            <Input.TextArea rows={3} />
          </Form.Item>

          <Typography.Text type="secondary" style={{ fontSize: 12 }}>
            A budget filters on the converted base price, so a listing whose currency has no
            exchange rate is left out rather than converted at a guess. Destination is recorded
            but not filtered — the eligibility rules per market do not exist yet.
          </Typography.Text>
        </Form>
      </Drawer>
    </Space>
  );
}

/**
 * The stock that fits one requirement.
 *
 * Loaded when the panel is opened rather than with the customer: a customer with six
 * requirements would otherwise fire six catalogue searches to render a page where most of them
 * are collapsed.
 */
function RequirementMatchesPanel({
  publicId, requirement, canManage, onOpenVehicle, onDelete,
}: {
  publicId: string;
  requirement: Requirement;
  canManage: boolean;
  onOpenVehicle: (id: string) => void;
  onDelete: () => void;
}) {
  const [matches, setMatches] = useState<RequirementMatches | null>(null);
  const [page, setPage] = useState(1);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    let cancelled = false;

    setLoading(true);

    getMatches(publicId, requirement.id, page, PAGE_SIZE)
      .then((result) => { if (!cancelled) setMatches(result); })
      .catch((e: unknown) => {
        if (!cancelled) setError(e instanceof Error ? e.message : 'Could not load matches.');
      })
      .finally(() => { if (!cancelled) setLoading(false); });

    return () => { cancelled = true; };
  }, [publicId, requirement.id, page]);

  return (
    <Space direction="vertical" size={12} style={{ width: '100%' }}>
      <Flex justify="space-between" align="center" wrap gap={8}>
        <Space size={4} wrap>
          {/* The server's own account of what it applied. Shown because a disappointing result
              is nearly always an over-tight requirement rather than an empty catalogue. */}
          {matches?.matchedOn.map((m) => (
            <Tag key={m} style={{ marginInlineEnd: 0, fontSize: 11 }}>{m}</Tag>
          ))}
          {matches?.matchedOn.length === 0 && (
            <Typography.Text type="secondary" style={{ fontSize: 12 }}>
              No criteria — this matches the whole catalogue.
            </Typography.Text>
          )}
        </Space>

        {canManage && (
          <Button size="small" danger type="text" onClick={onDelete}>Delete</Button>
        )}
      </Flex>

      {requirement.rawRequirementText && (
        <Typography.Text type="secondary" style={{ fontSize: 12, fontStyle: 'italic' }}>
          “{requirement.rawRequirementText}”
        </Typography.Text>
      )}

      {error && <Alert type="error" showIcon message={error} />}

      {matches && (
        <Typography.Text type="secondary" style={{ fontSize: 12 }}>
          <strong>{matches.totalCount.toLocaleString()}</strong>
          {matches.totalCount === 1 ? ' car fits' : ' cars fit'} · {matches.elapsedMilliseconds} ms
        </Typography.Text>
      )}

      {matches && matches.items.length === 0 && !loading
        ? (
          <Empty
            image={Empty.PRESENTED_IMAGE_SIMPLE}
            description="Nothing in stock fits this yet."
          />
        )
        : (
          <>
            <VehicleTable
              items={matches?.items ?? []}
              loading={loading}
              sort="PriceAscending"
              // Sorting a match list would mean re-querying with a different order, which is a
              // vehicle-screen concern. Here the cheapest fit first is the answer.
              onSortChange={() => undefined}
              onOpen={onOpenVehicle}
            />

            {/* VehicleTable draws no pager of its own - the search screen supplies one below
                it - so without this the panel said "46 cars fit" and showed the cheapest 25,
                with nothing on screen admitting the other 21 existed. */}
            {(matches?.totalCount ?? 0) > PAGE_SIZE && (
              <Flex justify="flex-end">
                <Pagination
                  size="small"
                  current={page}
                  total={matches?.totalCount ?? 0}
                  pageSize={PAGE_SIZE}
                  showSizeChanger={false}
                  onChange={setPage}
                />
              </Flex>
            )}
          </>
        )}
    </Space>
  );
}

/** A one-line rendering of what the requirement asks for, for the collapsed header. */
function summarise(r: Requirement): string {
  const parts = [
    [r.make, r.model].filter(Boolean).join(' '),
    r.minYear && r.maxYear ? `${r.minYear}–${r.maxYear}`
      : r.minYear ? `${r.minYear}+`
        : r.maxYear ? `up to ${r.maxYear}` : null,
    r.maxMileage ? `under ${r.maxMileage.toLocaleString()} km` : null,
    r.fuelType,
    r.maxPrice ? `up to ${r.maxPrice.toLocaleString()}` : null,
  ].filter(Boolean);

  return parts.length > 0 ? parts.join(' · ') : 'anything';
}
