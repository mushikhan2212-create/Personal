import { useEffect, useState } from 'react';
import {
  Alert, Button, Card, Descriptions, Flex, Select, Space, Statistic, Typography, Upload,
} from 'antd';
import type { UploadFile } from 'antd/es/upload/interface';
import { importFile, listSources } from '../api/client';
import type { ImportResult, VehicleSourceSummary } from '../api/types';

interface Props {
  onBack: () => void;
  onImported: () => void;
}

/**
 * Loads a JSON document into the catalog.
 *
 * The platform does not fetch from exporter websites (decision D13) - it accepts data, and
 * where that data came from is the operator's decision. This screen is the whole of that
 * story: pick the source the stock belongs to, check the file, then commit it.
 */
export function ImportPage({ onBack, onImported }: Props) {
  const [sources, setSources] = useState<VehicleSourceSummary[]>([]);
  const [code, setCode] = useState<string | undefined>();
  const [file, setFile] = useState<UploadFile | null>(null);
  const [result, setResult] = useState<ImportResult | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);

  useEffect(() => {
    listSources()
      .then((all) => {
        // Only DealerJson sources can read this format. Offering the others would let someone
        // pick a source that answers 400, which is a worse experience than not offering it.
        const importable = all.filter((s) => s.providerType === 'DealerJson');
        setSources(importable);
        setCode(importable[0]?.code);
      })
      .catch(() => setError('Could not load the source list.'));
  }, []);

  const run = async (dryRun: boolean): Promise<void> => {
    const raw = file?.originFileObj;

    if (!code || !raw) return;

    setBusy(true);
    setError(null);

    try {
      const outcome = await importFile(code, raw as File, dryRun);
      setResult(outcome);

      if (!dryRun) onImported();
    } catch (e) {
      setResult(null);
      setError(e instanceof Error ? e.message : 'Import failed.');
    } finally {
      setBusy(false);
    }
  };

  return (
    <Space direction="vertical" size={16} style={{ width: '100%', maxWidth: 900 }}>
      <Flex justify="space-between" align="center">
        <Typography.Title level={3} style={{ margin: 0 }}>Import vehicles</Typography.Title>
        <Button onClick={onBack}>Back to search</Button>
      </Flex>

      <Card size="small">
        <Space direction="vertical" size={12} style={{ width: '100%' }}>
          <div>
            <Typography.Text strong>Source</Typography.Text>
            <Select
              style={{ width: '100%', marginTop: 4 }}
              value={code}
              onChange={setCode}
              placeholder="No import-capable source is registered"
              options={sources.map((s) => ({
                value: s.code,
                label: `${s.name} (${s.code}) — ${s.vehicleCount.toLocaleString()} listing(s)`,
              }))}
            />
            <Typography.Text type="secondary" style={{ fontSize: 12 }}>
              Only sources registered as DealerJson can read this format. Every imported car is
              attributed to the source you pick.
            </Typography.Text>
          </div>

          <Upload
            accept="application/json,.json"
            maxCount={1}
            beforeUpload={() => false}
            fileList={file ? [file] : []}
            onChange={({ fileList }) => {
              setFile(fileList[0] ?? null);
              setResult(null);
            }}
          >
            <Button>Choose a JSON file</Button>
          </Upload>

          <Space>
            <Button onClick={() => void run(true)} loading={busy} disabled={!code || !file}>
              Check without importing
            </Button>
            <Button
              type="primary"
              onClick={() => void run(false)}
              loading={busy}
              disabled={!code || !file}
            >
              Import
            </Button>
          </Space>

          {/* Dry run first is a habit worth building: a malformed file then costs a few
              seconds and a report rather than a half-finished import to unpick. */}
          <Typography.Text type="secondary" style={{ fontSize: 12 }}>
            Check first on an unfamiliar file. It reports exactly what an import would do and
            writes nothing.
          </Typography.Text>
        </Space>
      </Card>

      {error && <Alert type="error" showIcon message={error} />}

      {result && (
        <Card
          size="small"
          title={result.dryRun ? 'Check only — nothing was written' : 'Import complete'}
        >
          <Flex gap={32} wrap style={{ marginBottom: 16 }}>
            <Statistic title={result.dryRun ? 'Would create' : 'Created'} value={result.created} />
            <Statistic title={result.dryRun ? 'Would update' : 'Updated'} value={result.updated} />
            <Statistic title="Merged with existing" value={result.autoMerged} />
            <Statistic
              title="Failed"
              value={result.failed}
              valueStyle={result.failed > 0 ? { color: '#cf1322' } : undefined}
            />
          </Flex>

          <Descriptions column={1} size="small" bordered>
            <Descriptions.Item label="Records in file">{result.recordsInFile}</Descriptions.Item>

            {/* The two numbers that explain a disappointing import, which is why they are
                spelled out rather than left to be inferred from a shortfall. */}
            <Descriptions.Item label="Skipped — outside this source's coverage">
              {result.skippedOutOfScope}
            </Descriptions.Item>
            <Descriptions.Item label="No VIN, chassis or lot number">
              {result.withoutStrongIdentifier}
              <Typography.Text type="secondary" style={{ marginLeft: 8, fontSize: 12 }}>
                these cannot be merged with the same car from another source
              </Typography.Text>
            </Descriptions.Item>

            <Descriptions.Item label="Took">{result.elapsedMs} ms</Descriptions.Item>
          </Descriptions>

          {result.errorMessage && (
            <Alert type="error" showIcon style={{ marginTop: 12 }} message={result.errorMessage} />
          )}
        </Card>
      )}
    </Space>
  );
}
