// SPDX-License-Identifier: GPL-2.0-or-later
//
// XIT in the web UI: the button, its offset chip, the always-visible flag
// badge, and the state that carries XIT from the server. Before this, the
// backend (and the G2 front panel, CAT, TCI, MIDI) could turn XIT on with
// nothing on screen to show it or turn it off.

import { act, createElement } from 'react';
import { createRoot, type Root } from 'react-dom/client';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { useConnectionStore } from '../state/connection-store';
import { normalizeState } from '../api/client';
import { XitButton, XitTxBadge } from './RitSplitButtons';

describe('XIT', () => {
  let container: HTMLDivElement;
  let root: Root;
  let fetchMock: ReturnType<typeof vi.fn>;

  beforeEach(() => {
    (globalThis as { IS_REACT_ACT_ENVIRONMENT?: boolean }).IS_REACT_ACT_ENVIRONMENT = true;
    container = document.createElement('div');
    document.body.appendChild(container);
    root = createRoot(container);
    fetchMock = vi.fn(async () => new Response('{}', { status: 200 }));
    vi.stubGlobal('fetch', fetchMock);
  });

  afterEach(() => {
    act(() => root.unmount());
    container.remove();
    vi.unstubAllGlobals();
    useConnectionStore.setState({ xitEnabled: false, xitHz: 0 });
  });

  const posted = () =>
    fetchMock.mock.calls.map(([url, init]) => [String(url), JSON.parse(String((init as RequestInit).body))]);

  it('toggles XIT on through POST /api/tx/xit', async () => {
    useConnectionStore.setState({ xitEnabled: false, xitHz: 0 });
    await act(async () => root.render(createElement(XitButton)));
    const btn = Array.from(container.querySelectorAll('button')).find((b) => b.textContent === 'XIT');
    expect(btn).toBeDefined();
    await act(async () => btn!.click());
    expect(posted()).toEqual([['/api/tx/xit', { enabled: true }]]);
  });

  it('shows the offset when on and steps it by 10 Hz, clamped', async () => {
    useConnectionStore.setState({ xitEnabled: true, xitHz: 99_995 });
    await act(async () => root.render(createElement(XitButton)));
    expect(container.textContent).toContain('+99995');
    const up = container.querySelector('button[aria-label="XIT up"]') as HTMLButtonElement;
    const down = container.querySelector('button[aria-label="XIT down"]') as HTMLButtonElement;
    await act(async () => up.click());
    await act(async () => down.click());
    expect(posted()).toEqual([
      ['/api/tx/xit', { hz: 99_999 }],   // clamped to the ±99999 Thetis range
      ['/api/tx/xit', { hz: 99_985 }],
    ]);
  });

  it('flag badge appears whenever XIT is on, however it was enabled', async () => {
    useConnectionStore.setState({ xitEnabled: false, xitHz: 250 });
    await act(async () => root.render(createElement(XitTxBadge)));
    expect(container.querySelector('[data-testid="xit-badge"]')).toBeNull();

    // e.g. the G2 front panel's RIT/XIT button, arriving as a state frame
    await act(async () => useConnectionStore.setState({ xitEnabled: true, xitHz: -250 }));
    const badge = container.querySelector('[data-testid="xit-badge"]');
    expect(badge?.textContent).toBe('XIT -250');
  });

  it('server state carries XIT; a server without the fields reads as off', () => {
    const withXit = normalizeState({ xitEnabled: true, xitHz: 120 });
    expect(withXit.xitEnabled).toBe(true);
    expect(withXit.xitHz).toBe(120);
    const legacy = normalizeState({});
    expect(legacy.xitEnabled).toBe(false);
    expect(legacy.xitHz).toBe(0);
  });
});
