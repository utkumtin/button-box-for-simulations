import { useEffect, useState } from 'react';
import type { HelloMessage } from '@bbs/protocol';
import { backoffDelay } from './backoff';
import { closeAction } from './closeAction';

export type ConnectionStatus = 'connecting' | 'connected' | 'reconnecting' | 'replaced';

/**
 * Keeps one WebSocket to the host. Mount it with a new React `key` to start over
 * (for example after the user presses Reconnect). Phase 2 only ever sends hello.
 */
export function useConnection(token: string, onTokenRejected: () => void): ConnectionStatus {
  const [status, setStatus] = useState<ConnectionStatus>('connecting');

  useEffect(() => {
    let disposed = false;
    let stopped = false;
    let welcomed = false;
    let attempt = 0;
    let timer: ReturnType<typeof setTimeout> | undefined;
    let ws: WebSocket | undefined;

    const detach = () => {
      if (!ws) return;
      ws.onopen = ws.onmessage = ws.onclose = ws.onerror = null;
      ws.close();
      ws = undefined;
    };

    const scheduleReconnect = () => {
      clearTimeout(timer);
      timer = setTimeout(connect, backoffDelay(attempt));
      attempt += 1;
    };

    function connect() {
      clearTimeout(timer);
      if (disposed || stopped) return;
      detach();
      welcomed = false;
      const socket = new WebSocket(`wss://${location.host}/ws`);
      ws = socket;
      socket.onopen = () => {
        const hello: HelloMessage = { type: 'hello', token };
        socket.send(JSON.stringify(hello));
      };
      socket.onmessage = (ev) => {
        if (typeof ev.data !== 'string') return;
        let msg: unknown;
        try {
          msg = JSON.parse(ev.data);
        } catch {
          return;
        }
        if (typeof msg === 'object' && msg !== null && (msg as { type?: unknown }).type === 'welcome') {
          welcomed = true;
          attempt = 0;
          setStatus('connected');
        }
      };
      socket.onclose = (ev) => {
        if (ws !== socket) return;
        ws = undefined;
        welcomed = false;
        const action = closeAction(ev.code);
        if (action === 'forget') {
          stopped = true;
          onTokenRejected();
        } else if (action === 'stop') {
          stopped = true;
          setStatus('replaced');
        } else {
          setStatus('reconnecting');
          scheduleReconnect();
        }
      };
    }

    const retryNow = () => {
      if (disposed || stopped || welcomed) return;
      if (ws && ws.readyState === WebSocket.CONNECTING) return;
      attempt = 0;
      connect();
    };
    // After iOS suspends the app, the old socket can look open while the PC has long dropped it, and nothing
    // would ever close it (Phase 2 sends only hello). Coming back to the foreground therefore always starts a
    // fresh connection; the host replaces the old one (decisions §9).
    const onVisible = () => {
      if (document.visibilityState !== 'visible' || disposed || stopped) return;
      if (ws && ws.readyState === WebSocket.CONNECTING) return;
      attempt = 0;
      connect();
    };

    document.addEventListener('visibilitychange', onVisible);
    window.addEventListener('online', retryNow);
    connect();

    return () => {
      disposed = true;
      clearTimeout(timer);
      document.removeEventListener('visibilitychange', onVisible);
      window.removeEventListener('online', retryNow);
      detach();
    };
  }, [token, onTokenRejected]);

  return status;
}
