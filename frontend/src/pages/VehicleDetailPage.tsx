import { useEffect, useState } from 'react';
import {
  Alert, App as AntApp, Button, Card, Col, Empty, Flex, Image, Input, Row, Space, Spin, Table,
  Tag, Tooltip, Typography,
} from 'antd';
import { getVehicle, setVehiclePricing } from '../api/client';
import { WhatsAppButton } from '../components/WhatsAppButton';
import { WhatsAppDrawer } from '../components/WhatsAppDrawer';
import type { CanonicalHashSource, VehicleDetail, VehicleDetailListing } from '../api/types';
import { STALE_AFTER_DAYS, ageInDays, formatUtc, specLabel } from '../format';

interface Props {
  id: string;
  onBack: () => void;
  /** Whether this user may message customers. The button is hidden rather than disabled. */
  canMessage: boolean;
  /** Whether this user may set the retail price. Hidden rather than disabled, likewise. */
  canPrice: boolean;
}

/** What deduplication matched this car on, in the words a person would use. */
const MATCHED_ON: Record<CanonicalHashSource, string> = {
  Unknown: 'nothing',
  Vin: 'its VIN',
  ChassisNumber: 'its chassis number',
  SourceLotNumber: 'its lot number within one source',
};

const money = (amount: number | null, currency: string | null): string => {
  if (amount === null) return '—';

  try {
    return new Intl.NumberFormat(undefined, {
      style: 'currency',
      currency: currency ?? 'USD',
      maximumFractionDigits: 0,
    }).format(amount);
  } catch {
    return `${amount.toLocaleString()} ${currency ?? ''}`.trim();
  }
};

/** Shown in place of a photo that will not load. Inline so it needs no network of its own. */
const MISSING_PHOTO = 'data:image/svg+xml;utf8,'
  + encodeURIComponent(
    '<svg xmlns="http://www.w3.org/2000/svg" width="640" height="360">'
    + '<rect width="640" height="360" fill="%23e2e8f0"/>'
    + '<text x="320" y="188" font-family="sans-serif" font-size="18" fill="%2394a3b8" '
    + 'text-anchor="middle">No photo</text></svg>',
  );

