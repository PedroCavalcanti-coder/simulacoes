# Graph Report - .  (2026-07-21)

## Corpus Check
- Corpus is ~37,645 words - fits in a single context window. You may not need a graph.

## Summary
- 799 nodes · 1447 edges · 42 communities (38 shown, 4 thin omitted)
- Extraction: 94% EXTRACTED · 6% INFERRED · 0% AMBIGUOUS · INFERRED: 81 edges (avg confidence: 0.8)
- Token cost: 0 input · 0 output

## Community Hubs (Navigation)
- Lab Architecture and Roadmap
- Multi-Liquid Identity and Surfaces
- Legacy FluidFree Runtime
- Original Fluid Demo
- Legacy Scene and UI Builders
- Fluid Lab Manager
- Playground Scene Builder
- Desktop Camera and Grab
- Multi-Flask Lab Controller
- Playground Control Panel
- Analytical Glassware Profiles
- Fluid Solver GPU Pipeline
- Flask Test Runtime
- Voxel SDF Glassware
- Volume Render Feature
- Lab Module Layout
- GPU Sort and Hash
- SSF Pass Data and Settings
- Fluid Flask Geometry
- Particle Source Construction
- SSF Feature Lifecycle
- Faucet and Legacy Camera
- Fluid Render Bridge
- Fluid Body Buffers
- Multi-Liquid Spawning
- Flask UI Binding
- Substance Spawn Points
- Fluid Boundary Buffers
- Collider Upload Pipeline
- Legacy Lab Panel
- SSF Target Recording
- Legacy Faucet Components
- Solver Runtime and Compaction
- Core PBD Namespace
- Compute Buffer Utilities
- SSF Visibility Culling
- Smoothing Kernels
- SSF Texture Descriptors
- Render Feature Settings
- Grid Debug Rendering

## God Nodes (most connected - your core abstractions)
1. `FlaskFluidTest` - 63 edges
2. `FluidFree` - 43 edges
3. `FluidSolver` - 39 edges
4. `LabController` - 34 edges
5. `FluidMultiSurfacePublisher` - 33 edges
6. `FluidPlaygroundController` - 33 edges
7. `DesktopVRSimulator` - 32 edges
8. `FluidBodyDemo` - 30 edges
9. `FluidLabManager` - 28 edges
10. `PBDFluidSSF` - 27 edges

## Surprising Connections (you probably didn't know these)
- `ParticlesFromList` --inherits--> `ParticleSource`  [EXTRACTED]
  Assets/PBDFluid/Scripts/ParticlesFromList.cs → Assets/PBDFluid/Scripts/ParticleSource.cs
- `FluidFree` --references--> `SIMULATION_SIZE`  [EXTRACTED]
  Assets/PBDFluid/FluidFree.cs → Assets/PBDFluid/FluidBodyDemo.cs
- `FluidBodyDemo` --references--> `FluidBody`  [EXTRACTED]
  Assets/PBDFluid/FluidBodyDemo.cs → Assets/PBDFluid/Scripts/FluidBody.cs
- `FluidBodyDemo` --references--> `FluidBoundary`  [EXTRACTED]
  Assets/PBDFluid/FluidBodyDemo.cs → Assets/PBDFluid/Scripts/FluidBoundary.cs
- `FluidBodyDemo` --references--> `FluidSolver`  [EXTRACTED]
  Assets/PBDFluid/FluidBodyDemo.cs → Assets/PBDFluid/Scripts/FluidSolver.cs

## Import Cycles
- None detected.

## Hyperedges (group relationships)
- **Shared Solver Dataflow** — assets_pbdfluid_lab_flask_lab_plan_liquiddef_substance, assets_pbdfluid_lab_flask_lab_plan_labcontroller, assets_pbdfluid_lab_flask_lab_plan_fluidsolver, assets_pbdfluid_lab_flask_lab_plan_fluidbody, assets_pbdfluid_lab_flask_lab_plan_per_particle_substance_index, assets_pbdfluid_lab_flask_lab_plan_render_only_fluid_feature [EXTRACTED 1.00]
- **Validated PBD Stability Fixes** — assets_pbdfluid_lab_flask_lab_plan_grid_hash_full_table_clearing, assets_pbdfluid_lab_flask_lab_plan_normalized_xsph_viscosity, assets_pbdfluid_lab_flask_lab_plan_spiky_gradient_epsilon_guard, assets_pbdfluid_lab_flask_lab_plan_cavity_clamp_glassware_resolution, assets_pbdfluid_lab_flask_lab_plan_mass_scale_calibration, assets_pbdfluid_lab_flask_lab_plan_collider_transform_follow_smoothing [EXTRACTED 1.00]
- **Implementation Phases** — assets_pbdfluid_lab_flask_lab_plan_phase_a_one_flask_core, assets_pbdfluid_lab_flask_lab_plan_phase_b_two_flask_transfer, assets_pbdfluid_lab_flask_lab_plan_phase_c_multi_liquid_core, assets_pbdfluid_lab_flask_lab_plan_phase_d_visual_and_culling, assets_pbdfluid_lab_flask_lab_plan_phase_e_optimization_and_extras [EXTRACTED 1.00]

