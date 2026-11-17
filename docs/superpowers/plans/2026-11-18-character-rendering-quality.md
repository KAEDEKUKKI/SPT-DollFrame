# Character Rendering Quality & Lighting Enhancement Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Eliminate the plastic look and messy lighting on DollFrame characters by calibrating PBR/stylized material parameters, fixing double-sided mesh normals, adding companion/procedural micro-normal maps, enforcing 4-bone skinning, and smoothing SDF face shading with mipmaps.

**Architecture:** All changes live in the C# runtime (`ShaderManager.cs`, `PmxRuntime.cs`, `CharacterRig.cs`, `SdfFaceShading.cs`) without modifying the prebuilt `dollframe_shaders.bundle` or `Plugin.cs` config keys. Generated normal maps are tracked in `PmxAssets.Textures` for LRU eviction cleanup, and `SdfFaceDriver.Update` remains zero-allocation per frame.

**Tech Stack:** C# (.NET Standard 2.1), UnityEngine (`Texture2D`, `Material`, `Mesh`, `SkinnedMeshRenderer`, `RenderTexture`), BepInEx.

**Spec:** `docs/superpowers/specs/2026-11-18-character-rendering-quality-design.md`

## Global Constraints

- Do not rebuild or rename `dollframe_shaders.bundle`; always guard shader property writes with `Material.HasProperty`.
- Maintain all `AGENTS.md` invariants: zero game-bone writes, `localRotation`-only per-frame drive, 18 log families, 31 `Plugin.cs` config keys unchanged.
- Face, eye, lash, and brow materials must not receive noisy procedural normal maps.
- Every created `Texture2D` must be tracked in `PmxAssets.Textures` (or `SdfFaceDriver`) and destroyed on eviction/termination.
- `dotnet build -c Release -p:CopyOutputToBepInEx=false` must succeed with 0 warnings and 0 errors.

## Review Focus

- `BuildUnityMesh` double-sided vertex splitting must switch `mesh.indexFormat` to `IndexFormat.UInt32` if duplicated vertices push total vertex count past 65,000.
- `GenerateMicroNormalMap` must handle non-readable GPU textures by blitting through a temporary `RenderTexture` and always releasing it in `finally`.
- Normal maps must be created with `linear: true` (sRGB off) so tangent-space Z/XY values are not gamma-distorted.
- `SdfFaceDriver.Update` must clamp `dt` (e.g. `Time.unscaledDeltaTime`) and guard zero-length vectors in `Slerp`.
- Materials using Unity `Standard` fallback vs. `dollframe_shaders.bundle` must both work cleanly when `_BumpMap` is bound (`EnableKeyword("_NORMALMAP")`).

---

### Task 1: Calibrate Rim/Sheen & Add Normal Map Support in `Character/ShaderManager.cs`

**Files:**
- Modify: `Character/ShaderManager.cs:102-167`

- [ ] **Step 1: Update `ConfigureMaterial` signature and normal map binding**
  Add optional `Texture2D normalMap = null, float bumpScale = 0.4f` parameters to `ConfigureMaterial`. When `normalMap != null` and `m.HasProperty("_BumpMap")`, set `m.SetTexture("_BumpMap", normalMap)`, set `_BumpScale` if `m.HasProperty("_BumpScale")`, and call `m.EnableKeyword("_NORMALMAP")`.
- [ ] **Step 2: Calibrate `ClassifyStylizedLook` per material category**
  Reduce milky white `_SheenIntensity` and cool rim wash on skin/face/cloth:
  - `eye`/`瞳`/`iris`: rim `(0.85, 0.92, 1.0)`, intensity `0.16f`, power `4.2f`, sheen `0.03f`
  - `face`/`skin`/`body`/`肌`/`顔`: rim `(1.0, 0.86, 0.78)`, intensity `0.26f`, power `4.0f`, sheen `0.03f`
  - `hair`/`髪`: rim `(0.85, 0.93, 1.0)`, intensity `0.72f`, power `3.3f`, sheen `0.14f`
  - `metal`...: rim `(0.85, 0.92, 1.0)`, intensity `0.85f`, power `3.0f`, sheen `0.04f`
  - `leather`...: rim `(0.82, 0.89, 1.0)`, intensity `0.68f`, power `3.2f`, sheen `0.16f`
  - default (cloth): rim `(0.80, 0.88, 1.0)`, intensity `0.55f`, power `3.4f`, sheen `0.08f`
  Set `_RimAmbientAdapt` to `0.90f`.
- [ ] **Step 3: Build to verify 0 warnings / 0 errors**
  Run: `dotnet build -c Release -p:CopyOutputToBepInEx=false`

---

