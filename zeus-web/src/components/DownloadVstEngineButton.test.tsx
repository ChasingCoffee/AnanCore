// SPDX-License-Identifier: GPL-2.0-or-later
//
// Issue #62: 'VST engine fails to install'. There is no engine to download,
// so the idle state must explain that rather than offer a download that can
// only fail. An engine already on disk still hides the control entirely.
import { act } from 'react';
import { createRoot, type Root } from 'react-dom/client';
import { afterEach, beforeEach, describe, expect, it } from 'vitest';
import { useAudioSuiteStore } from '../state/audio-suite-store';
import { DownloadVstEngineButton } from './DownloadVstEngineButton';

describe('DownloadVstEngineButton', () => {
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
  });

  it('explains instead of offering a download when no engine is installed', async () => {
    useAudioSuiteStore.setState({
      vstEngineAvailable: false,
      vstEngineInstall: { phase: 'idle', percent: 0, message: null },
    } as never);
    await act(async () => { root.render(<DownloadVstEngineButton />); });
    expect(container.textContent).toContain('VST engine not available');
    expect(container.querySelector('button')).toBeNull();
    expect(container.querySelector('[title]')?.getAttribute('title')).toContain('VSTHostEngine.exe');
  });

  it('renders nothing when an engine is already installed', async () => {
    useAudioSuiteStore.setState({
      vstEngineAvailable: true,
      vstEngineInstall: { phase: 'idle', percent: 0, message: null },
    } as never);
    await act(async () => { root.render(<DownloadVstEngineButton />); });
    expect(container.textContent).toBe('');
  });
});
