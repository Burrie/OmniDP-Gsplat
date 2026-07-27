# BiRP PCVR Memory and Performance Optimization Handoff

## Document Purpose

This document preserves both the optimization plan and the implementation that was
added to `gsplat_ver2`. It is intended for:

- later manual review;
- handing the work to another AI model;
- debugging Windows PCVR memory use;
- validating that later changes preserve ODGS, OmniGS, PVG, and perspective behavior.

The implementation described here was present in the working tree on 2026-07-28.
At the time this document was written, the changes had not been committed.

## Project Context

The primary target is:

- Unity 2022.3;
- Windows Standalone;
- Built-in Render Pipeline (BiRP);
- Meta Quest 3 connected through PCVR;
- the Windows executable forced onto the NVIDIA GPU;
- Vulkan or Direct3D 12;
- a fixed omnidirectional PVG model used for a complete application session.

The hybrid renderer creates a native ERP image at `1920x512`. The project requires
`Use Vertical Black Padding` with 224 rows above and below the native content, giving
a logical `1920x960` ERP.

The following behavior must remain unchanged:

- Perspective projection mode;
- Hybrid Omni-To-Perspective projection mode;
- ODGS ERP rasterization;
- OmniGS ERP rasterization;
- static Gaussian assets;
- PVG dynamic Gaussian assets;
- Spark and Uncompressed asset formats;
- existing PVG formulas and the `0.05` temporal marginal rejection threshold;
- ARGBHalf ERP output;
- BiRP multi-pass and the existing separate-eye OVR camera rig.

BiRP Single Pass Instanced remains unsupported.

## Original Problem

The Windows executable exhausted system RAM during PCVR use, eventually disrupting
the PCVR connection. The original implementation could retain several large classes
of memory at the same time:

1. Managed arrays deserialized from the imported Gaussian asset.
2. GPU buffers containing an uploaded copy of the same model.
3. A native ERP render texture.
4. A second, physically padded ERP render texture.
5. Temporary upload arrays and recurring managed allocations.
6. Recreated command buffers and repeated scene/camera searches.

Moving the application to the NVIDIA GPU does not remove CPU-side managed arrays.
Unity also uses one graphics adapter for this rendering workload; this work does not
split rendering between the Intel and NVIDIA GPUs.

## Original Requested Plan

### Virtual Vertical Padding

- Keep the physical ERP at `1920x512`.
- Preserve 224 opaque-black logical rows above and below the native content.
- Remove the physical `1920x960` padded ARGBHalf render texture.
- Apply padding in the ERP-to-perspective composite shader.
- Use manual bilinear sampling:
  - horizontal taps wrap at the ERP seam;
  - native content taps sample the physical ERP;
  - vertical taps outside native content return opaque black.
- Padding changes must remain composite-only and must not invalidate the native ERP.
- The debug preview must show a black 2:1 frame with native ERP content centered in it.

### Runtime Storage

Add a PLY importer control:

```text
Runtime Storage
  Embedded Managed
  Streamed Player Data
```

`Embedded Managed` remains the backward-compatible default.

`Streamed Player Data` writes GPU-ready model data to a versioned binary blob under
`Library`, excludes the full arrays from the imported runtime asset, includes the
blob in desktop builds, and uploads it through bounded reusable batches.

### GPU Residency

Add a project setting:

```text
Player GPU Residency
  Release At Zero References
  Pin Until Shutdown
```

`Release At Zero References` remains the default. `Pin Until Shutdown` is recommended
for a fixed model used for the whole PCVR session.

Pinned embedded assets may release their CPU arrays only after a complete successful
GPU upload. Shared GPU resources must be disposed on application shutdown or Unity
subsystem reset.

### Allocation and Scheduling Cleanup

- Reuse one ERP command buffer per viewer.
- Record the BiRP overlay command only when its attachment/material state changes.
- Replace recurring `FindObjectsOfType`, `Camera.allCameras`, LINQ filtering, and
  per-frame `ToArray` calls with registries and reusable buffers.
