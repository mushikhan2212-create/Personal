import { useCallback, useEffect, useState } from 'react';
import {
  Alert, App as AntApp, Avatar, Button, Card, Collapse, Drawer, Empty, Flex, Form,
  Input, InputNumber, Pagination, Select, Space, Spin, Tag, Typography,
} from 'antd';
import {
  addRequirement, deleteCustomer, deleteRequirement, getCustomer, getMatches,
} from '../api/client';
import type {
  CustomerDetail, Requirement, RequirementInput, RequirementMatches,
} from '../api/types';
import { VehicleCards } from '../components/VehicleCards';
import { WhatsAppButton } from '../components/WhatsAppButton';
import { WhatsAppDrawer } from '../components/WhatsAppDrawer';
import { PlusGlyph, TrashGlyph } from '../components/icons';
import { formatUtc, specLabel } from '../format';

interface Props {
  publicId: string;
  canManage: boolean;
  onBack: () => void;
  onOpenVehicle: (id: string) => void;
}

/** Matches per page. A requirement that fits 46 cars should not render 46 cards at once. */
const PAGE_SIZE = 12;

/** Customer states worth a colour. The rest read better plain. */
const CUSTOMER_STATUS_COLOUR: Record<string, string | undefined> = {
  Unknown: undefined,
  Lead: 'blue',
  Active: 'green',
  Customer: 'green',
  Dormant: undefined,
  Closed: undefined,
};

