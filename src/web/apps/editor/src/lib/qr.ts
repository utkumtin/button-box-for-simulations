import qrcodegen from '../third_party/qrcodegen/qrcodegen';

export const QR_QUIET_ZONE = 4;

export interface QrGeometry {
  /** Side length in modules, including the quiet zone on both sides. */
  size: number;
  /** SVG path data: one 1x1 square per dark module, already offset by the quiet zone. */
  path: string;
}

export function qrGeometryFromGrid(modules: boolean[][], border = QR_QUIET_ZONE): QrGeometry {
  const parts: string[] = [];
  modules.forEach((row, y) => {
    row.forEach((dark, x) => {
      if (dark) parts.push(`M${x + border},${y + border}h1v1h-1z`);
    });
  });
  return { size: modules.length + border * 2, path: parts.join('') };
}

export function buildQr(text: string): QrGeometry {
  const qr = qrcodegen.QrCode.encodeText(text, qrcodegen.QrCode.Ecc.MEDIUM);
  const grid: boolean[][] = [];
  for (let y = 0; y < qr.size; y++) {
    const row: boolean[] = [];
    for (let x = 0; x < qr.size; x++) row.push(qr.getModule(x, y));
    grid.push(row);
  }
  return qrGeometryFromGrid(grid);
}