- Do not prepare a Hybrid renderer from its normal `Update`.
- Let the active omni viewer prepare Hybrid renderers only when it actually renders ERP.
- Evaluate all active camera movement from one allocation-free camera snapshot per frame.
- Preserve per-eye forced sorting for the OVR separate-eye rig.
- Cache cutout data and PVG controller targets.

### PVG Shader Optimization

The temporal marginal must be calculated once for each splat:

```hlsl
beta = exp(rawScaleT);
dt = tau - PvgTime;
marginal = exp(-0.5 * dt * dt / (beta * beta));
```

The splat must be rejected when:

```hlsl
marginal <= 0.05
```

The same marginal must then be reused for opacity. This rejection should happen
before covariance and spherical harmonics work.

### Upload Ownership

Shared GPU resources require explicit states:

```text
NotStarted
Uploading
Complete
Failed
```

Multiple renderers referencing the same asset must not start duplicate uploads.
Both synchronous and asynchronous upload must support embedded and streamed assets.
`Render Before Upload Complete` must continue to use the uploaded splat count.

### Diagnostics

Add profiler markers and useful one-time diagnostics for:

- ERP rendering;
- ERP compositing;
- sorting;
- streamed upload batches;
- CPU-data release;
- missing stream blobs;
- upload failures.

## Implemented Architecture

## 1. Shader-Side Virtual ERP Padding

Primary files:

- `Runtime/GsplatOmniViewer.cs`
- `Runtime/Shaders/ERPToPerspective.shader`

`GsplatOmniViewer` now allocates only the native ERP render texture:

```text
Physical texture: 1920x512 ARGBHalf
Logical display:  1920x960
Content rows:     224 through 735
Top padding:      224 rows
Bottom padding:   224 rows
```

`DisplayErpWidth` and `DisplayErpHeight` describe the logical display dimensions.
`DisplayErpTexture` is retained only as an obsolete compatibility alias of the native
`ErpTexture`. There is no physical padded texture.

The composite material receives:

```text
_GsplatOmniDisplayData.x = logical display height
_GsplatOmniDisplayData.y = top content row
_GsplatOmniDisplayData.z = native content height
_GsplatOmniDisplayData.w = padding enabled
```

The shader maps logical ERP UV coordinates into logical pixel coordinates and
manually samples four taps. Horizontal coordinates wrap. Vertical samples outside
the native content return:

```hlsl
float4(0.0, 0.0, 0.0, 1.0)
```

The native render texture uses `FilterMode.Point` because bilinear interpolation is
performed explicitly by the shader. Leaving the texture on bilinear filtering would
filter each of the four manual taps a second time.

Changing vertical-padding settings no longer causes `ShouldRenderErp()` to return
true. `OnValidate()` invalidates ERP only for native-content changes such as ERP
resolution, near distance, rasterizer, or background color.

The debug ERP preview draws a black 2:1 rectangle and places the physical native ERP
at the logical vertical offset.

### Texture Memory Effect

ARGBHalf uses 8 bytes per pixel.

```text
1920x512 physical native ERP:  7.50 MiB
1920x960 removed padded ERP:   14.06 MiB
```

The removed texture saves approximately:

- `14.06 MiB` for one viewer;
- `28.13 MiB` for two separate-eye viewers.

This does not reduce the GPU memory required by the native ERP or Gaussian buffers.

## 2. Reused BiRP Command Buffers

Primary file:

- `Runtime/GsplatOmniViewer.cs`

Each viewer now owns and reuses:

- one ERP render command buffer;
- one optional BiRP camera overlay command buffer.

The ERP command buffer is cleared and recorded only for an actual ERP render.

The BiRP overlay command is recorded once and remains attached to
`CameraEvent.AfterEverything`. Camera direction, projection values, padding values,
ERP texture, and reference matrices are updated on the shared material without
rebuilding the command list every frame.

The standard BiRP `OnRenderImage` path remains available. The command-buffer overlay
path remains intended for the separate-eye Quest/OVR setup where image-effect
callbacks may not be reliable.

