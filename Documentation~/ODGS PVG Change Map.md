# ODGS and PVG Change Map

This file isolates the implementation areas added or changed during the ODGS/PVG upgrade workstream. It is intended as an audit guide: use it to inspect whether the code is correct, compare it against the Python/CUDA training code, and decide what to rewrite.

## Module 1: ODGS-Style Rendering

Goal: add `HybridOmniPerspective` beside the original perspective path. Hybrid mode renders hybrid gsplat renderers into an offscreen ERP texture, then composites that ERP texture back into the normal Unity camera view.

### Core Runtime Files

- `Runtime/GsplatRenderer.cs`
  - Added `GsplatRenderMode.Perspective` and `GsplatRenderMode.HybridOmniPerspective`.
  - Keeps direct screen rendering for `Perspective`.
  - Stops direct drawing for `HybridOmniPerspective`; hybrid renderers are drawn by `GsplatOmniViewer`.
  - Also carries PVG runtime fields, so this file belongs to both modules.

- `Runtime/GsplatRendererImpl.cs`
  - Added `RenderOmni(...)` for ERP offscreen drawing.
  - Uses shader pass `OmniERP`.
  - Draws three wrapped copies with `_GsplatOmniWrapOffset = -1, 0, +1` for ERP seam continuity.
  - Binds projection mode, ERP target size, omni near distance, and PVG parameters into the material property block.

- `Runtime/GsplatOmniViewer.cs`
  - New camera-side controller for Hybrid Omni rendering.
  - Owns the ERP `RenderTexture`.
  - Records ERP rendering into a `CommandBuffer`.
  - Provides `TryPrepareCompositeMaterial(...)` for URP/BiRP fullscreen compositing.
  - Re-renders ERP when camera position, renderer transform, renderer asset, `PvgTime`, or `PvgPeriod` changes.
  - Current fix: `Manual` update mode still refreshes if renderer state changes.

- `Runtime/GsplatSorter.cs`
  - Extended sorter interface to pass render mode and PVG time/period.
  - Perspective sorting stays view-space `z`.
  - Hybrid sorting uses radial distance from ERP viewer position.

- `Runtime/SRP/GsplatURPFeature.cs`
  - URP integration point.
  - Keeps the original perspective sort/render pass.
  - Adds `GsplatOmniErpPass` for offscreen ERP splat rendering.
  - Adds `GsplatOmniCompositePass` for ERP-to-perspective fullscreen compositing.
  - Required in the active Universal Renderer Data for Hybrid mode to display.

- `Runtime/GsplatMaterial.cs`
  - Generates normal material variants and omni-only material variants.
  - Enables `Perspective` pass for perspective materials.
  - Enables `OmniERP` pass for hybrid ERP materials.

### Shader Files

- `Runtime/Shaders/Gsplat.shader`
  - Original `Perspective` pass remains.
  - Added `OmniERP` pass with `ZWrite Off`, `ZTest Always`, `Cull Off`, and premultiplied alpha blending.
  - Both passes share the same splat data fetch and Gaussian fragment logic.

- `Runtime/Shaders/Gsplat.hlsl`
  - Added `_GsplatProjectionMode`.
  - Added spherical ERP center projection.
  - Added ODGS-style spherical covariance branch in `InitCorner(...)`.
  - Current ODGS convention fix: latitude uses `atan2(-y, distXZ)` to match ODGS CUDA.
  - Important audit point: this is ODGS-inspired. It does not exactly reproduce the CUDA tile/conic/radius rasterizer.

- `Runtime/Shaders/ERPToPerspective.shader`
  - Fullscreen composite shader.
  - Converts each perspective camera ray into ERP UV.
  - Samples `_GsplatOmniTex`.
  - Current ODGS convention fix: latitude uses `atan2(-y, distXZ)` in both URP and BiRP passes.

- `Runtime/Shaders/CalcDepth.compute`
  - Perspective mode sorts by view-space `z`.
  - Hybrid mode sorts by negative radial distance.
  - Also applies PVG dynamic position before sorting.

