import { useEffect, useMemo, useState } from 'react';
import {
  Alert, App as AntApp, Button, Card, Empty, Flex, Input, Modal, Popconfirm, Skeleton, Space,
  Tag, Typography,
} from 'antd';
import {
  createMessageTemplate, deleteMessageTemplate, listMessageTemplates,
  restoreStarterTemplates, updateMessageTemplate,
} from '../api/client';
import type { MessageTemplate, TemplatePlaceholder } from '../api/types';

/**
 * The dealer's own message wording.
 *
 * Editing is deliberately a modal over the list rather than a second screen: a template is
 * short, and the thing somebody needs while writing one is the list of placeholders, which fits
 * beside it. A separate route would mean navigating away from the set to change one of them.
 *
 * The preview is the part that earns its keep. A template is written in placeholders and read
 * as prose, and the two look nothing alike - in particular the rule that an empty placeholder
 * removes its whole line is impossible to guess and obvious once seen.
 */
export function TemplatesPage() {
  const { message } = AntApp.useApp();

  const [templates, setTemplates] = useState<MessageTemplate[]>([]);
  const [placeholders, setPlaceholders] = useState<TemplatePlaceholder[]>([]);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);
  const [editing, setEditing] = useState<MessageTemplate | 'new' | null>(null);

  const load = (): void => {
    setLoading(true);

    listMessageTemplates()
      .then((result) => {
        setTemplates(result.items);
        setPlaceholders(result.placeholders);
        setError(null);
      })
      .catch((e: unknown) =>
        setError(e instanceof Error ? e.message : 'Could not load the templates.'))
      .finally(() => setLoading(false));
  };

  useEffect(load, []);

  const remove = async (template: MessageTemplate): Promise<void> => {
    try {
      await deleteMessageTemplate(template.id);
      void message.success(`Deleted "${template.name}".`);
      load();
    } catch (e) {
      message.error(e instanceof Error ? e.message : 'Could not delete it.');
    }
  };

  const restore = async (): Promise<void> => {
    try {
      const { restored } = await restoreStarterTemplates();

      void message.success(
        restored.length === 0
          ? 'Nothing to restore — you already have all of them.'
          : `Put back: ${restored.join(', ')}.`);

      load();
    } catch (e) {
      message.error(e instanceof Error ? e.message : 'Could not restore them.');
    }
  };

  return (
    <Flex vertical gap={16}>
      <Flex justify="space-between" align="flex-start" gap={16} wrap>
        <Flex vertical gap={4}>
          <Typography.Title level={4} style={{ margin: 0 }}>Message templates</Typography.Title>

          <Typography.Text type="secondary">
            What a message starts from. You edit every one before it goes, so treat these as a
            first draft rather than a final word.
          </Typography.Text>
        </Flex>

        <Space>
          <Button onClick={() => void restore()}>Restore starters</Button>
          <Button type="primary" onClick={() => setEditing('new')}>New template</Button>
        </Space>
      </Flex>

      {error && <Alert type="error" showIcon message={error} />}

      {loading ? (
        <Skeleton active paragraph={{ rows: 8 }} />
      ) : templates.length === 0 ? (
        <Empty description="No templates yet.">
          <Button type="primary" onClick={() => void restore()}>Add the starters</Button>
        </Empty>
      ) : (
        <Flex vertical gap={12}>
          {templates.map((t) => (
            <Card key={t.id} size="small">
              <Flex justify="space-between" align="flex-start" gap={16} wrap>
                <Flex vertical gap={6} style={{ minWidth: 0, flex: 1 }}>
                  <Flex align="center" gap={8} wrap>
                    <Typography.Text strong>{t.name}</Typography.Text>

                    {t.needsVehicle && (
                      <Tag>needs a car</Tag>
                    )}

                    {t.revealsSource && (
                      <Tag color="warning">shows the exporter&apos;s link</Tag>
                    )}
                  </Flex>

                  <Typography.Paragraph
                    type="secondary"
                    style={{ margin: 0, whiteSpace: 'pre-wrap', fontSize: 12 }}
                    ellipsis={{ rows: 4 }}
                  >
                    {t.body}
                  </Typography.Paragraph>
                </Flex>

                <Space>
                  <Button size="small" onClick={() => setEditing(t)}>Edit</Button>

                  <Popconfirm
                    title={`Delete "${t.name}"?`}
                    description="You can put the starters back afterwards."
                    okText="Delete"
                    okButtonProps={{ danger: true }}
                    onConfirm={() => void remove(t)}
                  >
                    <Button size="small" danger>Delete</Button>
                  </Popconfirm>
                </Space>
              </Flex>
            </Card>
          ))}
        </Flex>
      )}

      {editing && (
        <TemplateEditor
          template={editing === 'new' ? null : editing}
          placeholders={placeholders}
          onClose={() => setEditing(null)}
          onSaved={() => { setEditing(null); load(); }}
        />
      )}
    </Flex>
  );
}