SRP fallback behavior is retained for compilation compatibility, but this optimization
targets BiRP.

## 3. Runtime Registries and Reusable Buffers

Primary files:

- `Runtime/GsplatRuntimeRegistry.cs`
- `Runtime/GsplatRenderer.cs`
- `Runtime/GsplatRendererImpl.cs`
- `Runtime/GsplatOmniViewer.cs`
- `Runtime/GsplatPvgTimeController.cs`
- `Runtime/GsplatSorter.cs`

`GsplatRuntimeRegistry` maintains active renderer and viewer sets through
`OnEnable`/`OnDisable`.

It also:

- rebuilds once after scene load as a safety check;
- stores a reusable camera array;
- calls `Camera.GetAllCameras` at most once per frame;
- grows the camera array only when necessary;
- exposes reusable renderer copying for automatic omni-viewer discovery.

Remaining `FindObjectsOfType` usage in this module occurs only during the one-time
post-scene-load rebuild, not in the steady frame loop.

Other allocation changes include:

- `GsplatRenderer` reuses an exact-size cutout array instead of LINQ filtering and
  `ToArray`;
- `GsplatRendererImpl` reuses cutout shader-data arrays;
- `GsplatPvgTimeController` uses explicit targets or the renderer registry;
- sorter camera cleanup/gathering no longer uses LINQ;
- omni-viewer renderer loops use indexed reusable collections.

Hybrid renderers no longer call `PrepareRenderer()` in their normal `Update`.
Perspective renderers keep their existing update path. The omni viewer prepares a
Hybrid renderer once per actual ERP render.

The separate-eye rig can continue enabling `ForceSortPerErpRender`, ensuring that
each eye receives depth ordering from its own viewpoint.

## 4. PVG Temporal Work Reduction

Primary files:

- `Runtime/Shaders/Gsplat.hlsl`
- `Runtime/Shaders/GsplatSpark.hlsl`
- `Runtime/Shaders/GsplatUncompressed.hlsl`

`EvaluatePvgMarginal` returns both the marginal and the already loaded PVG time data.
An `ApplyPvgPosition` overload reuses that time data, avoiding a duplicate buffer read.

Spark and Uncompressed shaders now follow this order:

1. Evaluate the temporal marginal once.
2. Reject the splat when marginal is `<= 0.05`.
3. Load/decode the remaining splat data.
4. Apply the PVG position formula.
5. Perform projection and covariance work.
6. Reuse the marginal for opacity.
7. Evaluate SH only for surviving splats through the existing render flow.

The PVG equations, period behavior, and rejection threshold are unchanged.

## 5. Explicit Shared Upload State

Primary files:

- `Runtime/GsplatResource.cs`
- `Runtime/GsplatAsset.cs`
- `Runtime/GsplatRendererImpl.cs`

`GsplatResource` now owns:

```csharp
GsplatUploadState UploadState;
uint UploadedCount;
string UploadError;
Task UploadTask;
```

The first renderer that binds an asset transitions its shared resource from
`NotStarted` to `Uploading`. Later renderers receive the same resource and task rather
than starting another upload.

On success:

- `UploadedCount` becomes the complete splat count;
- state becomes `Complete`;
- the previous error is cleared.

On failure:

- state becomes `Failed`;
- the error text is retained;
- one error is logged by the shared upload owner.

Partial asynchronous rendering still uses `UploadedCount`, preserving
`Render Before Upload Complete`.

## 6. Player GPU Residency

Primary files:

- `Runtime/GsplatSettings.cs`
- `Editor/GsplatSettingsProvider.cs`
- `Runtime/GsplatResourceManager.cs`
- `Runtime/GsplatAsset.cs`
- `Runtime/GsplatAssetSpark.cs`
- `Runtime/GsplatAssetUncompressed.cs`

`GsplatSettings` now exposes:

```csharp
public GsplatPlayerGpuResidency PlayerGpuResidency;
```

Modes:

