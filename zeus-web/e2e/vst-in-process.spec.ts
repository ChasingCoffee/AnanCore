// SPDX-License-Identifier: GPL-2.0-or-later
//
// VST3 on Windows, after the out-of-process engine's retirement. This spec
// used to drive "Download VST Engine": a separate executable fetched from
// upstream's server. That server and the engine's source are gone, and ANAN
// Core no longer launches closed or third-party executables — VST3 runs in the
// in-process bridge. The spec now pins what a Windows operator sees instead:
// Scan plugins on BOTH rails in Native mode (Windows used to get the engine
// button INSTEAD, and TX only in VST mode — no way to add a VST3 at all; field
// issue #62), NATIVE status, no Native/VST toggle, no engine download — and
// that Scan plugins opens the suite with its VST3 folder scan. Stubbed backend,
// Windows-shaped, with the engine routes retired exactly as the server has
// them (install/repair POST -> 410, processing-mode -> native).
import { expect, test, type Page, type Route } from '@playwright/test';

async function fulfillJson(route: Route, body: unknown) {
  await route.fulfill({
    status: 200,
    contentType: 'application/json',
    body: JSON.stringify(body),
  });
}

const allowedUserSession = {
  qrzConnected: true,
  callsign: 'N9WAR',
  displayName: 'N9WAR',
  accessAllowed: true,
  isAdmin: false,
  hasQrzXmlSubscription: true,
  subscriptionStatus: 'qrz-xml',
  subscriptionExpiresUtc: null,
  pluginAccessMode: 'all',
  pluginEntitlements: [],
  managedPlugins: [],
  denialReason: null,
  user: null,
};

// Server-side install + engine state, mutated by the stubbed endpoints so the
// flow reads like the real thing: engine absent → installed → active.
type EngineWorld = {
  engineInstalled: boolean;
  engineActive: boolean;
  installPosts: number;
  configurePuts: number;
};

// The install DTO a Windows server reports: the platform where the retired
// engine WAS supported — i.e. exactly the shape that used to hide Scan/Add.
const WINDOWS_INSTALL_DTO = {
  phase: 'idle',
  percent: 0,
  message: '',
  engineAvailable: false,
  engineSupported: true,
  inProcessHostSupported: true,
  auSupported: false,
};

async function stubZeusApi(page: Page): Promise<EngineWorld> {
  const world: EngineWorld = {
    engineInstalled: false,
    engineActive: false,
    installPosts: 0,
    configurePuts: 0,
  };

  // Context-wide: the Audio Suite opens as a separate popup window, which
  // needs the same stubbed backend as the page that opened it.
  await page.context().addInitScript(() => {
    const NativeWebSocket = window.WebSocket;
    class MockZeusWebSocket extends EventTarget {
      readonly url: string;
      readonly protocol = '';
      readonly extensions = '';
      binaryType: BinaryType = 'blob';
      bufferedAmount = 0;
      readyState = NativeWebSocket.CONNECTING;
      onopen: ((this: WebSocket, ev: Event) => unknown) | null = null;
      onmessage: ((this: WebSocket, ev: MessageEvent) => unknown) | null = null;
      onerror: ((this: WebSocket, ev: Event) => unknown) | null = null;
      onclose: ((this: WebSocket, ev: CloseEvent) => unknown) | null = null;

      constructor(url: string | URL) {
        super();
        this.url = String(url);
        window.setTimeout(() => {
          if (this.readyState !== NativeWebSocket.CONNECTING) return;
          this.readyState = NativeWebSocket.OPEN;
          const event = new Event('open');
          this.onopen?.call(this as unknown as WebSocket, event);
          this.dispatchEvent(event);
        }, 0);
      }

      send() {
        /* No realtime frames are needed for this install e2e. */
      }

      close() {
        if (this.readyState === NativeWebSocket.CLOSED) return;
        this.readyState = NativeWebSocket.CLOSED;
        const event = new CloseEvent('close');
        this.onclose?.call(this as unknown as WebSocket, event);
        this.dispatchEvent(event);
      }
    }

    window.WebSocket = MockZeusWebSocket as unknown as typeof WebSocket;
  });

  await page.context().route(/^https?:\/\/[^/]+\/api(?:\/|\?|$)/, async (route) => {
    const request = route.request();
    const url = new URL(request.url());
    const method = request.method();

    if (url.pathname === '/api/users/session') {
      await fulfillJson(route, allowedUserSession);
      return;
    }

    if (url.pathname === '/api/capabilities') {
      await fulfillJson(route, {
        host: 'desktop',
        platform: 'windows',
        architecture: 'x64',
        version: 'e2e',
        lanHttpsUrls: [],
        features: {},
      });
      return;
    }

    if (url.pathname === '/api/radio/selection') {
      await fulfillJson(route, {
        preferred: 'Auto',
        connected: 'Unknown',
        effective: 'Unknown',
        overrideDetection: false,
      });
      return;
    }

    if (url.pathname === '/api/plugins') {
      await fulfillJson(route, { sdkAbi: 1, sdkVersion: 'e2e', plugins: [] });
      return;
    }

    if (url.pathname === '/api/audio/devices') {
      await fulfillJson(route, {
        supported: true,
        inputDeviceId: null,
        outputDeviceId: null,
        activeInputDeviceId: null,
        activeOutputDeviceId: null,
        inputs: [],
        outputs: [],
        error: null,
      });
      return;
    }

    if (url.pathname === '/api/ui/layouts' && method === 'GET') {
      await fulfillJson(route, {
        radioKey: url.searchParams.get('radio') ?? 'default',
        layouts: [],
        activeLayoutId: 'default',
      });
      return;
    }
    if (url.pathname === '/api/ui/layouts') {
      await route.fulfill({ status: 204, body: '' });
      return;
    }

    if (url.pathname === '/api/theme-settings') {
      await fulfillJson(route, { theme: 'dark', overrides: {} });
      return;
    }

    // Engine routes as the server now has them: install/repair POSTs are gone
    // (410); GET still answers with the platform flags; the mode is native.
    if (url.pathname.endsWith('/vst-engine/install') || url.pathname.endsWith('/vst-engine/repair')) {
      if (method === 'POST') {
        world.installPosts += 1;
        await route.fulfill({
          status: 410,
          contentType: 'application/json',
          body: JSON.stringify({ error: 'The separate VST engine is not part of ANAN Core.' }),
        });
        return;
      }
      await fulfillJson(route, WINDOWS_INSTALL_DTO);
      return;
    }
    if (url.pathname.endsWith('/processing-mode')) {
      if (method === 'PUT') world.configurePuts += 1;
      await fulfillJson(route, { mode: 'native', engineAvailable: false, engineActive: false });
      return;
    }
    if (url.pathname.endsWith('/master-bypass')) {
      await fulfillJson(route, { bypassed: false });
      return;
    }
    if (url.pathname.endsWith('/profiles')) {
      await fulfillJson(route, { profiles: [] });
      return;
    }
    if (url.pathname === '/api/audio-suite/preview') {
      await fulfillJson(route, { supported: true, enabled: false, meterOnly: false });
      return;
    }
    if (url.pathname.endsWith('/chain/order')) {
      await fulfillJson(route, { pluginIds: [] });
      return;
    }

    await fulfillJson(route, {});
  });

  return world;
}

