import { useState } from 'react';
import { Flex, Skeleton, Table, Tag, Tooltip, Typography } from 'antd';
import type { ColumnsType } from 'antd/es/table';
import type { PriceType, VehicleSearchSort, VehicleSummary } from '../api/types';
import { describeAge, formatMoney, formatUtc } from '../format';

/** Incoterms read as codes in this trade, not as prose. */
const PRICE_TYPE_LABEL: Record<PriceType, string> = {
  Unknown: '—',
  ExWorks: 'EXW',
  FreeOnBoard: 'FOB',
  CostAndFreight: 'CFR',
  CostInsuranceFreight: 'CIF',
};

/**
 * Which API sort each sortable column maps to.
 *
 * The API offers five orderings, not ten: year only descending and mileage only ascending,
 * because nobody sorts for the oldest car with the most kilometres. So those columns are
 * declared single-direction rather than offering a toggle that would silently do nothing.
 */
const SORT_BY_COLUMN: Record<string, { ascend?: VehicleSearchSort; descend?: VehicleSearchSort }> = {
  price: { ascend: 'PriceAscending', descend: 'PriceDescending' },
  year: { descend: 'YearDescending' },
  mileage: { ascend: 'MileageAscending' },
  lastSeen: { descend: 'RecentlySeen' },
};

/** The reverse mapping, so the header arrow reflects the sort actually in force. */
function orderFor(column: string, sort: VehicleSearchSort): 'ascend' | 'descend' | null {
  const options = SORT_BY_COLUMN[column];

  if (options?.ascend === sort) return 'ascend';
  if (options?.descend === sort) return 'descend';

  return null;
}

interface Props {
  items: VehicleSummary[];
  loading: boolean;
  sort: VehicleSearchSort;
  onSortChange: (sort: VehicleSearchSort) => void;
  onOpen: (id: string) => void;
}

/**
 * The result set, as rows rather than cards.
 *
 * A card grid shows eight vehicles on a laptop screen; this shows fifteen, with the columns
 * aligned so prices and mileages can be compared down the page instead of hunted for inside
 * each tile. That is the trade this screen is for - a trader scanning stock, not a shopper
 * browsing.
 *
 * Everything the card carried is still here, because none of it was decoration: source
 * attribution is a POC acceptance criterion, the incoterm decides whether two prices are even
 * comparable, and the age is what makes stale data visible rather than merely present.
 */
