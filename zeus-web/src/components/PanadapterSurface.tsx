// SPDX-License-Identifier: GPL-2.0-or-later
//
// Renderer switch for the panadapter surface. The WebGL2 panadapter is the
// default; WebGPU 3D is opt-in (the pane's 3D pill or `?webgpuPanadapter=1`,
// since a7b331a), and falls back to WebGL2 when the GPU stack can't run it.

import { useEffect, useState, type ComponentProps } from 'react';
import { usePanadapterRenderStore } from '../state/panadapter-render-store';
import { Panadapter } from './Panadapter';
import { Panadapter3D } from './Panadapter3D';

type PanadapterProps = NonNullable<ComponentProps<typeof Panadapter>>;

export function PanadapterSurface(props: PanadapterProps) {
  const [pan3dUnavailable, setPan3dUnavailableLocal] = useState(false);
  const panadapter3dEnabled = usePanadapterRenderStore((s) => s.panadapter3dEnabled);
  const setPan3dUnavailable = (v: boolean) => {
    setPan3dUnavailableLocal(v);
    usePanadapterRenderStore.getState().setPan3dUnavailable(v);
  };
  const usePan3d = panadapter3dEnabled && !pan3dUnavailable;

  useEffect(() => {
    setPan3dUnavailable(false);
  }, [panadapter3dEnabled]);

  if (usePan3d) {
    return <Panadapter3D {...props} onUnavailable={() => setPan3dUnavailable(true)} />;
  }
  return <Panadapter {...props} />;
}
