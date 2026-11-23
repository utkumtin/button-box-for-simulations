import { ApiError, ProblemsError } from '../api';

/** Sentences for the user from a failed editor API call. Response bodies are never shown (the API class drops them). */

/** A failed change to the profile files: list, create, import, save, duplicate, delete, activate. */
export function profileErrorMessages(e: unknown): string[] {
  if (e instanceof ProblemsError) {
    return e.problems;
  }
  if (e instanceof ApiError) {
    switch (e.status) {
      case 404:
        return ['This profile no longer exists.'];
      case 409:
        return ['The host refused this change. The profile may be active, damaged or already changed.'];
      case 413:
        return ['The file is too large for a profile.'];
      case 503:
        return ['The host cannot reach the profile files right now. Try again in a moment.'];
      default:
        return [`The host answered with an error (${e.status}).`];
    }
  }
  return ['Cannot reach the host.'];
}

/** A failed test press (decisions §11: test mode works on the active profile only). */
export function testErrorMessage(e: unknown): string {
  if (e instanceof ApiError) {
    switch (e.status) {
      case 409:
        return 'Test mode works on the active profile only.';
      case 503:
        return 'The game input queue is full. Try again in a moment.';
      case 404:
        return 'The host does not know this action.';
      default:
        return `The press was refused (${e.status}).`;
    }
  }
  return 'Cannot reach the host.';
}