export function VehicleDetailPage({ id, onBack, canMessage, canPrice }: Props) {
  const [vehicle, setVehicle] = useState<VehicleDetail | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [loading, setLoading] = useState(true);
  const [messaging, setMessaging] = useState(false);

  useEffect(() => {
    setLoading(true);
    setError(null);

    getVehicle(id)
      .then(setVehicle)
      .catch((e: unknown) => setError(e instanceof Error ? e.message : 'Could not load vehicle.'))
      .finally(() => setLoading(false));
  }, [id]);

  if (loading) return <Spin style={{ display: 'block', margin: '64px auto' }} />;

  if (error || !vehicle) {
    return (
      <Space direction="vertical" style={{ width: '100%' }}>
        <Button onClick={onBack}>Back to search</Button>
        <Alert type="error" showIcon message={error ?? 'Vehicle not found.'} />
      </Space>
    );
  }

  const title = [vehicle.make, vehicle.model].filter(Boolean).join(' ') || 'Unidentified vehicle';
  const age = ageInDays(vehicle.listings[0]?.lastSeenAtUtc ?? null);

  // The cheapest offer is what the search screen showed, so it is what the price here has to
  // agree with. Anything else and the two screens contradict each other on the same car.
  const best = [...vehicle.listings]
    .filter((l) => l.price !== null)
    .sort((a, b) => (a.price ?? 0) - (b.price ?? 0))[0] ?? vehicle.listings[0];

  const columns = [
    {
      title: 'Source',
      key: 'source',
      render: (_: unknown, l: VehicleDetailListing) =>
        l.sourceUrl
          ? <a href={l.sourceUrl} target="_blank" rel="noreferrer noopener">{l.sourceName ?? 'listing'}</a>
          : (l.sourceName ?? '—'),
    },
    {
      title: 'Asking price',
      key: 'price',
      render: (_: unknown, l: VehicleDetailListing) => (
        <Typography.Text strong>{money(l.price, l.currencyCode)}</Typography.Text>
      ),
    },
    {
      title: 'In USD',
      key: 'base',
      render: (_: unknown, l: VehicleDetailListing) =>
        l.priceBaseCurrency === null
          ? (
            <Tooltip title="No exchange rate is stored for this currency, so this price cannot be compared or filtered on.">
              <Typography.Text type="secondary">not comparable</Typography.Text>
            </Tooltip>
          )
          : money(l.priceBaseCurrency, l.baseCurrencyCode),
    },
    {
      title: 'Incoterm',
      key: 'incoterm',
      render: (_: unknown, l: VehicleDetailListing) => (
        <Tag color={l.priceType === 'Unknown' ? 'default' : 'green'} style={{ marginInlineEnd: 0 }}>
          {specLabel(l.priceType)}
        </Tag>
      ),
    },
    {
      title: 'Port',
      key: 'port',
      render: (_: unknown, l: VehicleDetailListing) => l.portOfLoading ?? '—',
    },
    {
      title: 'Last confirmed',
      key: 'seen',
      align: 'right' as const,
      render: (_: unknown, l: VehicleDetailListing) => (
        <Typography.Text type="secondary" style={{ fontSize: 12 }}>
          {formatUtc(l.lastSeenAtUtc)}
        </Typography.Text>
      ),
    },
  ];

  return (
    <Space direction="vertical" size={16} style={{ width: '100%' }}>
      <Flex justify="space-between" align="center" wrap gap={12}>
        <Button type="text" size="small" onClick={onBack} style={{ paddingInline: 4 }}>
          ← Back to search
        </Button>

        {/* Here the car is known and the customer is not, so the drawer opens with a picker. */}
        {canMessage && (
          <WhatsAppButton onClick={() => setMessaging(true)}>Send to a customer</WhatsAppButton>
        )}
      </Flex>

      <WhatsAppDrawer
        open={messaging}
        onClose={() => setMessaging(false)}
        customerPublicId={null}
        vehiclePublicId={id}
      />

      {age !== null && age > STALE_AFTER_DAYS && (
        <Alert
          type="warning"
          showIcon
          message={`Last confirmed ${age} days ago`}
          description="No source has reported this car recently. It may already be sold."
        />
      )}

      {/*
        Photo and price side by side, which is the pair of facts anyone opens this screen for.
        The specification used to be a bordered definition list beside a wall of 160px
        thumbnails; both were legible and neither was what the page is about.
      */}
      <Row gutter={[16, 16]}>
        <Col xs={24} lg={14}>
          <Gallery urls={vehicle.imageUrls} alt={title} />
        </Col>

        <Col xs={24} lg={10}>
          <Card style={{ height: '100%' }}>
            <Flex vertical gap={16}>
              <Flex vertical gap={4}>
                <Typography.Title level={3} style={{ margin: 0 }}>{title}</Typography.Title>

                {vehicle.variant && (
                  <Typography.Text type="secondary">{vehicle.variant}</Typography.Text>
                )}
              </Flex>

              <Flex align="baseline" gap={10} wrap>
                <Typography.Text strong style={{ fontSize: 30, lineHeight: 1.1 }}>
                  {money(best?.price ?? null, best?.currencyCode ?? null)}
                </Typography.Text>

                <Tooltip
                  title={best?.priceType === 'Unknown'
                    ? 'The source did not state an incoterm, so this price is not comparable '
                      + 'with a quoted FOB or CIF price.'
                    : `Quoted ${specLabel(best?.priceType)}`}
                >
                  <Tag
                    color={best?.priceType === 'Unknown' ? 'default' : 'green'}
                    style={{ marginInlineEnd: 0 }}
                  >
                    {specLabel(best?.priceType)}
                  </Tag>
                </Tooltip>

                {vehicle.listings.length > 1 && (
                  <Tag color="blue" style={{ marginInlineEnd: 0 }}>
                    cheapest of {vehicle.listings.length}
                  </Tag>
                )}
              </Flex>

              <YourPrice
                vehicle={vehicle}
                canPrice={canPrice}
                onSaved={(price, currency) =>
                  setVehicle((v) =>
                    v === null ? v : { ...v, tenantPrice: price, tenantCurrencyCode: currency })}
              />

              <div style={{ borderTop: '1px solid var(--app-stroke)', paddingTop: 16 }}>
                <Row gutter={[12, 14]}>
                  <Spec label="Year" value={vehicle.year === null ? '—' : String(vehicle.year)} />
                  <Spec
                    label="Mileage"
                    value={vehicle.mileage === null
                      ? '—'
                      : `${vehicle.mileage.toLocaleString()} ${vehicle.mileageUnit === 'Miles' ? 'mi' : 'km'}`}
                  />
                  <Spec label="Steering" value={specLabel(vehicle.steeringSide)} />
                  <Spec label="Fuel" value={specLabel(vehicle.fuelType)} />
                  <Spec label="Transmission" value={specLabel(vehicle.transmission)} />
                  <Spec label="Drivetrain" value={specLabel(vehicle.drivetrain)} />
                  <Spec label="Body" value={vehicle.bodyType ?? '—'} />
                  <Spec
                    label="Engine"
                    value={vehicle.engineDisplacementCc ? `${vehicle.engineDisplacementCc} cc` : '—'}
                  />
                  <Spec label="Colour" value={vehicle.exteriorColor ?? '—'} />
                  <Spec label="Status" value={vehicle.status} />
                </Row>
              </div>
            </Flex>
          </Card>
        </Col>
      </Row>

      <Card title={`Offers (${vehicle.listings.length})`}>
        <Table
          rowKey={(l) => `${l.sourceName}-${l.externalListingId}`}
          dataSource={vehicle.listings}
          columns={columns}
          pagination={false}
          size="small"
          scroll={{ x: 700 }}
        />
      </Card>

      {/* The honest part of the screen: it says what this car was matched on, and admits when
          nothing could be. A merge nobody can inspect is a merge nobody should trust. */}
      <Card title="Identity and deduplication">
        <Row gutter={[12, 14]}>
          <Spec label="VIN" value={vehicle.vin ?? 'not supplied'} span={{ xs: 12, md: 6 }} />
          <Spec
            label="Chassis number"
            value={vehicle.chassisNumber ?? 'not supplied'}
            span={{ xs: 12, md: 6 }}
          />
          <Spec
            label="Lot number"
            value={vehicle.lotNumber ?? 'not supplied'}
            span={{ xs: 12, md: 6 }}
          />
          <Col xs={24} md={6}>
            <Typography.Text type="secondary" style={{ fontSize: 11, display: 'block' }}>
              Matched on
            </Typography.Text>

            {vehicle.canonicalHashSource === null || vehicle.canonicalHashSource === 'Unknown'
              ? (
                <Typography.Text type="warning" style={{ fontSize: 13 }}>
                  nothing — with no identifier, this car cannot be merged with the same car
                  offered by another source
                </Typography.Text>
              )
              : (
                <Typography.Text style={{ fontSize: 13 }}>
                  {MATCHED_ON[vehicle.canonicalHashSource]}
                </Typography.Text>
              )}
          </Col>
        </Row>
      </Card>
    </Space>
  );
}

