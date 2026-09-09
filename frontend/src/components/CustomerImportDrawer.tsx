import { useState } from 'react';
import {
  Alert, Button, Drawer, Flex, Statistic, Table, Tag, Typography, Upload,
} from 'antd';
import type { UploadFile } from 'antd';
import { importCustomers } from '../api/client';
import type { CustomerImportResult, ImportProblem } from '../api/types';

interface Props {
  open: boolean;
  onClose: () => void;
  /** Called after a real import that created at least one customer. */
  onImported: () => void;
}

/**
 * The columns the importer understands, as a starting file.
 *
 * Offered as a download rather than described in prose, because the failure this prevents -
 * a header the importer does not recognise - is one nobody can debug from a paragraph. The
 * headings are only a suggestion: the API matches many spellings, and the drawer says so.
 */
const TEMPLATE = [
  'First Name,Last Name,Phone,Email,City,Country,Status,Source,Notes',
  'Imran,Sheikh,+92 300 1234567,imran@example.com,Karachi,PK,Lead,Referral,Wants a 2016+ Corolla',
  'Yuki,Nakamura,+81 90 1234 5678,yuki@example.com,Osaka,JP,Active,WalkIn,',
].join('\n');

export function CustomerImportDrawer({ open, onClose, onImported }: Props) {
  const [file, setFile] = useState<UploadFile | null>(null);
  const [result, setResult] = useState<CustomerImportResult | null>(null);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);

  const reset = (): void => {
    setFile(null);
    setResult(null);
    setError(null);
  };

  const run = async (dryRun: boolean): Promise<void> => {
    const raw = file?.originFileObj ?? (file as unknown as File | null);

    if (!raw) return;

    setBusy(true);
    setError(null);

    try {
      const outcome = await importCustomers(raw as File, dryRun);
      setResult(outcome);

      if (!dryRun && outcome.created > 0) onImported();
    } catch (e) {
      setError(e instanceof Error ? e.message : 'The import failed.');
      setResult(null);
    } finally {
      setBusy(false);
    }
  };

  const downloadTemplate = (): void => {
    const url = URL.createObjectURL(new Blob([TEMPLATE], { type: 'text/csv;charset=utf-8' }));
    const link = document.createElement('a');

    link.href = url;
    link.download = 'customers-template.csv';
    link.click();

    // Released on the next tick rather than immediately: revoking synchronously races the
    // download in some browsers and produces an empty file.
    setTimeout(() => URL.revokeObjectURL(url), 1000);
  };

  const problemColumns = [
    { title: 'Row', dataIndex: 'row', key: 'row', width: 64 },
    {
      title: 'Who',
      dataIndex: 'label',
      key: 'label',
      width: 170,
      render: (label: string) => (
        <Typography.Text ellipsis={{ tooltip: label }} style={{ fontSize: 13 }}>
          {label}
        </Typography.Text>
      ),
    },
    {
      title: 'What happened',
      dataIndex: 'message',
      key: 'message',
      render: (message: string) => (
        <Typography.Text style={{ fontSize: 13 }}>{message}</Typography.Text>
      ),
    },
  ];

  return (
    <Drawer
      title="Import customers from a spreadsheet"
      open={open}
      width={640}
      onClose={() => { reset(); onClose(); }}
      footer={
        <Flex gap={8} justify="flex-end">
          <Button onClick={() => { reset(); onClose(); }}>Close</Button>

          <Button disabled={!file || busy} loading={busy} onClick={() => void run(true)}>
            Check without importing
          </Button>

          {/* Enabled only after a dry run that found something to do. Importing blind is the
              mistake this whole screen exists to prevent. */}
          <Button
            type="primary"
            disabled={!file || busy || !result?.dryRun || result.created === 0}
            loading={busy}
            onClick={() => void run(false)}
          >
            Import {result?.dryRun ? `${result.created}` : ''}
          </Button>
        </Flex>
      }
    >
      <Flex vertical gap={16}>
        <Typography.Paragraph type="secondary" style={{ marginBottom: 0 }}>
          A CSV whose first row is a header. Column names are matched loosely — “First Name”,
          “first_name” and “FIRSTNAME” are the same column, and “Mobile”, “WhatsApp” and
          “Contact Number” all mean phone. Anything unrecognised is ignored rather than
          rejected.
        </Typography.Paragraph>

        <Flex gap={8} wrap>
          <Button size="small" onClick={downloadTemplate}>Download a template</Button>
        </Flex>

        <Upload.Dragger
          accept=".csv,text/csv"
          maxCount={1}
          // Stopped here rather than posted by Ant: the upload goes through the API client so
          // it carries the access token and the app's error handling.
          beforeUpload={() => false}
          fileList={file ? [file] : []}
          onChange={({ fileList }) => { setFile(fileList[0] ?? null); setResult(null); }}
          onRemove={() => { reset(); return true; }}
        >
          <Typography.Text>Drop a CSV here, or click to choose one</Typography.Text>
          <br />
          <Typography.Text type="secondary" style={{ fontSize: 12 }}>
            Up to 5,000 customers per file
          </Typography.Text>
        </Upload.Dragger>

        {error && <Alert type="error" showIcon message={error} />}

        {result && <Report result={result} problemColumns={problemColumns} />}
      </Flex>
    </Drawer>
  );
}

function Report({ result, problemColumns }: {
  result: CustomerImportResult;
  problemColumns: object[];
}) {
  return (
    <Flex vertical gap={12}>
      <Alert
        type={result.dryRun ? 'info' : 'success'}
        showIcon
        message={result.dryRun
          ? 'Nothing has been written yet'
          : `${result.created} customer${result.created === 1 ? '' : 's'} imported`}
        description={result.dryRun
          ? 'This is what importing this file would do.'
          : undefined}
      />

      <Flex gap={32} wrap>
        <Statistic
          title={result.dryRun ? 'Would be added' : 'Added'}
          value={result.created}
          valueStyle={{ color: '#10B981' }}
        />
        <Statistic title="Already here" value={result.duplicates} />
        <Statistic
          title="Unreadable"
          value={result.invalid}
          valueStyle={result.invalid > 0 ? { color: '#DC3545' } : undefined}
        />
        <Statistic title="Rows in file" value={result.totalRows} />
      </Flex>

      {result.duplicates > 0 && (
        <Typography.Text type="secondary" style={{ fontSize: 12 }}>
          Someone already on your books is skipped, never overwritten — an import is usually a
          re-import, and overwriting would discard the notes and status your team has edited
          since. Change those records by hand if you need to.
        </Typography.Text>
      )}

      {result.sample.length > 0 && (
        <Flex gap={6} wrap align="center">
          <Typography.Text type="secondary" style={{ fontSize: 12 }}>
            {result.dryRun ? 'For example:' : 'Including:'}
          </Typography.Text>
          {result.sample.map((s) => (
            <Tag key={s} style={{ marginInlineEnd: 0 }}>{s}</Tag>
          ))}
        </Flex>
      )}

      {result.problems.length > 0 && (
        <>
          <Table
            rowKey="row"
            size="small"
            dataSource={result.problems as ImportProblem[]}
            columns={problemColumns}
            pagination={result.problems.length > 8 ? { pageSize: 8, size: 'small' } : false}
          />

          {result.unreportedProblems > 0 && (
            <Typography.Text type="secondary" style={{ fontSize: 12 }}>
              …and {result.unreportedProblems} more not listed.
            </Typography.Text>
          )}
        </>
      )}
    </Flex>
  );
}
