import { CloseCodes } from '@bbs/protocol';

export type CloseAction = 'forget' | 'stop' | 'reconnect';

/** Close code table of decisions §9. */
export function closeAction(code: number): CloseAction {
  if (code === CloseCodes.TokenRejected) return 'forget';
  if (code === CloseCodes.Replaced) return 'stop';
  return 'reconnect';
}
