import { useMemo } from 'react';
import { buildQr } from './lib/qr';

/** QR code as SVG elements (never innerHTML). Dark modules on a light background, also in the dark theme. */
export function QrCode({ text }: { text: string }) {
  const qr = useMemo(() => {
    try {
      return buildQr(text);
    } catch {
      return null;
    }
  }, [text]);
  if (!qr) return null;
  return (
    <svg
      className="qr"
      viewBox={`0 0 ${qr.size} ${qr.size}`}
      shapeRendering="crispEdges"
      role="img"
      aria-label={`QR code for ${text}`}
    >
      <rect width={qr.size} height={qr.size} fill="#ffffff" />
      <path d={qr.path} fill="#000000" />
    </svg>
  );
}
