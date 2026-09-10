import { useCallback, useEffect, useState } from 'react';
import {
  Alert, App as AntApp, Button, Card, Col, Empty, Flex, Modal, Pagination, Popconfirm, Row,
  Segmented, Skeleton, Tag, theme, Tooltip, Typography,
} from 'antd';
import {
  listDuplicates, listMerges, mergeDuplicate, rejectDuplicate, revertMerge, scanForDuplicates,
} from '../api/client';
import type { DuplicateCandidate, DuplicateSide, MergeRecord } from '../api/types';
import { formatMoney, formatUtc, specLabel } from '../format';
import { TrashGlyph } from '../components/icons';

interface Props {
  onOpenVehicle: (publicId: string) => void;
  /** Bumped after a merge so the vehicle screen re-runs its query. */
  onChanged: () => void;
}

const PAGE_SIZE = 10;

/**
 * The review queue for suggested duplicate vehicles.
 *
 * This is open item O15 made usable. The catalogue aggregates exporters who quote the same
 * wholesale stock at different margins and supply no VIN, so the platform cannot merge them
 * automatically without risking a merge that is wrong for every tenant at once (decision D1).
 * It suggests instead, and this is where a person decides.
 *
 * <b>The layout is a comparison, not a list.</b> The reviewer's question is never "what is this
 * row" but "are these two the same car", so the two vehicles sit side by side with their
 * specifications aligned, and the fields that differ are marked. A vertical list of pairs would
 * make them scroll between the two halves of every decision.
 */
export function DuplicatesPage({ onOpenVehicle, onChanged }: Props) {
  const { message } = AntApp.useApp();

  const [view, setView] = useState<'Pending' | 'Merged' | 'Rejected'>('Pending');
  const [items, setItems] = useState<DuplicateCandidate[]>([]);
  const [total, setTotal] = useState(0);
  const [page, setPage] = useState(1);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState<string | null>(null);
  const [scanning, setScanning] = useState(false);
  const [merges, setMerges] = useState<MergeRecord[] | null>(null);

  const load = useCallback(async (nextPage: number, status: typeof view): Promise<void> => {
    setLoading(true);
    setError(null);

    try {
      const result = await listDuplicates(status, nextPage, PAGE_SIZE);

      setItems(result.items);
      setTotal(result.totalCount);
    } catch (e) {
      setError(e instanceof Error ? e.message : 'Could not load the review queue.');
    } finally {
      setLoading(false);
    }
  }, []);

  useEffect(() => { void load(page, view); }, [load, page, view]);

  const act = async (id: string, what: 'merge' | 'reject'): Promise<void> => {
    setBusy(id);

    try {
      if (what === 'merge') {
        const result = await mergeDuplicate(id);

        void message.success(
          result.listingsMoved === 1
            ? 'Merged. Both offers now sit on one car.'
            : `Merged. ${result.listingsMoved} offers moved onto one car.`,
        );

        // The catalogue is one row shorter, so the vehicle screen's counts are stale.
        onChanged();
      } else {
        await rejectDuplicate(id);
        void message.success('Marked as two different cars. It will not be suggested again.');
      }

      await load(page, view);
    } catch (e) {
      message.error(e instanceof Error ? e.message : 'That did not work.');
    } finally {
      setBusy(null);
    }
  };

  const scan = async (): Promise<void> => {
    setScanning(true);

    try {
      const result = await scanForDuplicates();

      void message.success(
        result.candidatesRaised === 0
          ? `Nothing new. ${result.pairsExamined} pair(s) checked.`
          : `${result.candidatesRaised} new suggestion(s) from ${result.pairsExamined} pair(s).`,
      );

      await load(1, 'Pending');
      setPage(1);
      setView('Pending');
    } catch (e) {
      message.error(e instanceof Error ? e.message : 'The scan failed.');
    } finally {
      setScanning(false);
    }
  };

  return (
    <Flex vertical gap={16} style={{ width: '100%' }}>
      <Flex justify="space-between" align="center" wrap gap={12}>
        <Flex vertical gap={2}>
          <Typography.Title level={4} style={{ margin: 0 }}>Possible duplicates</Typography.Title>
          <Typography.Text type="secondary" style={{ fontSize: 12 }}>
            Two catalogue entries that look like one car. Nothing is merged until you say so.
          </Typography.Text>
        </Flex>

        <Flex gap={8} wrap>
          <Button
            onClick={() => { void listMerges().then((r) => setMerges(r.items)); }}
          >
            Merge history
          </Button>

          <Button type="primary" loading={scanning} onClick={() => void scan()}>
            Check now
          </Button>
        </Flex>
      </Flex>

      <Segmented
        value={view}
        onChange={(v) => { setView(v as typeof view); setPage(1); }}
        options={[
          { value: 'Pending', label: 'To review' },
          { value: 'Merged', label: 'Merged' },
          { value: 'Rejected', label: 'Not duplicates' },
        ]}
      />

      {error && <Alert type="error" showIcon message={error} />}

      {loading && items.length === 0 ? (
        <Skeleton active paragraph={{ rows: 8 }} />
      ) : items.length === 0 ? (
        <Card>
          <Empty
            image={Empty.PRESENTED_IMAGE_SIMPLE}
            description={view === 'Pending'
              ? 'Nothing waiting. Import a source and press Check now to look again.'
              : 'Nothing here yet.'}
          />
        </Card>
      ) : (
        <Flex vertical gap={14}>
          {items.map((c) => (
            <CandidateCard
              key={c.id}
              candidate={c}
              busy={busy === c.id}
              readOnly={view !== 'Pending'}
              onOpenVehicle={onOpenVehicle}
              onMerge={() => void act(c.id, 'merge')}
              onReject={() => void act(c.id, 'reject')}
            />
          ))}
        </Flex>
      )}

      {total > PAGE_SIZE && (
        <Flex justify="flex-end">
          <Pagination
            current={page}
            total={total}
            pageSize={PAGE_SIZE}
            showSizeChanger={false}
            onChange={setPage}
          />
        </Flex>
      )}

      <MergeHistory
        merges={merges}
        onClose={() => setMerges(null)}
        onReverted={async () => {
          void message.success('Merge undone. Both cars are back in the catalogue.');
          onChanged();
          await load(page, view);
          setMerges((await listMerges()).items);
        }}
      />
    </Flex>
  );
}

