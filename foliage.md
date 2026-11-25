# Physical Foliage MVP — Unity 6

Build a procedural foliage generator, renderer and physics simulator in Unity. The eventual goal is realistic, interactive vegetation that bends, twists, wraps around objects and can be cut anywhere.

Implement incrementally. Each step must be functional, visually inspectable and independently testable before proceeding.

The MVP ends when a single rooted plant bends around a movable primitive collider and recovers after release. Advanced contact, self-collision, cutting, additional species and rendering optimization are follow-on milestones.

## Step 0 — Debugging and interaction infrastructure

Create a dedicated interactive test scene and comprehensive debug interface from the beginning. Extend both alongside every subsequent implementation step.

Provide:

- Orbit camera, zoom and free camera movement.
- Pause, resume, single-frame stepping and adjustable simulation speed.
- Reset simulation and regenerate geometry.
- Editable parameters with immediate visual feedback.
- Mouse picking and selection of individual plants, nodes, rods and triangles.
- Mouse dragging of physical geometry once simulation is implemented.
- Adjustable debug visualization scale.
- Independent visualization toggles for every implemented subsystem.

### Mouse interaction

Implement a persistent mouse interaction system from the beginning:

- Left-click to select any visible botanical component.
- Click and drag to grab an arbitrary point on a stem or leaf.
- Scroll while dragging to adjust the grab target's depth.
- Release the mouse to release the physical attachment.
- Allow configurable grab stiffness, damping and maximum force.
- Allow dragging of test colliders to push, compress and manipulate vegetation.

Once physics is implemented, dragging must apply forces or temporary compliant attachment constraints through the actual solver. Never directly teleport simulated vertices.

Support grabbing arbitrary positions along rods and within triangles using interpolated attachment coordinates.

Forces must propagate through the plant naturally.

Visualize the selected point, mouse target, attachment constraint, applied force and displacement error.

Before physics is available, implement mouse picking and target visualization only.

### Debug visualization

| Category | Visualization |
|---|---|
| Geometry | Wireframes, vertices, edges, normals, triangle orientation |
| Topology | Connectivity, component IDs, attachments, disconnected components |
| Rods | Segment orientation, local frames, rest lengths, bending and twisting |
| Shells | Rest geometry, deformation, stretching and bending |
| Materials | Mass, stiffness, thickness and damping heatmaps |
| Dynamics | Velocity, acceleration, forces and displacement vectors |
| Constraints | Constraint connections, errors and residuals |
| Interaction | Grab position, target, applied force and attachment error |
| Collisions | Contact points, normals, penetration depth and collision thickness |
| OGC/DAT | Contact regions, displacement bounds and truncated motion |
| Solver | Iterations, convergence, graph colouring and numerical errors |
| Cutting | Intersection points, split edges, new vertices and detached components |
| Performance | CPU/GPU timings, buffer sizes, element counts and contact counts |

Use colour-coded heatmaps with legends and numerical ranges where appropriate.

Allow selected vertices and components to display their numerical properties in an inspector.

Detect and highlight NaN/Infinity values, invalid topology, degenerate triangles, excessive deformation and unresolved penetration. Report affected element IDs.

Debug functionality must inspect actual simulation data, not independently reconstructed approximations.

Do not implement debug views for algorithms that do not exist yet. Establish an extensible framework and add relevant visualizations alongside each step.

## Step 1 — Procedural plant geometry

Generate a single simple plant consisting of:

- A curved stem.
- Several leaf stalks.
- Individual triangulated leaves with curved silhouettes.

Use actual geometry, not alpha-masked cards.

Expose seed, stem thickness, height, leaf count, leaf shape, curvature and mesh resolution.

Design the data structure around connected nodes, rods and triangular surfaces suitable for subsequent physical simulation.

Initially use ordinary Unity mesh rendering.

**Validation:** Inspect topology, triangle quality, connectivity, normals and individual botanical components through the debug interface. Verify accurate mouse picking.

## Step 2 — Minimal VBD solver

Study and port the relevant parts of Newton's VBD solver to Unity compute shaders.

References:
- Newton: https://github.com/newton-physics/newton
- VBD: https://arxiv.org/html/2403.06321v4
- AVBD: https://graphics.cs.utah.edu/research/projects/avbd/

