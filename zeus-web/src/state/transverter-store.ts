// SPDX-License-Identifier: GPL-2.0-or-later
//
// Transverter bands on the client: band buttons, the VFO's XVTR line, and the
// settings table. The server owns conversion; this only needs to know the
// bands to label and select them.

import { create } from 'zustand';
import { getTransverters, setTransverters, type TransverterBand } from '../api/client';

type TransverterState = {
  bands: TransverterBand[];
  loaded: boolean;
  load: () => Promise<void>;
  save: (bands: TransverterBand[]) => Promise<void>;
};

export const useTransverterStore = create<TransverterState>((set) => ({
  bands: [],
  loaded: false,
  load: async () => {
    try {
      set({ bands: await getTransverters(), loaded: true });
    } catch {
      set({ loaded: true });
    }
  },
  save: async (bands) => {
    set({ bands: await setTransverters(bands), loaded: true });
  },
}));

/** The enabled transverter band containing RF `hz`, if any. */
export function transverterFor(hz: number, bands: TransverterBand[]): TransverterBand | null {
  for (const b of bands) if (b.enabled && hz >= b.minHz && hz <= b.maxHz) return b;
  return null;
}

/** The IF the radio is tuned to for RF `hz` on band `b`. */
export function transverterIfHz(hz: number, b: TransverterBand): number {
  return hz - (b.loHz + b.loErrorHz);
}
