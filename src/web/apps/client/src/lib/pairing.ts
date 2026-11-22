import type { PairRequest, PairResponse } from '@bbs/protocol';
import { parseDevice } from './device';
import { pairErrorMessage } from './pairErrors';

export type PairResult = { ok: true; response: PairResponse } | { ok: false; message: string };

export async function pair(request: PairRequest): Promise<PairResult> {
  let res: Response;
  try {
    res = await fetch('/api/pair', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(request),
    });
  } catch {
    return { ok: false, message: 'Cannot reach the PC. Check the Wi-Fi and try again.' };
  }
  if (res.status !== 200) return { ok: false, message: pairErrorMessage(res.status) };
  try {
    // Same shape check as stored devices: a malformed token would otherwise be saved and only rejected on reload.
    const device = parseDevice(await res.text());
    if (device) return { ok: true, response: device satisfies PairResponse };
  } catch {
    // Falls through to the generic message.
  }
  return { ok: false, message: 'Try again' };
}
