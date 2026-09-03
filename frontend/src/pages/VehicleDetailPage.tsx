import { useEffect, useState } from 'react';
import {
  Alert, Button, Card, Col, Descriptions, Empty, Flex, Image, Row, Space, Spin, Table, Tag,
  Tooltip, Typography,
} from 'antd';
import { getVehicle } from '../api/client';
import type { CanonicalHashSource, VehicleDetail, VehicleDetailListing } from '../api/types';
import { STALE_AFTER_DAYS, ageInDays, formatUtc } from '../format';

interface Props {
  id: string;
  onBack: () => void;
}

const PRICE_TYPE_LABEL: Record<string, string> = {
  Unknown: '—',
  ExWorks: 'EXW',
  FreeOnBoard: 'FOB',
  CostAndFreight: 'CFR',
  CostInsuranceFreight: 'CIF',
};

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

export function VehicleDetailPage({ id, onBack }: Props) {
  const [vehicle, setVehicle] = useState<VehicleDetail | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [loading, setLoading] = useState(true);

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
      render: (_: unknown, l: VehicleDetailListing) => money(l.price, l.currencyCode),
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
        <Tag color={l.priceType === 'Unknown' ? 'default' : 'green'}>
          {PRICE_TYPE_LABEL[l.priceType] ?? l.priceType}
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
      render: (_: unknown, l: VehicleDetailListing) => formatUtc(l.lastSeenAtUtc),
    },
  ];

  return (
    <Space direction="vertical" size={16} style={{ width: '100%' }}>
      <Flex justify="space-between" align="center" wrap gap={12}>
        <Space direction="vertical" size={0}>
          <Typography.Title level={3} style={{ margin: 0 }}>{title}</Typography.Title>
          <Typography.Text type="secondary">{vehicle.variant}</Typography.Text>
        </Space>
        <Button onClick={onBack}>Back to search</Button>
      </Flex>

      {age !== null && age > STALE_AFTER_DAYS && (
        <Alert
          type="warning"
          showIcon
          message={`Last confirmed ${age} days ago`}
          description="No source has reported this car recently. It may already be sold."
        />
      )}

      <Row gutter={[16, 16]}>
        <Col xs={24} lg={12}>
          <Card size="small" title={`Photos (${vehicle.imageUrls.length})`}>
            {vehicle.imageUrls.length === 0
              ? <Empty description="No photos supplied by the source" />
              : (
                <Image.PreviewGroup>
                  <Flex wrap gap={8}>
                    {vehicle.imageUrls.map((url) => (
                      <Image
                        key={url}
                        src={url}
                        width={160}
                        height={120}
                        style={{ objectFit: 'cover', borderRadius: 4 }}
                      />
                    ))}
                  </Flex>
                </Image.PreviewGroup>
              )}
          </Card>
        </Col>

        <Col xs={24} lg={12}>
          <Card size="small" title="Specification">
            <Descriptions column={1} size="small" bordered>
              <Descriptions.Item label="Year">{vehicle.year ?? '—'}</Descriptions.Item>
              <Descriptions.Item label="Mileage">
                {vehicle.mileage === null
                  ? '—'
                  : `${vehicle.mileage.toLocaleString()} ${vehicle.mileageUnit === 'Miles' ? 'mi' : 'km'}`}
              </Descriptions.Item>
              <Descriptions.Item label="Steering">{vehicle.steeringSide}</Descriptions.Item>
              <Descriptions.Item label="Fuel">{vehicle.fuelType}</Descriptions.Item>
              <Descriptions.Item label="Transmission">{vehicle.transmission}</Descriptions.Item>
              <Descriptions.Item label="Drivetrain">{vehicle.drivetrain}</Descriptions.Item>
              <Descriptions.Item label="Body">{vehicle.bodyType ?? '—'}</Descriptions.Item>
              <Descriptions.Item label="Engine">
                {vehicle.engineDisplacementCc ? `${vehicle.engineDisplacementCc} cc` : '—'}
              </Descriptions.Item>
              <Descriptions.Item label="Colour">{vehicle.exteriorColor ?? '—'}</Descriptions.Item>
              <Descriptions.Item label="Status">{vehicle.status}</Descriptions.Item>
            </Descriptions>
          </Card>
        </Col>
      </Row>

      <Card size="small" title={`Offers (${vehicle.listings.length})`}>
        <Table
          rowKey={(l) => `${l.sourceName}-${l.externalListingId}`}
          dataSource={vehicle.listings}
          columns={columns}
          pagination={false}
          size="small"
        />

        {vehicle.tenantPrice !== null && (
          <Typography.Paragraph type="success" style={{ marginTop: 12, marginBottom: 0 }}>
            Your price: {money(vehicle.tenantPrice, vehicle.tenantCurrencyCode)}
          </Typography.Paragraph>
        )}
      </Card>

      {/* The honest part of the screen: it says what this car was matched on, and admits when
          nothing could be. A merge nobody can inspect is a merge nobody should trust. */}
      <Card size="small" title="Identity and deduplication">
        <Descriptions column={{ xs: 1, md: 2 }} size="small" bordered>
          <Descriptions.Item label="VIN">{vehicle.vin ?? 'not supplied'}</Descriptions.Item>
          <Descriptions.Item label="Chassis number">
            {vehicle.chassisNumber ?? 'not supplied'}
          </Descriptions.Item>
          <Descriptions.Item label="Lot number">{vehicle.lotNumber ?? 'not supplied'}</Descriptions.Item>
          <Descriptions.Item label="Matched on">
            {vehicle.canonicalHashSource === null || vehicle.canonicalHashSource === 'Unknown'
              ? (
                <Typography.Text type="warning">
                  nothing — with no identifier, this car cannot be merged with the same car
                  offered by another source
                </Typography.Text>
              )
              : MATCHED_ON[vehicle.canonicalHashSource]}
          </Descriptions.Item>
        </Descriptions>
      </Card>
    </Space>
  );
}
