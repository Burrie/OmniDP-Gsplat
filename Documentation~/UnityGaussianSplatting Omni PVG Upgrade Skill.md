# UnityGaussianSplatting Omni + PVG Upgrade Skill

## Purpose
This handoff records where the UnityGaussianSplatting upgrade starts and what was changed to make it compatible with ODGS/OmniGS-style omnidirectional rendering and PVG-style dynamic Gaussians.

## Architecture Map
- `UnityGaussianSplatting/package/Editor/Utils/GaussianFileReader.cs` reads PLY/SPZ into `InputSplatData`; PVG fields are detected here.
- `UnityGaussianSplatting/package/Editor/GaussianSplatAssetCreator.cs` converts input splats into packed `_pos`, `_oth`, `_col`, `_shs`, optional `_chk`, and optional `_pvg` sidecar bytes.
- `UnityGaussianSplatting/package/Runtime/GaussianSplatRenderer.cs` owns GPU buffers, sorting, compute dispatch, and draw calls.
- `UnityGaussianSplatting/package/Shaders/SplatUtilities.compute` computes sort depth and per-view splat quad data.
- `UnityGaussianSplatting/package/Shaders/GaussianSplatting.hlsl` owns decoding, covariance, PVG deformation, and ODGS/OmniGS ERP math.
- `UnityGaussianSplatting/package/Runtime/GaussianOmniViewer.cs` owns the hidden ERP render texture and perspective composite state.

## PVG Formula Contract
PVG is an asset capability, not a render mode. If the imported PLY contains all fields `t`, `scale_t`, `v_0`, `v_1`, `v_2`, runtime uses:

```text
a = 2*pi / max(abs(PvgPeriod), epsilon)
mean(t) = mean0 + velocity * sin((PvgTime - tau) * a) / a
beta = exp(rawScaleT)
opacity(t) = opacity0 * exp(-0.5 * (tau - PvgTime)^2 / beta^2)
```

Assumptions: PVG PLY opacity is already activated, spatial scale is raw log scale, temporal scale `scale_t` is raw log beta, and rotation is normalized WXYZ in the source PLY before Unity's existing swizzle/packing.

## Omni Rendering Contract
`GaussianSplatRenderer.ProjectionMode` chooses the camera projection path:
- `Perspective`: existing behavior.
- `HybridOmniPerspective`: render into a hidden 2:1 ERP texture, then composite into the normal perspective camera via `GaussianERPToPerspective.shader`.

`GaussianOmniViewer.Rasterizer` selects ERP covariance math:
- `ODGS`: spherical-frame ODGS covariance.
- `OmniGS`: direct ERP Jacobian covariance from `computeOmniCov2D_OmniGS`.

## Implementation Checklist
- Keep static PLY files backward compatible.
- Reject partial PVG headers clearly.
- Preserve Morton ordering by reordering `_pvg` data with the same permutation as splats.
- Apply PVG dynamic position in both sorting and view-data compute.
- Apply PVG opacity before alpha packing.
- For hybrid rendering, draw ERP seam wrap offsets `-1`, `0`, and `1`.
- In URP, enqueue ERP and composite passes only when the camera has `GaussianOmniViewer`.

## Validation Checklist
- Static perspective asset renders unchanged.
- PVG asset has `asset.isPvgDynamic == true` and non-null `_pvg.bytes`.
- At `PvgTime = tau`, center is base position and opacity is maximal.
- At `PvgTime = tau + PvgPeriod / 4`, center offset is approximately `velocity / a`.
- Hybrid debug ERP is nonblank.
- Final camera output is perspective-resampled, not raw ERP.
- ODGS/OmniGS selector changes splat footprint in ERP.

## Known Limitations
- V1 composites as an image layer and does not solve exact depth interaction with arbitrary Unity geometry.
- HDRP parity is not implemented in this first pass.
- UnityGaussianSplatting editing/export tools remain primarily static; dynamic sidecar preservation during advanced editing should be audited before relying on editor mutations.
