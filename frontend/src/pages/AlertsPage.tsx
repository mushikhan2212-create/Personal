import { useCallback, useEffect, useState } from 'react';
import {
  Alert, App as AntApp, Badge, Button, Card, Empty, Flex, Pagination, Segmented, Skeleton, Tag,
  Tooltip, Typography,
} from 'antd';
import {
  listAlerts, markAlertSeen, markAllAlertsSeen, scanForAlerts,
} from '../api/client';
import type { RequirementAlertItem } from '../api/types';
import { formatMoney, formatUtc } from '../format';

interface Props {
  canManage: boolean;
  onOpenCustomer: (publicId: string) => void;
  onOpenVehicle: (publicId: string) => void;
  /** Bumped so the header bell recounts after anything here changes. */
  onChanged: () => void;
}

const PAGE_SIZE = 20;

/**
 * Cars that turned up after a customer asked for them.
 *
 * The screen open item O11 asks for: stock moves fast in this trade and the dealer who calls
 * first usually gets the sale, so the platform has to say "this arrived" rather than wait to be
 * asked. Everything needed to decide whether to pick up the phone is on the row - who wants it,
 * what they asked for, which car, what it cost when it appeared - because an inbox that only
 * said "3 new matches" would make every alert a navigation exercise.
 */
export function AlertsPage({ canManage, onOpenCustomer, onOpenVehicle, onChanged }: Props) {
  const { message } = AntApp.useApp();

  const [items, setItems] = useState<RequirementAlertItem[]>([]);
  const [total, setTotal] = useState(0);
  const [page, setPage] = useState(1);
  const [unseenOnly, setUnseenOnly] = useState(true);
  const [loading, setLoading] = useState(true);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);

  const load = useCallback(async (nextPage: number, onlyUnseen: boolean): Promise<void> => {
    setLoading(true);
    setError(null);

    try {
      const result = await listAlerts(onlyUnseen, nextPage, PAGE_SIZE);
      setItems(result.items);
      setTotal(result.totalCount);
    } catch (e) {
      setError(e instanceof Error ? e.message : 'Could not load alerts.');
    } finally {
      setLoading(false);
    }
  }, []);

  useEffect(() => {
    void load(1, true);
  }, [load]);

  const clearOne = async (alert: RequirementAlertItem): Promise<void> => {
    // Optimistic: the row is already gone from the list the user is looking at, and a lag
    // between the click and the row moving reads as a dead button.
    setItems((all) => (unseenOnly
      ? all.filter((a) => a.id !== alert.id)
      : all.map((a) => (a.id === alert.id
        ? { ...a, seenAtUtc: new Date().toISOString() }
        : a))));

    try {
      await markAlertSeen(alert.id);
      onChanged();
    } catch (e) {
      message.error(e instanceof Error ? e.message : 'Could not mark that seen.');
      void load(page, unseenOnly);
    }
  };

  const clearAll = async (): Promise<void> => {
    setBusy(true);

    try {
      const { marked } = await markAllAlertsSeen();
      void message.success(`${marked} alert${marked === 1 ? '' : 's'} marked seen.`);
      onChanged();
      await load(1, unseenOnly);
      setPage(1);
    } catch (e) {
      message.error(e instanceof Error ? e.message : 'Could not clear the alerts.');
    } finally {
      setBusy(false);
    }
  };

  const scanNow = async (): Promise<void> => {
    setBusy(true);

    try {
      const result = await scanForAlerts();

      void message.success(
        result.alertsRaised === 0
          ? `Checked ${result.requirementsScanned} open requirement`
            + `${result.requirementsScanned === 1 ? '' : 's'} — nothing new.`
          : `${result.alertsRaised} new alert${result.alertsRaised === 1 ? '' : 's'}.`,
      );

      onChanged();
      await load(1, unseenOnly);
      setPage(1);
    } catch (e) {
      message.error(e instanceof Error ? e.message : 'The scan failed.');
    } finally {
      setBusy(false);
    }
  };

  const switchView = (onlyUnseen: boolean): void => {
    setUnseenOnly(onlyUnseen);
    setPage(1);
    void load(1, onlyUnseen);
  };

  return (
    <Flex vertical gap={16} style={{ width: '100%' }}>
      <Flex justify="space-between" align="center" wrap gap={12}>
        <Typography.Title level={4} style={{ margin: 0 }}>New matches</Typography.Title>

        <Flex gap={8} wrap align="center">
          <Segmented
            value={unseenOnly ? 'new' : 'all'}
            onChange={(v) => switchView(v === 'new')}
            options={[{ label: 'New', value: 'new' }, { label: 'Everything', value: 'all' }]}
          />

          {canManage && (
            <>
              {/* The hourly job is the normal path; this is for the moment right after an
                  import, when waiting an hour to see whether it worked is not an option. */}
              <Tooltip title="Check the catalogue now instead of waiting for the hourly check.">
                <Button loading={busy} onClick={() => void scanNow()}>Check now</Button>
              </Tooltip>

              <Button
                disabled={busy || items.every((a) => a.seenAtUtc !== null)}
                onClick={() => void clearAll()}
              >
                Mark all seen
              </Button>
            </>
          )}
        </Flex>
      </Flex>

      <Card size="small" styles={{ body: { padding: 14 } }}>
        <Typography.Text type="secondary">
          A car appears here when it is added to the catalogue <strong>after</strong> a customer
          told you what they were looking for. Stock that was already here when the requirement
          was written is not an alert — it is on the requirement’s own matches list.
        </Typography.Text>
      </Card>

      {error && <Alert type="error" showIcon message={error} />}

      {loading
        ? <Card><Skeleton active paragraph={{ rows: 4 }} /></Card>
        : items.length === 0
          ? (
            <Card>
              <Empty
                image={Empty.PRESENTED_IMAGE_SIMPLE}
                description={unseenOnly
                  ? 'Nothing new. Every alert has been seen.'
                  : 'No alerts yet. They appear when new stock fits an open requirement.'}
              />
            </Card>
          )
          : (
            <Flex vertical gap={12}>
              {items.map((a) => (
                <AlertRow
                  key={a.id}
                  alert={a}
                  canManage={canManage}
                  onOpenCustomer={onOpenCustomer}
                  onOpenVehicle={onOpenVehicle}
                  onClear={() => void clearOne(a)}
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
            onChange={(p) => { setPage(p); void load(p, unseenOnly); }}
          />
        </Flex>
      )}
    </Flex>
  );
}

function AlertRow({ alert: a, canManage, onOpenCustomer, onOpenVehicle, onClear }: {
  alert: RequirementAlertItem;
  canManage: boolean;
  onOpenCustomer: (publicId: string) => void;
  onOpenVehicle: (publicId: string) => void;
  onClear: () => void;
}) {
  const who = [a.customer.firstName, a.customer.lastName].filter(Boolean).join(' ')
    || a.customer.phone
    || 'a customer';

  const car = [a.vehicle.make, a.vehicle.model].filter(Boolean).join(' ')
    || 'Unidentified vehicle';

  const asked = a.requirement.name
    ?? ([a.requirement.make, a.requirement.model].filter(Boolean).join(' ') || 'their requirement');

  return (
    <Card
      size="small"
      styles={{ body: { padding: 14 } }}
      // Unseen alerts carry the accent. Once cleared the row stays readable but stops
      // competing for attention with the ones still needing a phone call.
      style={a.seenAtUtc === null ? { borderInlineStart: '3px solid #3C50E0' } : { opacity: 0.7 }}
    >
      <Flex gap={14} align="center" wrap>
        <Thumbnail src={a.vehicle.imageUrl} alt={car} />

        <Flex vertical gap={3} style={{ flex: '1 1 260px', minWidth: 0 }}>
          <Flex align="center" gap={8} wrap>
            {a.seenAtUtc === null && <Badge status="processing" />}

            <Typography.Link strong onClick={() => onOpenVehicle(a.vehicle.publicId)}>
              {car}
            </Typography.Link>

            {a.vehicle.year !== null && <Tag style={{ marginInlineEnd: 0 }}>{a.vehicle.year}</Tag>}

            {a.vehicle.mileage !== null && (
              <Typography.Text type="secondary" style={{ fontSize: 12 }}>
                {a.vehicle.mileage.toLocaleString()}
                {a.vehicle.mileageUnit === 'Miles' ? ' mi' : ' km'}
              </Typography.Text>
            )}
          </Flex>

          {a.vehicle.variant && (
            <Typography.Text type="secondary" style={{ fontSize: 12 }} ellipsis>
              {a.vehicle.variant}
            </Typography.Text>
          )}

          <Typography.Text style={{ fontSize: 13 }}>
            fits{' '}
            <Typography.Link onClick={() => onOpenCustomer(a.customer.publicId)}>
              {who}
            </Typography.Link>
            {' '}— “{asked}”
          </Typography.Text>
        </Flex>

        <Flex vertical align="flex-end" gap={2}>
          <Typography.Text strong style={{ fontSize: 16, whiteSpace: 'nowrap' }}>
            {formatMoney(a.priceBaseAtMatch, a.baseCurrencyCode)}
          </Typography.Text>

          {/* The price is the one recorded when the alert was raised. Saying so keeps the
              screen honest when the exporter has since moved it. */}
          <Tooltip title={`Price when this appeared, on ${formatUtc(a.matchedAtUtc)}`}>
            <Typography.Text type="secondary" style={{ fontSize: 11 }}>
              when it appeared
            </Typography.Text>
          </Tooltip>
        </Flex>

        {canManage && a.seenAtUtc === null && (
          <Button size="small" onClick={onClear}>Mark seen</Button>
        )}

        {a.seenAtUtc !== null && (
          <Typography.Text type="secondary" style={{ fontSize: 11, whiteSpace: 'nowrap' }}>
            seen {formatUtc(a.seenAtUtc)}
          </Typography.Text>
        )}
      </Flex>
    </Card>
  );
}

/** Small, because on this screen the car is the subject but the customer is the point. */
function Thumbnail({ src, alt }: { src: string | null; alt: string }) {
  const [failed, setFailed] = useState(false);

  const box: React.CSSProperties = {
    width: 84,
    height: 60,
    flexShrink: 0,
    borderRadius: 6,
    objectFit: 'cover',
    background: 'rgba(127,127,127,0.10)',
  };

  if (!src || failed) {
    return (
      <div style={{ ...box, display: 'grid', placeItems: 'center' }}>
        <Typography.Text type="secondary" style={{ fontSize: 10 }}>No photo</Typography.Text>
      </div>
    );
  }

  return <img src={src} alt={alt} loading="lazy" style={box} onError={() => setFailed(true)} />;
}
