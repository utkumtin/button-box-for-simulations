import { useCallback, useEffect, useRef, useState } from 'react';
import type { HelloMessage } from '@bbs/protocol';
import type { GestureOut } from '@bbs/widgets';
import { backoffDelay } from './backoff';
import { closeAction } from './closeAction';
import { applyHostMessage, gestureFrame, initialHostModel, type HostModel } from './hostModel';

export type ConnectionStatus = 'connecting' | 'connected' | 'reconnecting' | 'replaced';

export interface Connection {
  status: ConnectionStatus;
  host: HostModel;
  /** Sends a finished gesture on the open, welcomed socket; otherwise drops it (never queued). */
  sendGesture: (g: GestureOut) => void;
}

/**
 * Keeps one WebSocket to the host. Mount it with a new React `key` to start over
 * (for example after the user presses Reconnect). Sends hello, then finished gestures.
 */
export function useConnection(token: string, onTokenRejected: () => void): Connection {
  const [status, setStatus] = useState<ConnectionStatus>('connecting');
  const [host, setHost] = useState<HostModel>(initialHostModel);
  const live = useRef<{ ws: WebSocket | undefined; welcomed: boolean }>({ ws: undefined, welcomed: false });
  const sendGesture = useCallback((g: GestureOut) => {
    const { ws, welcomed } = live.current;
    const frame = gestureFrame(ws?.readyState === WebSocket.OPEN, welcomed, g);
    if (frame !== null && ws) ws.send(frame);
  }, []);

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
      live.current = { ws: undefined, welcomed: false };
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
      live.current = { ws: socket, welcomed: false };
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
          live.current.welcomed = true;
          attempt = 0;
          setStatus('connected');
          return;
        }
        // Only a welcomed connection is authenticated; ignore anything before that.
        if (welcomed) setHost((m) => applyHostMessage(m, msg));
      };
      socket.onclose = (ev) => {
        if (ws !== socket) return;
        ws = undefined;
        welcomed = false;
        live.current = { ws: undefined, welcomed: false };
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

  return { status, host, sendGesture };
}