interface EditorProps {
  template: MessageTemplate | null;
  placeholders: TemplatePlaceholder[];
  onClose: () => void;
  onSaved: () => void;
}

/** Stand-in values, so the preview reads like a message rather than like a form. */
const SAMPLE: Record<string, string> = {
  FirstName: 'Imran',
  LastName: 'Sheikh',
  City: 'Karachi',
  Country: 'PK',
  DealerName: 'your business name',
  Vehicle: 'Toyota Corolla Altis',
  Make: 'Toyota',
  Model: 'Corolla',
  Variant: 'Altis',
  Year: '2016',
  Mileage: '62,620 km',
  Colour: 'Black',
  Fuel: 'Petrol',
  Transmission: 'CVT',
  Steering: 'RHD',
  Drivetrain: 'FWD',
  BodyType: 'Sedan',
  Engine: '1,800cc',
  Price: 'USD 7,200',
  ListingUrl: 'https://exporter.example/stock/12345',
};

/**
 * Mirrors TemplateRenderer closely enough to show what the rule does.
 *
 * Deliberately not a second implementation of it: the server renders what actually gets sent,
 * and this only has to make the line-drop rule visible while somebody types. It is driven by the
 * same placeholder list the API serves, so a placeholder added on the server appears here
 * without a second edit.
 */
function preview(body: string, known: Set<string>, omit: Set<string>): string {
  const token = /\{([A-Za-z][A-Za-z0-9]*)(?:\|([^}]*))?\}/g;

  const lines = body.replace(/\r\n?/g, '\n').split('\n').flatMap((line) => {
    let dropped = false;

    const rendered = line.replace(token, (_match, name: string, fallback?: string) => {
      const value = known.has(name) && !omit.has(name) ? SAMPLE[name] ?? '' : '';

      if (value !== '') return value;
      if (fallback !== undefined) return fallback;

      dropped = true;
      return '';
    });

    return dropped ? [] : [rendered];
  });

  return lines.join('\n').replace(/\n{3,}/g, '\n\n').trim();
}

