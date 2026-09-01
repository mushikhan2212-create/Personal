import { Card, Space, Tag, Tooltip, Typography } from 'antd';
import type { PriceType, VehicleSummary } from '../api/types';

/** Incoterms read as codes in this trade, not as prose. */
const PRICE_TYPE_LABEL: Record<PriceType, string> = {
  Unknown: '—',
  ExWorks: 'EXW',
  FreeOnBoard: 'FOB',
  CostAndFreight: 'CFR',
  CostInsuranceFreight: 'CIF',
};

const formatMoney = (amount: number | null, currency: string | null): string => {
  if (amount === null) return '—';

  try {
    return new Intl.NumberFormat(undefined, {
      style: 'currency',
      currency: currency ?? 'USD',
      maximumFractionDigits: 0,
    }).format(amount);
  } catch {
    // An unrecognised currency code should show the number, not crash the grid.
    return `${amount.toLocaleString()} ${currency ?? ''}`.trim();
  }
};

export function VehicleCard({ vehicle }: { vehicle: VehicleSummary }) {
  const title = [vehicle.make, vehicle.model].filter(Boolean).join(' ') || 'Unidentified vehicle';

  return (
    <Card
      hoverable
      styles={{ body: { padding: 16 } }}
      cover={
        vehicle.imageUrl
          ? <img alt={title} src={vehicle.imageUrl} style={{ height: 180, objectFit: 'cover' }} />
          : <div style={{
              height: 180, display: 'grid', placeItems: 'center',
              background: '#fafafa', color: '#bfbfbf',
            }}>
              No photo
            </div>
      }
    >
      <Space direction="vertical" size={6} style={{ width: '100%' }}>
        <Typography.Text strong ellipsis={{ tooltip: title }}>{title}</Typography.Text>

        {vehicle.variant && (
          <Typography.Text type="secondary" ellipsis={{ tooltip: vehicle.variant }}>
            {vehicle.variant}
          </Typography.Text>
        )}

        <Space size={4} wrap>
          {vehicle.year && <Tag>{vehicle.year}</Tag>}

          {vehicle.mileage !== null && (
            <Tag>
              {vehicle.mileage.toLocaleString()}{' '}
              {vehicle.mileageUnit === 'Miles' ? 'mi' : 'km'}
            </Tag>
          )}

          {vehicle.steeringSide !== 'Unknown' && (
            <Tag color="blue">{vehicle.steeringSide === 'RightHandDrive' ? 'RHD' : 'LHD'}</Tag>
          )}

          {vehicle.fuelType !== 'Unknown' && <Tag>{vehicle.fuelType}</Tag>}
        </Space>

        <Space align="baseline" size={6}>
          <Typography.Text strong style={{ fontSize: 18 }}>
            {formatMoney(vehicle.price, vehicle.currencyCode)}
          </Typography.Text>

          {/* Always shown, even when unknown. A price whose incoterm is unstated is not
              comparable with one that is, and hiding the tag would imply it were. */}
          <Tooltip title={
            vehicle.priceType === 'Unknown'
              ? 'The source did not state an incoterm, so this price is not comparable with a quoted FOB or CIF price.'
              : `Quoted ${PRICE_TYPE_LABEL[vehicle.priceType]}`
          }>
            <Tag color={vehicle.priceType === 'Unknown' ? 'default' : 'green'}>
              {PRICE_TYPE_LABEL[vehicle.priceType]}
            </Tag>
          </Tooltip>
        </Space>

        {vehicle.tenantPrice !== null && (
          <Typography.Text type="success">
            Your price: {formatMoney(vehicle.tenantPrice, vehicle.tenantCurrencyCode)}
          </Typography.Text>
        )}

        {/* Attribution is a POC acceptance criterion, not decoration. */}
        <Typography.Text type="secondary" style={{ fontSize: 12 }}>
          Source:{' '}
          {vehicle.sourceUrl
            ? <a href={vehicle.sourceUrl} target="_blank" rel="noreferrer noopener">{vehicle.sourceName ?? 'listing'}</a>
            : (vehicle.sourceName ?? 'unknown')}
        </Typography.Text>
      </Space>
    </Card>
  );
}
