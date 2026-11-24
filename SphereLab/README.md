# Hangar experiment

Launch `Build/Hangar.exe`, or open this folder in Unity 6000.3.24f1 and play `Assets/Hangar.unity`. WASD moves, mouse looks, Space jumps, and left click fires the railgun. The railgun needs one second between shots. After a rail hit or sphere contact, the camera eases toward the sphere over 0.75 seconds. R starts a new trial. Esc releases the pointer and clicking the game captures it again.

## Maps and builds

The hangar uses one sphere and its survival planner. To use a different map, assign an Arena asset with the geometry, spawns and navigation grid. BuildExperiment.Windows builds the enabled scenes in Build Settings.

## Movable crates

Five orange 12 kg crates sit at drop edges on the east and west terraces, transfer bridge, rear dock and west shelf. Both the player and the sphere can push them. F4 cycles through the five ledge trials and back to the ordinary spawn. R repeats the current start.

The planner snapshots every crate’s position and velocity for each branch and simulates gravity, static and crate-to-crate collisions, player pushes and sphere pushes in a separate PhysX scene. Rotation is locked for this experiment. Each hypothesized crate pose can block a rail shot or provide cover. A downward underside crossing over the player's head at more than 2 units/s ends the trial. A predicted impact is a win under the current player movement forecast; a moving human can dodge. No score rewards pushing, and no push maneuver is scripted.

In the FPS recorder the white line from the crate traces its predicted motion. Version 8 logs include all actual crate positions, predicted motion for the sampled crate, and push and impact identifiers. The stationary ledge experiment uses `-crateProbe`. Use `-cargoMechanicsProbe` to exercise pushes, cover and falling impacts independently.

## Flight recorder

The live recorder starts enabled in the FPS view. F1 toggles it. Tab freezes play at the current camera pose. Move the mouse to look around while paused. Left and Right browse up to 48 decisions from their recorded player viewpoints. Tab resumes from your original gameplay pose.

Cyan marks the sphere’s starting point for a decision. Green shows the chosen route, blue the immediate alternatives, red the arcs with a predicted hit, and gold the player model’s predicted aim. The marks remain visible through cover. G adds the arena graph and deeper explored alternatives. While paused, B highlights one immediate choice in magenta and shows its exposure and predicted hit time.

Each launch writes a timestamped JSON Lines file under `%USERPROFILE%/AppData/LocalLow/SphereLab/Hangar Experiment/Decisions`. The inspector displays the actual path. Each line is an independent JSON object. Plans are flushed on every decision. The recorder keeps full explored edge lists on disk, so long sessions can produce large files.

- `plan.data.observed` records the player's eye, velocity, aim, and seconds until the rail is ready. Nonpositive values mean ready.
- `choices` records explored edges. A parent of -1 means the observed starting state. Other parent values index the same choices array. Exposure is sampled time during which full-body concealment could not be established. Version 7 records the selected surfacePath at 16 time intervals per move and discarded paths at four. Observations include supportNormal and surfaceClearance. A white ring at the start of a plan marks its contact plane. A nonnegative firstShot is the elapsed time from the start of the decision to a predicted hit.
- `route` indexes the chosen edges. `forecast` contains samples along them. Compare `plan.data.time + sample.t` against subsequent observation times. Forecasts beyond the executed first edge describe a hypothetical continuation. Later replanning can change that route.
- `observation`, `rail`, and `outcome` record actual simulation events, with the round and the active decision ID. Times are simulation seconds. Paused time is excluded.

## Rail death review

After a rail kill, the camera finishes its turn and the FPS recorder opens on the last decision that forecast full-horizon survival or safe contact. Left and Right scrub actual frames. Cyan shows your recorded eye, aim and sphere position. Yellow shows the forecast interpolated to the same time. Magenta shows the alternate aim if enabled. The review opens at the first sample with at least 0.25 units of eye error or 5 degrees of primary aim error. With no such sample it opens on the last matched frame. These are display thresholds. Sphere displacement is shown separately because the sphere may have replanned during the comparison. A forecast ending before the kill stops at its own time limit. Tab leaves the review, V reopens it, and R starts a trial. A kill with no preceding survival forecast records an empty review.