- `Runtime/Shaders/CalcDepthSpark.compute`
  - Same as `CalcDepth.compute`, but reads positions from Spark packed data.

### ODGS Audit Checklist

- Compare Unity ERP projection against ODGS CUDA:
  - ODGS uses `lat = atan2(-p_view.y, dist_xz)`.
  - ODGS uses `lon = atan2(p_view.x, p_view.z)`.
  - ODGS maps `x = (lon / pi + 1) * W / 2`.
  - ODGS maps `y = (0.5 - lat / pi) * H`.
- Verify `GsplatOmniViewer.WorldAlignedCameraMatrix(...)` is the intended view frame for your exported scene.
- Verify the `W` matrix in `Gsplat.hlsl` covariance branch matches ODGS `viewmatrix` layout.
- Check poles and seam behavior manually. Seam wrapping exists, but pole distortion remains a hard ERP case.
- Remember that Hybrid is an image-layer composite; it does not provide exact depth interaction with arbitrary Unity geometry.

## Module 2: PVG-Style Gaussian Modelling

Goal: keep the two render modes intact, but automatically animate Gaussian mean and opacity when the `.ply` contains PVG dynamic fields.

### Core Runtime Files

- `Runtime/GsplatAsset.cs`
  - `PlyHeaderInfo` detects PVG fields:
    - `t`
    - `scale_t`
    - `v_0`
    - `v_1`
    - `v_2`
  - Rejects partial PVG headers.
  - Adds asset-level metadata:
    - `IsPvgDynamic`
    - `PvgTimeData`
    - `PvgVelocities`
    - `PvgMaxVelocityMagnitude`
  - Adds helper methods to allocate, upload, bind, and set PVG compute parameters.

- `Runtime/GsplatAssetUncompressed.cs`
  - Reads PVG fields into `PvgTimeData` and `PvgVelocities`.
  - Treats PVG opacity as already activated alpha.
  - Keeps static opacity as original 3DGS logit plus sigmoid.
  - Stores uncompressed positions, scales, rotations, colors, and SH coefficients.
  - Assumes spatial scales are raw/log values and applies `exp(...)` on import.

- `Runtime/GsplatAssetSpark.cs`
  - Reads PVG fields into dynamic buffers.
  - Treats PVG opacity as already activated alpha.
  - Packs position, color, scale, rotation, and SH data into Spark format.
  - Assumes spatial scales are raw/log values and applies `exp(...)` before log-scale packing.
  - Important audit point: Spark quaternion import/packing should be verified carefully against your chosen PLY quaternion convention.

- `Runtime/GsplatResource.cs`
  - Adds `PvgTimeBuffer` and `PvgVelocityBuffer`.
  - Dynamic assets allocate one entry per splat.
  - Static assets allocate one dummy entry so shaders can always bind valid buffers.

- `Runtime/GsplatRenderer.cs`
  - Adds per-renderer PVG controls:
    - `PvgTime`
    - `PvgPeriod`
  - Forces sort refresh when PVG time or period changes.
  - Default `PvgPeriod` is `0.2`.

- `Runtime/GsplatRendererImpl.cs`
  - Binds `_PvgDynamic`, `_PvgTime`, and `_PvgPeriod`.
  - Expands draw bounds by the maximum possible PVG displacement.

- `Runtime/GsplatOmniViewer.cs`
  - Includes `PvgTime` and `PvgPeriod` in the hybrid ERP cache signature.
  - This is required so dynamic PVG motion re-renders the hidden ERP texture.

### Shader Files

- `Runtime/Shaders/Gsplat.hlsl`
  - Adds PVG shader buffers and uniforms:
    - `_PvgDynamic`
    - `_PvgTime`
    - `_PvgPeriod`
    - `_PvgTimeBuffer`
    - `_PvgVelocityBuffer`
  - Implements:
    - `a = 2*pi / l`
    - `mean(t) = mean0 + v * sin((t - tau) * a) / a`
    - `beta = exp(scale_t)`
    - `opacity(t) = opacity0 * exp(-0.5 * (tau - t)^2 / beta^2)`