/**
 * What you sell this car at, as opposed to what the exporter asks for it.
 *
 * Two different numbers, and the distinction is the point of the overlay: the listing price
 * above is the supplier's, and this one is yours. It is also the only number a message template
 * will ever quote — `{Price}` reads this and nothing else, so a car left unpriced simply has no
 * price line in a quote rather than leaking what you paid.
 */
function YourPrice({ vehicle, canPrice, onSaved }: {
  vehicle: VehicleDetail;
  canPrice: boolean;
  onSaved: (price: number | null, currency: string | null) => void;
}) {
  const { message } = AntApp.useApp();

  const [editing, setEditing] = useState(false);
  const [value, setValue] = useState<string>(vehicle.tenantPrice?.toString() ?? '');
  const [saving, setSaving] = useState(false);

  const save = async (): Promise<void> => {
    const trimmed = value.trim();
    const parsed = trimmed === '' ? null : Number(trimmed);

    if (parsed !== null && (Number.isNaN(parsed) || parsed < 0)) {
      message.error('That is not a price.');
      return;
    }

    setSaving(true);

    try {
      const saved = await setVehiclePricing(vehicle.id, parsed, vehicle.tenantCurrencyCode);

      onSaved(saved.tenantPrice, saved.tenantCurrencyCode);
      setEditing(false);

      void message.success(parsed === null ? 'Price cleared.' : 'Price saved.');
    } catch (e) {
      message.error(e instanceof Error ? e.message : 'Could not save the price.');
    } finally {
      setSaving(false);
    }
  };

  if (!canPrice) {
    return vehicle.tenantPrice === null ? null : (
      <Typography.Text type="success">
        Your price: {money(vehicle.tenantPrice, vehicle.tenantCurrencyCode)}
      </Typography.Text>
    );
  }

  if (editing) {
    return (
      <Flex gap={8} align="center" wrap>
        <Input
          autoFocus
          value={value}
          onChange={(e) => setValue(e.target.value)}
          onPressEnter={() => void save()}
          prefix={vehicle.tenantCurrencyCode ?? undefined}
          placeholder="Leave empty to clear"
          style={{ maxWidth: 200 }}
        />

        <Button type="primary" size="small" loading={saving} onClick={() => void save()}>
          Save
        </Button>

        <Button
          size="small"
          onClick={() => { setValue(vehicle.tenantPrice?.toString() ?? ''); setEditing(false); }}
        >
          Cancel
        </Button>
      </Flex>
    );
  }

  return (
    <Flex gap={8} align="center" wrap>
      {vehicle.tenantPrice === null ? (
        <Typography.Text type="secondary">
          You have not set your price for this car.
        </Typography.Text>
      ) : (
        <Typography.Text type="success">
          Your price: {money(vehicle.tenantPrice, vehicle.tenantCurrencyCode)}
        </Typography.Text>
      )}

      <Button size="small" type="link" onClick={() => setEditing(true)} style={{ padding: 0 }}>
        {vehicle.tenantPrice === null ? 'Set a price' : 'Change'}
      </Button>
    </Flex>
  );
}