export function VehicleTable({ items, loading, sort, onSortChange, onOpen }: Props) {
  const columns: ColumnsType<VehicleSummary> = [
    {
      title: 'Vehicle',
      key: 'vehicle',
      render: (_, v) => {
        const title = [v.make, v.model].filter(Boolean).join(' ') || 'Unidentified vehicle';

        // Mileage and year are columns of their own, so the spec line carries what is not
        // already aligned down the page.
        const specs = [
          v.steeringSide !== 'Unknown' && (v.steeringSide === 'RightHandDrive' ? 'RHD' : 'LHD'),
          v.fuelType !== 'Unknown' && v.fuelType,
          v.transmission !== 'Unknown' && v.transmission,
        ].filter(Boolean).join(' · ');

        return (
          <Flex gap={12} align="center">
            <Thumbnail src={v.imageUrl} alt={title} />

            <Flex vertical gap={2} style={{ minWidth: 0 }}>
              <Typography.Text strong ellipsis={{ tooltip: title }}>{title}</Typography.Text>

              {v.variant && (
                <Typography.Text
                  type="secondary"
                  style={{ fontSize: 12 }}
                  ellipsis={{ tooltip: v.variant }}
                >
                  {v.variant}
                </Typography.Text>
              )}

              <Typography.Text type="secondary" style={{ fontSize: 12 }}>
                {specs || '—'}
              </Typography.Text>
            </Flex>
          </Flex>
        );
      },
    },
    {
      title: 'Year',
      key: 'year',
      width: 88,
      align: 'right',
      sorter: true,
      sortDirections: ['descend'],
      sortOrder: orderFor('year', sort),
      render: (_, v) => v.year ?? '—',
    },
    {
      title: 'Mileage',
      key: 'mileage',
      width: 120,
      align: 'right',
      sorter: true,
      sortDirections: ['ascend'],
      sortOrder: orderFor('mileage', sort),
      render: (_, v) => (v.mileage === null
        ? '—'
        : `${v.mileage.toLocaleString()} ${v.mileageUnit === 'Miles' ? 'mi' : 'km'}`),
    },
    {
      title: 'Price',
      key: 'price',
      width: 168,
      align: 'right',
      sorter: true,
      sortOrder: orderFor('price', sort),
      render: (_, v) => (
        <Flex vertical align="flex-end" gap={2}>
          <Flex align="center" gap={6}>
            <Typography.Text strong style={{ fontSize: 14, whiteSpace: 'nowrap' }}>
              {formatMoney(v.price, v.currencyCode)}
            </Typography.Text>

            {/* Always shown, even when unknown. A price whose incoterm is unstated is not
                comparable with one that is, and hiding the tag would imply it were. */}
            <Tooltip
              title={v.priceType === 'Unknown'
                ? 'The source did not state an incoterm, so this price is not comparable with a quoted FOB or CIF price.'
                : `Quoted ${PRICE_TYPE_LABEL[v.priceType]}`}
            >
              <Tag
                color={v.priceType === 'Unknown' ? 'default' : 'green'}
                style={{ marginInlineEnd: 0, fontSize: 11, lineHeight: '16px' }}
              >
                {PRICE_TYPE_LABEL[v.priceType]}
              </Tag>
            </Tooltip>
          </Flex>

          {v.tenantPrice !== null && (
            <Typography.Text type="success" style={{ fontSize: 12, whiteSpace: 'nowrap' }}>
              Yours: {formatMoney(v.tenantPrice, v.tenantCurrencyCode)}
            </Typography.Text>
          )}
        </Flex>
      ),
    },
    {
      title: 'Source',
      key: 'source',
      width: 180,
      render: (_, v) => (
        // Attribution is a POC acceptance criterion, not decoration. When several sources
        // offer the same car the API sends no single name, because naming one of three would
        // misattribute the other two - so the cell says how many instead.
        v.offerCount > 1
          ? (
            <Tooltip title="The cheapest offer is the one priced above.">
              <Tag color="blue" style={{ marginInlineEnd: 0 }}>
                {v.offerCount} offers · {v.sourceCount} source{v.sourceCount === 1 ? '' : 's'}
              </Tag>
            </Tooltip>
          )
          : (
            <Typography.Text type="secondary" style={{ fontSize: 12 }} ellipsis>
              {v.sourceUrl
                ? (
                  <a
                    href={v.sourceUrl}
                    target="_blank"
                    rel="noreferrer noopener"
                    onClick={(e) => e.stopPropagation()}
                  >
                    {v.sourceName ?? 'listing'}
                  </a>
                )
                : (v.sourceName ?? 'unknown')}
            </Typography.Text>
          )
      ),
    },
    {
      title: 'Seen',
      key: 'lastSeen',
      width: 104,
      align: 'right',
      sorter: true,
      sortDirections: ['descend'],
      sortOrder: orderFor('lastSeen', sort),
      render: (_, v) => {
        const { label, isStale } = describeAge(v.lastSeenAtUtc);

        return (
          <Tooltip title={`Last confirmed by the source: ${formatUtc(v.lastSeenAtUtc)}${
            isStale ? ' — old enough that it may no longer be available.' : ''}`}
          >
            <Typography.Text
              type={isStale ? 'warning' : 'secondary'}
              style={{ fontSize: 12, whiteSpace: 'nowrap' }}
            >
              {isStale && '⚠ '}{label}
            </Typography.Text>
          </Tooltip>
        );
      },
    },
  ];

  // A skeleton rather than a spinner over an empty box: the page keeps its height and its
  // column positions, so nothing jumps when the rows arrive.
  if (loading && items.length === 0) {
    return (
      <div style={{ padding: 16 }}>
        {Array.from({ length: 8 }, (_, i) => (
          <Flex key={i} gap={12} align="center" style={{ padding: '10px 0' }}>
            <Skeleton.Node active style={{ width: 88, height: 64, borderRadius: 6 }}>
              <span />
            </Skeleton.Node>
            <Skeleton active paragraph={{ rows: 1, width: ['40%'] }} title={{ width: '22%' }} />
          </Flex>
        ))}
      </div>
    );
  }

  return (
    <Table<VehicleSummary>
      rowKey="id"
      columns={columns}
      dataSource={items}
      loading={loading}
      pagination={false}
      size="middle"
      sticky={{ offsetHeader: 56 }}
      scroll={{ x: 900 }}
      onRow={(v) => ({ onClick: () => onOpen(v.id), style: { cursor: 'pointer' } })}
      onChange={(_pagination, _filters, sorter) => {
        const next = Array.isArray(sorter) ? sorter[0] : sorter;
        const column = String(next?.columnKey ?? '');
        const options = SORT_BY_COLUMN[column];

        // Clearing a sort in Ant sends order: undefined. Fall back to the default rather than
        // sending nothing, so the result set always has a defined order.
        if (!next?.order || !options) {
          onSortChange('RecentlySeen');
          return;
        }

        onSortChange((next.order === 'ascend' ? options.ascend : options.descend)
          ?? 'RecentlySeen');
      }}
    />
  );
}

/**
 * The listing photo, at a size that helps identify a car without dominating the row.
 *
 * A source's image URL can 404 or be blocked, and a broken-image icon in every row looks like
 * the app is failing rather than the photo missing - so a failure swaps in the same placeholder
 * a vehicle with no photo at all gets.
 */
function Thumbnail({ src, alt }: { src: string | null; alt: string }) {
  const [failed, setFailed] = useState(false);

  const box: React.CSSProperties = {
    width: 88,
    height: 64,
    flexShrink: 0,
    borderRadius: 6,
    objectFit: 'cover',
    background: 'rgba(127,127,127,0.10)',
  };

  // Falls back to the same placeholder a vehicle with no photo gets. Merely hiding the broken
  // image would leave an 88px hole in every row, which reads as a layout bug rather than a
  // missing picture - and one unreachable image host is enough to do that to the whole page.
  if (!src || failed) {
    return (
      <div style={{ ...box, display: 'grid', placeItems: 'center' }}>
        <Typography.Text type="secondary" style={{ fontSize: 10 }}>No photo</Typography.Text>
      </div>
    );
  }

  return (
    <img src={src} alt={alt} loading="lazy" style={box} onError={() => setFailed(true)} />
  );
}
