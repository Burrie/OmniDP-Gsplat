## Implementation Details

### Resources Setup

**Material & Mesh**: The `GsplatSettings` singleton owns global rendering resources. It:

- Maintains a `GsplatMaterial` array indexed by `CompressionMode` (e.g. `Uncompressed`, `Spark`), where each `GsplatMaterial` contains a `DefaultMaterial`, a `CalcDepthShader`, and an `InitOrderShader`. The `GsplatMaterial` generates lazily `Materials` for each SH band (0-3) and Render Order combination (defined by `GsplatSettings.MaxRenderOrder`).
- Procedurally generates a `Mesh` that consists of multiple quads. The number of quads is defined by `SplatInstanceSize`. Each vertex of these quads has its z-coordinate encoded with an intra-instance index, which is used in the vertex shader to fetch the splat order.

**Gsplat Data**: This package supports importing PLY file in two modes via `GsplatAsset` implementations:

- **Uncompressed**: `GsplatAssetUncompressed` stores per\-splat arrays (`Positions`, `Colors`, `Scales`, `Rotations`, optional `SHs`) and uploads them to dedicated GPU `GraphicsBuffer`s.
- **Spark (Packed)**: `GsplatAssetSpark` packs each splat into a fixed 16\-byte layout (`uint4` per splat in `PackedSplats`) plus optional packed SH arrays (2 uints per splat for SH1, 4 uints for SH2, and 4 uints for SH3). Packing includes float16 position, log\-encoded scale, RGBA8, and octahedral axis\+angle quaternion encoding, which is inspired by [spark.js](https://github.com/sparkjsdev/spark/blob/main/src/shaders/splatDefines.glsl#L237C6-L237C25).

**GPU Resources & Lifetime**:

- `GsplatRendererImpl` creates a per\-renderer `OrderBuffer` (which will later store the sorted indices of the splats), small buffers for cutouts data (`CutoutsBuffer`, `OrderSizeBuffer`, `BoundsBuffer`) and an `ISorterResource` (sorting support buffers and key buffer).
- Per\-asset GPU data buffers are allocated and cached by `GsplatResourceManager` (reference counted), so multiple renderers can share the same uploaded asset.
- Upload can be synchronous (`UploadData`) or asynchronous batched (`UploadDataAsync`), controlled by `GsplatRenderer.AsyncUpload`. The renderer can optionally draw before upload completes (`RenderBeforeUploadComplete`).

### Rendering Pipeline

The following steps are performed each frame for every active camera, except for the Sorting pass, which is executed only every Nth frame, and the Compute pass, which is executed only every Nth sort, as configured in the `GsplatRenderer`. The sorting and compute are also triggered when a camera moves past a certain threshold and can be manually triggered.

### Compute Prepass

This pre-pass performs precalculations when needed. Currently, it is only used to generate the index buffer when cutouts are enabled.

*   **InitOrder** (Optional): `GsplatRendererImpl.DispatchInitOrder`, if cutouts are used and have changed since last call, generate a sequential indices buffer (`OrderBuffer`), similar to `InitPayload`. While doing so, the prepass query the splats position to ignore any splats culled by a cutout. The new Bounds of the gaussian is calculated at the same time. Then, the remaining number of splats is extracted from the `OrderBuffer`.

#### Sorting Pass

This pass sorts the splats by their depth to the camera. The sorting is performed entirely on the GPU using `Gsplat.compute`. This compute shader leverages a highly optimized radix sort implementation from `DeviceRadixSort.hlsl`.

*   **Integration**: The sorting is initiated by custom render pipeline hooks: `GsplatURPFeature` for URP, `GsplatHDRPPass` for HDRP, or `GsplatSorter.OnPreCullCamera` for BiRP. These hooks call `GsplatSorter.DispatchSort`.
*   **Sorting Steps**:
    1.  **InitPayload** (Optional): If the payload buffer (`b_sortPayload`/`OrderBuffer`) has not been initialized, fill it with sequential indices (0, 1, 2, ... `SplatCount`-1).
    2.  **CalcDepth**: `IGsplat.ComputeDepth` runs an asset\-specific compute kernel (`CalcDepth` or `CalcDepthSpark`) to calculates view-space depth of each splat, and stores them into `SorterResource.InputKeys` which will be used as the sorting key.
    3.  **DeviceRadixSort**: The `Upsweep`, `Scan`, and `Downsweep` kernels execute a device-wide radix sort. It sorts the depth values in the `b_sort` buffer. Crucially, it applies the same reordering operations to the `b_sortPayload` buffer.
*   **Result**: After the sort, the `b_sortPayload` buffer (which is the `OrderBuffer` from `GsplatRendererImpl`) contains the original splat indices, now sorted from back-to-front based on their depth to the camera.

#### Render Pass

With the splats sorted, they can now be drawn using `Gsplat.shader`.
*   **Draw Call**: The `GsplatRendererImpl.Render` method issues a single draw call via `Graphics.RenderMeshPrimitives`. It uses GPU instancing to render multiple instances of the procedurally generated quad mesh, and a material from `GsplatAsset.Material` is selected based on the desired `SHBands`. All necessary buffers and parameters (`_MATRIX_M`, `_SplatCount`, etc.) are passed to the shader via a `MaterialPropertyBlock`.
*   **Vertex Shader**:
    1.  **Index Calculation**: It determines the final splat `order` to render by combining the `instanceID` with the intra-instance index stored in the vertex's z-component.
    2.  **Fetch Sorted ID**: It uses this `order` to look up the actual splat `id` from the `_OrderBuffer`. This `id` corresponds to the correct, depth-sorted splat.
    3.  **Fetch Splat Data**: Using this sorted `id`, it fetches (extracts) the splat's position, rotation, scale, color, and SH data from their respective buffers.
    4.  **Apply Scale factor**: The splat's UV coordinates are multiplied by the splat's `_ScaleFactor`, cropping the splat to the given scale.
    5.  **Covariance & Projection**: It calculates the 2D covariance matrix of the Gaussian in screen space. This determines the shape and size of the splat on the screen. It performs frustum and small-splat culling for efficiency.
    6.  **SH Calculation** (Optional): If SHs are used, `EvalSH` is called to calculate the view-dependent color component, which is then added to the base color.
    7.  **Vertex Output**: It calculates the final clip-space position of the quad's vertex by offsetting it from the splat's projected center based on the 2D covariance. The final color and UV coordinates (representing the position within the Gaussian ellipse) are passed to the fragment shader.
*   **Fragment Shader**:
    1.  It calculates the squared distance from the pixel to the center of the Gaussian ellipse using the interpolated UVs.
    2.  If the pixel is outside the ellipse (`A > 1.0`), it is discarded.
    3.  The alpha is calculated using an exponential falloff based on the distance, modulated by the splat's opacity. Pixels with very low alpha are discarded.
    4.  An additional falloff, based on the scaling factor, is added to the alpha to keep the gaussian splats smooth. This prevents the harsh edges of cropped splats.
    5.  The final color is the vertex color multiplied by the calculated alpha. An optional `Gamma To Linear` conversion can be applied before output.

### Hybrid Omni-To-Perspective Render Mode

The default path above remains the perspective renderer. `GsplatRenderer.RenderMode` can also be set to `HybridOmniPerspective` for ODGS/OmniGS-style assets trained with an equirectangular rasterizer.

In hybrid mode, `GsplatOmniViewer` on the user camera allocates a 2:1 ERP render texture and renders only the hybrid-mode `GsplatRenderer` instances into that offscreen target. The splat shader switches to spherical projection: the center is mapped from the viewer position to longitude/latitude, then to ERP pixel space. The viewer's `Rasterizer` field selects the covariance projection math used for this ERP splat pass:

- `ODGS` keeps the previous hybrid behavior and uses the ODGS `computeOmniCov2D` spherical-frame factorization.
- `OmniGS` uses the direct ERP projection Jacobian from OmniGS. The center projection, radial sorting, seam wrapping, and ERP-to-perspective composite remain the same.

Three horizontal wrap copies are issued to keep splats near the left/right ERP seam continuous.

Sorting also changes only for hybrid mode. `CalcDepth` and `CalcDepthSpark` use radial distance from the ERP viewer position as the key, while perspective renderers continue sorting by view-space `z`.

After the ERP pass, `ERPToPerspective` composites the ERP texture back into the active perspective camera by converting each screen pixel ray into longitude/latitude and sampling the ERP texture. BiRP runs this through `GsplatOmniViewer.OnRenderImage`; URP runs it through an extra `GsplatURPFeature` fullscreen pass before post-processing. Rotation-only camera changes therefore reuse the existing ERP texture and only re-run the fullscreen composite; camera translation invalidates the ERP texture and triggers a new omnidirectional splat pass.

Vertical black padding is virtual. The viewer keeps only the native `ARGBHalf` ERP target and supplies the logical display height and content-row offset to `ERPToPerspective`. Its manual bilinear sampler wraps horizontally, samples the native texture inside the content rows, and returns opaque black for taps in the top and bottom padding. A `1920x512` ERP with 224 rows on each side therefore behaves like a `1920x960` padded texture without allocating or copying that second target.

## Player Memory

The PLY importer supports embedded and streamed runtime storage. Streamed assets are serialized into GPU-ready sections under `Library/GsplatStreamData`; desktop build preprocessing adds those blobs to `StreamingAssets/Gsplat`. Runtime upload reads one configured batch at a time and validates each section while uploading, so temporary CPU memory is bounded by `GsplatSettings.UploadBatchSize`.

GPU resources have a shared upload state (`NotStarted`, `Uploading`, `Complete`, or `Failed`) so multiple renderers cannot start duplicate uploads. With `Player GPU Residency = Pin Until Shutdown`, shared GPU buffers remain cached when the last renderer is disabled. In non-Editor players, embedded CPU arrays are released only after all required buffers finish uploading successfully.

The BiRP viewer reuses its ERP and overlay command buffers. Runtime registries replace recurring scene searches, camera snapshots are shared once per frame, and cutout/PVG target buffers are reused to avoid steady-frame managed allocations.

The v1 hybrid compositor is an image layer over the camera color buffer. Exact depth interaction with arbitrary Unity scene geometry is intentionally deferred.

