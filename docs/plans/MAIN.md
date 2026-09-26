# Main Plan

Open work only, in working order: the next item is the first line from the top. The design, the user's decisions and each step's proving test are in [2026-09-25-first-version.md](./2026-09-25-first-version.md).

## Now: the first version, replacing godot-mcp-runtime at parity

- [x] Step 0: scaffold (solution, server and test projects, the GDScript bridge folder, the gates, the InputProbe fixture project)
- [x] Step 1: wire and lifecycle (listener, handshake, override.cfg inject/clean/refuse, .git/info/exclude; run_project with pass-through arguments, stop_project, get_debug_output)
- [x] Gate gaps from step 1: wall-clock ceilings (`tools/gate.ps1`), complexity ≤ 8 enforced (CA1502), the handshake's refusal cases tested, GitExclude through `--show-prefix`
- [x] Step 2: runtime reads (take_screenshot with crop and preview, get_ui_elements, run_script with diagnostics)
- [ ] Step 3: input (click, drag, type_text, key/button hold and release, raw simulate_input; viewport-to-window mapping); the fixture drag is the headline test
- [ ] Step 4: attach_project and detach_project
- [ ] Step 5: the 16 headless scene and node tools and validate
- [ ] Step 6: cutover in opening-hand and delve-the-dungeon (registration, their docs and conventions, the debugger agent's allow-list; one scratch drive each; git status clean)

## Later (not in the first version)

- [ ] In-engine recording (frame-perfect video and audio from inside the engine)
- [ ] Several sessions at once, one per worktree or agent
- [ ] C#-aware runtime tools (read and call C# members of running nodes)
- [ ] The profiler, autoload-editing and file-parsing tools, if a need shows up
