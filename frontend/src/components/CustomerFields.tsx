import { Flex, Form, Input, Select, Typography } from 'antd';
import type { CustomerStatus } from '../api/types';

const STATUSES: CustomerStatus[] = ['Lead', 'Active', 'Customer', 'Dormant', 'Closed'];

const LEAD_SOURCES = [
  'WalkIn', 'Referral', 'Website', 'WhatsApp', 'SocialMedia', 'Marketplace', 'Repeat',
] as const;

interface Props {
  /**
   * Whether to offer the opening note.
   *
   * Only on create. Afterwards notes are their own dated log with its own controls, and a box
   * here would either duplicate that or - worse - look like it works and quietly do nothing,
   * since the update path deliberately ignores the field.
   */
  firstNote: boolean;
}

/**
 * The fields a customer record is made of, shared by the add and edit forms.
 *
 * Shared rather than written twice because of what the update endpoint does: it applies every
 * field from the request, so a field missing from one form is not left alone - it is set to
 * null. Two hand-maintained copies of this list would mean the first field added to one of them
 * silently wipes itself whenever somebody edits from the other screen.
 *
 * The same rule is why <c>preferredLanguage</c> is here at all: the detail page displays it, so
 * an edit form without it would blank a value the user can see.
 */
export function CustomerFields({ firstNote }: Props) {
  return (
    <>
      <Flex gap={12}>
        <Form.Item name="firstName" label="First name" style={{ flex: 1 }}>
          <Input />
        </Form.Item>
        <Form.Item name="lastName" label="Last name" style={{ flex: 1 }}>
          <Input />
        </Form.Item>
      </Flex>

      <Form.Item name="phone" label="Phone" help="Usually the WhatsApp number too.">
        <Input />
      </Form.Item>

      <Form.Item name="email" label="Email" style={{ marginTop: 16 }}>
        <Input />
      </Form.Item>

      <Flex gap={12}>
        <Form.Item name="city" label="City" style={{ flex: 1 }}>
          <Input />
        </Form.Item>
        <Form.Item name="countryCode" label="Country" style={{ width: 110 }}>
          <Input placeholder="PK" maxLength={2} />
        </Form.Item>
      </Flex>

      <Flex gap={12}>
        <Form.Item name="status" label="Status" initialValue="Lead" style={{ flex: 1 }}>
          <Select options={STATUSES.map((s) => ({ value: s, label: s }))} />
        </Form.Item>
        <Form.Item name="leadSource" label="Came from" style={{ flex: 1 }}>
          <Select
            allowClear
            placeholder="Unknown"
            options={LEAD_SOURCES.map((s) => ({ value: s, label: s }))}
          />
        </Form.Item>
      </Flex>

      <Form.Item
        name="preferredLanguage"
        label="Language"
        help="What to write to them in. Shown on their page."
      >
        <Input placeholder="en, ur, ja" maxLength={16} />
      </Form.Item>

      {firstNote && (
        // Becomes the first entry in the customer's note log rather than a field on the
        // record — so "referred by his brother, pays cash" sits in the same list as
        // everything learned afterwards.
        <Form.Item
          name="notes"
          label="First note"
          tooltip="Optional. Anything you already know."
          style={{ marginTop: 16 }}
        >
          <Input.TextArea rows={3} placeholder="How you know them, what they're after…" />
        </Form.Item>
      )}

      {/* The API requires one of four. Said here so the 400 is never a surprise. */}
      <Typography.Text type="secondary" style={{ fontSize: 12 }}>
        A name or a way to contact them is required — everything else can follow.
      </Typography.Text>
    </>
  );
}