## Independent experiment controls

F2 toggles the escape check and F3 toggles the alternate aim model. Both start enabled. Changes apply on the next decision and persist across rounds. For comparisons from launch, use `-noEscapeCheck`, `-noAlternateAim`, or both. Each plan logs its settings, alternate turn speed (radians/sec), alternate-only threat count, number of escape candidates checked, and the selected continuation. The white tail in the FPS overlay shows the tested continuation beyond 1.5 seconds.

The escape check shortlists up to four complete routes, giving different first moves representation where available. From the endpoint it tests two more quarter-second arcs: brake and hold, or steer toward a neighbor then brake in cover. It prefers a surviving extension. When none is found it retains the original forecast and records “No tested escape.” Safe contact ends the search immediately.

The alternate player turns toward visible targets using recent observed head-turn speed (a decaying peak with a half-second half-life, clamped to 0.5–12 radians/sec). Both aim forecasts share movement, cover geometry and the same rail cooldown. A predicted hit in either model counts against the move. Session version 3 adds the two aim directions and the time-aligned `death-review` records.

## Contact confidence

A nominal touch gets a second check before it can end the search. The planner replays the candidate flight at 32 samples/sec against four short player responses. The first keeps the observed movement. The others change input after 0.125 seconds to stop horizontal movement or strafe left or right. Each response uses gravity, wall collisions and the observed rail cooldown. During exposed samples it checks whether a turn from the observed crosshair could bring the rail onto the sphere by that time, including turns starting outside the tracking cone. A shot at the same sample as contact wins.

The same path must reach all four responses by its predicted contact time to receive winning priority. A rejected candidate releases the search to try other approaches or survival routes. If only an uncertain attack remains preferable to a predicted hit, the recorder labels it “Unverified contact.” The FPS display and version 4 plan logs report the main search's contact checks, rejection count and latest reason; individual rejected edges carry `contactVeto`. This is a finite stress test, not a calibrated probability of winning.

## Planner assumptions

The graph samples the faces of solid graybox colliders, including inclined walls, ceilings and pillars. Static shortest-path distances around cover guide a bounded best-first search through time. Braking, steering toward graph guides, steering past visible bends, and approaching the player directly are candidate moves. At each decision the model extrapolates the current horizontal player velocity with collision and gravity, and predicts turning toward a visible sphere at up to 2.6 radians/sec. Its vision cone has an approximately 63-degree half-angle. The actual player can turn more quickly, change direction, jump, or choose when to shoot.

Each quarter-second move is integrated in 16 surface-constrained increments. Tangential steering and surface projection let the sphere wrap around edges and transfer between faces. The same trajectory drives planning and execution. Each edge has four threat samples. The search attempts a 1.5-second forecast, allocates expansions across successive time steps, and gives competing first moves representation before expanding another continuation of the same move. It expands at most 64 states. A predicted hit eliminates a continuation from the safe set. Within safe complete routes the planner considers predicted contact, elapsed time, travel remaining and exposure. If it cannot certify a whole horizon it labels the partial forecast or reports an attempt to delay a predicted hit. Full concealment requires a box to block the projected sphere silhouette. For partial cover the player model searches for an aim point on the rim. Unproven cover counts as exposure. Actual and predicted shots share the same ray/sphere intersection and obstruction test. Shots between forecast samples and faster human reactions remain sources of prediction error.

There is no reward for occupying a spot behind the player. To investigate flanking, repeat trials from the same spawn with different initial facing directions. Look at the contact bearing relative to recorded player aim and compare decisions leading up to the approach.

For a short stationary smoke run, launch the executable with `-batchmode -nographics -stationaryProbe`. This records the same planner and arena with fixed simulation steps, then exits after 18 decisions or an outcome.
