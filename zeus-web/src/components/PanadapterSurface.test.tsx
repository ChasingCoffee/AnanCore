// SPDX-License-Identifier: GPL-2.0-or-later

/** @vitest-environment jsdom */

import { afterEach, describe, expect, it, vi } from 'vitest';
import { createElement } from 'react';

import { act, render } from './meters/__tests__/harness';
import { PanadapterSurface } from './PanadapterSurface';
import { usePanadapterRenderStore } from '../state/panadapter-render-store';

vi.mock('./Panadapter', () => ({
  Panadapter: () => createElement('div', { 'data-testid': 'webgl-panadapter' }),
}));

vi.mock('./Panadapter3D', () => ({
  Panadapter3D: () => createElement('div', { 'data-testid': 'webgpu-panadapter-3d' }),
}));

describe('PanadapterSurface', () => {
  afterEach(() => {
    act(() => {
      usePanadapterRenderStore.setState({ panadapter3dEnabled: false });
    });
    localStorage.clear();
    vi.restoreAllMocks();
  });

  it('uses the WebGL panadapter by default (3D is opt-in since a7b331a)', () => {
    const { container, unmount } = render(createElement(PanadapterSurface));

    expect(container.querySelector('[data-testid="webgl-panadapter"]')).not.toBeNull();
    expect(container.querySelector('[data-testid="webgpu-panadapter-3d"]')).toBeNull();
    unmount();
  });

  it('uses the WebGPU 3D panadapter when the operator enables it', () => {
    act(() => {
      usePanadapterRenderStore.getState().setPanadapter3dEnabled(true);
    });

    const { container, unmount } = render(createElement(PanadapterSurface));

    expect(container.querySelector('[data-testid="webgpu-panadapter-3d"]')).not.toBeNull();
    expect(container.querySelector('[data-testid="webgl-panadapter"]')).toBeNull();
    unmount();
  });

  it('switches live between 3D and 2D renderers', () => {
    act(() => {
      usePanadapterRenderStore.getState().setPanadapter3dEnabled(true);
    });
    const { container, unmount } = render(createElement(PanadapterSurface));

    expect(container.querySelector('[data-testid="webgpu-panadapter-3d"]')).not.toBeNull();

    act(() => {
      usePanadapterRenderStore.getState().setPanadapter3dEnabled(false);
    });

    expect(container.querySelector('[data-testid="webgl-panadapter"]')).not.toBeNull();
    expect(container.querySelector('[data-testid="webgpu-panadapter-3d"]')).toBeNull();

    act(() => {
      usePanadapterRenderStore.getState().setPanadapter3dEnabled(true);
    });

    expect(container.querySelector('[data-testid="webgpu-panadapter-3d"]')).not.toBeNull();
    expect(container.querySelector('[data-testid="webgl-panadapter"]')).toBeNull();
    unmount();
  });
});