- `Runtime/Shaders/GsplatUncompressed.hlsl`
  - Applies PVG dynamic position before projection.
  - Applies PVG dynamic opacity before fragment alpha falloff.

- `Runtime/Shaders/GsplatSpark.hlsl`
  - Applies PVG dynamic position and opacity after unpacking Spark splat data.

- `Runtime/Shaders/CalcDepth.compute`
  - Applies PVG dynamic position before depth key computation.

- `Runtime/Shaders/CalcDepthSpark.compute`
  - Applies PVG dynamic position before Spark depth key computation.

### PVG Helper Files

- `Runtime/GsplatPvgTimeController.cs`
  - Optional Play Mode helper.
  - Press `K` to toggle time animation.
  - `TimeSpeed > 0` increases `PvgTime`.
  - `TimeSpeed < 0` decreases `PvgTime`.
  - Supports Unity Input System and legacy input.

- `Runtime/Gsplat.asmdef`
  - Added optional Input System reference and version define for `GsplatPvgTimeController`.

### PVG Export Assumptions

The current Unity importer expects your PVG `.ply` to be exported with:

```python
use_activated_opacity=True
use_activated_scaling=False
use_normalized_rotation=True
use_activated_scale_t=False
```

Expected property meaning:

- `opacity`: activated alpha in `[0, 1]` for PVG assets.
- `scale_0..2`: raw/log spatial scale.
- `rot_0..3`: normalized quaternion. Audit WXYZ versus XYZW carefully.
- `t`: temporal center `tau`.
- `scale_t`: raw/log temporal scale, so Unity computes `beta = exp(scale_t)`.
- `v_0..2`: PVG velocity vector used directly by the deformation equation.

### PVG Audit Checklist

- Confirm your PLY header contains either all PVG fields or none of them.
- Confirm PVG opacity is activated. If it is a logit, PVG assets will be too opaque or too transparent.
- Confirm spatial scale is raw/log. If it is already activated scale, Unity will apply `exp(...)` incorrectly.
- Confirm `scale_t` is raw/log. If it is already activated beta, Unity will apply `exp(...)` incorrectly.
- Verify quaternion convention:
  - Uncompressed path stores `rot_0..3` directly into `float4` consumed by `CalcCovariance`.
  - Spark path constructs `UnityEngine.Quaternion(x, y, z, w)` before packing.
  - This is a high-priority consistency check if Spark and Uncompressed render differently.
- Test formula anchors:
  - At `PvgTime = tau`, dynamic mean should equal base position.
  - At `PvgTime = tau + l / 4`, offset should be approximately `v / a`.
  - Far from `tau`, opacity should fade toward zero.

## Extra Debug/Validation Module

This module is not part of ODGS rendering or PVG modelling, but was added to help validate scene alignment against training poses.

- `Runtime/GsplatTrainingPoseViewer.cs`
  - Reads OpenMVG `data_views.json` and `data_extrinsics.json`.
  - Lists training image poses.
  - Applies selected pose to a Unity camera.

- `Editor/GsplatTrainingPoseViewerEditor.cs`
  - Adds inspector buttons for loading JSON and applying previous/next poses.

Use this module to stand at training camera centers and compare Unity output with training images.

## General Build Hygiene Changes

- `Runtime/GsplatRenderer.cs`
- `Runtime/GsplatSettings.cs`
- `Runtime/GsplatCutouts.cs`

These runtime files had `UnityEditor` imports guarded with `#if UNITY_EDITOR` so player builds do not reference editor-only assemblies.

## Existing Documentation

- `Documentation~/Implementation Details.md`
  - General implementation explanation.

- `Documentation~/Hybrid Omni Checkpoint.md`
  - Earlier conversation checkpoint for the Hybrid Omni implementation.

This file should be treated as the audit-oriented change map.
