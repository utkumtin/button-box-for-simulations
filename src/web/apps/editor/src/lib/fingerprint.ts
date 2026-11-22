export interface FingerprintGroup {
  /** One byte as two upper-case hex digits. */
  text: string;
  /** True for the first 4 and the last 4 groups, which the user compares first. */
  highlight: boolean;
}

const SHA256_HEX = /^[0-9A-F]{64}$/;
const EDGE_GROUPS = 4;

/** Splits a SHA-256 hex fingerprint into 32 byte groups, the way iOS displays it. Throws on malformed input. */
export function groupFingerprint(hex: string): FingerprintGroup[] {
  if (!SHA256_HEX.test(hex)) throw new Error('Malformed fingerprint');
  const groups: FingerprintGroup[] = [];
  for (let i = 0; i < 32; i++) {
    groups.push({
      text: hex.slice(i * 2, i * 2 + 2),
      highlight: i < EDGE_GROUPS || i >= 32 - EDGE_GROUPS,
    });
  }
  return groups;
}
