import { useState } from 'react';
import { Card, Col, Flex, Row, Skeleton, Tag, Tooltip, Typography } from 'antd';
import type { PriceType, VehicleSummary } from '../api/types';
import { WhatsAppButton } from './WhatsAppButton';
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
 * How many cards sit across the grid at each width.
 *
 * Four on a large desktop, one on a phone. Deliberately not five: a card narrower than about
 * 260px cannot hold "16AUTO-ELEGANCE-REVCAM-PSTART" and a price on the same line, and every
 * card then ends in an ellipsis.
 */
const SPAN = { xs: 24, sm: 12, lg: 8, xxl: 6 } as const;

interface Props {
  items: VehicleSummary[];
  loading: boolean;
  onOpen: (id: string) => void;
  /**
   * Offers a WhatsApp button on every card when supplied.
   *
   * Given on a customer's match list, where the recipient is already known and sending is the
   * next thing anyone does. Omitted on the search screen, where there is no customer in scope
   * and the button would ask "to whom?" on every one of twenty-four cards.
   */
  onMessage?: (id: string) => void;
}

/**
 * The result set, as a grid of cards.
 *
 * This replaced a table. The table showed more rows per screen and aligned prices down the
 * page, which is genuinely better for comparing stock - but the photo is how anyone actually
 * recognises a car, and in a row it was 88 pixels wide. The product owner asked for cards, and
 * on a catalogue of near-identical Corolla trims the picture is doing more work than the
 * alignment was.
 *
 * Nothing the row carried was dropped. Source attribution is a POC acceptance criterion, the
 * incoterm decides whether two prices are even comparable, and the age is what makes stale
 * data visible rather than merely present - so all three are on the card.
 *
 * Sorting moved to the control above the grid. A card has no column header to click, which is
 * the one thing genuinely lost here, and the screen already had that dropdown.
 */
export function VehicleCards({ items, loading, onOpen, onMessage }: Props) {
  if (loading && items.length === 0) {
    return (
      <Row gutter={[16, 16]}>
        {Array.from({ length: 8 }, (_, i) => (
          <Col key={i} {...SPAN}>
            {/* A skeleton shaped like the card, not a spinner over an empty box: the grid keeps
                its height and its columns, so nothing jumps when the cards arrive. */}
            <Card size="small" styles={{ body: { padding: 14 } }}>
              <Skeleton.Node active style={{ width: '100%', height: 150 }}>
                <span />
              </Skeleton.Node>
              <Skeleton active paragraph={{ rows: 2 }} style={{ marginTop: 14 }} />
            </Card>
          </Col>
        ))}
      </Row>
    );
  }

  return (
    <Row gutter={[16, 16]}>
      {items.map((v) => (
        <Col key={v.id} {...SPAN}>
          <VehicleCard vehicle={v} onOpen={onOpen} onMessage={onMessage} />
        </Col>
      ))}
    </Row>
  );
}

