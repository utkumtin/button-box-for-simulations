import { describe, expect, it } from 'vitest';
import { ApiError, ProblemsError } from '../api';
import { profileErrorMessages, testErrorMessage } from './errors';

describe('profileErrorMessages', () => {
  it('shows the sentences of a 422 as they are, because they tell the user what to fix', () => {
    expect(profileErrorMessages(new ProblemsError(['Widget a overlaps b.', 'Grid too big.']))).toEqual([
      'Widget a overlaps b.',
      'Grid too big.',
    ]);
  });

  it('explains 404, 409 and 503 and never echoes anything from the response', () => {
    for (const status of [404, 409, 503, 500]) {
      const [message] = profileErrorMessages(new ApiError(status));
      expect(message).toBeTruthy();
    }
    expect(profileErrorMessages(new ApiError(409))[0]).toMatch(/active|damaged/);
    expect(profileErrorMessages(new TypeError('Failed to fetch'))).toEqual(['Cannot reach the host.']);
  });
});

describe('testErrorMessage', () => {
  it('409 says test mode needs the active profile; 503 says the queue is full', () => {
    expect(testErrorMessage(new ApiError(409))).toBe('Test mode works on the active profile only.');
    expect(testErrorMessage(new ApiError(503))).toMatch(/queue is full/);
  });
});