test('Windows operator adds VST3 in-process: Scan plugins on both rails, no engine', async ({ page }) => {
  const pageErrors: string[] = [];
  page.on('pageerror', (err) => pageErrors.push(err.message));
  const world = await stubZeusApi(page);

  await page.goto('/#pa');
  await expect(page.getByRole('region', { name: 'Settings' })).toBeVisible();
  await page.getByRole('tab', { name: 'AUDIO TOOLS' }).click();

  const txRail = page.getByRole('region', { name: 'TX Audio' });
  const rxRail = page.getByRole('region', { name: 'RX Audio' });

  // Scan/Add on BOTH rails, in Native mode, on the Windows-shaped server.
  await expect(txRail.getByRole('button', { name: 'Scan plugins' })).toBeVisible();
  await expect(rxRail.getByRole('button', { name: 'Scan plugins' })).toBeVisible();
  await expect(txRail.getByText('NATIVE')).toBeVisible();

  // No engine download, no engine status, no Native/VST route toggle.
  await expect(page.getByRole('button', { name: 'Download VST Engine' })).toHaveCount(0);
  await expect(page.getByText(/VST engine ready/i)).toHaveCount(0);
  await expect(txRail.getByRole('button', { name: 'VST', exact: true })).toHaveCount(0);
  await expect(txRail.getByRole('button', { name: 'Native', exact: true })).toHaveCount(0);

  // Scan plugins opens the TX Audio Suite — in a separate window
  // (openAudioSuiteWindow) — with its VST3 folder scan.
  const suitePromise = page.context().waitForEvent('page');
  await txRail.getByRole('button', { name: 'Scan plugins' }).click();
  const suite = await suitePromise;
  suite.on('pageerror', (err) => pageErrors.push(`suite: ${err.message}`));
  await suite.waitForLoadState();
  await expect(suite.getByRole('button', { name: 'Scan All' })).toBeVisible();
  await expect(suite.getByRole('button', { name: 'Scan CLAP' })).toBeVisible();
  await expect(suite.getByRole('button', { name: 'Scan VST3' })).toBeVisible();
  await expect(suite.getByRole('button', { name: /Set paths/ })).toBeVisible();
  await expect(suite.getByRole('button', { name: 'Clear DB' })).toBeVisible();

  // Nothing tried to install or configure an engine.
  expect(world.installPosts).toBe(0);
  expect(world.configurePuts).toBe(0);
  expect(pageErrors).toEqual([]);
});
