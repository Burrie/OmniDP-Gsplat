# Hybrid Omni-To-Perspective Checkpoint

This file is a handoff note for future work on the upgraded `gsplat_ver2` package. It captures the current implementation state so a new conversation can continue without re-deriving the same context.

## Goal

The package was upgraded from the original perspective-only gsplat renderer toward a hybrid ODGS/OmniGS render mode:

1. Render ODGS-trained splats into an offscreen 2:1 ERP texture from the viewer camera position.
2. Composite that ERP result back into the normal Unity perspective camera view.
3. Keep the original perspective path unchanged unless `HybridOmniPerspective` is selected.

## User Setup

For URP:

1. Add `Gsplat URP Feature` to the active `Universal Renderer Data`.
2. Add `Gsplat Omni Viewer` to the active camera.
3. Add `Gsplat Renderer` to a separate GameObject.
4. Assign the imported `.ply` `GsplatAsset`.
5. Set `Render Mode` to `Hybrid Omni Perspective`.
6. Start with ERP resolution `1024x512` or `2048x1024`.

If `Gsplat URP Feature` does not appear, clear compile errors first, then reimport/restart Unity. It is compiled only when URP is installed and `GSPLAT_ENABLE_URP` is active.

## Main Files Changed

- `Runtime/GsplatRenderer.cs`
  - Added `GsplatRenderMode` enum.
  - Added serialized `RenderMode` property using `m_renderMode`.
  - Split renderer preparation from perspective drawing.
  - Added `RenderOmni(...)` entry point.

- `Runtime/GsplatOmniViewer.cs`
  - New component for the user camera.
  - Allocates the ERP render texture.
  - Renders hybrid-mode gsplats into the ERP target.
  - Exposes ERP settings, update policy, clear color, renderer tracking, and debug preview.
  - Provides `TryPrepareCompositeMaterial(...)` for URP/BiRP compositing.

- `Runtime/SRP/GsplatURPFeature.cs`
  - Made the feature `public` so Unity can list it in URP renderer data.
  - Kept the existing sort pass.
  - Added a hybrid fullscreen composite pass for URP.
  - Unity 6 path uses RenderGraph blit.
  - Older URP path uses `Blitter.BlitCameraTexture`.

- `Runtime/Shaders/Gsplat.hlsl`
  - Added hybrid spherical projection branch.
  - Computes center longitude/latitude into ERP clip space.
  - Ports ODGS-style `computeOmniCov2D` covariance math.
  - Draws three horizontal wrap copies through `_GsplatOmniWrapOffset` for seam continuity.

- `Runtime/Shaders/ERPToPerspective.shader`
  - New fullscreen compositor.
  - Pass 0: SRP/URP blit path.
  - Pass 1: Built-in `OnRenderImage` path.
  - Converts each perspective screen pixel ray to ERP UV and samples `_GsplatOmniTex`.

- `Runtime/Shaders/CalcDepth.compute`
- `Runtime/Shaders/CalcDepthSpark.compute`
  - Added `_GsplatProjectionMode`.
  - Perspective mode sorts by view-space `z`.
  - Hybrid mode sorts by negative radial distance from the ERP viewer position.

- `Runtime/GsplatSorter.cs`
  - `IGsplat` now exposes `RenderMode`.
  - Normal camera gather defaults to perspective renderers only.
  - Added per-renderer `DispatchSort(...)` overload for the omni viewer.

- `Runtime/GsplatSettings.cs`
  - Hardened settings initialization.
  - Repairs missing default materials/shaders when settings load.
  - Avoids null exceptions during import/gizmos.

- `Runtime/GsplatAsset.cs`
- `Runtime/GsplatAssetSpark.cs`
- `Runtime/GsplatAssetUncompressed.cs`
- `Runtime/GsplatRendererImpl.cs`
- `Runtime/Shaders/Gsplat.shader`
  - Support plumbing for render mode, target size, omni near distance, and omni draw calls.

## Important Fixes Already Applied

- `IGsplat.RenderMode` must be implemented as a property, not a public field. `GsplatRenderer` now uses:
  - `[SerializeField] GsplatRenderMode m_renderMode`
  - `public GsplatRenderMode RenderMode { get; set; }`

- Unity 2022 has ambiguous overloads for `AssetDatabase.TryGetGUIDAndLocalFileIdentifier`. The fix uses explicit:

```csharp
long localId;
AssetDatabase.TryGetGUIDAndLocalFileIdentifier(GsplatAsset, out var guid, out localId)
```

- `GsplatSettings.OnValidate()` previously assumed `Materials` was non-null. It now repairs/defaults references safely.

- `GsplatAssetSpark.SetupMaterialPropertyBlock()` nulls were likely caused by invalid settings/material references. `PrepareRenderer()` now checks `GsplatSettings.Instance.Valid` before binding assets.

## Current Limitations

- The hybrid path is an image-layer composite over the camera color buffer.
- Exact depth interaction with arbitrary Unity scene geometry is deferred.
- ERP re-render is expensive and happens on camera translation, renderer set/asset/transform changes, or `Always` update mode.
- Camera rotation should reuse the ERP texture and only redo the fullscreen composite.
- Unity compilation must be verified in the editor; local shell checks only covered text/static consistency.

## Expected Performance Behavior

- Rotation-only camera changes: cheap, fullscreen ERP-to-perspective composite only.
- Translation: expensive, re-renders the full ERP splat target.
- Recommended test resolutions:
  - `1024x512`
  - `2048x1024`
  - `4096x2048` only after correctness is verified.

## If Future Work Breaks

Use this file plus the current Git checkpoint as fallback context. Ask the new chat to read:

- `Documentation~/Hybrid Omni Checkpoint.md`
- `Runtime/GsplatOmniViewer.cs`
- `Runtime/GsplatRenderer.cs`
- `Runtime/SRP/GsplatURPFeature.cs`
- `Runtime/Shaders/Gsplat.hlsl`
- `Runtime/Shaders/ERPToPerspective.shader`

Then continue from the current hybrid implementation instead of redesigning from scratch.
