# Proposal: C#-aware runtime tools

Draft for the plan line "C#-aware runtime tools beyond `call_method`" (MAIN.md, Singles). Being built: §7 lists the steps and which have landed. Godot is cited from `4.7.2-stable`; .NET from `dotnet/runtime` `v10.0.0` (checked against `v8.0.0`); MSBuild from `dotnet/msbuild` main.

## 1. Who needs it

Both games keep their state in plain C# (`Duel.Engine`, `Delve.Rules`), and their nodes pass it around in types Godot cannot marshal:

- opening-hand `DuelScreen`: `internal Decision? PendingDecision`, `internal SessionUpdate? BoundUpdate` (plain records), `internal void Bind(SessionUpdate)`, `internal List<CardFace> ListCardNodes()`; `Main.StartQuickBattle(IDuelSession)`.
- delve `Combat`: `internal EnemyState? FindShownEnemy(int)`, `internal int? FindShownSideMaxHp(int)`, `public void Bind(GameUpdated, RoomView?, string, ContentCatalog?)`; statics on plain classes: `RememberedNames.OwedNames(…)`, `Updater.Logged`.
- delve's DEVELOPMENT.md L69 already records the wall: "A player's saved `Fight animations` pace cannot be set from a `run_script` (the session is no Variant)", and L83 makes every scratch scene add public, Variant-typed wrappers so a drive can reach it.

What Godot's call reaches today, from the source generator: a generic method is skipped (`ExtensionMethods.cs` L307-308), so is any method whose return or parameter type does not marshal (L310-324), and overloads with one name and one argument count collapse to the first (`ScriptMethodsGenerator.cs` L58-67, L133-134). Static methods with marshallable signatures on a script class are reachable through the script (`InvokeGodotClassStaticMethod`, L227-244; `csharp_script.cpp` L2582-2591); statics on plain classes, static properties and fields are not. Properties of plain types are not exposed at all.

## 2. Routes, checked against the source

