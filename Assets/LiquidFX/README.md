# LiquidFX

Liquid leaving one piece of glassware and arriving in another, for URP on mobile.

The flask shading itself stays with LiquidVolumePro. This package covers everything **between**
containers: the falling stream, the impact, the standing liquid in a sink, and the spill on the
bench when you miss — plus the ledger that keeps the volume honest while it travels.

---

## Build it

In Unity: **Tools > LiquidFX > Build Everything**

That generates textures, meshes, materials and prefabs under `Generated/`, then assembles two
scenes under `Scenes/`:

| Scene | What it is for |
|---|---|
| `SinkFaucet.unity` | The faucet and sink, rebuilt. Valve, drain, object impacts, spills. |
| `FlaskPour.unity` | A 250 mL erlenmeyer tipping into a 250 mL beaker. |

Nothing here depends on a binary asset: rerun the menu item after any tuning change and the whole
package regenerates.

### Controls in the test scenes

| Input | Effect |
|---|---|
| `A` / `D` (or arrows), or drag up/down | Open and close the valve, or tip the flask |
| `S` | Toggle the sink drain |
| `Q` | Cycle the quality tier (Low / Medium / High) |
| `R` | Refill |

---

## What was wrong with the old sink, and what changed

The previous prototype (`Assets/LiquidVisualPrototype`) had the right ideas but four defects made
the water read as fake. All four are fixed here.

**1. The water was opaque.** `WaterSurfaceMobile.shader` ended with `return half4(color, 1.0h)`.
Everything below the surface — the drain, the basin floor, the submerged half of a sphere — was
hidden behind a solid quad that only *simulated* refraction by offsetting a screen sample. Now the
surface samples the scene depth, computes how much liquid the light travelled through, applies
Beer-Lambert absorption, and outputs a real alpha. Shallow water is nearly clear, deep water tints.

**2. Refraction pulled in foreground pixels.** Offsetting the screen UV samples whatever is at that
pixel, including objects standing *in front of* the water, which smear across the surface. The new
shader checks the depth at the refracted sample and falls back to the unrefracted one when the
sample is closer than the surface.

**3. Contact edges were fake.** Basin edge highlighting was computed from the quad's own UV, so it
drew a rectangle regardless of where the walls actually were and ignored submerged objects
entirely. Contact foam now comes from real scene depth, so it hugs the basin walls and every
object breaking the surface.

**4. Ripple units were inconsistent.** `AddImpulseWorld(worldPosition, strength, radius)` took the
radius in world metres and handed it straight to the shader, which treated it as a UV distance —
`RippleWaterSurface.WorldRadiusToUv` existed but the faucet path never called it. A 4 cm ring was
4 cm only at one particular quad scale. Everything is now expressed in metres and seconds from C#
through to the shader, so the same numbers work on a sink and on a beaker.

Two more, outside the shader:

**5. The stream did not retarget.** The old faucet was a cone of particles whose speed × lifetime
happened to equal the distance to the water. As soon as the level rose, the particles overshot
through the surface. The ribbon solves its landing point against the receiver's current surface
plane every frame.

**6. Filling was unbounded and had no drain.** `fillSurfaceWhileFlowing` incremented a normalised
fill value forever. The basin now holds a volume in millilitres, and the level is that volume
divided by the basin area — so it is bounded, drainable, and predictable.

---

## Architecture

```
LiquidPourController          the only thing that moves volume
  ├── source: FlaskVolume (tilt) or a valve
  ├── LiquidFlightQueue       millilitres in the air, with the real fall time
  ├── receiver: ILiquidContainer, resolved from LiquidContainerRegistry
  ├── LiquidStreamRibbon      the visual, one procedural mesh
  ├── LiquidImpactFX          splash, droplets, ring, bubbles
  └── LiquidSpillManager      pooled puddles for whatever misses
```

### Volume is a ledger, not a physics result

Every frame the controller removes millilitres from the source, queues them with the fall time
solved from the ballistic curve, and credits them to the receiver when they land. Nothing depends
on a collider, a Rigidbody, or `OnParticleCollision`.

This matters for two reasons. On mobile, particle-collision callbacks are expensive and
frame-rate dependent, so the same pour fills a different amount on a slow phone than on a fast
one. And visually, it means the receiving flask starts filling exactly when the stream reaches it,
not one physics tick later.

`LiquidVolumePro`'s own pouring demo takes the opposite approach: a cube with a Rigidbody parked
under the surface counting particle hits. That is fine for a desktop demo, not for this.

### Millilitres, not normalised levels