/** One labelled fact in the specification grid. */
function Spec({ label, value, span }: {
  label: string;
  value: string;
  span?: { xs: number; md: number };
}) {
  return (
    <Col xs={span?.xs ?? 12} md={span?.md ?? 8}>
      <Typography.Text type="secondary" style={{ fontSize: 11, display: 'block' }}>
        {label}
      </Typography.Text>
      <Typography.Text style={{ fontSize: 14 }}>{value}</Typography.Text>
    </Col>
  );
}

/**
 * How many thumbnails sit under the main photo before the rest fold into a "+N" tile.
 *
 * Eight is one row on a laptop. Not a style choice: a real BE FORWARD listing carries sixty-six
 * photos, and laying them all out buries the offers table and the identity panel under a wall
 * of tiles.
 */
const STRIP_LIMIT = 8;

/**
 * One large photo with a strip of the first few beneath it, and the rest one click away.
 *
 * The grid of equal 160px tiles it replaced gave a car with twelve photos the same visual
 * weight as its specification, and none of them big enough to judge condition from - which is
 * the only reason to look at them.
 */
function Gallery({ urls, alt }: { urls: string[]; alt: string }) {
  const [active, setActive] = useState(0);
  const [open, setOpen] = useState(false);

  if (urls.length === 0) {
    return (
      <Card style={{ height: '100%' }}>
        <Flex align="center" justify="center" style={{ minHeight: 320 }}>
          <Empty description="No photos supplied by the source" />
        </Flex>
      </Card>
    );
  }

  // A source can withdraw a photo between the sync and this page load, so the index is clamped
  // rather than trusted - a stale one would render nothing at all.
  const current = urls[Math.min(active, urls.length - 1)];
  const shown = urls.slice(0, STRIP_LIMIT);
  const hidden = urls.length - shown.length;

  return (
    <Card styles={{ body: { padding: 12 } }}>
      {/* items rather than a hidden stack of Image elements: the group takes the whole list and
          renders only the one on screen. Real listings carry sixty-odd photos, and mounting all
          of them to make the preview work put sixty invisible boxes in the layout. */}
      <Image.PreviewGroup
        items={urls}
        preview={{
          visible: open,
          current: Math.min(active, urls.length - 1),
          onVisibleChange: setOpen,
          onChange: setActive,
        }}
      >
        <div style={{ borderRadius: 8, overflow: 'hidden', background: 'rgba(127,127,127,0.08)' }}>
          <Image
            src={current}
            alt={alt}
            fallback={MISSING_PHOTO}
            width="100%"
            style={{ aspectRatio: '16 / 10', objectFit: 'cover', display: 'block' }}
          />
        </div>
      </Image.PreviewGroup>

      {urls.length > 1 && (
        <Flex gap={8} wrap style={{ marginTop: 12 }}>
          {shown.map((url, i) => (
            <Thumb
              key={url}
              url={url}
              label={`Photo ${i + 1} of ${urls.length}`}
              selected={i === active}
              onClick={() => setActive(i)}
            />
          ))}

          {/* The rest are reachable, just not laid out. Sixty-six tiles is a wall that pushes
              the offers and the identity panel off the screen entirely. */}
          {hidden > 0 && (
            <button
              type="button"
              onClick={() => { setActive(STRIP_LIMIT); setOpen(true); }}
              aria-label={`View the other ${hidden} photos`}
              style={{
                width: 84,
                height: 60,
                border: '1px solid var(--app-stroke)',
                borderRadius: 6,
                cursor: 'pointer',
                background: 'rgba(127,127,127,0.08)',
                color: 'inherit',
                fontSize: 13,
              }}
            >
              +{hidden}
            </button>
          )}
        </Flex>
      )}
    </Card>
  );
}

function Thumb({ url, label, selected, onClick }: {
  url: string;
  label: string;
  selected: boolean;
  onClick: () => void;
}) {
  return (
    <button
      type="button"
      onClick={onClick}
      aria-label={label}
      aria-current={selected}
      style={{
        padding: 0,
        border: selected ? '2px solid #3C50E0' : '1px solid var(--app-stroke)',
        borderRadius: 6,
        overflow: 'hidden',
        cursor: 'pointer',
        background: 'none',
        lineHeight: 0,
      }}
    >
      <img
        src={url}
        alt=""
        loading="lazy"
        onError={(e) => { e.currentTarget.src = MISSING_PHOTO; }}
        style={{ width: 84, height: 60, objectFit: 'cover', display: 'block' }}
      />
    </button>
  );
}
