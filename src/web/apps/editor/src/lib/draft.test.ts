import { describe, expect, it } from 'vitest';
import { createDraft, currentProfile, draftReducer, isDirty, jsonEqual } from './draft';
import { profileWith } from './testFixtures';

const rename = (name: string) => (p: ReturnType<typeof profileWith>) => ({ ...p, name });

describe('draft', () => {
  it('is dirty after an edit', () => {
    const d = draftReducer(createDraft(profileWith()), { type: 'apply', edit: rename('Other') });
    expect(isDirty(d)).toBe(true);
    expect(currentProfile(d).name).toBe('Other');
  });

  // The "unsaved changes" mark must go away when the user undoes back to what is on disk.
  it('is not dirty after undo back to the saved state', () => {
    let d = draftReducer(createDraft(profileWith()), { type: 'apply', edit: rename('Other') });
    d = draftReducer(d, { type: 'undo' });
    expect(isDirty(d)).toBe(false);
    d = draftReducer(d, { type: 'redo' });
    expect(isDirty(d)).toBe(true);
  });

  // Editing back to an equal but newly built object is also clean: the comparison is structural.
  it('is not dirty when an edit restores equal content in a new object', () => {
    let d = draftReducer(createDraft(profileWith()), { type: 'apply', edit: rename('Other') });
    d = draftReducer(d, { type: 'apply', edit: rename('Test') });
    expect(isDirty(d)).toBe(false);
  });

  it('saved resets the baseline and keeps the history', () => {
    let d = draftReducer(createDraft(profileWith()), { type: 'apply', edit: rename('Other') });
    d = draftReducer(d, { type: 'saved', profile: currentProfile(d) });
    expect(isDirty(d)).toBe(false);
    d = draftReducer(d, { type: 'undo' });
    expect(currentProfile(d).name).toBe('Test');
    expect(isDirty(d)).toBe(true);
  });

  it('records nothing for an edit that returns the same object', () => {
    const d = createDraft(profileWith());
    expect(draftReducer(d, { type: 'apply', edit: (p) => p })).toBe(d);
  });

  it('load replaces everything', () => {
    let d = draftReducer(createDraft(profileWith()), { type: 'apply', edit: rename('Other') });
    d = draftReducer(d, { type: 'load', profile: { ...profileWith(), id: 'next-0000' } });
    expect(isDirty(d)).toBe(false);
    expect(d.history.past).toHaveLength(0);
  });

  it('jsonEqual ignores key order and undefined values', () => {
    expect(jsonEqual({ a: 1, b: [1, { c: 2 }] }, { b: [1, { c: 2 }], a: 1, d: undefined })).toBe(true);
    expect(jsonEqual({ a: 1 }, { a: 2 })).toBe(false);
    expect(jsonEqual([1, 2], [1])).toBe(false);
  });
});