| Route | Verdict | Why |
|---|---|---|
| (a) The bridge ships a `.cs` and Godot compiles it at run time | No | `CSharpScript::reload` compiles nothing: "reload doesn't really do any script reloading" (`csharp_script.cpp` L2594-2605); a script is valid only when its path is in the map built from `[ScriptPath]` types of the project assembly (`ScriptManagerBridge.cs` L300-326, L436-462), else `can_instantiate` fails (`csharp_script.cpp` L2347-2358) |
| (a') An `override.cfg` autoload naming a `.cs` outside the assembly | No | Same lookup: the script loads invalid |
| (a'') `override.cfg` setting `dotnet/project/assembly_name` to a shim | No | Godot looks scripts up in that one assembly only (`gd_mono.cpp` L765-780, GodotPlugins `Main.cs` L140-154), so every game script would go missing |
| (b) Reflection through an entry point already in the game | No | Nothing in GodotSharp takes a type or assembly name from Variant-land; the only reach is the generated tables above |
| (d1) `DOTNET_STARTUP_HOOKS` set by the server at launch | No | Hooks run only in `Assembly::ExecuteMainMethod` before `Main` (`assembly.cpp` L1324-1341, L1405; v8.0.0 L1420, L1502). Godot never runs a `Main`: the editor binary our runs use gets a function pointer through `hdt_load_assembly_and_get_function_pointer` (`gd_mono.cpp` L432-462) |
| (d2) `DiagnosticsClient.ApplyStartupHook` | No | The diagnostic hook paths go through the same `RunManagedStartup` (`assembly.cpp` L1337-1340) |
| (d3) ClrMD heap reads from the server | Reads only | No calls, no property getters (backing fields only), the game suspended while it reads; mapping a node to its managed object unverified |
| (d4) ICorDebug func-eval | Not advised | A debugger engine in the server; the main thread sits in native Godot code most of the time, where eval cannot run (unverified) |
| (c1) Prep's build compiles a helper into the game's assembly | Yes | `dotnet build … -p:CustomAfterMicrosoftCommonTargets=<server>/GodotMcp.targets` imports a server file (`Microsoft.Common.CurrentVersion.targets` L7140) that adds a `.cs` copied to `.godot/godot-mcp/dotnet/`; the generator gives it `res://.godot/godot-mcp/dotnet/…` (`ScriptPathAttributeGenerator.cs` L97-108). No tracked file changes |
| (c2) The bridge loads a helper at run time | Yes, after a spike | `GDExtensionManager.load_extension` is bound to script (`gdextension_manager.cpp` L468) and initialises a late extension up to the current level (L44-56). A small native shim finds the already-loaded `hostfxr`, whose `hostfxr_initialize_for_runtime_config` now returns a secondary context (`native-hosting.md` L513-518) that loads the managed helper into an isolated load context (L424). A C# `Node` type constructed in C# gets a path-less script (`InteropUtils.cs` L50-73, `ScriptManagerBridge.cs` L481-501, L611-626), so GDScript can call it |

## 3. Recommended: (c2), a helper loaded at run time

It leaves the game's build untouched, works for attached sessions and any Debug build, and costs nothing until the first call.

**Loading.** On the first C# tool call the bridge (`bridge/godot_mcp_dotnet.gd`) calls `GDExtensionManager.load_extension` on `godot_mcp_dotnet.gdextension` published beside the exe. The shim's init loads `GodotMcp.Dotnet.Loader.dll` through `hostfxr`. The loader, which references nothing of Godot's, finds the load context holding `GodotSharp` (GodotPlugins' own, `Main.cs` L81-83, which the game's `PluginLoadContext` shares it from, `PluginLoadContext.cs` L51-52) and loads `GodotMcp.Dotnet.dll` into it, so the helper binds the game's own `GodotSharp`. The helper adds a `GodotMcpDotnet` node under the bridge. Three steps are unproven and are the spike: a `.gdextension` outside `res://` loading at run time; the secondary host context inside Godot's process; `GodotObject.InstanceFromId` in the helper returning the game's own instance.

**Tools** (each ≤ 5 parameters, destructive like `call_method`):

- `cs_members(target, options {name, nonPublic, offset, limit}, session)`: `{type, members: [{kind, name, signature, static}]}`, overloads spelled out.
- `cs_get(target, member, options {maxDepth, keep}, session)`: a property or field, a dotted path allowed (`BoundUpdate.Pending.Options[0]`): `{value, type, handle?}`.
- `cs_set(target, member, value, session)`: converted by the member's type, read back as `set_property` does.
- `cs_call(target, member, args, options {signature, typeArgs, keep, timeoutMs}, session)`: `{value, type, handle?}`; a `Task` is awaited.

`target` is exactly one of `{node}` (a path, the node's managed instance), `{type}` (a full type name, for statics and `new`: `member: ".ctor"`) or `{handle}`. Errors say which: no such member; ambiguous overloads, listing each `signature`; an argument that does not convert, naming the parameter and type; the member threw, with the exception's type, message and stack.

**Marshalling.** `System.Text.Json` from the shared framework, so no new package. Arguments convert by the chosen parameter's `Type`: records, enums (name or number), `List<T>`, `ImmutableArray<T>`, nullable. An interface or abstract parameter (`IDuelSession`) takes `{"$handle": "h3"}` or `{"$node": "path"}`. Overloads resolve by count, then by which candidates the JSON converts to; still ambiguous, `options.signature` (`["int", "string"]`) decides. Generic methods take `options.typeArgs`. Results: a `GodotObject` as `{"$node": path}` (or `{"$object": class, id}` off-tree, `"<freed object>"` when freed); Godot structs in the bridge JSON module's shapes (`{x, y}`); cycles cut; depth 8; 20000 characters then `{valuePreview, valueLength}`, as `run_script`. `options.keep` also returns a handle (a table in the helper, 256 at most, emptied when the game exits).

**Cost to a game that is not C#.** None until a C# tool is called; then a refusal, "this project has no C# assembly", from the server's own `PrepScan`, before the bridge is asked.

**Footguns and safety.**
- It runs arbitrary game code and reads private state; a getter with side effects runs when read.
- Every helper entry catches everything: an exception reaching the shim would take the game down.
- Calls run on the main thread only.
- The helper is built against one Godot minor's `GodotSharp`; a new engine version needs a rebuild.
- Exported release builds (a different host path, `gd_mono.cpp` L465-490) are out of scope.

**Tests.**
- Unit: the helper's marshalling and overload choice are plain .NET, tested in `tests/GodotMcp.Tests` with no Godot; server argument checks likewise.
- CsProbe gains: a method taking and one returning a record, `List<T>` and `int?` returns, a generic method, `Hit(int)`/`Hit(float)`, an `async Task<int>`, a plain static class with a static property, and an interface parameter fed a handle. A new `CSharpToolTests` goes in an `itest` group; one test proves a GDScript-only InputProbe run refuses cleanly.

## 4. Ranked options and each one's worst case

1. **(c2) Run-time helper.** Worst case: the spike fails on the secondary context or the shared `GodotSharp`, which costs the spike (about a day, unmeasured) and falls back to (c1). In use: a shim bug crashes the game mid-drive.
2. **(c1) Helper compiled in by prep.** Worst case in use: the user's own builds and the server's alternate, each forcing a full recompile of the client, since the compile inputs differ; a game the user launched (an attach) lacks the helper; the user's editor runs carry a dormant helper class.
3. **(d3) ClrMD reads only.** Worst case: agents still cannot call `Bind` or set the fight pace; the game freezes for each heap walk (unmeasured).
4. **Status quo.** The games keep adding Variant-typed wrappers per drive (delve DEVELOPMENT.md L83).

## 5. Closing questions

Decided (user, 2026-09-26): route (c2), the run-time helper, with the spike first and (c1) as the fallback; the shim in C# NativeAOT; four tools, `cs_members`/`cs_get`/`cs_set`/`cs_call`; non-public members reached by default; `run_csharp` built now, with the four tools.

## 6. Spike result (2026-09-26): all three steps proved

Headless runs of a CsProbe copy on Godot 4.7.2 .NET (runtime 10.0.12), each one short session; no windowed game, no GC load on the two runtimes. The spike's sources are kept untracked in `.tmp/cs-spike/` of the main checkout (shim, loader, helper, the `.gdextension`, the probe autoload).

1. **A `.gdextension` outside `res://` loads at run time.** `GDExtensionManager.load_extension(<absolute path>)` returned `LOAD_STATUS_OK`, and the shim saw every level initialise and de-initialise; a second load returns `LOAD_STATUS_ALREADY_LOADED`. Sources: `gdextension_manager.cpp` L44-56 (a late extension is initialised up to the current level), `gdextension_library_loader.cpp` L365-375 (a relative library path resolves beside the `.gdextension`).
2. **The running .NET runtime accepts a second assembly.** The shim found the loaded `hostfxr.dll`; `hostfxr_initialize_for_runtime_config` returned `0x2` (`Success_DifferentRuntimeProperties`, `host-error-codes.md` L11: a secondary context whose extra properties are ignored, `native-hosting.md` L278-282), and `load_assembly_and_get_function_pointer` loaded the loader into its own isolated context. The NativeAOT runtime in the shim and CoreCLR ran side by side in all four runs, with clean exits.
3. **The helper sees the game's own objects.** The loader found GodotPlugins' `IsolatedComponentLoadContext` (holding `GodotSharp`; the game's assembly sits in a `PluginLoadContext`) and loaded the helper into it, so the helper's `GodotSharp` is the game's; on the main thread `GodotObject.InstanceFromId` returned the game's own node, and reflection read a `List<int>` property and a private record field that Godot's `get` returns null for.

Measured: the shim is 1.1 MB (its pdb need not ship); the first load after a build took 2.2 s (unexplained, likely a cold disk or antivirus scan of new binaries), warm loads 13-15 ms, a helper call 5-12 ms. Toolchain: .NET SDK 10.0.401 with ILCompiler 10.0.12, and the MSVC linker from VS Build Tools 2022 17.14 (VS 2026 here has no VC tools); ILCompiler's `findvcvarsall.bat` looks for `vswhere` under `%ProgramFiles(x86)%`, which Git Bash does not pass on, so the publish runs from pwsh or with vswhere's folder on `PATH`.

What changes in §3: the editor binary initialises extensions up to level 3 (EDITOR) even for a game, so the shim acts at the first level at or above SCENE, not at a fixed one; the loader's `runtimeconfig.json` rolls forward to the latest major (`rollForward: LatestMajor`, as `GodotPlugins.runtimeconfig.json` does), since games run on .NET 10 while the helper targets net8.0; and a `Callable.From(Func<...>)` stored as SceneTree meta was enough for GDScript to call the helper repeatedly, a possible stand-in for the planned `GodotMcpDotnet` node.

## 7. Build steps (planned 2026-09-26)

Each step lands as a green commit. After S1, the steps S2, S3, S4 and S8a touch disjoint files and may run in parallel worktrees; S5 → S6 → S7 → S8b run in order (one dispatch table, one test class). The spike's sources in `.tmp/cs-spike/` are the starting point for S1 and S3.

**Decided for the build.** By the user (2026-09-26): a `run_csharp` snippet is a method body (statements or one expression), compiled as the `RunAsync()` of a class deriving from a globals base class the helper defines (so its members are in scope unqualified; the proposal first wrote it as `Run(Ctx ctx)`), with `Tree`, `Root`, `Node(path)`, `Handle(id)` and the reflection `Get`/`Set`/`Call` in scope, and `usings` from its options; private members are reached through `Get`/`Call`, never by switching off Roslyn's access checks. Orchestrator defaults (2026-09-26), each open to a reversal:
- Projects under `src/`: `GodotMcp.Dotnet.Shim` (net10.0, NativeAOT), `GodotMcp.Dotnet.Loader`, `GodotMcp.Dotnet.Core` (Godot-free marshalling, unit-tested) and `GodotMcp.Dotnet` (the helper), the last three net8.0 like CsProbe and `GodotSharp` 4.7.2; `Directory.Build.props` applies to all of them, and a catch-everything entry point suppresses CA1031 with its reason.
- The helper compiles against the NuGet `GodotSharp` 4.7.2 (`PrivateAssets=all`, `ExcludeAssets=runtime`); the loader refuses a `GodotSharp` of another major.minor.
- The helper is reached through a `Callable` stored as SceneTree meta `godot_mcp_dotnet` (proved by the spike), not a node; its reply is a JSON string the server parses, since GDScript's parser reads every number as a float.
- A game holds the shim and helper dlls locked until it exits, so the server loads them from a copy under `%LOCALAPPDATA%\godot-mcp-cache\dotnet\<content hash>\`, pruning stale copies at start.
- Handles live in the helper, 256 at most, least recently used evicted; ids carry an epoch (`h<epoch>.<n>`), so a handle from before a restart fails as "dropped when the game restarted".
- Roslyn reads the game's assembly and its folder from `PrepScan.AssemblyPath`; `run_csharp` refuses when the assembly on disk is not the one loaded (its MVID differs), naming `restart_project`.
- `run_csharp(code, options {usings, timeoutMs, keep, maxDepth}, session)`, a `batch_drive` step like `run_script`; timeouts as `call_method` (10000 ms, at most 120000).
- A project with no C# assembly, or an unbuilt one, is refused by the server from `PrepScan` before the bridge is asked.
- A shim or loader failure reaches the bridge through the environment variable `GODOT_MCP_DOTNET_ERROR`.
- The itests get a built CsProbe from a class fixture and one launch per class, in a new `csharp` itest group.

**Steps.**
- **S1. Projects, publish, install.** Landed 2026-09-26: the shim, loader and helper projects (Core waits for S2), `run.ps1 dotnet`, `publish`/`install` carrying `bin/dotnet`, `Installation.FindDotnetExtension`; a headless CsProbe run loaded it and found the meta. ILCompiler needs both `%ProgramFiles(x86)%` (`findvcvarsall.bat` L9-10) and vswhere's folder on `PATH` (a later batch file calls it by bare name), and `run.ps1` sets both. `itest` does not run the `dotnet` step yet: S3 adds it with the first test that loads the helper.
- **S2. Marshalling core.** Landed 2026-09-26: `GodotMcp.Dotnet.Core` with `ValueReader`, `ValueWriter`, `MemberPath`, `Signatures`, `OverloadResolver`, `HandleTable` (tests under `tests/GodotMcp.Tests/Dotnet/`). Carried forward: S3's reply serialisation needs relaxed JSON escaping, or the writer's markers (`"<cycle: T>"`) arrive as `<`; S6 needs a type-rooted entry to `MemberPath.Walk` for statics reached from `{type}`; S7 should catch a resolver's own exception (a bad handle) per candidate rather than let it end overload choice, and knows a resolver runs once per candidate.
- **S3. Bridge loader and a smoke call.** Landed 2026-09-26 but for its integration tests: the bridge's `dotnet` module, the loader's resolver, the helper's `ping`, `CSharpBridge` and `HelperCache`, to the wire contract now in ARCHITECTURE.md (orchestrator defaults: the command `dotnet` with the request and reply as JSON strings, `loadedNow` on the reply, a failed load remembered, the copy's `.complete` marker, the prune at a server's first copy; Core grants the helper `InternalsVisibleTo`). Left: the `csharp` itest group and its three tests below, with `run.ps1 itest` running the `dotnet` step. As planned: `bridge/godot_mcp_dotnet.gd` loads the extension once per process from the path the server sends; the server's `CSharpBridge.SendAsync` does the refusal and the shadow copy; the helper answers `ping`; the helper references `GodotMcp.Dotnet.Core`, which `run.ps1 dotnet` copies into `helper/` and the loader resolves itself (a `Resolving` handler on the GodotSharp context, scoped to the helper's folder), since that context's own resolver looks only in GodotPlugins' folder; `itest` runs the `dotnet` step. Proof: `pwsh run.ps1 gdtest`; `CSharpToolTests.TheHelperLoadsIntoTheGameAndSharesItsGodotSharp`, `AGDScriptProjectIsRefusedBeforeTheBridgeIsAsked`, `ASecondCallDoesNotLoadTheExtensionAgain`.
- **S4. CsProbe additions.** Landed 2026-09-26: `CsTypes.cs` (`Mood`, `Point2`, `Update`, `IGreeter`/`Greeter`, the static `Tally`) and `CsTargets.cs` (a `Node` no scene holds, which the tests add at run time: a private record field, `Numbers`, `MaybeCount`, `Last`/`Take`, `Echo<T>`, `Hit(int)`/`Hit(float)`, `CountLaterAsync`, `GreetWith(IGreeter)`, `Mood`, `Fail`); `CsProbeNode.cs` and `main.tscn` untouched.
- **S5. `cs_members`**, with the `{node}|{type}|{handle}` target and the smoke-test entry. Proof: `CSharpValidationTests`; `MembersListBothHitOverloadsAndPrivateFields`, `MembersOfAStaticClassByTypeName`; `McpServerSmokeTests`.
- **S6. `cs_get`, `cs_set`.** Proof: `GetReadsAPrivateRecordFieldGodotReturnsNullFor`, `GetFollowsADottedPathIntoAList`, `SetConvertsAnEnumByNameAndReadsBack`, `KeepReturnsAHandleUsableAsATarget`.
- **S7. `cs_call`**: overloads, `signature`, `typeArgs`, `.ctor`, `$handle`/`$node` arguments, an awaited `Task` under `timeoutMs`, a thrown exception's type, message and stack. Proof: `CallPicksHitFloatBySignature`, `CallGenericWithTypeArgs`, `CallAwaitsAsyncTaskInt`, `CallPassesAHandleToAnInterfaceParameter`, `CallReportsTheThrownException`, `AnAmbiguousCallListsEachSignature`.
- **S8a. `run_csharp`'s compiler.** Landed 2026-09-26: `CSharp/SnippetCompiler.cs` on Microsoft.CodeAnalysis.CSharp 5.9.0; the globals base class is an input (`GlobalsType`), so S8b defines it in the helper with `Tree`, `Root`, `Node`, `Handle`, `Get`/`Set`/`Call`. A collectible context is not freed until a GC runs after `Unload()`.
- **S8b. `run_csharp` in the game**: the helper loads the bytes into a collectible context; the stale-build refusal. Proof: `RunCSharpCallsBindWithAPlainRecord`, `RunCSharpAwaits`, `RunCSharpCompileErrorNamesTheLine`, `RunCSharpRefusesAStaleBuild`.
- **S9. Docs**: ARCHITECTURE, DEVELOPMENT (the MSVC and vswhere toolchain, `run.ps1 dotnet`, the footguns: locked dlls, one Godot minor, main thread only), TOOLS, the skill; each of S5-S8b already adds its own tools' rows.