### Task 2: Refine PBR Values, Texture Filtering, Double-Sided Normals & Micro-Normal Maps in `Character/PmxRuntime.cs`

**Files:**
- Modify: `Character/PmxRuntime.cs:774-1080`

- [ ] **Step 1: Calibrate `ClassifyMaterial` smoothness & metallic values**
  - `face`: `(3, 0f, 0.15f)`
  - `eye`/`lash`/`brow`: `(0, 0f, 0.65f)`
  - `skin`: `(1, 0f, 0.18f)`
  - `hair`: `(2, 0f, 0.36f)`
  - `metal`...: `(0, 0.82f, 0.72f)`
  - `leather`...: `(0, 0.05f, 0.42f)`
  - `cloth`...: `(0, 0f, 0.14f)`
  - default: `(0, 0f, 0.16f)`
- [ ] **Step 2: Improve `LoadTexture` and `Downscale`**
  - Set `filterMode = FilterMode.Trilinear` and `anisoLevel = 8` on loaded textures.
  - In `Downscale`, use iterative 2x `Graphics.Blit` steps when shrinking by > 2x to prevent aliasing.
- [ ] **Step 3: Add `TryLoadCompanionNormalMap` and `GenerateMicroNormalMap`**
  - Check `textureBytes` for `<stem>_n`, `<stem>_normal`, `<stem>_bump`, `<stem>_nrm` (`.png`/`.jpg`) first (`linear: true`).
  - If absent and `!isTransparent && !isCutout` and not face/eye/lash/brow, generate a tangent-space normal map (capped at `Mathf.Min(maxTexSize, 512)`) combining luminance Sobel gradients with subtle category micro-detail (cloth weave, leather grain, hair strand, brushed metal, or soft skin).
  - Track all created normal maps in `extraTextures` so they are added to `a.Textures` (and destroyed on catch/eviction).
  - Pass `normalMap` and category `bumpScale` into `ShaderManager.ConfigureMaterial`.
- [ ] **Step 4: Fix `BuildUnityMesh` double-sided vertex normals**
  - For `DoubleSided` submeshes, duplicate the vertices referenced by those triangles with negated normals (`-normal`), keeping position, UV, and bone weights identical, and remap the reversed-winding triangles (`t, t+2, t+1`) to the duplicated vertices.
  - Ensure `mesh.indexFormat` is set based on the final vertex count (`> 65000 ? IndexFormat.UInt32 : IndexFormat.UInt16`).
- [ ] **Step 5: Build to verify 0 warnings / 0 errors**
  Run: `dotnet build -c Release -p:CopyOutputToBepInEx=false`

---

### Task 3: Enforce 4-Bone Skinning in `Character/CharacterRig.cs`

**Files:**
- Modify: `Character/CharacterRig.cs:395-427`

- [ ] **Step 1: Set `SkinQuality.Bone4` on `Body_SMR` and `Head_SMR`**
  Add `smr.quality = SkinQuality.Bone4;` and `headSmr.quality = SkinQuality.Bone4;`.
- [ ] **Step 2: Build to verify 0 warnings / 0 errors**
  Run: `dotnet build -c Release -p:CopyOutputToBepInEx=false`

---

### Task 4: Enable Mipmaps, Smooth Light Direction & Ambient Adaptation in `Character/SdfFaceShading.cs`

**Files:**
- Modify: `Character/SdfFaceShading.cs:37-215`

- [ ] **Step 1: Enable mipmaps & trilinear/aniso filtering on `BuildCloneTexture`**
  Change `new Texture2D(a.CloneW, a.CloneH, TextureFormat.RGBA32, true, false)` with `filterMode = FilterMode.Trilinear`, `anisoLevel = 4`, and call `Apply(true, false)` in `BuildCloneTexture` and `Composite`.
- [ ] **Step 2: Smooth light direction & ambient-adaptive shadow depth in `SdfFaceDriver`**
  - Add `_smoothDir` field and slerp toward target `L` in `Update()`.
  - Compute `envLum` from `_probeCols` + sun intensity and pass `shadowDepth` to `Composite(psi, shadowDepth)` so indoor/dark scenes avoid double-darkening.
- [ ] **Step 3: Build to verify 0 warnings / 0 errors**
  Run: `dotnet build -c Release -p:CopyOutputToBepInEx=false`

---

### Task 5: Changelog & Final Verification

**Files:**
- Modify: `CHANGELOG.md`

- [ ] **Step 1: Update `CHANGELOG.md` with the rendering & lighting enhancements**
- [ ] **Step 2: Run full Release build**
  Run: `dotnet build -c Release -p:CopyOutputToBepInEx=false`
  Expected: Build succeeded, 0 warnings, 0 errors.