`LiquidVolume.level` is 0..1 against the flask mesh. Pouring 50 mL out of a 250 mL erlenmeyer into
a 50 mL beaker with normalised levels would be wrong by a factor of five. `FlaskVolume` calibrates
each piece of glassware with a real `capacityML` plus the two normalised levels that correspond to
empty and full, and every transfer goes through millilitres.

### The stream is a mesh, not particles

One camera-facing ribbon along the real ballistic curve. 10 to 24 segments depending on the
quality tier, one draw call, zero allocation per frame.

Width follows mass continuity — radius proportional to `1/sqrt(speed)` — so the jet visibly thins
as it accelerates downward. That is the single cue that separates a falling jet from a stretched
texture. The shader then rebuilds a round cross-section from the across-ribbon coordinate, giving
a specular line down the middle and refraction that bends hardest at the silhouette.

---

## Starting and stopping

Both are animated by moving a **head** and a **tail** along the curve, never by fading alpha.

**Opening:** the head descends from the lip at the local flow speed and takes the real fall time to
reach the surface. Width ramps up over ~120 ms.

**Closing:** the tail chases the head down the curve at ~1.45× the local speed, so the stream
physically breaks, the last section falls free, and the lip drips for about a second afterwards.
A global alpha fade reads as a bug; a retracting tail reads as a closing tap.

Then, in order:

1. `MeshRenderer.enabled = false`
2. the ribbon stops rebuilding its mesh
3. after `DormantTimeout` (8 s) the vertex buffer is released
4. puddles dry from the rim inward, then disable their own renderer *and component*

Nothing is `Destroy`ed at runtime and no material is instantiated: everything goes through
`MaterialPropertyBlock` and fixed pools. A session of clumsy pouring never grows the heap.

---

## Mobile budget

| | Low (default on device) | Medium | High |
|---|---|---|---|
| Stream segments | 10 | 16 | 24 |
| Concurrent streams | 2 | 4 | 4 |
| Particle budget | 24 | 48 | 96 |
| Refraction (framebuffer copy) | off | on | on |

Set with `LiquidFXRuntime.Quality`. It defaults to `Low` on `Application.isMobilePlatform`.

The costly item is refraction: sampling the opaque texture forces URP to copy the framebuffer
every frame. On the low tier the stream and the surface fall back to plain scene colour, which on a
phone screen is very hard to tell apart.

Depth and opaque textures are required. Both project URP assets already enable them, and the
generated cameras force them on per camera as well.

---

## Files

```
Runtime/Core
  LiquidFXRuntime.cs        quality tiers, budgets, ballistics helpers
  LiquidFXDemoRig.cs        test input only, not part of the effects
Runtime/Containers
  ILiquidContainer.cs       anything that holds millilitres
  LiquidContainerRegistry.cs  who is under the stream
  LiquidFlightQueue.cs      liquid in the air, allocation free
  FlaskVolume.cs            LiquidVolumePro flask calibrated in millilitres
  LiquidSurface.cs          sink basin: volume, level, drain, ripples
Runtime/Stream
  LiquidStreamRibbon.cs     the procedural stream mesh
  LiquidPourController.cs   the ledger and everything it drives
Runtime/Impact
  LiquidImpactFX.cs         splash, droplets, ring, bubbles, budgeted
  LiquidSurfaceImpacts.cs   objects entering the liquid
Runtime/Spill
  LiquidSpillPuddle.cs      one puddle: grows by volume, dries, retires
  LiquidSpillManager.cs     fixed pool, merges nearby spills
Shaders
  LiquidRipples.hlsl        analytical ripples in metres, shared
  LiquidSurface.shader      standing liquid
  LiquidStream.shader       the falling jet
  LiquidPuddle.shader       spills
  LiquidParticle.shader     every particle
Editor
  LiquidFXBuilder.cs        builds assets, prefabs and both scenes
  LiquidFXTextureFactory.cs procedural textures
  LiquidFXPaths.cs
```

---

## Known limits

- **Layer mixing is a colour blend.** Pouring liquid A into a flask holding liquid B lerps the
  LiquidVolumePro colours weighted by volume. LiquidVolumePro supports real stacked layers; wiring
  those up is a separate decision because it changes the `ILiquidContainer` API.
- **The basin is a rectangle.** `LiquidSurface` assumes a flat rectangular footprint, so the level
  is volume ÷ area. A tapered or round basin needs a volume-to-height curve.
- **Filling a sink is genuinely slow.** 300 mL/s into a 50 × 40 cm basin raises the level about
  1.5 mm per second, because that is what the physics says. Press `R` in the test scene rather than
  waiting.
- **The old prototype is untouched.** `Assets/LiquidVisualPrototype` still builds and runs its own
  scenes; nothing here modifies it.