- `ReleaseAtZeroReferences`: existing default-style behavior;
- `PinUntilShutdown`: preserve the shared GPU resource after the last renderer releases it.

Pinned resources are disposed by:

- `Application.quitting`;
- `RuntimeInitializeLoadType.SubsystemRegistration`.

For an embedded asset in a non-Editor player, a successful complete upload checks
whether the shared resource is pinned. If it is pinned, the asset releases:

- Spark packed splats and SH arrays;
- Uncompressed position, scale, rotation, color, and SH arrays;
- PVG time and velocity arrays.

CPU arrays are never released before successful completion. This prevents a failed
or partial upload from destroying its recovery data.

## 7. Streamed Player Data

Primary files:

- `Runtime/GsplatStreamData.cs`
- `Runtime/GsplatAsset.cs`
- `Runtime/GsplatAssetSpark.cs`
- `Runtime/GsplatAssetUncompressed.cs`
- `Editor/GsplatImporter.cs`
- `Editor/GsplatStreamBuildProcessor.cs`

The importer exposes:

```csharp
GsplatRuntimeStorage.EmbeddedManaged
GsplatRuntimeStorage.StreamedPlayerData
```

The importer version was increased from 1 to 2.

For streamed assets, import performs:

1. Normal PLY parsing and conversion.
2. Generation of GPU-ready Spark or Uncompressed arrays.
3. Writing a binary blob to:

```text
Library/GsplatStreamData/<asset-guid>.gsplatbin
```

4. Clearing the large CPU arrays before Unity serializes the runtime asset.

The runtime asset retains metadata such as:

- compression mode;
- splat count;
- SH bands;
- PVG dynamic flag;
- stream data identifier;
- bounds and existing import metadata.

### Binary Blob Format

The stream format includes:

- magic `GSPB`;
- format version 1;
- compression mode;
- splat count;
- SH band count;
- PVG flag;
- section count;
- section offsets;
- section byte lengths;
- element counts;
- element strides;
- per-section FNV-1a validation hashes.

Possible sections are:

```text
PackedSplats
PackedSH1
PackedSH2
PackedSH3
Positions
Scales
Rotations
Colors
SH
PvgTime
PvgVelocity
```

Only sections relevant to the selected compression mode and asset capabilities contain
data.

The reader validates:

- magic;
- format version;
- section count and identifiers;
- asset metadata;
- element stride;
- requested ranges;
- bytes consumed;
- per-section hash.

If reader construction fails because of a malformed header, the file handle is closed
immediately.

### Desktop Build Integration

`GsplatStreamBuildProcessor` scans streamed gsplat assets before supported desktop
builds. Missing cache files are force-reimported. Valid files are added through:

```csharp
BuildPlayerContext.AddAdditionalPathToStreamingAssets(...)
```

Player location:

```text
Application.streamingAssetsPath/Gsplat/<asset-guid>.gsplatbin
```

Supported streamed targets:

- Windows;
- Linux;
- macOS.

Android remains on `Embedded Managed`. No Android jar/URI streaming implementation
was added.

The build processor currently includes every `StreamedPlayerData` gsplat asset found
in the project, not only assets reachable from enabled scenes. This is robust but may
increase build size when unused streamed assets exist.

### Bounded Upload

Spark and Uncompressed upload paths allocate reusable typed arrays sized from
`GsplatSettings.UploadBatchSize`. Each loop:

1. Seeks to the relevant sections.
2. Reads one splat batch and its associated SH/PVG data.
3. Uploads all attributes for that batch.
4. Advances `UploadedCount`.
5. Optionally yields for asynchronous upload.

The temporary managed memory is bounded by the batch size and the selected format,
not by total model size.

Approximate managed model data avoided for SH3 plus PVG:

```text
Spark:       76 bytes * splat count
Uncompressed: 256 bytes * splat count
```

These estimates exclude Unity object overhead, sort buffers, temporary upload batches,
and GPU memory.

## 8. Profiler Markers and Diagnostics

Implemented markers:

