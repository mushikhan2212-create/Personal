import { useCallback, useEffect, useState } from 'react';
import {
  Alert, Badge, Button, Card, Col, Drawer, Empty, Flex, Form, Input, InputNumber,
  Pagination, Row, Select, Space, Tag, Typography,
} from 'antd';
import { searchVehicles } from '../api/client';
import type { VehicleSearchResponse, VehicleSearchSort } from '../api/types';
import { VehicleCards } from '../components/VehicleCards';

interface Props {
  onOpenVehicle: (id: string) => void;
  onOpenMySources: () => void;
  /** Bumped by an import or a source change, so returning here re-runs the query. */
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

/**
 * How many cards a page holds, which depends on whether anyone has asked for anything.
 *
 * Opening the screen is not a search - it is a glance at what is there. Loading a full page of
 * cards for that means the catalogue's photos and the query behind them are fetched before
 * anybody has said what they want, and on a large catalogue that is the slowest thing the app
 * does for the least reason. Ten is enough to show the screen works; a real search gets a real
 * page.
 *
 * The cap is stated on screen rather than left to be inferred, because a silent limit is
 * indistinguishable from a catalogue that only has ten cars in it.
 */
const BROWSE_PAGE_SIZE = 10;
const SEARCH_PAGE_SIZE = 24;

const EMPTY: Filters = { q: '', sort: 'RecentlySeen' };

/** Whether the user has actually asked for something, as opposed to just arriving. */
function isNarrowed(f: Filters): boolean {
  return f.q.trim() !== '' || REFINEMENTS.some((k) => f[k] !== undefined && f[k] !== '');
}

const pageSizeFor = (f: Filters): number =>
  (isNarrowed(f) ? SEARCH_PAGE_SIZE : BROWSE_PAGE_SIZE);

/** Everything except the free-text box and the sort, which have their own controls. */
const REFINEMENTS = [
  'steeringSide', 'fuelType', 'transmission',
  'minYear', 'maxYear', 'maxMileage', 'minPrice', 'maxPrice',
] as const;

/** Enum values arrive as API names; a chip should read the way the dropdown did. */
const VALUE_LABELS: Record<string, string> = {
  RightHandDrive: 'Right-hand drive',
  LeftHandDrive: 'Left-hand drive',
  PluginHybrid: 'Plug-in hybrid',
  ContinuouslyVariable: 'CVT',
  DualClutch: 'Dual clutch',
};

/**
 * How a filter's value reads on its chip.
 *
 * Numbers get thousands separators; enum names get their friendly form where one exists, and
 * otherwise stand as they are - "Petrol" and "Diesel" need no translation, and coercing every
 * value through Number would render them as NaN.
 */
function chipValue(value: string | number): string {
  if (typeof value === 'number') return value.toLocaleString();

  return VALUE_LABELS[value] ?? value;
}

const LABELS: Record<(typeof REFINEMENTS)[number], string> = {
  steeringSide: 'Steering',
  fuelType: 'Fuel',
  transmission: 'Transmission',
  minYear: 'Year from',
  maxYear: 'Year to',
  maxMileage: 'Max mileage',
  minPrice: 'Min price',
  maxPrice: 'Max price',
};

export function SearchPage({ onOpenVehicle, onOpenMySources, catalogVersion }: Props) {
  const [filters, setFilters] = useState<Filters>(EMPTY);
  const [page, setPage] = useState(1);
  const [result, setResult] = useState<VehicleSearchResponse | null>(null);
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [drawerOpen, setDrawerOpen] = useState(false);

  const runSearch = useCallback(async (nextPage: number, current: Filters): Promise<void> => {
    setLoading(true);
    setError(null);

    try {
      setResult(await searchVehicles({
        ...current, page: nextPage, pageSize: pageSizeFor(current),
      }));
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

  const set = <K extends keyof Filters>(key: K, value: Filters[K]): void =>
    setFilters((f) => ({ ...f, [key]: value }));

  const submit = (next: Filters = filters): void => {
    setPage(1);
    void runSearch(1, next);
  };

  /** Sorting is applied by the server, so changing it is a new query, not a re-render. */
  const changeSort = (sort: VehicleSearchSort): void => {
    const next = { ...filters, sort };
    setFilters(next);
    setPage(1);
    void runSearch(1, next);
  };

  const clearOne = (key: (typeof REFINEMENTS)[number]): void => {
    const next = { ...filters, [key]: undefined };
    setFilters(next);
    submit(next);
  };

  const clearAll = (): void => {
    const next: Filters = { ...EMPTY, sort: filters.sort };
    setFilters(next);
    submit(next);
  };

  const active = REFINEMENTS.filter((k) => filters[k] !== undefined && filters[k] !== '');
  const hasQuery = isNarrowed(filters);
  const pageSize = pageSizeFor(filters);

  // Only worth saying when the cap is actually hiding something. On a catalogue of six cars
  // "showing 10 of 6" would be noise about a limit nobody reached.
  const capped = !hasQuery && result !== null && result.totalCount > BROWSE_PAGE_SIZE;

  return (
    <Space direction="vertical" size={16} style={{ width: '100%' }}>
      <Flex justify="space-between" align="center" wrap gap={12}>
        <Typography.Title level={4} style={{ margin: 0 }}>Vehicles</Typography.Title>

        {result && (
          // Query time is surfaced rather than buried: decision D4 makes this measurement the
          // gate for whether a dedicated search engine is ever needed.
          <Typography.Text type="secondary">
            <strong>{result.totalCount.toLocaleString()}</strong>
            {result.totalCount === 1 ? ' vehicle' : ' vehicles'} · {result.elapsedMilliseconds} ms
          </Typography.Text>
        )}
      </Flex>

      <Card size="small" styles={{ body: { padding: 12 } }}>
        <Flex gap={8} wrap>
          <Input.Search
            placeholder="Make, model or variant"
            value={filters.q}
            onChange={(e) => set('q', e.target.value)}
            onSearch={() => submit()}
            loading={loading}
            allowClear
            style={{ flex: '1 1 260px', minWidth: 200 }}
          />

          <Badge count={active.length} size="small" offset={[-4, 4]}>
            <Button onClick={() => setDrawerOpen(true)}>Filters</Button>
          </Badge>

          <Select
            value={filters.sort}
            onChange={changeSort}
            style={{ width: 178 }}
            options={[
              { value: 'RecentlySeen', label: 'Recently seen' },
              { value: 'PriceAscending', label: 'Price, low to high' },
              { value: 'PriceDescending', label: 'Price, high to low' },
              { value: 'YearDescending', label: 'Newest first' },
              { value: 'MileageAscending', label: 'Lowest mileage' },
            ]}
          />
        </Flex>

        {/* Applied filters stay visible as removable chips. Hidden behind a drawer they are
            invisible, and an invisible filter is how someone concludes the catalogue is empty
            when it is only narrowed. */}
        {active.length > 0 && (
          <Flex gap={6} wrap style={{ marginTop: 10 }}>
            {active.map((key) => (
              <Tag key={key} closable onClose={() => clearOne(key)} style={{ marginInlineEnd: 0 }}>
                {LABELS[key]}: {chipValue(filters[key] as string | number)}
              </Tag>
            ))}

            <Button type="link" size="small" onClick={clearAll} style={{ padding: 0, height: 22 }}>
              Clear all
            </Button>
          </Flex>
        )}
      </Card>

      {error && <Alert type="error" showIcon message={error} />}

      {capped && (
        <Alert
          type="info"
          showIcon
          message={`Showing the first ${BROWSE_PAGE_SIZE} of `
            + `${result.totalCount.toLocaleString()} vehicles. Search or filter to narrow the `
            + 'catalogue down to what you are actually looking for.'}
        />
      )}

      {result && result.items.length === 0 && !loading ? (
        <Card size="small">
          <div style={{ padding: '48px 16px' }}>
            <Empty
              description={
                // With no filters applied, "nothing matched" is not the real explanation:
                // either the catalogue is empty or this person has switched their sources off.
                // Saying so beats a bare "no results" that reads as a broken catalogue.
                hasQuery
                  ? 'No vehicles match these filters.'
                  : 'Nothing to show. The catalogue may be empty, or you may have switched '
                    + 'your sources off.'
              }
            >
              {hasQuery
                ? <Button onClick={clearAll}>Clear filters</Button>
                : <Button onClick={onOpenMySources}>My sources</Button>}
            </Empty>
          </div>
        </Card>
      ) : (
        <VehicleCards
          items={result?.items ?? []}
          loading={loading}
          onOpen={onOpenVehicle}
        />
      )}

      {result && result.totalCount > pageSize && (
        <Flex justify="flex-end">
          <Pagination
            current={page}
            total={result.totalCount}
            pageSize={pageSize}
            showSizeChanger={false}
            onChange={(p) => {
              setPage(p);
              void runSearch(p, filters);
            }}
          />
        </Flex>
      )}

      <Drawer
        title="Filters"
        open={drawerOpen}
        onClose={() => setDrawerOpen(false)}
        width={340}
        footer={
          <Flex gap={8} justify="flex-end">
            <Button onClick={clearAll}>Clear all</Button>
            <Button
              type="primary"
              onClick={() => {
                submit();
                setDrawerOpen(false);
              }}
            >
              Apply
            </Button>
          </Flex>
        }
      >
        <Form layout="vertical">
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

          <Form.Item label="Transmission">
            <Select
              allowClear
              placeholder="Any"
              value={filters.transmission}
              onChange={(v) => set('transmission', v)}
              options={['Manual', 'Automatic', 'ContinuouslyVariable', 'DualClutch']
                .map((v) => ({ value: v, label: v }))}
            />
          </Form.Item>

          <Row gutter={12}>
            <Col span={12}>
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

            <Col span={12}>
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
          </Row>

          <Form.Item label="Max mileage (km)">
            <InputNumber
              style={{ width: '100%' }}
              value={filters.maxMileage}
              onChange={(v) => set('maxMileage', v ?? undefined)}
              min={0}
              step={10_000}
            />
          </Form.Item>

          <Row gutter={12}>
            <Col span={12}>
              <Form.Item label="Min price">
                <InputNumber
                  style={{ width: '100%' }}
                  value={filters.minPrice}
                  onChange={(v) => set('minPrice', v ?? undefined)}
                  min={0}
                  step={1000}
                />
              </Form.Item>
            </Col>

            <Col span={12}>
              <Form.Item label="Max price">
                <InputNumber
                  style={{ width: '100%' }}
                  value={filters.maxPrice}
                  onChange={(v) => set('maxPrice', v ?? undefined)}
                  min={0}
                  step={1000}
                />
              </Form.Item>
            </Col>
          </Row>

          {/* Prices filter on the base currency, which only listings with a pinned exchange
              rate have (decision D6). Said here rather than discovered as a missing car. */}
          <Typography.Text type="secondary" style={{ fontSize: 12 }}>
            Price filters apply to the converted base-currency price. A listing whose currency
            has no exchange rate is excluded from a price range rather than converted at a guess.
          </Typography.Text>
        </Form>
      </Drawer>
    </Space>
  );
}
