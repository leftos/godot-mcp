# Render diagnosis

The render half of GMCP-20, "Design step-until-a-condition and render diagnosis (ideas 5 and 6)", from idea 6 of the [survey](../../research/2026-09-29-godot-mcp-survey.md) (L124-128). The whole ruled design, the step-until half included, is archived in [step-until-render.md](./step-until-render.md); this file carries the render half's open step, R1, under GMCP-45 (Document render diagnosis).

The owner ruled render diagnosis documentation only ([DECISIONS.md](../../DECISIONS.md) decision 31): no read tool until a game repo needs one.

Status: R1 landed. Its drive findings are in decision 31 of [DECISIONS.md](../../DECISIONS.md) (engine facts) and in the "See why something is not drawn" section of [TOOLS.md](../../TOOLS.md), and section 2's unverified note is settled there: `set_property` reaches the root's `debug_draw`, and a node added after start draws its shape under a launch-time `--debug-collisions`.

## 1. Need

Neither game repo writes a visibility probe by hand.

What they do write: delve compares renderers by passing `engineArgs: ["--rendering-driver", "vulkan"]` and measures a first-draw hitch as the longest gap between two `process_frame`s, the watch's `frame_ms` (`X:/dev/delve-the-dungeon/docs/development/scratch-scenes.md` L145-153).

It solves a die numeral's drawn "up" from the live mesh in a `run_script` (L195), an orientation question, not a visibility one; and it notes the headless renderer "never triangulates", so a failed polygon logs nothing headless (L101). opening-hand (2D) reads detail at 2x through `--resolution` (`X:/dev/opening-hand/docs/DEVELOPMENT.md` L61).

The survey's own fit line (L128) names the cheapest step: the `--debug-collisions` family already passes through `engineArgs`, undocumented. The world-node aim of [input-targets.md](./input-targets.md) (decision 25) already explains why a node's origin is not on screen when an input tool aims at it: off-screen with the point and the rect, behind the camera, no current camera, hidden, not drawn by any viewport.

## 2. Engine facts

Cited from the `4.7.2-stable` tag of `godotengine/godot` or the 4.7 class reference in `F:/Godot/docs/class-ref-xml/doc/classes/`.

| Part | Fact | Source |
|---|---|---|
| Debug draw flags | `--debug-collisions`, `--debug-paths`, `--debug-navigation`, `--debug-avoidance` and `--debug-canvas-item-redraw` are parsed in debug builds and set on the SceneTree in `Main::start`, before the main scene loads | `main/main.cpp` L670-675, L1902-1911, L4436-4455 |
| The hints at run time | `SceneTree.debug_collisions_hint`, `debug_navigation_hint` and `debug_paths_hint` are "not designed to be changed at run-time" | `SceneTree.xml` L266-276 |
| `Viewport.debug_draw` | A property of every Viewport (the root Window included): unshaded, lighting, overdraw, wireframe, normal buffer. Lighting and overdraw replace every material, custom shaders included; wireframe under Compatibility needs `RenderingServer.set_debug_generate_wireframes(true)` before any mesh loads; normal buffer is Forward+ only | `Viewport.xml` L341-343, L650-676; `RenderingServer.xml` L3600-3605, L5525-5526 |
| Renderers in use | The default rendering method is `forward_plus`; delve sets none (`rendering_device/driver.windows="d3d12"`), so its 3D runs on Forward+; InputProbe uses `gl_compatibility` | `ProjectSettings.xml` L3279; `tests/fixtures/InputProbe/project.godot` L39-40 |

Unverified until R1's drive: whether `set_property` reaches the root Window's `debug_draw` through the bridge's node lookup, and whether a node added after start draws its collision shape under `--debug-collisions`.

## 3. Design: documentation

- A TOOLS.md subsection "See why something is not drawn" under "See the game", with a line in SKILL.md:
  - Collision shapes, paths, navigation and avoidance are launch flags: `run_project {engineArgs: ["--debug-collisions"]}` (also `--debug-paths`, `--debug-navigation`, `--debug-avoidance`, and `--debug-canvas-item-redraw` for what redraws in low-processor mode), or a `godot-mcp.json` preset (`"collisions": {"engineArgs": ["--debug-collisions"], "description": "collision shapes drawn"}`).

    They cannot be turned on mid-run (SceneTree.xml L266-276), so a running session needs `stop_project` then `run_project` with the flag.
  - Draw modes are `Viewport.debug_draw` on the root: `set_property {node: "/root", property: "debug_draw", value: 3}` (overdraw), back to 0 afterwards. Lighting and overdraw replace custom shaders, wireframe under Compatibility shows nothing unless the game generates wireframes before its meshes load, and normal buffer needs Forward+ (`engineArgs: ["--rendering-method", "forward_plus"]` to try it on a Compatibility project).
  - A world node's on-screen point, or why it has none, comes from the input tools' world aim (`aimedAt`, or the off-screen, behind-camera, no-camera and hidden refusals); a `hover` reports it, and moves the pointer.
  - A screenshot baseline taken in one draw mode fails in another.

## 4. Costs and risks

- `--debug-collisions` draws every shape every frame, which moves a watch's `frame_ms`.
- A `debug_draw` left set stays until set back or the game restarts.

## 5. Alternatives considered

- **A `render_probe {node, session}` read tool**, ruled out by decision 31: it would answer why a `CanvasItem` or `GeometryInstance3D` is not drawn (the first hidden ancestor, the product of `modulate` alphas, `visibility_layer` against the viewport's `canvas_cull_mask` or `layers` against the camera's `cull_mask`, the global AABB against the frustum, near and far, the visibility range, the projected rect), read-only, reusing `godot_mcp_targets.gd`'s projection.

  No game repo writes such a probe by hand (section 1).
- **Toggling the SceneTree debug hints with `set_property`**: the class reference says they are not designed to change at run time (SceneTree.xml L268).
- **A `debugDraw` option on `take_screenshot`**, set for one capture and restored: worth it only if `set_property` cannot reach the root's `debug_draw`, which R1 checks (decision 31's ruled-out list).
- **satelliteoflove's `godot_validate_meshes` and `godot_scene3d`**: neither game builds meshes or asks for a spatial dump; delve's die orientation question is answered by a `run_script`.

## 6. Step

1. **R1, render docs**: the TOOLS.md subsection, the SKILL.md line and the preset example of section 3, proven by a drive (`pwsh run.ps1 drive -Calls .tmp/drive-debug-draw.json`) on an InputProbe copy: a launch with `--debug-collisions` and a `run_script`-built `Area2D` against a launch without, compared through `compare_screenshot` (changed pixels in the shape's rect).

   Then `set_property {node: "/root", property: "debug_draw", value: 3}` read back and screenshotted under Compatibility, and again with `--rendering-method forward_plus`; whether a node added after start draws its collision shape.

   The drive's findings replace section 2's unverified note before the docs land. No code, no itest.
