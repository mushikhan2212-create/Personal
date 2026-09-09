import { useEffect, useRef, useState } from 'react';
import { Alert, App as AntApp, Button, Drawer, Flex, Input, Select, Skeleton, Tag, Typography } from 'antd';
import { draftWhatsApp, listCustomers, savePhoto } from '../api/client';
import type { CustomerListItem, MessageDraft, MessagePhoto } from '../api/types';
import { DownloadGlyph } from './icons';

interface Props {
  open: boolean;
  onClose: () => void;
  /**
   * Who the message goes to. Null opens the drawer with a customer picker instead - which is
   * how the vehicle screen uses it, where the car is known and the person is not.
   */
  customerPublicId: string | null;
  /** The car to write about, if there is one. */
  vehiclePublicId?: string;
  /** For the drawer title, so it is obvious who is being messaged. */
  customerName?: string;
}

/** Long enough that typing does not fire a request per keystroke, short enough to feel live. */
const REDRAFT_DELAY_MS = 400;

/**
 * Composing a WhatsApp message to a customer.
 *
 * The message is editable and nothing leaves the platform until a person taps through — master
 * prompt §18 excludes autonomous customer messaging, and a draft somebody reviews is the
 * difference between assisting and acting.
 *
 * The link is rebuilt server-side on every edit rather than assembled here. That keeps the
 * phone normalisation — which decides whether a number can be reached at all, and refuses
 * rather than guessing a country — in one tested place, and it means the anchor below always
 * carries a URL matching the text on screen. A real anchor rather than window.open, because a
 * popup opened after an await is blocked by the browser and the failure is silent.
 */
export function WhatsAppDrawer({
  open, onClose, customerPublicId, vehiclePublicId, customerName,
}: Props) {
  const { message } = AntApp.useApp();

  const [draft, setDraft] = useState<MessageDraft | null>(null);
  const [body, setBody] = useState('');
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [picked, setPicked] = useState<string | null>(null);
  const [choices, setChoices] = useState<CustomerListItem[]>([]);
  const [searching, setSearching] = useState(false);
  const [savingPhotos, setSavingPhotos] = useState(false);

  const needsPicker = customerPublicId === null;
  const activeCustomer = customerPublicId ?? picked;

  // Distinguishes "opening, compose one for me" from "they edited it, keep their words".
  const composed = useRef(false);

  useEffect(() => {
    if (!open || !needsPicker) return;

    // The first page of customers, so the picker is useful before anyone types. Searching
    // narrows it server-side rather than filtering a list the browser happens to hold.
    setSearching(true);

    listCustomers('', undefined, 1, 25)
      .then((result) => setChoices(result.items))
      .catch(() => setChoices([]))
      .finally(() => setSearching(false));
  }, [open, needsPicker]);

  useEffect(() => {
    if (!open) return;

    // Reopening with no customer chosen yet starts clean rather than showing the last
    // person's message.
    if (!activeCustomer) {
      setDraft(null);
      setBody('');
      composed.current = false;
      return;
    }

    composed.current = false;
    setBody('');
    setDraft(null);
    setError(null);
    setLoading(true);

    let cancelled = false;

    draftWhatsApp(activeCustomer, vehiclePublicId)
      .then((result) => {
        if (cancelled) return;

        setDraft(result);
        setBody(result.body);
        composed.current = true;
      })
      .catch((e: unknown) => {
        if (!cancelled) setError(e instanceof Error ? e.message : 'Could not prepare a message.');
      })
      .finally(() => { if (!cancelled) setLoading(false); });

    return () => { cancelled = true; };
  }, [open, activeCustomer, vehiclePublicId]);

  useEffect(() => {
    // Only after the first composition, so this does not fire a second request for the text
    // the server just sent us.
    if (!open || !activeCustomer || !composed.current) return;

    const timer = setTimeout(() => {
      draftWhatsApp(activeCustomer, vehiclePublicId, body)
        .then(setDraft)
        // A failed re-link leaves the previous one in place; the text on screen is what the
        // salesperson can always copy by hand.
        .catch(() => undefined);
    }, REDRAFT_DELAY_MS);

    return () => clearTimeout(timer);
  }, [body, open, activeCustomer, vehiclePublicId]);

  const saveAll = async (): Promise<void> => {
    if (!draft) return;

    setSavingPhotos(true);

    try {
      // One at a time. Firing ten downloads at once makes the browser's pop-up blocker treat
      // the later ones as unsolicited, and they vanish without saying so.
      for (const photo of draft.photos) {
        if (photo.downloadUrl) await savePhoto(photo.downloadUrl, `photo-${photo.index + 1}.jpg`);
      }

      void message.success(
        `${draft.photos.length} photo${draft.photos.length === 1 ? '' : 's'} saved. `
        + 'Attach them in WhatsApp with the paperclip.',
        6,
      );
    } catch (e) {
      message.error(e instanceof Error ? e.message : 'Could not save the photos.');
    } finally {
      setSavingPhotos(false);
    }
  };

  const ready = draft?.canSend === true && draft.handoffUrl !== null;

  return (
    <Drawer
      title={customerName ? `Message ${customerName}` : 'Send a WhatsApp message'}
      open={open}
      onClose={() => { setPicked(null); onClose(); }}
      width={480}
      footer={
        <Flex gap={8} justify="flex-end" align="center">
          <Button onClick={() => { setPicked(null); onClose(); }}>Cancel</Button>

          <Button
            type="primary"
            disabled={!ready}
            href={ready ? draft.handoffUrl! : undefined}
            target="_blank"
            rel="noreferrer noopener"
            style={ready ? { background: '#25D366', borderColor: '#25D366' } : undefined}
          >
            Open WhatsApp
          </Button>
        </Flex>
      }
    >
      <Flex vertical gap={14}>
        {needsPicker && (
          <Select
            showSearch
            allowClear
            value={picked}
            onChange={(v) => setPicked(v ?? null)}
            onSearch={(q) => {
              setSearching(true);
              void listCustomers(q, undefined, 1, 25)
                .then((result) => setChoices(result.items))
                .catch(() => setChoices([]))
                .finally(() => setSearching(false));
            }}
            loading={searching}
            filterOption={false}
            placeholder="Which customer?"
            notFoundContent={searching ? 'Searching…' : 'No customers match that.'}
            style={{ width: '100%' }}
            options={choices.map((c) => ({
              value: c.publicId,
              label: [c.firstName, c.lastName].filter(Boolean).join(' ')
                || c.phone
                || c.email
                || '(no name)',
            }))}
          />
        )}

        {needsPicker && !activeCustomer && (
          <Typography.Text type="secondary" style={{ fontSize: 12 }}>
            Pick a customer and a message about this car will be written for you.
          </Typography.Text>
        )}
      </Flex>

      {!activeCustomer ? null : loading && !draft ? (
        <Skeleton active paragraph={{ rows: 6 }} style={{ marginTop: 14 }} />
      ) : (
        <Flex vertical gap={14} style={{ marginTop: 14 }}>
          {error && <Alert type="error" showIcon message={error} />}

          {draft && !draft.canSend && (
            // Not an error — it is something a person fixes on the customer record, so it says
            // what to fix rather than that something went wrong.
            <Alert
              type="warning"
              showIcon
              message="No WhatsApp link for this customer"
              description={draft.reason}
            />
          )}

          {draft?.canSend && (
            <Flex align="center" gap={8} wrap>
              <Typography.Text type="secondary" style={{ fontSize: 12 }}>To</Typography.Text>
              <Tag style={{ marginInlineEnd: 0 }}>+{draft.normalizedPhone}</Tag>

              {draft.to && draft.to.replace(/\D/g, '') !== draft.normalizedPhone && (
                // The stored number and the dialled one differ when a national number was
                // resolved from the customer's country. Showing both means a wrong country on
                // the record is visible here, before the message goes anywhere.
                <Typography.Text type="secondary" style={{ fontSize: 12 }}>
                  stored as {draft.to}
                </Typography.Text>
              )}
            </Flex>
          )}

          <Input.TextArea
            value={body}
            onChange={(e) => setBody(e.target.value)}
            autoSize={{ minRows: 10, maxRows: 20 }}
            placeholder="Your message"
          />

          {(draft?.photos.length ?? 0) > 0 && (
            <Photos
              photos={draft!.photos}
              saving={savingPhotos}
              onSaveAll={() => void saveAll()}
            />
          )}

          <Typography.Text type="secondary" style={{ fontSize: 12 }}>
            {draft?.canSendDirectly
              ? 'This will be sent from your business number.'
              : 'This opens WhatsApp on your device with the message ready — nothing is sent '
                + 'until you press send there. Replies go to your phone, not to this app.'}
          </Typography.Text>
        </Flex>
      )}
    </Drawer>
  );
}

