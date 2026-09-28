// The VFO shows CTUN whenever it is on, however it was switched on (the CTUN
// button, the front panel, a remote session) — CTUN changes what tuning does,
// so it must never be invisible.
import { act, createElement } from 'react';
import { createRoot, type Root } from 'react-dom/client';
import { afterEach, beforeEach, describe, expect, it } from 'vitest';
import { useConnectionStore } from '../state/connection-store';
import { VfoDisplay } from './VfoDisplay';

describe('CTUN indicator on the VFO', () => {
  let container: HTMLDivElement;
  let root: Root;
  beforeEach(() => {
    (globalThis as { IS_REACT_ACT_ENVIRONMENT?: boolean }).IS_REACT_ACT_ENVIRONMENT = true;
    container = document.createElement('div');
    document.body.appendChild(container);
    root = createRoot(container);
  });
  afterEach(() => {
    act(() => root.unmount());
    container.remove();
    useConnectionStore.setState({ ctunEnabled: false });
  });

  it('appears when CTUN turns on and goes when it turns off', async () => {
    useConnectionStore.setState({ ctunEnabled: false, vfoHz: 14_200_000 });
    await act(async () => root.render(createElement(VfoDisplay)));
    expect(container.querySelector('[data-testid="vfo-ctun-tag"]')).toBeNull();

    await act(async () => useConnectionStore.setState({ ctunEnabled: true }));
    expect(container.querySelector('[data-testid="vfo-ctun-tag"]')?.textContent).toContain('CTUN');

    await act(async () => useConnectionStore.setState({ ctunEnabled: false }));
    expect(container.querySelector('[data-testid="vfo-ctun-tag"]')).toBeNull();
  });
});
