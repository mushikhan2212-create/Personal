import { Button } from 'antd';
import type { ButtonProps } from 'antd';

interface Props {
  onClick: () => void;
  size?: ButtonProps['size'];
  children?: string;
}

/**
 * The one way into the WhatsApp compose drawer, so the action looks the same everywhere.
 *
 * WhatsApp's green, because a button that opens WhatsApp should be recognisable as one at a
 * glance - it is the only place in the app that borrows a colour from outside the palette, and
 * that is the point.
 */
export function WhatsAppButton({ onClick, size, children }: Props) {
  return (
    <Button size={size} onClick={onClick} icon={<WhatsAppGlyph />} style={{ color: '#128C7E' }}>
      {children ?? 'WhatsApp'}
    </Button>
  );
}

function WhatsAppGlyph() {
  return (
    <svg width="15" height="15" viewBox="0 0 24 24" fill="currentColor" aria-hidden="true">
      <path d="M12.04 2C6.58 2 2.13 6.45 2.13 11.91c0 1.75.46 3.45 1.32 4.95L2 22l5.25-1.38a9.9 9.9 0 004.79 1.22h.01c5.46 0 9.91-4.45 9.91-9.91 0-2.65-1.03-5.14-2.9-7.01A9.82 9.82 0 0012.04 2zm0 1.67c2.2 0 4.27.86 5.83 2.42a8.18 8.18 0 012.41 5.82c0 4.54-3.7 8.24-8.25 8.24a8.23 8.23 0 01-4.2-1.15l-.3-.18-3.12.82.83-3.04-.2-.31a8.19 8.19 0 01-1.26-4.38c0-4.54 3.7-8.24 8.26-8.24zm-2.5 4.1c-.17 0-.44.06-.67.31-.23.25-.88.86-.88 2.1s.9 2.44 1.03 2.6c.13.17 1.76 2.68 4.27 3.76.6.26 1.06.41 1.42.53.6.19 1.14.16 1.57.1.48-.07 1.48-.6 1.68-1.19.21-.58.21-1.08.15-1.19-.06-.1-.23-.16-.48-.29-.25-.12-1.48-.73-1.71-.81-.23-.09-.4-.13-.56.12-.17.25-.64.81-.79.98-.14.16-.29.19-.54.06-.25-.12-1.05-.39-2-1.24a7.5 7.5 0 01-1.39-1.72c-.14-.25-.01-.38.11-.5.11-.11.25-.29.37-.44.13-.14.17-.25.25-.41.09-.17.04-.31-.02-.44-.06-.12-.55-1.35-.76-1.84-.2-.48-.4-.42-.55-.42h-.47z" />
    </svg>
  );
}