Do not port the entire Newton engine.

Begin with the smallest possible implementation:

1. Particle integration and gravity.
2. Pinned vertices.
3. Elastic energy evaluation.
4. Vertex block updates.
5. Parallel solver scheduling.
6. A small elastic triangular sheet.
7. Physical mouse grabbing and dragging.

Validate stretching, bending, damping, elastic recovery and numerical stability using mouse interaction.

Also run a repeatable sheet test. Use a 10 × 10 quad grid triangulated with fixed diagonals, a 0.20 m edge length for the whole sheet, one pinned edge, a 1/60 s timestep and a fixed iteration budget. Settle under gravity, move a point on the opposite edge 0.04 m over 30 steps, hold for 30, release, then measure after 240 steps. Record material settings, iteration count, maximum pin drift, peak edge strain, recovery error relative to the settled pose, and any non-finite values. Set the numeric acceptance limits with the project owner before using this fixture as a pass/fail gate. The project owner judges implementation sufficiency.

**Debug:** Positions, velocities, forces, pinned vertices, elastic energy, convergence, vertex update groups, grab constraints and per-iteration errors.

### Step 2 fixture measurement

In Play mode, select **VBD sheet** in the foliage lab. **Tools → Foliage → Run VBD sheet fixture** runs an independent GPU sheet and writes the measurements to the Console. **Check sheet mouse interaction** exercises picking, depth adjustment, a force-limited pull and release through the lab's pointer handlers.

The reference run used a GeForce GTX 1070 Ti, 121 particles, 200 triangles, 11 fixed nodes, 4 update groups and 12 sweeps per step. Surface density was 0.35 kg/m², edge stiffness 12 N/m, hinge stiffness 0.00006 N and material damping time 0.012 s. The grab used 3 N/m stiffness, 0.012 N·s/m damping and a 0.09 N force cap. After 360 settling steps, the target moved 40 mm out of plane over 30 steps, held for 30 and detached for 240.

| Measurement | Reference run |
|---|---:|
| Maximum pin drift | 0 mm |
| Peak absolute edge strain | 5.803% |
| Maximum grabbed-point displacement | 37.570 mm |
| Peak grab force | 0.01555 N |
| Peak stretch / bending energy | 293.7 / 3.21 µJ |
| Recovery error against settled pose, RMS / maximum | 0.188 / 0.680 mm |
| Final maximum vertex speed | 6.53 mm/s |
| Peak free-node residual at end of pull, first / last sweep | 0.00403 / 0.000841 N |
| Non-finite values detected | 0 |

Repeating the same fixture with zero material damping gave 0.509 mm RMS recovery error and 11.22 mm/s final maximum speed. These measurements are available for the owner's sufficiency review.

## Step 3 — Physical leaves

Connect generated leaf geometry to the VBD solver.

Implement thin-shell mechanics:

- Resistance to stretching.
- Flexible bending.
- Elastic recovery.
- Directional stiffness representing the central leaf vein.

Test individual leaves by grabbing, pulling, folding and releasing them with the mouse. They should behave like plant tissue, not rubber or loose fabric.

**Debug:** Rest shape overlay, strain heatmap, bending energy, vein stiffness and deformation magnitude.

In Play mode, choose **Leaf** to inspect one blade from the current procedural plant. Selecting a blade in **Plant** first opens that blade directly. The arrows switch blades. The stalk holds a four-node patch at the base. Each hinge retains its generated rest angle. The midrib multiplier controls longitudinal edge stiffness and the bending hinges that cross the central vein.

**Tools → Foliage → Check physical leaves** replays camera-space tip and side drags using the same pick, depth and release handlers as the mouse. On seed 17 at resolution 8, the default 6× midrib gave 6.32% peak edge strain in the tip test, compared with 8.28% at 1×. Ten seconds after release, the reinforced blade's RMS distance from its pre-grab pose was 8.5 mm, down from a peak of 138 mm. The side test on leaf 7 rotated a face by 28° and recovered from 96.3 mm peak RMS displacement to 3.2 mm. The pinned patch stayed fixed in both tests. These measurements describe the procedural material preset.

## Step 4 — Physical stems

Implement discrete elastic rods supporting bending and twisting.

