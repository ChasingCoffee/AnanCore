// SPDX-License-Identifier: GPL-2.0-or-later
//
// The keyboard and mouse bindings, in one list. The '?' overlay renders it,
// and docs/manual/chapters/27-keyboard-and-mouse-shortcuts.md mirrors it.
// A binding added in code and not here is a binding no operator will find:
// field report #62 asked for keyboard tuning that had existed all along.
//
// Keep this in step with the handlers it describes:
//   util/use-keyboard-shortcuts.ts   (arrows, Space PTT, Alt+arrows)
//   App.tsx                          ('/', Alt+8)
//   components/filter/FilterRibbon   (arrows while the ribbon is open)

export type ShortcutEntry = { keys: string; action: string };
export type ShortcutGroup = { title: string; entries: ShortcutEntry[] };

export const SHORTCUT_GROUPS: ShortcutGroup[] = [
  {
    title: 'Tuning and display',
    entries: [
      { keys: '← / →', action: 'Tune down / up by the current step' },
      { keys: '↑ / ↓', action: 'Zoom the panadapter in / out' },
      { keys: 'Alt + ↑ / ↓', action: 'Zoom the world map background' },
      { keys: '← / → (filter ribbon open)', action: 'Move the filter edge instead of tuning' },
    ],
  },
  {
    title: 'Transmit',
    entries: [
      { keys: 'Space (hold)', action: 'Push-to-talk — transmits while held, releases on key-up' },
    ],
  },
  {
    title: 'Workspaces',
    entries: [
      { keys: '/', action: 'Jump to the logbook callsign box' },
      { keys: 'Alt + 8', action: 'Toggle the FT8 workspace' },
      { keys: 'Esc', action: 'Close Settings, popovers and floating windows' },
      { keys: '?', action: 'Show or hide this list' },
      { keys: 'Ctrl + Shift + R', action: 'Hard-reload the interface after an update' },
    ],
  },
  {
    title: 'Mouse and touch',
    entries: [
      { keys: 'Scroll on the VFO digits', action: 'Step that digit up or down' },
      { keys: 'Click the VFO digits', action: 'Type a frequency in kHz' },
      { keys: 'Scroll on the panadapter', action: 'Tune by the current step' },
      { keys: 'Drag the panadapter', action: 'Slide the view' },
      { keys: 'Pinch', action: 'Zoom (touch screens)' },
    ],
  },
];