function TemplateEditor({ template, placeholders, onClose, onSaved }: EditorProps) {
  const { message } = AntApp.useApp();

  const [name, setName] = useState(template?.name ?? '');
  const [body, setBody] = useState(template?.body ?? '');
  const [saving, setSaving] = useState(false);

  // Toggles a field to "not known for this car", which is how the line-drop rule becomes
  // visible: half this catalogue is missing a colour or an engine size, and a template that
  // looks right against a complete car can read badly against a real one.
  const [omit, setOmit] = useState<Set<string>>(new Set());

  const known = useMemo(() => new Set(placeholders.map((p) => p.name)), [placeholders]);

  const unknown = useMemo(() => {
    const found = [...body.matchAll(/\{([A-Za-z][A-Za-z0-9]*)(?:\|[^}]*)?\}/g)]
      // flatMap rather than map+filter: the capture group is typed as possibly undefined, and
      // narrowing it here is honest about that rather than asserting it away.
      .flatMap((m) => (m[1] === undefined ? [] : [m[1]]))
      .filter((n) => !known.has(n));

    return [...new Set(found)];
  }, [body, known]);

  const save = async (): Promise<void> => {
    setSaving(true);

    try {
      if (template) {
        await updateMessageTemplate(template.id, { name: name.trim(), body: body.trim() });
      } else {
        await createMessageTemplate({ name: name.trim(), body: body.trim() });
      }

      void message.success('Saved.');
      onSaved();
    } catch (e) {
      message.error(e instanceof Error ? e.message : 'Could not save it.');
    } finally {
      setSaving(false);
    }
  };

  const insert = (placeholder: string): void =>
    setBody((current) => `${current}{${placeholder}}`);

  return (
    <Modal
      open
      title={template ? `Edit "${template.name}"` : 'New template'}
      onCancel={onClose}
      width={860}
      okText="Save"
      confirmLoading={saving}
      okButtonProps={{
        disabled: name.trim() === '' || body.trim() === '' || unknown.length > 0,
      }}
      onOk={() => void save()}
    >
      <Flex vertical gap={14} style={{ marginTop: 12 }}>
        <Input
          value={name}
          onChange={(e) => setName(e.target.value)}
          placeholder="What you will pick it by, e.g. Price quote"
          maxLength={80}
        />

        <Flex gap={16} wrap align="flex-start">
          <Flex vertical gap={8} style={{ flex: '1 1 380px', minWidth: 280 }}>
            <Typography.Text type="secondary" style={{ fontSize: 12 }}>
              The template
            </Typography.Text>

            <Input.TextArea
              value={body}
              onChange={(e) => setBody(e.target.value)}
              autoSize={{ minRows: 12, maxRows: 22 }}
              placeholder="Hi {FirstName|there},"
            />

            {unknown.length > 0 && (
              <Alert
                type="error"
                showIcon
                message={
                  unknown.length === 1
                    ? `There is no placeholder called {${unknown[0]}}.`
                    : `These placeholders do not exist: ${unknown.map((u) => `{${u}}`).join(', ')}.`
                }
                description="It would be sent to your customer exactly as written, braces and all."
              />
            )}

            {/\{ListingUrl/i.test(body) && (
              <Alert
                type="warning"
                showIcon
                message="This shows the exporter's link"
                description={
                  'The link names who you buy from, so a customer who follows it can go '
                  + 'direct. Keep it only if you mean to.'
                }
              />
            )}
          </Flex>

          <Flex vertical gap={8} style={{ flex: '1 1 320px', minWidth: 260 }}>
            <Typography.Text type="secondary" style={{ fontSize: 12 }}>
              How it will read
            </Typography.Text>

            <div
              style={{
                background: '#DCF8C6',
                color: 'rgba(0,0,0,0.88)',
                borderRadius: 10,
                padding: '10px 12px',
                whiteSpace: 'pre-wrap',
                fontSize: 13,
                minHeight: 160,
              }}
            >
              {preview(body, known, omit) || (
                <Typography.Text type="secondary" style={{ fontSize: 12 }}>
                  Nothing yet.
                </Typography.Text>
              )}
            </div>

            <Typography.Text type="secondary" style={{ fontSize: 12 }}>
              Tap a field to see what happens when a car does not have it — the whole line goes,
              rather than leaving a label with nothing after it. Write
              {' '}<Typography.Text code>{'{Colour|not stated}'}</Typography.Text>{' '}
              to keep the line and fill in something instead.
            </Typography.Text>

            <Flex gap={6} wrap>
              {placeholders.filter((p) => new RegExp(`\\{${p.name}\\b`, 'i').test(body)).map((p) => (
                <Tag.CheckableTag
                  key={p.name}
                  checked={!omit.has(p.name)}
                  onChange={(on) =>
                    setOmit((current) => {
                      const next = new Set(current);

                      if (on) next.delete(p.name);
                      else next.add(p.name);

                      return next;
                    })}
                >
                  {p.name}
                </Tag.CheckableTag>
              ))}
            </Flex>
          </Flex>
        </Flex>

        <Flex vertical gap={6}>
          <Typography.Text type="secondary" style={{ fontSize: 12 }}>
            Click to add
          </Typography.Text>

          <Flex gap={6} wrap>
            {placeholders.map((p) => (
              <Tag
                key={p.name}
                style={{ cursor: 'pointer', marginInlineEnd: 0 }}
                color={p.name === 'ListingUrl' ? 'warning' : undefined}
                onClick={() => insert(p.name)}
                title={p.description}
              >
                {`{${p.name}}`}
              </Tag>
            ))}
          </Flex>
        </Flex>
      </Flex>
    </Modal>
  );
}
