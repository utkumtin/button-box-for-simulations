import { useEffect, useRef } from 'react';

/**
 * Calls `fetcher` every `intervalMs`, waiting for each call to finish first (no overlapping requests).
 * Stops when the component unmounts. Errors go to `onError`; polling continues.
 */
export function usePoll<T>(
  fetcher: () => Promise<T>,
  onResult: (value: T) => void,
  onError: (error: unknown) => void,
  intervalMs = 1000,
): void {
  const latest = useRef({ fetcher, onResult, onError });
  useEffect(() => {
    latest.current = { fetcher, onResult, onError };
  });

  useEffect(() => {
    let stopped = false;
    let timer: ReturnType<typeof setTimeout> | undefined;
    const tick = async () => {
      try {
        const value = await latest.current.fetcher();
        if (!stopped) latest.current.onResult(value);
      } catch (e) {
        if (!stopped) latest.current.onError(e);
      }
      if (!stopped) timer = setTimeout(() => void tick(), intervalMs);
    };
    void tick();
    return () => {
      stopped = true;
      if (timer !== undefined) clearTimeout(timer);
    };
  }, [intervalMs]);
}