## Communities (42 total, 4 thin omitted)

### Community 0 - "Lab Architecture and Roadmap"
Cohesion: 0.06
Nodes (59): Adaptive Rest Damping, Bitonic MIN_ELEMENTS Floor, Boundary Particles, Cavity-Clamp Glassware Resolution, Collider Transform Follow Smoothing, ColliderFluidTest Scene, CPU / GPU / Render Responsibility Split, Dead-Particle Buffer Compaction (+51 more)

### Community 1 - "Multi-Liquid Identity and Surfaces"
Cohesion: 0.06
Nodes (30): Entry, bool, Color, int, Material, string, Vector3, FluidLiquidDefinition (+22 more)

### Community 2 - "Legacy FluidFree Runtime"
Cohesion: 0.05
Nodes (26): bool, Bounds, Camera, Collider, Color, ComputeBuffer, float, IList (+18 more)

### Community 3 - "Original Fluid Demo"
Cohesion: 0.09
Nodes (25): bool, Bounds, Camera, Color, float, IList, int, Material (+17 more)

### Community 4 - "Legacy Scene and UI Builders"
Cohesion: 0.09
Nodes (21): Color, GameObject, Material, MenuItem, Mesh, string, ColliderDemoBuilder, Button (+13 more)

### Community 5 - "Fluid Lab Manager"
Cohesion: 0.08
Nodes (20): bool, Bounds, float, GlasswareGPU, int, List, Material, Mesh (+12 more)

### Community 6 - "Playground Scene Builder"
Cohesion: 0.15
Nodes (12): Color, FlaskFluidTest, FluidLiquidDefinition, GameObject, Material, MenuItem, Mesh, string (+4 more)

### Community 7 - "Desktop Camera and Grab"
Cohesion: 0.11
Nodes (10): bool, Camera, float, LayerMask, Quaternion, Vector2, Vector3, DesktopVRSimulator (+2 more)

### Community 8 - "Multi-Flask Lab Controller"
Cohesion: 0.10
Nodes (16): bool, Color, FlaskSDFGPU, float, FluidBody, FluidBoundary, FluidSolver, int (+8 more)

### Community 9 - "Playground Control Panel"
Cohesion: 0.11
Nodes (9): bool, float, int, Material, Rect, Vector2, FluidPlaygroundController, QualityPreset (+1 more)

### Community 10 - "Analytical Glassware Profiles"
Cohesion: 0.13
Nodes (12): bool, float, GlasswareGPU, int, List, Quaternion, Vector2, Vector3 (+4 more)

### Community 11 - "Fluid Solver GPU Pipeline"
Cohesion: 0.16
Nodes (13): bool, ComputeBuffer, ComputeShader, float, int, Texture3D, Vector3, Vector4 (+5 more)

### Community 12 - "Flask Test Runtime"
Cohesion: 0.09
Nodes (22): bool, Bounds, ComputeBuffer, Dictionary, Entry, float, FluidBody, FluidBoundary (+14 more)

### Community 13 - "Voxel SDF Glassware"
Cohesion: 0.17
Nodes (10): bool, Color, FlaskSDFGPU, float, int, List, Substance, Vector3 (+2 more)

### Community 14 - "Volume Render Feature"
Cohesion: 0.13
Nodes (14): ContextContainer, Material, RenderGraph, RenderingData, RenderTexture, ScriptableRenderer, Vector3, FluidPass (+6 more)

### Community 15 - "Lab Module Layout"
Cohesion: 0.14
Nodes (5): float, int, LabConstants, PBDFluid.Render, PBDFluid.Lab

### Community 16 - "GPU Sort and Hash"
Cohesion: 0.17
Nodes (9): ComputeBuffer, ComputeShader, int, BitonicSort, Bounds, ComputeBuffer, ComputeShader, int (+1 more)

### Community 17 - "SSF Pass Data and Settings"
Cohesion: 0.17
Nodes (16): bool, ComputeBuffer, float, int, Material, Matrix4x4, Mesh, RenderPassEvent (+8 more)

### Community 18 - "Fluid Flask Geometry"
Cohesion: 0.22
Nodes (8): bool, float, int, List, Vector3, FluidFlask, Shape, Shape

### Community 19 - "Particle Source Construction"
Cohesion: 0.15
Nodes (8): Matrix4x4, Matrix4x4, Bounds, List, ParticlesFromBounds, IList, Vector3, ParticleSource

### Community 20 - "SSF Feature Lifecycle"
Cohesion: 0.22
Nodes (7): Dictionary, DebugView, PBDFluidSSF, SurfaceTargets, RTHandle, RuntimeQuality, SSFPass

