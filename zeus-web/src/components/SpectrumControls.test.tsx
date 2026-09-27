// SPDX-License-Identifier: GPL-2.0-or-later

/** @vitest-environment jsdom */

import { afterEach, describe, expect, it, vi } from 'vitest';
import { createElement } from 'react';

import { act, render } from './meters/__tests__/harness';
import { usePanadapterRenderStore } from '../state/panadapter-render-store';
import { SpectrumControls } from './SpectrumControls';

function findButton(container: HTMLElement, label: string): HTMLButtonElement | null {
  return Array.from(container.querySelectorAll('button')).find(
    (button): button is HTMLButtonElement => button.textContent?.trim() === label,
  ) ?? null;
}

describe('SpectrumControls', () => {
  afterEach(() => {
    act(() => {
      usePanadapterRenderStore.setState({ panadapter3dEnabled: false });
    });
    localStorage.clear();
    vi.restoreAllMocks();
  });

  it('toggles the panadapter between legacy 2D (the default) and 3D beside Pop', () => {
    const { container, unmount } = render(createElement(SpectrumControls));

    const off = findButton(container, '2D');
    expect(off).not.toBeNull();
    expect(off!.getAttribute('aria-pressed')).toBe('false');

    act(() => {
      off!.dispatchEvent(new MouseEvent('click', { bubbles: true }));
    });

    const on = findButton(container, '3D');
    expect(on).not.toBeNull();
    expect(on!.getAttribute('aria-pressed')).toBe('true');
    expect(localStorage.getItem('zeus.panadapter.webgpu3d')).toBe('1');

    act(() => {
      on!.dispatchEvent(new MouseEvent('click', { bubbles: true }));
    });

    const offAgain = findButton(container, '2D');
    expect(offAgain).not.toBeNull();
    expect(offAgain!.getAttribute('aria-pressed')).toBe('false');
    expect(localStorage.getItem('zeus.panadapter.webgpu3d')).toBe('0');
    unmount();
  });
});