function VehicleCard({ vehicle: v, onOpen, onMessage }: {
  vehicle: VehicleSummary;
  onOpen: (id: string) => void;
  onMessage?: (id: string) => void;
}) {
  const title = [v.make, v.model].filter(Boolean).join(' ') || 'Unidentified vehicle';
  const { label: age, isStale } = describeAge(v.lastSeenAtUtc);

  const specs = [
    v.steeringSide !== 'Unknown' && (v.steeringSide === 'RightHandDrive' ? 'RHD' : 'LHD'),
    v.fuelType !== 'Unknown' && v.fuelType,
    v.transmission !== 'Unknown' && v.transmission,
  ].filter(Boolean) as string[];

  return (
    <Card
      className="app-clickable-card"
      size="small"
      styles={{ body: { padding: 14 } }}
      onClick={() => onOpen(v.id)}
      cover={<Cover src={v.imageUrl} alt={title} offerCount={v.offerCount} stale={isStale} />}
    >
      <Flex vertical gap={10}>
        <Flex vertical gap={2}>
          <Typography.Text strong style={{ fontSize: 15 }} ellipsis={{ tooltip: title }}>
            {title}
          </Typography.Text>

          <Typography.Text
            type="secondary"
            style={{ fontSize: 12, minHeight: 18 }}
            ellipsis={{ tooltip: v.variant ?? undefined }}
          >
            {/* Held open even when empty, so a card with no variant does not sit two pixels
                shorter than the one beside it and break the row's baseline. */}
            {v.variant ?? ' '}
          </Typography.Text>
        </Flex>

        <Flex gap={12} wrap>
          <Fact label="Year" value={v.year === null ? '—' : String(v.year)} />
          <Fact
            label="Mileage"
            value={v.mileage === null
              ? '—'
              : `${v.mileage.toLocaleString()} ${v.mileageUnit === 'Miles' ? 'mi' : 'km'}`}
          />
        </Flex>

        {specs.length > 0 && (
          <Flex gap={4} wrap>
            {specs.map((s) => (
              <Tag key={s} style={{ marginInlineEnd: 0, fontSize: 11 }}>{s}</Tag>
            ))}
          </Flex>
        )}

        <div style={{ borderTop: '1px solid var(--app-stroke)', paddingTop: 10 }}>
          <Flex justify="space-between" align="flex-end" gap={8}>
            <Flex vertical gap={2} style={{ minWidth: 0 }}>
              <Flex align="center" gap={6}>
                <Typography.Text strong style={{ fontSize: 17, whiteSpace: 'nowrap' }}>
                  {formatMoney(v.price, v.currencyCode)}
                </Typography.Text>

                {/* Always shown, even when unknown. A price whose incoterm is unstated is not
                    comparable with one that is, and hiding the tag would imply it were. */}
                <Tooltip
                  title={v.priceType === 'Unknown'
                    ? 'The source did not state an incoterm, so this price is not comparable '
                      + 'with a quoted FOB or CIF price.'
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

            <Flex vertical align="flex-end" gap={4} style={{ minWidth: 0 }}>
              {onMessage && (
                <div
                  // The whole card opens the vehicle, so the button has to stop the click
                  // reaching it - otherwise sending a message also navigates away from the
                  // list the salesperson is working through.
                  onClick={(e) => { e.stopPropagation(); onMessage(v.id); }}
                  role="presentation"
                >
                  <WhatsAppButton size="small" onClick={() => undefined}>Send</WhatsAppButton>
                </div>
              )}

              <Typography.Text
                type="secondary"
                style={{ fontSize: 11, maxWidth: 130 }}
                ellipsis={{ tooltip: v.sourceName ?? undefined }}
              >
                {/* Attribution is a POC acceptance criterion, not decoration. When several
                    sources offer the same car the API sends no single name, because naming one
                    of three would misattribute the other two - the cover badge says how many
                    instead. */}
                {v.offerCount > 1
                  ? `${v.sourceCount} source${v.sourceCount === 1 ? '' : 's'}`
                  : (v.sourceName ?? 'unknown source')}
              </Typography.Text>

              <Tooltip title={`Last confirmed by the source: ${formatUtc(v.lastSeenAtUtc)}${
                isStale ? ' — old enough that it may no longer be available.' : ''}`}
              >
                <Typography.Text
                  type={isStale ? 'warning' : 'secondary'}
                  style={{ fontSize: 11, whiteSpace: 'nowrap' }}
                >
                  {isStale && '⚠ '}{age}
                </Typography.Text>
              </Tooltip>
            </Flex>
          </Flex>
        </div>
      </Flex>
    </Card>
  );
}

/** A small labelled number, for the facts that are read rather than compared. */
function Fact({ label, value }: { label: string; value: string }) {
  return (
    <Flex vertical gap={0}>
      <Typography.Text type="secondary" style={{ fontSize: 11 }}>{label}</Typography.Text>
      <Typography.Text style={{ fontSize: 13, whiteSpace: 'nowrap' }}>{value}</Typography.Text>
    </Flex>
  );
}

/**
 * The listing photo, at 16:9 across the top of the card.
 *
 * A source's image URL can 404 or be blocked, and a broken-image icon on every card looks like
 * the app is failing rather than the photo missing - so a failure swaps in the same placeholder
 * a vehicle with no photo at all gets, at the same height. Collapsing the box instead would
 * leave every card in the row a different height.
 */
function Cover({ src, alt, offerCount, stale }: {
  src: string | null;
  alt: string;
  offerCount: number;
  stale: boolean;
}) {
  const [failed, setFailed] = useState(false);

  const frame: React.CSSProperties = {
    position: 'relative',
    aspectRatio: '16 / 9',
    background: 'rgba(127, 127, 127, 0.10)',
    borderStartStartRadius: 8,
    borderStartEndRadius: 8,
    overflow: 'hidden',
  };

  return (
    <div style={frame}>
      {!src || failed
        ? (
          <Flex align="center" justify="center" style={{ height: '100%' }}>
            <Typography.Text type="secondary" style={{ fontSize: 12 }}>No photo</Typography.Text>
          </Flex>
        )
        : (
          <img
            src={src}
            alt={alt}
            loading="lazy"
            onError={() => setFailed(true)}
            style={{ width: '100%', height: '100%', objectFit: 'cover', display: 'block' }}
          />
        )}

      {offerCount > 1 && (
        <Tooltip title="Offered by more than one source. The cheapest is priced below.">
          <Tag
            color="blue"
            style={{ position: 'absolute', top: 8, insetInlineStart: 8, marginInlineEnd: 0 }}
          >
            {offerCount} offers
          </Tag>
        </Tooltip>
      )}

      {stale && (
        <Tag
          color="warning"
          style={{ position: 'absolute', top: 8, insetInlineEnd: 8, marginInlineEnd: 0 }}
        >
          Stale
        </Tag>
      )}
    </div>
  );
}