export function CustomerDetailPage({ publicId, canManage, onBack, onOpenVehicle }: Props) {
  const { message, modal } = AntApp.useApp();

  const [customer, setCustomer] = useState<CustomerDetail | null>(null);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);
  const [drawerOpen, setDrawerOpen] = useState(false);
  const [messaging, setMessaging] = useState(false);
  const [messagingVehicle, setMessagingVehicle] = useState<string | null>(null);
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
    const name = [customer?.firstName, customer?.lastName].filter(Boolean).join(' ')
      || 'this customer';

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
  const where = [customer.city, customer.countryCode].filter(Boolean).join(', ');
  const initials = (customer.firstName?.[0] ?? '') + (customer.lastName?.[0] ?? '');
  const open = customer.requirements.filter((r) => r.status === 'Open').length;

  return (
    <Space direction="vertical" size={16} style={{ width: '100%' }}>
      <Button type="text" size="small" onClick={onBack} style={{ paddingInline: 4 }}>
        ← Back to customers
      </Button>

      {/*
        One identity card rather than a "Details" panel of label/value rows.
        A customer is a person, and the things you need before picking up the phone - who they
        are, where they are, how to reach them, how warm they are - belong together at the top
        where they can be read in one look. The previous layout put six labelled rows in a side
        column, which is a database view of a person.
      */}
      <Card>
        <Flex justify="space-between" align="flex-start" wrap gap={20}>
          <Flex gap={18} align="center" wrap>
            <Avatar
              size={72}
              style={{ backgroundColor: '#3C50E0', fontSize: 26, flexShrink: 0 }}
            >
              {initials.toUpperCase() || '?'}
            </Avatar>

            <Flex vertical gap={8}>
              <Flex align="center" gap={10} wrap>
                <Typography.Title level={4} style={{ margin: 0 }}>{name}</Typography.Title>

                <Tag
                  color={CUSTOMER_STATUS_COLOUR[customer.status]}
                  style={{ marginInlineEnd: 0 }}
                >
                  {customer.status}
                </Tag>

                {customer.leadSource !== 'Unknown' && (
                  <Tag style={{ marginInlineEnd: 0 }}>via {customer.leadSource}</Tag>
                )}
              </Flex>

              <Flex gap={20} wrap>
                <Contact label="Phone" value={customer.phone} href={customer.phone
                  ? `tel:${customer.phone.replace(/\s/g, '')}` : null}
                />
                <Contact label="Email" value={customer.email} href={customer.email
                  ? `mailto:${customer.email}` : null}
                />
                <Contact label="Location" value={where || null} href={null} />
                <Contact label="Language" value={customer.preferredLanguage} href={null} />
              </Flex>

              <Typography.Text type="secondary" style={{ fontSize: 12 }}>
                Added {formatUtc(customer.createdAtUtc)}
                {open > 0 && ` · ${open} open requirement${open === 1 ? '' : 's'}`}
              </Typography.Text>
            </Flex>
          </Flex>

          <Flex gap={8} wrap>
            {canManage && <WhatsAppButton onClick={() => setMessaging(true)} />}
            {canManage && (
              <Button danger icon={<TrashGlyph />} onClick={removeCustomer}>Delete</Button>
            )}
          </Flex>
        </Flex>

        {customer.notes && (
          <div
            style={{
              marginTop: 18,
              paddingTop: 16,
              borderTop: '1px solid var(--app-stroke)',
            }}
          >
            <Typography.Text type="secondary" style={{ fontSize: 12 }}>Notes</Typography.Text>
            <Typography.Paragraph style={{ marginTop: 4, marginBottom: 0 }}>
              {customer.notes}
            </Typography.Paragraph>
          </div>
        )}
      </Card>

      <Card
        title={`Looking for (${customer.requirements.length})`}
        extra={canManage && (
          <Button type="primary" icon={<PlusGlyph />} onClick={() => setDrawerOpen(true)}>
            Add requirement
          </Button>
        )}
        styles={{ body: { padding: customer.requirements.length === 0 ? 24 : 12 } }}
      >
        {customer.requirements.length === 0
          ? <Empty description="Nothing recorded yet." image={Empty.PRESENTED_IMAGE_SIMPLE} />
          : (
            <Collapse
              accordion
              // Bordered panels rather than the ghost variant: each requirement is a separate
              // thing with its own matches, and a flat list of headings made two requirements
              // look like one with a subheading.
              items={customer.requirements.map((r) => ({
                key: String(r.id),
                style: { marginBottom: 8, borderRadius: 8, overflow: 'hidden' },
                label: (
                  <Flex vertical gap={2} style={{ minWidth: 0 }}>
                    <Typography.Text strong>
                      {r.name ?? ([r.make, r.model].filter(Boolean).join(' ') || 'Requirement')}
                    </Typography.Text>
                    <Typography.Text type="secondary" style={{ fontSize: 12 }}>
                      {summarise(r)}
                    </Typography.Text>
                  </Flex>
                ),
                children: (
                  <RequirementMatchesPanel
                    publicId={publicId}
                    requirement={r}
                    canManage={canManage}
                    onOpenVehicle={onOpenVehicle}
                    onDelete={() => removeRequirement(r)}
                    onMessageVehicle={(id) => setMessagingVehicle(id)}
                  />
                ),
              }))}
            />
          )}
      </Card>

      <WhatsAppDrawer
        open={messaging || messagingVehicle !== null}
        onClose={() => { setMessaging(false); setMessagingVehicle(null); }}
        customerPublicId={publicId}
        vehiclePublicId={messagingVehicle ?? undefined}
        customerName={name}
      />

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
                options={['Petrol', 'Diesel', 'Hybrid', 'Electric']
                  .map((v) => ({ value: v, label: specLabel(v) }))}
              />
            </Form.Item>
            <Form.Item name="transmission" label="Gearbox" style={{ flex: 1 }}>
              <Select
                allowClear
                placeholder="Any"
                options={['Automatic', 'Manual']
                  .map((v) => ({ value: v, label: specLabel(v) }))}
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

/** One contact fact, as a link where following it is the obvious next action. */
function Contact({ label, value, href }: {
  label: string;
  value: string | null;
  href: string | null;
}) {
  return (
    <Flex vertical gap={1}>
      <Typography.Text type="secondary" style={{ fontSize: 11 }}>{label}</Typography.Text>

      {value === null
        ? <Typography.Text type="secondary">—</Typography.Text>
        : href
          ? <Typography.Link href={href}>{value}</Typography.Link>
          : <Typography.Text>{value}</Typography.Text>}
    </Flex>
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
  publicId, requirement, canManage, onOpenVehicle, onDelete, onMessageVehicle,
}: {
  publicId: string;
  requirement: Requirement;
  canManage: boolean;
  onOpenVehicle: (id: string) => void;
  onDelete: () => void;
  onMessageVehicle: (vehiclePublicId: string) => void;
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
          <Button size="small" danger icon={<TrashGlyph />} onClick={onDelete}>Delete</Button>
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
          {matches.totalCount === 1 ? ' car fits' : ' cars fit'} · sorted by price, cheapest
          first · {matches.elapsedMilliseconds} ms
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
            <VehicleCards
              items={matches?.items ?? []}
              loading={loading}
              onOpen={onOpenVehicle}
              // The customer is known here, so sending one of these to them is one click.
              onMessage={canManage ? onMessageVehicle : undefined}
            />

            {/* Without this the panel said "46 cars fit" and showed the cheapest handful, with
                nothing on screen admitting the rest existed. */}
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

/**
 * A one-line rendering of what the requirement asks for, under the collapsed header.
 *
 * The car itself is named in the heading above this line, so it is repeated here only when the
 * requirement has been given a name of its own and the heading shows that instead. Otherwise
 * the panel read "Toyota Corolla" twice, once in bold and once immediately below it in grey.
 *
 * Every criterion the requirement can carry appears, because this line is what a salesperson
 * scans to decide whether the match list below it is the one they meant. Leaving transmission
 * out made a requirement for a CVT Corolla read identically to one for a manual - two
 * different requirements, one summary, and no way to tell from the panel which was which.
 */
function summarise(r: Requirement): string {
  const car = [r.make, r.model, r.variant].filter(Boolean).join(' ');

  const parts = [
    // Only when the heading is showing the requirement's own name rather than the car.
    r.name ? car : null,
    r.bodyType,
    r.minYear && r.maxYear ? `${r.minYear}–${r.maxYear}`
      : r.minYear ? `${r.minYear}+`
        : r.maxYear ? `up to ${r.maxYear}` : null,
    mileage(r.minMileage, r.maxMileage),
    r.fuelType ? specLabel(r.fuelType) : null,
    r.transmission ? specLabel(r.transmission) : null,
    price(r.minPrice, r.maxPrice, r.currencyCode),

    // Colour and destination are recorded on the requirement but never applied to the match -
    // the server's own `matchedOn` omits the first and marks the second "not filtered". Listing
    // them here would put a criterion in the summary that the chips directly below it say was
    // not used, which is worse than leaving them out.
  ].filter(Boolean);

  return parts.length > 0 ? parts.join(' · ') : 'anything';
}

/** A mileage range, however many of its two ends were given. */
function mileage(min: number | null, max: number | null): string | null {
  if (min && max) return `${min.toLocaleString()}–${max.toLocaleString()} km`;
  if (max) return `under ${max.toLocaleString()} km`;
  if (min) return `over ${min.toLocaleString()} km`;

  return null;
}

/**
 * A price range, with its currency.
 *
 * The currency is stated rather than assumed: this dealer's customers are in several countries
 * and a bare "up to 7,000" against a catalogue quoted in yen is a number nobody can act on.
 */
function price(min: number | null, max: number | null, currency: string | null): string | null {
  const unit = currency ? ` ${currency}` : '';

  if (min && max) return `${min.toLocaleString()}–${max.toLocaleString()}${unit}`;
  if (max) return `up to ${max.toLocaleString()}${unit}`;
  if (min) return `from ${min.toLocaleString()}${unit}`;

  return null;
}
