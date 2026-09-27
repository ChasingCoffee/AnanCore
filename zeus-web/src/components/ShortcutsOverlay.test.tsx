// SPDX-License-Identifier: GPL-2.0-or-later
import { act } from 'react';
import { createRoot, type Root } from 'react-dom/client';
import { afterEach, beforeEach, describe, expect, it } from 'vitest';
import { ShortcutsOverlay } from './ShortcutsOverlay';

describe('ShortcutsOverlay', () => {
  let container: HTMLDivElement;
  let root: Root;
  beforeEach(async () => {
    (globalThis as { IS_REACT_ACT_ENVIRONMENT?: boolean }).IS_REACT_ACT_ENVIRONMENT = true;
    container = document.createElement('div');
    document.body.appendChild(container);
    root = createRoot(container);
    await act(async () => { root.render(<ShortcutsOverlay />); });
  });
  afterEach(() => {
    act(() => root.unmount());
    container.remove();
    document.querySelectorAll('input').forEach((el) => el.remove());
  });

  const press = async (key: string, target: EventTarget = window) => {
    await act(async () => {
      target.dispatchEvent(new KeyboardEvent('keydown', { key, bubbles: true }));
    });
  };

  it('opens on ? and lists the tuning keys, closes on Esc', async () => {
    expect(container.textContent).toBe('');
    await press('?');
    expect(container.textContent).toContain('Tune down / up by the current step');
    expect(container.textContent).toContain('Push-to-talk');
    await press('Escape');
    expect(container.textContent).toBe('');
  });

  it('ignores ? typed into a text field', async () => {
    const input = document.createElement('input');
    document.body.appendChild(input);
    await press('?', input);
    expect(container.textContent).toBe('');
  });
});
