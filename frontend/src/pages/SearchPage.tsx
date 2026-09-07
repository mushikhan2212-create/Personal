import { useCallback, useEffect, useState } from 'react';
import {
  Alert, Button, Card, Col, Empty, Flex, Form, Input, InputNumber,
  Pagination, Row, Select, Space, Spin, Statistic, Tag, Typography,
} from 'antd';
import { searchVehicles } from '../api/client';
import type { VehicleSearchResponse, VehicleSearchSort } from '../api/types';
import { VehicleCard } from '../components/VehicleCard';
import type { Session } from '../App';

interface Props {
  session: Session;
  onSignOut: () => void;
  onOpenVehicle: (id: string) => void;
  onOpenImport: () => void;
  onOpenMySources: () => void;
  /** Bumped by an import, so returning here re-runs the query instead of showing stale counts. */
  catalogVersion: number;
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

export function SearchPage({
  session, onSignOut, onOpenVehicle, onOpenImport, onOpenMySources, catalogVersion,
}: Props) {
  const [filters, setFilters] = useState<Filters>({ q: '', sort: 'RecentlySeen' });
  const [page, setPage] = useState(1);
  const [result, setResult] = useState<VehicleSearchResponse | null>(null);
  const [loading, setLoading] = useState(false);
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

  useEffect(() => {
    void runSearch(1, filters);
    // On mount, and again after an import or a change on the My sources screen. Not on every
    // keystroke - that would spend a request per character typed.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [catalogVersion]);

  const submit = (): void => {
    setPage(1);
    void runSearch(1, filters);
  };

  const set = <K extends keyof Filters>(key: K, value: Filters[K]): void =>
    setFilters((f) => ({ ...f, [key]: value }));

  // Sort always carries a value, so it is not a filter the user applied.
  const hasFilters = Object.entries(filters)
    .some(([key, value]) => key !== 'sort' && value !== undefined && value !== '');

  return (
    <Space direction="vertical" size={16} style={{ width: '100%' }}>
      <Flex justify="space-between" align="center" wrap gap={12}>
        <Space>
          <Typography.Text strong>{session.tenant.name}</Typography.Text>
          <Tag>{session.tenant.slug}</Tag>
          <Typography.Text type="secondary">{session.email}</Typography.Text>
        </Space>
        <Space>
          {/* Available to everyone: it only changes what this person sees. */}
          <Button onClick={onOpenMySources}>My sources</Button>
          {canSync && <Button onClick={onOpenImport}>Import vehicles</Button>}
          <Button onClick={onSignOut}>Sign out</Button>
        </Space>
      </Flex>

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
              // With no filters applied, "nothing matched" is not the real explanation: either
              // the catalogue is empty or this person has switched their sources off. Saying
              // so beats a bare "no results" that reads as a broken catalogue.
              hasFilters
                ? 'No vehicles match these filters.'
                : 'Nothing to show. The catalogue may be empty, or you may have switched your '
                  + 'sources off on the My sources screen.'
            }
          >
            {!hasFilters && <Button onClick={onOpenMySources}>My sources</Button>}
          </Empty>
        ) : (
          <Row gutter={[16, 16]}>
            {result?.items.map((v) => (
              <Col key={v.id} xs={24} sm={12} md={8} lg={6}>
                <VehicleCard vehicle={v} onOpen={() => onOpenVehicle(v.id)} />
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