/**
 * The car's photos, to save and attach.
 *
 * WhatsApp's click-to-chat link carries text and nothing else, so a photo cannot travel in the
 * message however it is encoded. Putting the image URL in the text is the usual workaround and
 * is worse than useless here: it names the exporter to the customer, which is exactly what
 * leaving the source listing link out was for. So the photos are downloaded and attached by
 * hand - two taps, and the customer receives a picture with no address on it.
 *
 * Sending the image itself becomes possible with the WhatsApp Business API, which supports an
 * image message with a caption.
 */
function Photos({ photos, saving, onSaveAll }: {
  photos: MessagePhoto[];
  saving: boolean;
  onSaveAll: () => void;
}) {
  return (
    <Flex vertical gap={8}>
      <Flex justify="space-between" align="center" gap={8} wrap>
        <Typography.Text type="secondary" style={{ fontSize: 12 }}>
          {photos.length} photo{photos.length === 1 ? '' : 's'} — save and attach in WhatsApp
        </Typography.Text>

        <Button size="small" loading={saving} icon={<DownloadGlyph />} onClick={onSaveAll}>
          Save {photos.length === 1 ? 'photo' : 'all'}
        </Button>
      </Flex>

      <Flex gap={6} wrap>
        {photos.map((photo) => (
          <img
            key={photo.index}
            src={photo.url}
            alt=""
            loading="lazy"
            onError={(e) => { e.currentTarget.style.display = 'none'; }}
            style={{
              width: 68,
              height: 50,
              objectFit: 'cover',
              borderRadius: 6,
              border: '1px solid var(--app-stroke)',
            }}
          />
        ))}
      </Flex>
    </Flex>
  );
}