Reference:
https://github.com/InteractiveComputerGraphics/PositionBasedDynamics

Connect rods and leaf surfaces within the same simulation. Forces must propagate between stems and leaves.

Dragging a leaf must affect its stalk, parent branch and connected plant structure.

**Debug:** Rod centre lines, rotational frames, bending, twisting, stretching and attachment errors.

In Play mode, **Stems** runs the current generated plant as one coupled VBD system. The first stem segment is clamped. Each subsequent rod carries a material frame, with separate elastic responses to bending, twisting and axial stretch. Branch junctions retain their generated rest orientations. Each leaf shares its base node with the stalk and three nearby blade nodes attach to the stalk frame through compliant constraints. Drag a blade margin to load the plant off-axis, or drag a stem between nodes. **Inspect** shows frame axes, strain and rotation maps, and collar errors in millimetres.

**Tools → Foliage → Check physical stems** repeats the bidirectional drag check. With seed 17 and gravity disabled to isolate the applied load, a 0.60 N peak pull on leaf 4 moved its stalk end 69.6 mm, the parent stem junction 26.9 mm, and another stalk end 56.5 mm. Peak stalk twist was 5.88° and peak axial strain on the loaded path was 0.69%. Maximum loaded collar error was 3.26 mm. Eight seconds after release the parent junction was 2.5 mm from rest. A separate interpolated stem grab also displaced a blade tip.

## Step 5 — Collisions

**Status: closed.** The Play mode checks passed for primitive pushes, OGC contact regions, sphere and cylinder wrapping, stem passage through a two-wall gap, plant self-contact and fast drags with DAT.

Implement incrementally:

1. Collisions against spheres, capsules and boxes.
2. OGC geometric contact.
3. Leaf and branch self-collisions.
4. DAT displacement safeguards.

References:
- OGC: https://ankachan.github.io/Projects/OGC/
- DAT: https://ankachan.github.io/Projects/DAT/

Consult Newton's implementation.

Test by dragging leaves around cylinders, pulling stems through narrow gaps and dragging colliders through foliage.

Leaves and stems must wrap around objects without excessive stretching, penetration or instability.

**Debug:** Broad-phase bounds, collision candidates, active contacts, contact normals, penetration depths, displacement bounds and rejected movement.

### Cylinder wrap and narrow-gap checks

Run **Tools → Foliage → Check cylinder wrap and narrow gap** in Play mode. The wrap check pulls a visible leaf tip across a horizontal capsule using the lab's mouse handlers. It measures the angle between simultaneous contacts on the cylindrical sidewall and excludes both rounded ends. After release it withdraws the support and measures free recovery.

On the seed-17 reference plant, simultaneous sidewall contacts spanned 29.9°. The tip traveled 95.2 mm, peak blade strain was 1.92%, and peak post-step penetration was 0.00 mm at reporting precision. After withdrawal the tip was 1.1 mm from its starting position.

The gap check pulls the upper stem of the seed-17 plant through two fixed box walls. The opening is 42.8 mm wide for a 19.0 mm stem with a 12 mm OGC contact offset on each side. The grabbed point must engage both walls' contact offsets while inside the slot, then clear the far face while still between the walls. The run recorded 41 bilateral-contact ticks, 113.7 mm of travel beyond the exit, 0.000 mm peak post-step penetration and 0.10% peak rod strain. After release and gate withdrawal, the grabbed point recovered to within 9.6 mm of its starting position with zero remaining contacts.

For manual testing, select **Stems → Test collider → Narrow gap**. The interactive gate has a 60 mm opening. Drag either wall to position the pair, then grab the stem and pull it through. **Inspect** shows contact points and bounds for both walls.

### Primitive contact milestone

In **Stems**, select **Sphere**, **Capsule** or **Box** under **Test collider**. Left-drag the visible collider or any exposed part of the plant. Scroll while holding to change depth. **Inspect** shows candidate elements, collider bounds, contact points, outward normals, thickness and penetration. The kinematic collider follows the target at the fixed physics timestep. The solver checks whole leaf triangles and thick rod segments against the selected shape on every sweep and distributes the contact load to their nodes.