```text
Gsplat.RenderERP
Gsplat.CompositeERP
Gsplat.Sort
Gsplat.Upload
Gsplat.StreamedUploadBatch
Gsplat.ReleaseCpuData
Gsplat.MissingStreamBlob
Gsplat.UploadFailure
```

Upload failures retain an error message in the shared resource and log the asset name.
Missing files, invalid metadata, corrupt sections, and hash failures all enter the
shared failure path.

Successful embedded CPU-data release logs the approximate released MiB.

## Public Configuration

## Recommended PCVR Configuration

For the fixed Windows PCVR model:

```text
PLY Importer:
  Compression: Spark, unless Uncompressed is required
  Runtime Storage: Streamed Player Data

Project Settings > Gsplat:
  Player GPU Residency: Pin Until Shutdown
  Upload Batch Size: tune while profiling

Gsplat Renderer:
  Async Upload: optional
  Render Before Upload Complete: optional

Gsplat Omni Viewer:
  Erp Width: 1920
  Erp Height: 512
  Use Vertical Black Padding: enabled
  Vertical Black Padding Pixels: 224
```

Changing `Runtime Storage` requires applying/reimporting the source PLY so the blob is
created and the imported asset representation changes.

`Async Upload` spreads upload over frames but does not reduce final GPU memory. A
smaller upload batch lowers temporary RAM but increases the number of upload/yield
iterations.

## Backward-Compatible Configuration

To preserve the previous asset behavior:

```text
Runtime Storage: Embedded Managed
Player GPU Residency: Release At Zero References
```

All existing render modes and model types remain available in either storage mode.

## File-by-File Change Map

### New Files

`Runtime/GsplatRuntimeRegistry.cs`

- Active viewer and renderer registries.
- Shared reusable camera snapshot.
- One-time post-scene-load registry rebuild.

`Runtime/GsplatStreamData.cs`

- Runtime storage enum.
- Versioned binary stream writer and reader.
- Section metadata and hashes.
- Editor cache, build-relative, and player path helpers.

`Editor/GsplatStreamBuildProcessor.cs`

- Adds streamed blobs to supported desktop builds.
- Reimports missing cache data.
- Fails supported desktop builds when required blobs remain missing.

### Modified Runtime Files

`Runtime/GsplatOmniViewer.cs`

- Removes physical padded ERP allocation.
- Makes display dimensions logical.
- Reuses BiRP ERP and overlay command buffers.
- Uses renderer registry and reusable scratch collection.
- Makes padding composite-only.
- Draws a logically padded debug preview.
- Adds ERP render/composite profiler markers.

`Runtime/Shaders/ERPToPerspective.shader`

- Shared composite code for the existing passes.
- Logical ERP sampling.
- Wrapped horizontal manual bilinear taps.
- Opaque-black virtual vertical padding.

`Runtime/GsplatRenderer.cs`

- Registers active renderers.
- Reuses cutout arrays.
- Stops normal `Update` preparation for Hybrid renderers.
- Uses the viewer registry for missing-viewer diagnostics.

`Runtime/GsplatRendererImpl.cs`

- Reuses cutout shader data.
- Uses the shared camera snapshot.
- Participates in the shared upload state.

`Runtime/GsplatPvgTimeController.cs`

- Uses explicit renderer targets or the runtime registry.
- Removes recurring renderer discovery.

`Runtime/GsplatSorter.cs`

- Removes LINQ allocation paths.
- Uses shared camera data.
- Adds the sort profiler marker.

`Runtime/GsplatResource.cs`

- Adds upload state, task, error, and uploaded count.

`Runtime/GsplatResourceManager.cs`

- Adds pinned shared-resource lifetime.
- Disposes all resources at shutdown/subsystem reset.

`Runtime/GsplatSettings.cs`

- Adds player GPU residency setting.
- Restores release-at-zero as the reset default.

`Runtime/GsplatAsset.cs`

- Owns shared sync/async upload transitions.
- Handles completion and failure.
- Releases embedded CPU arrays after successful pinned upload.
- Adds stream metadata and profiler markers.