### Community 21 - "Faucet and Legacy Camera"
Cohesion: 0.17
Nodes (8): float, int, Transform, Vector3, PlaygroundFaucet, float, FreeCam, MonoBehaviour

### Community 22 - "Fluid Render Bridge"
Cohesion: 0.17
Nodes (12): bool, Bounds, ComputeBuffer, float, int, Mesh, RenderTexture, Vector3 (+4 more)

### Community 23 - "Fluid Body Buffers"
Cohesion: 0.22
Nodes (6): Bounds, Camera, ComputeBuffer, Material, Mesh, FluidBody

### Community 24 - "Multi-Liquid Spawning"
Cohesion: 0.25
Nodes (4): Color, List, Vector3, Vector4

### Community 25 - "Flask UI Binding"
Cohesion: 0.24
Nodes (7): Button, Color, Slider, Text, Toggle, FlaskFluidUI, UnityAction

### Community 26 - "Substance Spawn Points"
Cohesion: 0.20
Nodes (7): Color, float, int, List, Substance, Vector3, FluidSpawnPoint

### Community 27 - "Fluid Boundary Buffers"
Cohesion: 0.22
Nodes (8): Bounds, Camera, ComputeBuffer, int, Material, Mesh, FluidBoundary, IDisposable

### Community 28 - "Collider Upload Pipeline"
Cohesion: 0.28
Nodes (4): ColliderGPU, Collider, ColliderGPU, IEnumerable

### Community 29 - "Legacy Lab Panel"
Cohesion: 0.22
Nodes (5): bool, float, KeyCode, Rect, LabPanel

### Community 30 - "SSF Target Recording"
Cohesion: 0.28
Nodes (6): ContextContainer, RenderGraph, Vector4, SurfaceTargets, UniversalCameraData, UniversalResourceData

### Community 31 - "Legacy Faucet Components"
Cohesion: 0.25
Nodes (6): Color, float, Substance, Transform, Vector3, Faucet

### Community 34 - "Compute Buffer Utilities"
Cohesion: 0.29
Nodes (3): ComputeBuffer, IList, CBUtility

### Community 35 - "SSF Visibility Culling"
Cohesion: 0.32
Nodes (4): Camera, Entry, RenderingData, ScriptableRenderer

### Community 38 - "SSF Texture Descriptors"
Cohesion: 0.47
Nodes (3): List, SSFPass, TextureDesc

### Community 39 - "Render Feature Settings"
Cohesion: 0.40
Nodes (5): Color, float, RenderPassEvent, Shader, Settings

## Knowledge Gaps
- **8 isolated node(s):** `QualityPreset`, `Shape`, `DebugView`, `EventSystem`, `FlaskFluidUI` (+3 more)
  These have ≤1 connection - possible missing edges or undocumented components.
- **4 thin communities (<3 nodes) omitted from report** — run `graphify query` to explore isolated nodes.

## Suggested Questions
_Questions this graph is uniquely positioned to answer:_

- **Why does `FlaskFluidTest` connect `Flask Test Runtime` to `Solver Runtime and Compaction`, `Multi-Liquid Identity and Surfaces`, `Fluid Diagnostics Controls`, `Legacy Surface Proxy`, `Playground Control Panel`, `Analytical Glassware Profiles`, `Lab Module Layout`, `Faucet and Legacy Camera`, `Multi-Liquid Spawning`, `Flask UI Binding`, `Collider Upload Pipeline`?**
  _High betweenness centrality (0.159) - this node is a cross-community bridge._
- **Why does `FluidFree` connect `Legacy FluidFree Runtime` to `Multi-Liquid Identity and Surfaces`, `Original Fluid Demo`, `Fluid Lab Manager`, `Fluid Solver GPU Pipeline`, `Lab Module Layout`, `Faucet and Legacy Camera`, `Fluid Body Buffers`, `Fluid Boundary Buffers`?**
  _High betweenness centrality (0.152) - this node is a cross-community bridge._
- **Why does `PBDFluid.Lab` connect `Lab Module Layout` to `Multi-Liquid Identity and Surfaces`, `Legacy Scene and UI Builders`, `Faucet and Legacy Camera`, `Flask UI Binding`, `Substance Spawn Points`, `Legacy Lab Panel`, `Legacy Faucet Components`?**
  _High betweenness centrality (0.149) - this node is a cross-community bridge._
- **What connects `QualityPreset`, `Shape`, `DebugView` to the rest of the system?**
  _8 weakly-connected nodes found - possible documentation gaps or missing edges._
- **Should `Lab Architecture and Roadmap` be split into smaller, more focused modules?**
  _Cohesion score 0.05727644652250146 - nodes in this community are weakly interconnected._
- **Should `Multi-Liquid Identity and Surfaces` be split into smaller, more focused modules?**
  _Cohesion score 0.062310949788263764 - nodes in this community are weakly interconnected._
- **Should `Legacy FluidFree Runtime` be split into smaller, more focused modules?**
  _Cohesion score 0.05224963715529753 - nodes in this community are weakly interconnected._