**Tools → Foliage → Check primitive collisions** pushes each shape against a blade margin and the main stem with gravity disabled. Each push lasts 60 ticks, holds for 30, retreats for 60, then recovers for 480. Penetration is measured after each full solver step. Strain is the maximum absolute change in rod or shell edge length relative to rest.

| Collider and target | Peak penetration | Peak strain | Pushed-point travel | Remaining displacement after recovery |
|---|---:|---:|---:|---:|
| Sphere, leaf | 1.42 mm | 0.58% | 102.8 mm | 1.45 mm |
| Sphere, stem | 1.42 mm | 4.29% | 64.9 mm | 2.37 mm |
| Capsule, leaf | 1.06 mm | 0.71% | 186.2 mm | 2.09 mm |
| Capsule, stem | 1.18 mm | 4.26% | 70.3 mm | 2.89 mm |
| Box, leaf | 1.10 mm | 0.78% | 212.5 mm | 2.47 mm |
| Box, stem | 1.16 mm | 4.01% | 63.8 mm | 2.65 mm |

**Tools → Foliage → Check stationary sphere wrap** exercises collider dragging, then pulls a tip over a stationary sphere through the plant's pointer handlers. Simultaneous contact normals on the same leaf spanned 39.5°. Peak penetration during the pull was 0.29 mm and peak blade-edge strain was 3.71%. After 403.6 mm of tip travel, withdrawal of the support and free recovery left 6.1 mm of displacement. These measurements are for the primitive-contact MVP. OGC, plant self-contact and DAT are follow-on work.

### Offset geometric contact for primitive obstacles

**Stems → Test collider** defaults to **OGC** and also offers **Penalty**. OGC applies force within a 12 mm offset of the physical skin. Each leaf triangle supplies a witness on its face, an edge or a vertex. Shared edges and vertices have one owner. The deformed neighboring triangles determine whether the contact normal lies in that feature's region. Separate faces in a fold can both contribute. Round obstacles use their center or spine. Boxes use a separating axis and one clipped witness per plant element. Rods use their thick centerline segments.

The GPU refreshes contact geometry before velocity prediction and after each sweep. Node updates and collider translation use 45% of the available clearance as a motion bound. **Inspect** shows the minimum gap, smallest node bound and largest rejected node movement. When a requested collider spawn penetrates foliage, placement moves the collider along contact normals until clear. If opposing surfaces block that correction, the collider starts just above or below the plant. Reset checks clearance again against the restored plant. The stationary-sphere fixture checks its starting clearance before pulling.

On the seed-17 plant with tapered leaves, all six sphere, capsule and box pushes reached the full 170 mm target in both modes. Peak post-step penetration across the pushes was 1.45 mm for Penalty and 0.00 mm at reporting precision for OGC. Mean wall time per step, including readback, ranged from 5.10–7.41 ms for Penalty and 6.47–6.89 ms for OGC in one Editor run.

| Stationary sphere wrap from clear start | Penalty | OGC |
|---|---:|---:|
| Peak post-step penetration during pull | 0.29 mm | 0.00 mm |
| Peak blade-edge strain | 2.35% | 2.50% |
| Contact normal span | 38.3° | 38.6° |
| Tip travel | 385.8 mm | 386.7 mm |
| Remaining displacement after withdrawal | 2.8 mm | 3.2 mm |

**Tools → Foliage → Check OGC regions** checks a shared edge, a shared vertex and a concave fold. The primitive and wrap checks run both contact modes. The next collision milestone is leaf and branch self-contact.

### Plant self-collision

**Stems → Plant self-collision** enables two-way contact between blades and thick rod segments, including folds within a blade. Elements sharing a simulation node are excluded so adjoining triangles, rod joints and attached collars can flex. **Inspect** reports the active contact count and estimated penetration. Magenta crosses mark active plant contacts.

The CPU sorts expanded element bounds and finds nearby vertex/face and edge/edge pairs. The GPU distributes normal forces to both elements using their interpolation weights. Contact geometry updates once per fixed tick and the solver updates relative displacement each sweep.

**Tools → Foliage → Check plant self-collision** runs matched drags with contact enabled and disabled. All three enabled fixtures slid at least 43 mm and released. In the leaf-on-rod test, the blade kept 2.0 mm of surface clearance while sliding 47.7 mm. With contact disabled, it crossed the rod axis.

