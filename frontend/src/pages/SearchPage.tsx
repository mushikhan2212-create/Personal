import { useCallback, useEffect, useState } from 'react';
import {
  Alert, App as AntApp, Button, Card, Col, Empty, Flex, Form, Input, InputNumber,
  Pagination, Row, Select, Space, Spin, Statistic, Tag, Typography,
} from 'antd';
import { listSources, searchVehicles, syncSource } from '../api/client';
import type {
  VehicleSearchResponse, VehicleSearchSort, VehicleSourceSummary,
} from '../api/types';
import { VehicleCard } from '../components/VehicleCard';
import { formatUtc } from '../format';
import type { Session } from '../App';

interface Props {
  session: Session;
  onSignOut: () => void;
}

interface Filters {
  q: string;
  steeringSide?: string;
  fuelType?: string;
  transmission?: string;
  minYear?: number;
  maxYear?: number;
  maxMileage?: number;
  minPrice?: number;
  maxPrice?: number;
  sort: VehicleSearchSort;
}

const PAGE_SIZE = 24;

export function SearchPage({ session, onSignOut }: Props) {
  const { message } = AntApp.useApp();

  const [filters, setFilters] = useState<Filters>({ q: '', sort: 'RecentlySeen' });
  const [page, setPage] = useState(1);
  const [result, setResult] = useState<VehicleSearchResponse | null>(null);
  const [sources, setSources] = useState<VehicleSourceSummary[]>([]);
  const [loading, setLoading] = useState(false);
  const [syncing, setSyncing] = useState<string | null>(null);
  const [error, setError] = useState<string | null>(null);

  const canSync = session.permissions.includes('vehicles.sync');

  const runSearch = useCallback(async (nextPage: number, current: Filters): Promise<void> => {
    setLoading(true);
    setError(null);

    try {
      setResult(await searchVehicles({ ...current, page: nextPage, pageSize: PAGE_SIZE }));
    } catch (e) {
      setError(e instanceof Error ? e.message : 'Search failed.');
    } finally {
      setLoading(false);
    }
  }, []);

  const refreshSources = useCallback(async (): Promise<void> => {
    try {
      setSources(await listSources());
    } catch {
      // A source list that will not load must not block searching, which is the main job.
    }
  }, []);

  useEffect(() => {
    void runSearch(1, filters);
    void refreshSources();
    // Deliberately once on mount: later searches are driven by the button and the pager, not
    // by every keystroke, which would spend a request per character.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);

  const submit = (): void => {
    setPage(1);
    void runSearch(1, filters);
  };

  const runSync = async (code: string, fetchDetail: boolean): Promise<void> => {
    setSyncing(code);

    try {
      const result = await syncSource(code, 2, fetchDetail);

      message.success(
        `${code}: ${result.created} created, ${result.updated} updated, `
        + `${result.autoMerged} merged, ${result.failed} failed — `
        + `${result.requestCount} request(s) in ${result.elapsedMs}ms. `
        + `${result.withoutStrongIdentifier} record(s) had no strong identifier.`,
        10,
      );

      await refreshSources();
      void runSearch(1, filters);
    } catch (e) {
      message.error(e instanceof Error ? e.message : 'Sync failed.', 8);
    } finally {
      setSyncing(null);
    }
  };

  const set = <K extends keyof Filters>(key: K, value: Filters[K]): void =>
    setFilters((f) => ({ ...f, [key]: value }));

  return (
    <Space direction="vertical" size={16} style={{ width: '100%' }}>
      <Flex justify="space-between" align="center" wrap gap={12}>
        <Space>
          <Typography.Text strong>{session.tenant.name}</Typography.Text>
          <Tag>{session.tenant.slug}</Tag>
          <Typography.Text type="secondary">{session.email}</Typography.Text>
        </Space>
        <Button onClick={onSignOut}>Sign out</Button>
      </Flex>

      <Card size="small" title="Sources">
        <Flex wrap gap={12}>
          {sources.length === 0 && <Typography.Text type="secondary">No sources registered.</Typography.Text>}

          {sources.map((s) => (
            <Card key={s.code} size="small" style={{ minWidth: 260 }}>
              <Space direction="vertical" size={4} style={{ width: '100%' }}>
                <Space>
                  <Typography.Text strong>{s.name}</Typography.Text>
                  <Tag>{s.code}</Tag>
                </Space>

                <Typography.Text type="secondary" style={{ fontSize: 12 }}>
                  {s.vehicleCount.toLocaleString()} listing(s)
                  {s.lastSyncAtUtc && ` · last sync ${formatUtc(s.lastSyncAtUtc)}`}
                </Typography.Text>

                {/* A failed run is called a failure. Without this the card shows only the
                    last successful sync, and a source whose every attempt has failed is
                    indistinguishable from one nobody has tried yet. */}
                {s.lastAttemptStatus === 'Failed' && (
                  <Typography.Text type="danger" style={{ fontSize: 12 }}>
                    Last attempt failed ({formatUtc(s.lastAttemptAtUtc)})
                  </Typography.Text>
                )}

                {canSync && (
                  <Space size={4}>
                    <Button size="small" loading={syncing === s.code} onClick={() => void runSync(s.code, false)}>
                      Sync
                    </Button>

                    {/* The expensive path, labelled as such: one request per vehicle instead
                        of one per page, in exchange for VINs and source prices. */}
                    <Button
                      size="small"
                      loading={syncing === s.code}
                      onClick={() => void runSync(s.code, true)}
                      title="Fetches each vehicle's detail record. Costs one request per vehicle, and is what makes deduplication and pricing work."
                    >
                      Sync + detail
                    </Button>
                  </Space>
                )}
              </Space>
            </Card>
          ))}
        </Flex>
      </Card>

      <Card size="small">
        <Form layout="vertical" onFinish={submit}>
          <Row gutter={12}>
            <Col xs={24} md={8}>
              <Form.Item label="Search">
                <Input
                  placeholder="Make, model or variant"
                  value={filters.q}
                  onChange={(e) => set('q', e.target.value)}
                  allowClear
                />
              </Form.Item>
            </Col>

            <Col xs={12} md={4}>
              <Form.Item label="Steering">
                <Select
                  allowClear
                  placeholder="Any"
                  value={filters.steeringSide}
                  onChange={(v) => set('steeringSide', v)}
                  options={[
                    { value: 'RightHandDrive', label: 'Right-hand drive' },
                    { value: 'LeftHandDrive', label: 'Left-hand drive' },
                  ]}
                />
              </Form.Item>
            </Col>

            <Col xs={12} md={4}>
              <Form.Item label="Fuel">
                <Select
                  allowClear
                  placeholder="Any"
                  value={filters.fuelType}
                  onChange={(v) => set('fuelType', v)}
                  options={['Petrol', 'Diesel', 'Hybrid', 'PluginHybrid', 'Electric']
                    .map((v) => ({ value: v, label: v }))}
                />
              </Form.Item>
            </Col>

            <Col xs={12} md={4}>
              <Form.Item label="Year from">
                <InputNumber
                  style={{ width: '100%' }}
                  value={filters.minYear}
                  onChange={(v) => set('minYear', v ?? undefined)}
                  min={1950}
                  max={2100}
                />
              </Form.Item>
            </Col>

            <Col xs={12} md={4}>
              <Form.Item label="Year to">
                <InputNumber
                  style={{ width: '100%' }}
                  value={filters.maxYear}
                  onChange={(v) => set('maxYear', v ?? undefined)}
                  min={1950}
                  max={2100}
                />
              </Form.Item>
            </Col>

            <Col xs={12} md={4}>
              <Form.Item label="Max mileage (km)">
                <InputNumber
                  style={{ width: '100%' }}
                  value={filters.maxMileage}
                  onChange={(v) => set('maxMileage', v ?? undefined)}
                  min={0}
                  step={10_000}
                />
              </Form.Item>
            </Col>

            <Col xs={12} md={4}>
              <Form.Item
                label="Sort"
              >
                <Select
                  value={filters.sort}
                  onChange={(v) => set('sort', v)}
                  options={[
                    { value: 'RecentlySeen', label: 'Recently seen' },
                    { value: 'PriceAscending', label: 'Price, low to high' },
                    { value: 'PriceDescending', label: 'Price, high to low' },
                    { value: 'YearDescending', label: 'Newest first' },
                    { value: 'MileageAscending', label: 'Lowest mileage' },
                  ]}
                />
              </Form.Item>
            </Col>

            <Col xs={24} md={4}>
              <Form.Item label=" ">
                <Button type="primary" htmlType="submit" loading={loading} block>Search</Button>
              </Form.Item>
            </Col>
          </Row>
        </Form>
      </Card>

      {error && <Alert type="error" showIcon message={error} />}

      {result && (
        <Flex gap={24} wrap>
          <Statistic title="Matches" value={result.totalCount} />
          {/* Surfaced rather than buried: decision D4 makes this measurement the gate for
              whether a dedicated search engine is ever needed. */}
          <Statistic title="Query time" value={result.elapsedMilliseconds} suffix="ms" />
        </Flex>
      )}

      <Spin spinning={loading}>
        {result && result.items.length === 0 && !loading ? (
          <Empty
            description={
              sources.every((s) => s.vehicleCount === 0)
                ? 'The catalog is empty. Run a sync above to populate it.'
                : 'No vehicles match these filters.'
            }
          />
        ) : (
          <Row gutter={[16, 16]}>
            {result?.items.map((v) => (
              <Col key={v.id} xs={24} sm={12} md={8} lg={6}>
                <VehicleCard vehicle={v} />
              </Col>
            ))}
          </Row>
        )}
      </Spin>

      {result && result.totalCount > PAGE_SIZE && (
        <Flex justify="center">
          <Pagination
            current={page}
            pageSize={PAGE_SIZE}
            total={result.totalCount}
            showSizeChanger={false}
            onChange={(p) => { setPage(p); void runSearch(p, filters); }}
          />
        </Flex>
      )}
    </Space>
  );
}