/**
 * One pair, side by side.
 *
 * The score is shown as a word rather than only as a number: "0.65" means nothing to somebody
 * deciding whether to archive a car, whereas "worth a look" sets the right expectation about how
 * hard to look.
 */
function CandidateCard({ candidate: c, busy, readOnly, onOpenVehicle, onMerge, onReject }: {
  candidate: DuplicateCandidate;
  busy: boolean;
  readOnly: boolean;
  onOpenVehicle: (publicId: string) => void;
  onMerge: () => void;
  onReject: () => void;
}) {
  const title = [c.left.make, c.left.model].filter(Boolean).join(' ') || 'Unidentified vehicle';

  const confidence = c.score >= 0.9
    ? { label: 'Almost certainly the same car', colour: 'green' }
    : c.score >= 0.7
      ? { label: 'Probably the same car', colour: 'gold' }
      : { label: 'Worth a look', colour: 'default' };

  return (
    <Card size="small" styles={{ body: { padding: 16 } }}>
      <Flex vertical gap={14}>
        <Flex justify="space-between" align="flex-start" wrap gap={12}>
          <Flex vertical gap={4} style={{ minWidth: 0 }}>
            <Typography.Text strong style={{ fontSize: 15 }}>{title}</Typography.Text>

            <Flex gap={6} wrap align="center">
              <Tag color={confidence.colour} style={{ marginInlineEnd: 0 }}>
                {confidence.label}
              </Tag>
              <Typography.Text type="secondary" style={{ fontSize: 12 }}>
                {Math.round(c.score * 100)}% match · found {formatUtc(c.createdAtUtc)}
              </Typography.Text>
            </Flex>
          </Flex>

          {!readOnly && (
            <Flex gap={8} wrap>
              <Popconfirm
                title="Merge these two?"
                description={
                  'The offers move onto one car and the other is archived. '
                  + 'You can undo this from Merge history.'
                }
                okText="Merge"
                onConfirm={onMerge}
              >
                <Button type="primary" loading={busy}>Same car</Button>
              </Popconfirm>

              <Button icon={<TrashGlyph />} loading={busy} onClick={onReject}>
                Different cars
              </Button>
            </Flex>
          )}
        </Flex>

        {/* Why the platform thinks so. Shown above the comparison rather than below it, because
            it tells the reviewer which fields are worth checking first. */}
        <Flex gap={6} wrap>
          {c.signals.map((s) => (
            <Tag
              key={s.name}
              color={s.weight < 0 ? 'red' : 'default'}
              style={{ marginInlineEnd: 0, fontSize: 11 }}
            >
              {s.detail}
            </Tag>
          ))}
        </Flex>

        <Row gutter={[12, 12]}>
          <Col xs={24} md={12}>
            <Side side={c.left} other={c.right} onOpen={onOpenVehicle} />
          </Col>
          <Col xs={24} md={12}>
            <Side side={c.right} other={c.left} onOpen={onOpenVehicle} />
          </Col>
        </Row>
      </Flex>
    </Card>
  );
}

