# Hybrid Omni VR Composite Plan

This note captures the future implementation plan for making the Hybrid Omni-To-Perspective composite robust for VR/XR and asymmetric camera projections.

## Summary

The current `ERPToPerspective` composite reconstructs each screen ray from:

- camera forward/right/up vectors
- vertical `fieldOfView`
- camera `aspect`

That is acceptable for a normal centered perspective camera, but it is not robust for VR headsets, stereo per-eye projection matrices, lens-shifted cameras, physical cameras, oblique frusta, or platform-specific render target Y flips. For HCI/VR use, replace this with projection-matrix-based ray reconstruction.

## Target Behavior

- The hidden ERP splat render stays unchanged: `GsplatOmniViewer` renders ODGS/OmniGS splats into the 2:1 ERP texture from the viewer position.
- The final user-facing screen must always look like a natural perspective camera view, not raw ERP.
- Rotating the camera/head should only re-run the fullscreen composite.
- Translating the camera/head should re-render the ERP texture and then composite.
- VR stereo should sample correct ERP directions for each eye.

## Key Changes

### Composite Inputs

Replace the current composite inputs:

- `_GsplatCompositeCameraForward`
- `_GsplatCompositeCameraRight`
- `_GsplatCompositeCameraUp`
- `_GsplatCompositeProjectionData`

with matrix-driven inputs:

- inverse view-projection matrix for the active eye/camera
- world-to-ERP-view matrix already stored as `_GsplatOmniWorldToCamera`
- source camera color texture
- ERP texture

For non-XR perspective cameras, compute the inverse view-projection matrix from the camera's actual projection matrix and `worldToCameraMatrix`.

For XR, use Unity's per-eye view/projection data when available. Each eye must reconstruct rays from its own inverse view-projection matrix.

### Shader Ray Reconstruction

In `ERPToPerspective.shader`, reconstruct each ray using clip/NDC coordinates:

1. Convert fullscreen UV to NDC.
2. Build a clip-space point on the far plane.
3. Transform by inverse view-projection into world space.
4. Ray direction is `normalize(worldFar.xyz / worldFar.w - cameraWorldPosition)`.
5. Transform that world ray through `_GsplatOmniWorldToCamera`.
6. Convert to the existing ERP UV convention:
   - `lon = atan2(x, z)`
   - `lat = atan2(-y, length(xz))`
   - `uv = (lon / (2*pi) + 0.5, 0.5 - lat / pi)`

Keep the current premultiplied alpha composite:

```hlsl
result.rgb = splat.rgb + scene.rgb * (1.0 - splat.a);
result.a = saturate(splat.a + scene.a * (1.0 - splat.a));
```

### Camera/Viewer Handling

In `GsplatOmniViewer.TryPrepareCompositeMaterial(...)`:

- Detect orthographic cameras and warn once that Hybrid Omni composite currently requires perspective projection.
- For normal perspective cameras, upload inverse view-projection and camera world position.
- For XR/stereo, upload per-eye matrices using Unity stereo constants or material arrays, depending on the URP version path.
- Continue uploading `_GsplatOmniTex` and `_GsplatOmniWorldToCamera`.

### URP Path

In `GsplatURPFeature`:

- Keep the current ERP pass.
- Keep the composite pass timing before post-processing.
- Ensure the composite shader samples the active camera color texture with URP-compatible full-screen blit UVs.
- Verify render-target Y orientation on desktop and Android/Quest.

### Built-In Fallback

Keep `OnRenderImage` support for BiRP:

- Use the same inverse view-projection shader path.
- Upload the source texture as `_BlitTexture`.
- Preserve existing alpha composite behavior.

## Test Plan

- Desktop centered perspective camera:
  - Compare output before/after rewrite; view direction should remain visually unchanged.
- Lens-shift/asymmetric projection:
  - Apply camera lens shift or custom projection matrix; output should follow the actual camera frustum.
- Orthographic camera:
  - Confirm a clear warning and no misleading "correct" composite claim.
- URP desktop:
  - Check no vertical flip and no raw ERP showing on screen.
- Quest/Android URP:
  - Verify left and right eyes see correct perspective views.
  - Rotate headset: ERP is reused, only composite changes.
  - Translate headset: ERP re-renders from the new position.
- Debug isolation:
  - If `Show Debug Erp` is nonblank but screen is wrong, issue is composite ray/UV logic.
  - If ERP is blank, issue is the omni splat pass before compositing.

## Assumptions

- Hybrid mode remains an image-layer composite and does not solve exact depth interaction with arbitrary Unity geometry.
- The ERP texture is rendered from the viewer/camera position, not a camera rotation.
- ODGS and OmniGS rasterizer selection affects the hidden ERP splat covariance only; the final ERP-to-perspective composite is shared.
- First VR target is Unity 2022.3 URP on Quest-class hardware.