`Runtime/GsplatAssetSpark.cs`

- Adds streamed sync/async batch upload.
- Adds CPU byte accounting and release.

`Runtime/GsplatAssetUncompressed.cs`

- Adds streamed sync/async batch upload.
- Adds CPU byte accounting and release.

`Runtime/Shaders/Gsplat.hlsl`

- Evaluates PVG marginal once.
- Reuses loaded PVG time data for dynamic position.

`Runtime/Shaders/GsplatSpark.hlsl`

- Rejects inactive PVG splats before expensive work.
- Reuses marginal for alpha.

`Runtime/Shaders/GsplatUncompressed.hlsl`

- Rejects inactive PVG splats before expensive work.
- Reuses marginal for alpha.

### Modified Editor and Documentation Files

`Editor/GsplatImporter.cs`

- Importer version 2.
- Runtime storage control.
- Stream blob creation and managed-array release.

`Editor/GsplatSettingsProvider.cs`

- Exposes player GPU residency in Project Settings.

`README.md`

- Documents storage modes, desktop streaming, GPU residency, and virtual padding.

`Documentation~/Implementation Details.md`

- Documents player memory architecture and resource state.

`CHANGELOG.md`

- Records the optimization features under Unreleased.

## Validation Already Performed

The complete source was compiled outside a Unity project using Unity 2022.3.62f3
assemblies.

Successful compiler passes:

1. Runtime assembly with player defines.
2. Runtime assembly with editor defines.
3. Editor importer/build assembly referencing the validated runtime assembly.

Additional checks:

- `git diff --check` passed.
- No temporary validation script remains in the repository.
- The generated repository navigation index was removed after use.
- The physical padded texture field/allocation no longer exists.
- Hot-path scene searches were replaced; remaining runtime `FindObjectsOfType` calls
  are limited to the one-time registry rebuild.

## Validation Not Yet Performed

The following must still be tested inside the actual Unity/PCVR project:

- Unity shader import and platform shader compilation;
- Vulkan and Direct3D 12 player builds;
- fixed-camera image comparison before and after optimization;
- exactly 224 black logical rows above and below content;
- bilinear behavior at the two content/padding boundaries;
- ERP horizontal seam filtering;
- BiRP `OnRenderImage`;
- BiRP command-buffer overlay;
- separate left/right OVR eye viewers;
- Spark and Uncompressed assets;
- static and PVG assets;
- Perspective and Hybrid projection modes;
- ODGS and OmniGS rasterizers;
- synchronous and asynchronous upload;
- rendering before upload completes;
- missing and corrupted streamed blobs;
- disable/re-enable with shared and pinned resources;
- shutdown disposal;
- steady-state GC allocation;
- 30-minute process memory trend;
- startup peak RAM;
- PCVR stability.

The implementation is compiler-validated, not yet visually or performance validated.

## Required Test Procedure

### Visual Regression

Capture fixed-camera images before and after the optimization for:

```text
Perspective + static + Spark
Perspective + PVG + Spark
Hybrid ODGS + static + Spark
Hybrid ODGS + PVG + Spark
Hybrid OmniGS + static + Spark
Hybrid OmniGS + PVG + Spark
```

Repeat critical comparisons with Uncompressed assets.

Use identical:

- camera transform and FOV;
- PVG time and period;
- ERP resolution;
- background color;
- sort settings;
- opacity, SH degree, and brightness;
- Unity color space and graphics API.

### Padding Validation

With `1920x512`, padding enabled, and padding value 224:

- logical display height must be 960;
- native content must occupy rows 224 through 735;
- rows 0 through 223 must be opaque black;
- rows 736 through 959 must be opaque black;
- changing padding must not trigger an ERP re-render;
- no `1920x960` render texture should appear in the Memory Profiler.

### Upload Validation

Test all combinations:

```text
Embedded Managed + synchronous
Embedded Managed + asynchronous
Streamed Player Data + synchronous
Streamed Player Data + asynchronous
```