/** One vehicle of the pair, with the fields that differ from its twin marked. */
function Side({ side, other, onOpen }: {
  side: DuplicateSide;
  other: DuplicateSide;
  onOpen: (publicId: string) => void;
}) {
  const rows: { label: string; value: string; differs: boolean }[] = [
    {
      label: 'Year',
      value: side.modelYear === null ? '—' : String(side.modelYear),
      differs: side.modelYear !== other.modelYear,
    },
    {
      label: 'Mileage',
      value: side.mileage === null
        ? '—'
        : `${side.mileage.toLocaleString()} ${side.mileageUnit === 'Miles' ? 'mi' : 'km'}`,
      differs: side.mileage !== other.mileage,
    },
    {
      label: 'Colour',
      value: side.exteriorColor ?? '—',
      differs: (side.exteriorColor ?? '').toLowerCase() !== (other.exteriorColor ?? '').toLowerCase(),
    },
    {
      label: 'Engine',
      value: side.engineDisplacementCc ? `${side.engineDisplacementCc} cc` : '—',
      differs: side.engineDisplacementCc !== other.engineDisplacementCc,
    },
    {
      label: 'Gearbox',
      value: specLabel(side.transmission),
      differs: side.transmission !== other.transmission,
    },
    {
      label: 'Fuel',
      value: specLabel(side.fuelType),
      differs: side.fuelType !== other.fuelType,
    },
  ];

  // Through Ant's token rather than a CSS variable of our own: this card sits inside another
  // one and needs to read as a panel in both themes, and the palette already has a colour for
  // exactly that.
  const { token } = theme.useToken();

  return (
    <Card
      size="small"
      styles={{ body: { padding: 12 } }}
      style={{ background: token.colorFillQuaternary, height: '100%' }}
    >
      <Flex vertical gap={10}>
        <Flex gap={10} align="flex-start">
          {side.photo
            ? (
              <img
                src={side.photo}
                alt=""
                loading="lazy"
                onError={(e) => { e.currentTarget.style.display = 'none'; }}
                style={{ width: 84, height: 62, objectFit: 'cover', borderRadius: 6 }}
              />
            )
            : (
              <Flex
                align="center"
                justify="center"
                style={{
                  width: 84, height: 62, borderRadius: 6,
                  border: '1px dashed var(--app-stroke)', flex: '0 0 auto',
                }}
              >
                <Typography.Text type="secondary" style={{ fontSize: 10 }}>No photo</Typography.Text>
              </Flex>
            )}

          <Flex vertical gap={2} style={{ minWidth: 0 }}>
            <Typography.Link onClick={() => onOpen(side.publicId)} style={{ fontSize: 13 }}>
              {[side.make, side.model, side.variant].filter(Boolean).join(' ') || 'Open'}
            </Typography.Link>

            <Typography.Text type="secondary" style={{ fontSize: 11 }}>
              Added {formatUtc(side.createdAtUtc)}
            </Typography.Text>

            {/* Only the states that change what the reviewer is looking at. An imported
                vehicle whose feed never stated a status reads as "Unknown", and tagging every
                card with that is noise; "Archived" means this side has already been merged
                away and genuinely needs saying. */}
            {(side.status === 'Archived' || side.status === 'Sold') && (
              <Tag color="default" style={{ marginInlineEnd: 0, fontSize: 10 }}>{side.status}</Tag>
            )}
          </Flex>
        </Flex>

        <Flex vertical gap={3}>
          {rows.map((r) => (
            <Flex key={r.label} justify="space-between" gap={8}>
              <Typography.Text type="secondary" style={{ fontSize: 12 }}>{r.label}</Typography.Text>

              {/* A differing value is coloured rather than merely listed. The whole decision is
                  "what is not the same about these", and making the reviewer diff two columns
                  by eye is how a wrong merge happens. */}
              <Typography.Text
                type={r.differs ? 'danger' : undefined}
                style={{ fontSize: 12 }}
                strong={r.differs}
              >
                {r.value}
              </Typography.Text>
            </Flex>
          ))}
        </Flex>

        <Flex vertical gap={4} style={{ borderTop: '1px solid var(--app-stroke)', paddingTop: 8 }}>
          {side.offers.length === 0
            ? <Typography.Text type="secondary" style={{ fontSize: 12 }}>No offer</Typography.Text>
            : side.offers.map((o) => (
              <Flex key={o.id} justify="space-between" gap={8}>
                <Typography.Text
                  type="secondary"
                  style={{ fontSize: 12, maxWidth: 130 }}
                  ellipsis={{ tooltip: o.source ?? undefined }}
                >
                  {o.source ?? 'Unknown source'}
                </Typography.Text>

                <Typography.Text strong style={{ fontSize: 12, whiteSpace: 'nowrap' }}>
                  {formatMoney(o.price, o.currencyCode)}
                  {o.priceType !== 'Unknown' && (
                    <Typography.Text type="secondary" style={{ fontSize: 11 }}>
                      {' '}{specLabel(o.priceType)}
                    </Typography.Text>
                  )}
                </Typography.Text>
              </Flex>
            ))}
        </Flex>
      </Flex>
    </Card>
  );
}