### DAT displacement safeguards

In **Stems**, **DAT displacement safeguards** is enabled with plant self-collision. Each fixed tick, nearby disjoint elements receive a separating plane with room for their collision skins. Prediction and every solver update keep the nodes on their assigned sides. Bounds are relative to the start of the tick, so successive solver sweeps cannot spend the same clearance twice. A per-axis travel limit also bounds motion toward elements outside the candidate search. **Inspect** displays the smallest available motion bound and the largest rejected update. The safeguard assumes initially separated elements and maintains the existing shared-node exclusions. The plane construction follows the [Divide and Truncate framework](https://ankachan.github.io/Projects/DAT/).

**Tools → Foliage → Check fast drag safeguards** drives the target 80 mm into and through the support's original plane at 0.6, 2.4 and 4.8 m/s, then holds it for twelve ticks. Both runs enable self-contact. With DAT enabled, minimum sampled clearance was 1.24–1.67 mm for leaf/leaf and 0.93–1.74 mm for leaf/stem. Both pairs crossed at the two higher speeds with DAT disabled. The slow leaf/leaf, leaf/stem and stem/stem checks slid 46.7–47.7 mm and released.

## Step 6 — Cutting

Begin with cutting stems at existing simulation nodes.

Then implement:

- Cutting stems between nodes.
- Slicing leaves across triangles.
- Updating physical and rendering topology.
- Continued simulation of detached fragments.
- Mouse-controlled cutting tool.

Preserve existing motion and deformation during cutting. Detached fragments must remain grabbable and physically interactive.

**Debug:** Cutting surface, intersections, split topology, newly generated elements, removed constraints and disconnected components.

In Play mode, select **Stems → Cut**. Click a stem or stalk to split it at the picked position. The outer 4% of a segment snaps to its joint. For a leaf, press on the blade, drag along the desired cut line, and release. The stroke defines a plane through the picked connected blade. Simulation pauses while the stroke is held and resumes at its previous clock setting. **Grab** can pick either side after the cut. **Reset** restores the original plant.

**Inspect** shows the cutting plane and its intersections during a stroke. After release, colored outlines identify detached components, paired crosses mark the seam, and red and green lines show removed and new structural links. The rebuilt solver retains rest shape, current deformation, interpolated velocities and the active obstacle. Fresh cut surfaces begin coincident and become ordinary collision partners once they clear each other's collision skin.

Detached fragments inherit their pre-cut velocities. Gravity advances the whole free fragment during prediction, and air resistance opposes motion normal to its blade surface. **Tools / Foliage / Check detached leaf release** checks for spontaneous movement with gravity off, then checks the fall with gravity on.

**Tools → Foliage → Check cutting** slices a moving blade, checks state transfer, catches its detached tip on a sphere, grabs it again, then cuts a stem inside a segment and at a joint. On the default plant the tip fell 96.3 mm and moved 79.4 mm under the subsequent grab. The three cuts produced four components and reset restored one.

## Step 7 — Expand the generator

### Branch test preset

In Play mode, select **Stems → Branch test** to load the seed-17, 3.2 m plant with eight main branches, sixteen side forks and 240 leaves. Main branches spiral up the tapered trunk. Each shoot carries one terminal blade and nine alternating side leaves with short petioles, curved surfaces, midribs and lateral veins. The default unbranched plant has 70 leaves on seven shoots.

Leave **Plant self-collision** enabled to see contacts form under gravity, or select **Grab** and drag an outer fork. **Branch contacts** counts active contact points between rods of different branches. **Blade contacts** counts points between different leaves. A pair of leaves can have several contact points. Select **Single stem** to return to the previous plant settings.

#### Initial overlap checks

At generation time, each blade gets the nearest vertical stalk-tip adjustment that clears earlier blades, including both blade skins and the self-contact envelope. This keeps the rendered surface, collar and solver rest shape together. Gravity and grabs can bring the leaves back into ordinary contact.

In Play mode, **Tools / Foliage / Check spawn overlaps** checks unloaded leaf clearance and deliberately embeds a sphere, capsule, box and gate in the canopy under both Penalty and OGC. Run the Windows player with `-benchmarkFoliageSpawn` to time the seed-17 branch preset after placing a 150 mm radius sphere directly on a blade.

With the corrected sphere present, the 1547 x 870 Editor run on a GTX 1070 Ti averaged **102.50 FPS** under gravity and **94.90 FPS** under continuous dragging. The respective 95th-percentile frame times were 13.49 ms and 14.70 ms. Both 12-second passes advanced about 60 physics ticks/s and recorded 0.000 mm peak post-step obstacle penetration.

#### Debug rendering

Use **Debug rendering** in the Plant, Sheet, Leaf or Stems panel to toggle the scene overlays and diagnostic color maps. The switch retains your individual overlay selections as you move between tests. Debug rendering starts enabled. Add `-noFoliageDebugRendering` to any Windows player benchmark command to start with it disabled. The benchmark log includes the setting beside each FPS result.

#### Branched plant performance

In Play mode, run **Tools / Foliage / Benchmark branched plant**. The benchmark warms up for 120 frames, then measures 12 seconds under gravity and 12 seconds with a continuous 18 cm sinusoidal rod grab. The Windows player runs the same benchmark with `-benchmarkBranchedPlant` and returns exit code 1 if either pass averages 60 FPS or less, physics drops below 59 ticks/s, or the solver detects a non-finite node.

Before the spawn-clearance update: Unity 6000.3.24f1 Editor, GeForce GTX 1070 Ti, 1547 × 870 Game view, default Solver panel, external obstacle off. The plant has 629 simulation nodes, 122 rod segments and 480 blade triangles. Both runs used 18 solver sweeps, normal gravity, self-collision and DAT. Contact counts are the separate peaks observed in each pass.

| Editor workload | Average rendered FPS | 95th-percentile frame time | Physics ticks/s | Peak branch contacts | Peak blade contacts |
|---|---:|---:|---:|---:|---:|
| Gravity | 108.75 | 12.80 ms | 60.00 | 6 | 13 |
| Continuous rod drag | 103.06 | 13.52 ms | 60.02 | 5 | 12 |

Branch test turns off vSync while active and restores the previous setting on exit. Its default controls use a retained UI panel. **Show solver settings** exposes the material, sweep and grab controls. **Inspect** enables the per-sweep residual measurements. Normal playback batches self-contact witness searches with Burst and overlaps GPU sample readback with rendering. The following physics tick consumes the completed positions before building its contacts.

#### Player capsule orbit

Select **Player capsule orbit** under **Stems / Test collider**. The upright capsule is 1.80 m tall and 0.60 m wide. It follows a 1.50 m radius circle through the ten-plant fixture at 2.40 m/s. Each lap takes about 3.93 seconds of simulation time. The rooted stems have distributed elastic support along their lengths. The preset starts with penalty contact. With OGC selected, clearance limits can slow the capsule. The panel reports rod and face contacts and penetration. Select **Stop player capsule** to remove it. Launch the Windows build with `-playerCapsuleOrbit` to open the test directly. Use `-benchmarkTenPlantCapsule` to measure the same orbit for 700 ticks. It reports render FPS, physics rate, peak rod elongation and longest continuous contact on a rod. All 2,400 leaves are rendered. Four plants near the orbit use individual collision beams and leaf shells where the player can reach them. The other six use shoot-level collision beams.

#### 100-plant capsule stress test

Select **100 plants + circling player** below **Debug rendering** in the foliage lab. The adjacent **10 plants + circling player** button opens the smaller orbit test. The preset generates a staggered 10 by 10 stand at 1.28 m spacing with seeds 17 through 116. The same player capsule follows a 2.29 m radius circle at 2.40 m/s, completing one lap in about 6.00 seconds of simulation time. Use the debug rendering switch to compare overlay costs. The Windows player accepts `-hundredPlantCapsuleOrbit` for interactive play or `-benchmarkHundredPlantCapsule` for a 700-tick run that logs frame rate, physics rate, capsule travel, contacts and peak penetration.

The 100-plant stand renders 24,000 leaves. Two plants selected by their terminal blades' distance to the orbit use higher-detail physics. Their lower shoots use shell blades and individual leaf beams within reach of the capsule. The other 98 plants use a reduced trunk and shoot-level collision beams. All 100 share the contact solver and its 18 sweeps. Add `-fullDetailFoliage` to either capsule preset to simulate every blade as a shell.

The earlier 0.38 m orbit benchmark at 1547 x 870 on a GTX 1070 Ti used 12,308 physics nodes. A refitted spatial tree finds candidate collisions. GPU kernels deform the render mesh, compute its normals, and solve each plant's structural constraints in color order. Shared contact snapshots couple neighboring plants on every sweep.

| Debug rendering | Original FPS | Optimized FPS | Physics ticks/s | p95 frame time after the first 60 ticks |
| --- | ---: | ---: | ---: | ---: |
| On | 5.10 | 445.14 | 59.93 | 6.80 ms |
| Off | 5.29 | 497.12 | 59.94 | 6.78 ms |

Both runs completed 700 ticks and 5.318 m of capsule travel. Maximum simultaneous counts were seven rod contacts and eight face contacts. Peak capsule penetration was 28.87 mm with the penalty model.

#### Ten-plant collision stress test

Select **Stems → 10 plants**, or run **Tools / Foliage / Benchmark ten plants** in Play mode. The Windows build accepts `-benchmarkTenPlants`. Ten independently generated branch presets (seeds 17 through 26) share one solver. Trunk centers are 1.28 m apart within each of two staggered rows. Row separation is also 1.28 m. All ten roots are pinned. The current stand has 2,400 leaves. The measurements below describe the earlier 240-leaf stand.

The generator applies its ordinary within-plant leaf clearance separately to each plant. Neighboring canopies enter the solver with their generated overlaps intact. **Between plants** counts active contact witnesses whose features belong to different plants. After one unloaded tick the fixture recorded 33 such witnesses and 9.83 mm maximum estimated inter-plant penetration. The benchmark resets the fixture, warms up under gravity for 60 physics ticks, then measures 12 wall-clock seconds each of gravity and continuous rod dragging. Both passes keep self-collision, DAT and 18 sweeps enabled.

Measured at 1547 × 870 on a GeForce GTX 1070 Ti with the fixed 1.28 m spacing.

| Runtime | Load | Average FPS | p95 frame time | Physics ticks/s | Peak between-plant witnesses | Peak estimated penetration |
| --- | --- | ---: | ---: | ---: | ---: | ---: |
| Windows player | Gravity | 2.87 | 377.05 ms | 17.19 | 66 | 0.69 mm |
| Windows player | Rod drag | 2.69 | 419.15 ms | 16.14 | 68 | 0.71 mm |
| Unity Editor | Gravity | 1.97 | 576.77 ms | 11.84 | 64 | 0.68 mm |
| Unity Editor | Rod drag | 1.53 | 768.46 ms | 9.18 | 63 | 0.69 mm |

The optimized Windows player uses a Burst-compiled sweep over the horizontal axis to find overlapping swept feature bounds. It reuses the previous tick�s ordering and processes candidate pairs in larger parallel batches. The final build used the same fixture, safeguards, 18 sweeps and benchmark.

| Load | Average FPS | p95 frame time | Physics ticks/s | Peak between-plant witnesses | Peak estimated penetration |
| --- | ---: | ---: | ---: | ---: | ---: |
| Gravity | 182.30 | 9.60 ms | 60.15 | 74 | 0.73 mm |
| Rod drag | 185.70 | 9.29 ms | 60.04 | 64 | 0.75 mm |

Contact counts include touching features. These aggregate measurements do not establish how long individual pairs stayed penetrated.

### Planned plant types

Generate additional plant types:

- Monstera.
- Fern.
- Ivy.
- Bushes.
- Small trees.

All species must share the same underlying geometry and physics primitives.

## Step 8 — Rendering and optimization

Once the physics works:

- Move rendering to shared GPU buffers.
- Add two-sided shading, translucency and shadows.
- Implement GPU-driven geometry rendering.
- Introduce sleeping and simulation LOD.
- Benchmark increasingly dense vegetation.

**Debug:** Simulation versus render geometry, active elements, sleeping state, LOD levels, GPU memory and per-subsystem execution times.

## Implementation rules

- Introduce AVBD extensions only if ordinary VBD proves insufficient.
- Do not substitute physics with fake or other algorithms
- Prefer faithful implementations of published algorithms.