For asynchronous tests, repeat with `Render Before Upload Complete` both enabled and
disabled.

Verify that two renderers referencing one asset share:

- one GPU resource;
- one upload state;
- one upload task;
- one streamed file read/upload sequence.

### Memory Profiling

Build a Windows Development Player and profile:

1. Process startup.
2. Model upload.
3. First ERP render.
4. Five minutes of head rotation.
5. Five minutes of translation.
6. At least 30 minutes of PVG time playback.
7. Renderer disable/re-enable.
8. Application shutdown.

Record:

- process private bytes;
- Unity reserved and used memory;
- managed heap;
- graphics memory;
- temporary upload memory;
- GC allocations per frame;
- profiler marker durations.

Expected outcomes:

- no physical `1920x960` padded ERP;
- no steady growth in process memory;
- no duplicate upload for shared assets;
- no steady-frame gsplat managed allocations;
- streamed temporary RAM scales with upload batch, not total model size;
- pinned GPU buffers survive renderer disable/re-enable;
- pinned resources dispose at shutdown.

## Known Limitations and Review Risks

1. Streamed loading is desktop-filesystem-only. Android is not implemented.
2. The desktop build processor scans all streamed gsplat assets in the project, which
   can include unused assets.
3. Stream blobs live under `Library` and are regenerated from the source PLY. They
   should not be treated as source-controlled canonical model data.
4. `Pin Until Shutdown` intentionally keeps GPU memory allocated for the session.
5. Embedded CPU-array release occurs only in non-Editor players and only for a pinned
   resource after successful upload.
6. The upload batch is bounded, but its actual bytes depend on compression, SH degree,
   and PVG fields. `UploadBatchSize` is a splat count, not a byte count.
7. Manual bilinear sampling depends on the native ERP texture remaining point-filtered.
8. BiRP Single Pass Instanced remains unsupported.
9. Exact depth interaction between Hybrid output and arbitrary Unity scene geometry
   remains outside this optimization.
10. This work reduces CPU RAM and padded ERP memory but does not reduce splat count,
    SH degree, native ERP resolution, sorting quality, or final Gaussian GPU-buffer size.

## Guidance for a Future AI Model

Before changing this implementation:

1. Read this document.
2. Read `Documentation~/ODGS PVG Change Map.md`.
3. Read `Documentation~/Implementation Details.md`.
4. Inspect the current git diff because these optimization changes may still be
   uncommitted.
5. Do not replace the existing ODGS, OmniGS, PVG, perspective, or hybrid math while
   debugging memory behavior.
6. Treat `GsplatAsset`, `GsplatResourceManager`, and `GsplatResource` as one shared
   upload/lifetime subsystem.
7. Treat `GsplatOmniViewer` and `ERPToPerspective.shader` as one virtual-padding and
   composite subsystem.
8. Preserve the distinction between physical ERP dimensions and logical display
   dimensions.
9. Reimport streamed PLY assets after changing the stream format or importer.
10. Bump the stream format version if binary layout or section semantics change.
11. Bump the scripted importer version when imported asset serialization changes.
12. Validate both sync and async paths after any upload modification.
13. Validate two renderers sharing one asset after any resource ownership modification.
14. Run a real Windows build before claiming shader, StreamingAssets, or PCVR success.

## Quick Handoff Summary

The implementation reduces RAM without intentionally lowering rendering quality by:

- eliminating the physical padded ERP texture;
- applying required vertical black padding in the composite shader;
- keeping large model data outside the serialized runtime asset when desktop streaming
  is selected;
- uploading model data in bounded batches;
- allowing fixed-session GPU resources to remain pinned while CPU arrays are released;
- sharing one authoritative upload across renderers;
- removing recurring managed allocations and command-buffer churn;
- rejecting temporally inactive PVG splats before expensive shader work.

The code compiles against Unity 2022.3.62f3. Real Unity shader, image, build, memory,
and Quest PCVR validation remains the next required stage.