/**
 * What has been merged, and the way back.
 *
 * Under D1 a wrong merge is wrong for every tenant at once, and the person who spots it is
 * often not the person who did it - so the record is somebody else's only route to undoing it.
 */
function MergeHistory({ merges, onClose, onReverted }: {
  merges: MergeRecord[] | null;
  onClose: () => void;
  onReverted: () => Promise<void>;
}) {
  const { message } = AntApp.useApp();
  const [busy, setBusy] = useState<string | null>(null);

  const undo = async (id: string): Promise<void> => {
    setBusy(id);

    try {
      await revertMerge(id);
      await onReverted();
    } catch (e) {
      message.error(e instanceof Error ? e.message : 'Could not undo that.');
    } finally {
      setBusy(null);
    }
  };

  return (
    <Modal
      title="Merge history"
      open={merges !== null}
      onCancel={onClose}
      footer={<Button onClick={onClose}>Close</Button>}
      width={620}
    >
      {merges?.length === 0 ? (
        <Empty image={Empty.PRESENTED_IMAGE_SIMPLE} description="Nothing has been merged yet." />
      ) : (
        <Flex vertical gap={10}>
          {merges?.map((m) => (
            <Flex key={m.id} justify="space-between" align="center" gap={12} wrap>
              <Flex vertical gap={2} style={{ minWidth: 0 }}>
                <Typography.Text style={{ fontSize: 13 }}>
                  {[m.surviving?.make, m.surviving?.model, m.surviving?.modelYear]
                    .filter(Boolean).join(' ') || 'Vehicle'}
                </Typography.Text>

                <Typography.Text type="secondary" style={{ fontSize: 11 }}>
                  {formatUtc(m.mergedAtUtc)}
                  {m.mergedBy ? ` · ${m.mergedBy}` : ''}
                  {m.revertedAtUtc ? ' · undone' : ''}
                </Typography.Text>
              </Flex>

              {m.revertedAtUtc
                ? <Tag style={{ marginInlineEnd: 0 }}>Undone</Tag>
                : (
                  <Tooltip title="Puts the archived car and its offers back">
                    <Button
                      size="small"
                      loading={busy === m.id}
                      onClick={() => void undo(m.id)}
                    >
                      Undo
                    </Button>
                  </Tooltip>
                )}
            </Flex>
          ))}
        </Flex>
      )}
    </Modal>
  );
}
