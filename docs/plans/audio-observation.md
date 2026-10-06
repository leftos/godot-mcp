# Audio observation: which players play, bus levels

Design draft for Linear GMCP-21, "Design audio observation: which players play, bus levels (idea 9)", from idea 9 of the [survey](../research/2026-09-29-godot-mcp-survey.md) (L140-143), paired with the closed GitHub #50 (the `mute` option). It builds on the timeline watch of [timeline-watch.md](./archive/timeline-watch.md) ([DECISIONS.md](../DECISIONS.md) decision 29, GMCP-19). Every question in section 7 is ruled.

Status: the blocker is cleared, since the watch's steps 2 to 4 have landed (property and expression tracks d638a9f, signal tracks 4d9d406, monitor tracks 42f1184). Next is the spike, section 6's step 1.

**The answer in one line**: audio observation is two new track kinds of the `watch` tool, voices and buses, not a tool of its own: the question an agent asks ("did the hit sound fire when I clicked") is an event in a window, which a read between calls misses for any sound shorter than the gap between them, and a watch already runs beside every input, wait and `frame_control` call and stamps everything on one frame clock.

## 1. Need

An agent has no tool that names a sound. Today it can:

- read one named player's `playing` with `inspect_node`, at the moment of the call;
- wait for one named player's `playing` to become true with `wait_for {node, property, equals}`, or for its `finished` signal;
- read buses only through a `run_script` calling `AudioServer`;
- from the watch's step 2 on, sample a named player's `playing` or `volume_linear` as a property track, and a bus's peak as an expression track through `Engine.get_singleton("AudioServer")` (Expression has no singletons of its own: 4.7.2 `core/math/expression.cpp` has no `has_singleton` lookup, and `Engine` is one of the bridge's `EXPRESSION_INPUTS`, `bridge/godot_mcp_conditions.gd`).

Each of those reads `playing`, which misleads three ways (section 2): a second voice of a polyphonic player leaves `playing` true, so the restart is invisible; a pause turns `playing` false, so it reads as a stop; and a player made and freed for one sound (a fire-and-forget player) has no path to name before it exists. None of them lists the players or says which stream a voice played.

**delve-the-dungeon** checks its sound by hand and writes per-frame samplers in scratch code:

- `docs/manual-tests.md` L47 (4a, "Sound in a fight": each line plays one sound as its first character shows, a skip plays nothing for the lines skipped, `Effects volume` at 0 silences all of it), L51 (4e, sound on the map and in the dice box) and L52 (4f, music fades, loops, ducks under the victory stinger): every check is a person listening.
- `docs/DEVELOPMENT.md` L88: a scratch "there to be heard (`FightSoundsScratch`, `MusicScratch`) is launched … through the MCP … with `userArgs: ["--sound"]` and `options.quiet: false`, since a quiet run is silent", and "`SettingsScratch` reads the Effects bus's volume, `MusicScratch` its players' `volume_linear`". The repo believes a quiet run cannot exercise sound; section 2 shows its mixer runs.
- `src/Delve.Client/Scratch/FightSoundsScratch.cs` L59 and L173-192: `_Process` samples every `Sounds` player's `Playing` each frame and writes down each one that starts, to check that a blow, a heal and a downing each started their own sound in order (L84-95). Its players hold two voices each (`Scripts/Components/Sounds.cs` L45, `MaxPolyphony = 2`), so a second blow while the first rings is the restart that sampler cannot see.
- `src/Delve.Client/Scratch/MusicScratch.cs` L22-23: "Headless audio is the dummy driver, so a run reads the players' state, never the sound"; L217-274 reads both music players' `VolumeLinear` and `Playing` the frame after a change, by a `ProcessFrame` handler it connects itself.
- `docs/DEVELOPMENT.md` L66 and L79: whole-game drives pass `--nosound`, and watched runs `options.mute`; the game's `SoundLevels.IsMasterMuted` (`Scripts/SoundLevels.cs` L60-61) also mutes Master while the window is unfocused when the player asked for it, which every quiet run's window is.

**opening-hand** has one player that swaps its stream per line: `OverworldScratch.cs` L632 and L651-652 (`voice.Stream = SeniorVoice[lineIndex]; voice.Play()`). Its `docs/DEVELOPMENT.md` names no sound check (L32 sends sound checks to `duel.ps1 play`, by hand; L62 notes `dropIdle` drops the sound from a clip). Which line spoke is the stream of the voice, which no property track reports.

The issue's condition, "useful only if it reads anything under the Dummy audio driver a quiet run uses", is met in the source (section 2) and is step 1's first measurement.

## 2. Engine facts

Cited from the `4.7.2-stable` tag of `godotengine/godot` (fetched raw for this draft) or the 4.7 class reference in `F:/Godot/docs/class-ref-xml/doc/classes/`. **Status** says which rows the spike (section 6, step 1) still has to measure.

| Part | Fact | Status | Source |
|---|---|---|---|
| The Dummy driver mixes | `AudioDriverDummy::init` starts a thread when `use_threads` (default true); the thread calls `audio_server_process(buffer_frames, …)` then sleeps `buffer_frames / mix_rate` seconds, so the whole `AudioServer` mix (players, buses, effects, peaks) runs; only the output is dropped | Confirmed from source; measured in step 1 | `servers/audio/audio_driver_dummy.cpp` L49-51, L56-74; `audio_driver_dummy.h` L46 (`buffer_frames = 4096`), L55 (`use_threads = true`) |
| A quiet run's mix comes in bursts | At the default 44100 Hz (`audio_server.h` L170) the thread mixes 4096 frames, 92.9 ms of audio, at once, then sleeps 92.9 ms plus however long the mix took: audio advances in about 93 ms steps of wall time, slightly slower than real time | Source; the burst period and drift unmeasured | as above; `audio_server.cpp` L1531 (`buffer_size = 512`, so a burst is 8 mix steps) |
| Who gets the Dummy driver | `--headless` sets the Dummy driver (`main.cpp` L1505-1508), so gdtests and scratch runs mix as a quiet run does; the server passes `--audio-driver Dummy` to a quiet run (decision 10); a recording always takes it, unthreaded | Confirmed | `main/main.cpp` L1505-1508, L2829-2833 |
| A recording mixes per frame | With `--write-movie`, the Dummy driver runs without its thread (`set_use_threads(false)`, `main.cpp` L2832) and `MovieWriter::add_frame` mixes `mix_rate / fps` frames each iteration (`movie_writer.cpp` L244), 800 at the AVI writer's 48000 Hz (`modules/jpg/movie_writer_mjpeg.cpp` L273, setting default `movie_writer.cpp` L151) and 60 fps; `add_frame` runs at the end of the iteration (`main.cpp` L5149-5152). Audio is locked to frames: 1/60 s of audio a frame, however slowly the game renders | Confirmed from source | as cited |
| Peaks | `_mix_step` sets each bus channel's `peak_volume` to the largest sample of the step just mixed, after the bus's volume and mute (and solo) are applied, in dB (`linear_to_db(peak + 1e-10)`); a channel with no audio for `audio/buses/channel_disable_time` (2 s) goes inactive and reads `AUDIO_MIN_PEAK_DB`, -200 | Confirmed from source | `servers/audio/audio_server.cpp` L614-660, L1527-1528; `core/math/audio_frame.h` L49-50 |
| A peak is one step's | Each `_mix_step` (512 frames, 11.6 ms) overwrites the peak, so a read sees only the last step mixed: in a quiet run the last of a burst's 8 steps, so a sound that ends inside a burst's first 81 ms can leave no trace in any read; in a recording one or two steps a frame | Source; how often a short sound is missed is step 1's | `audio_server.cpp` L287-289, L650 |
| A child bus's peak survives a muted Master | A bus's peak is computed with its own volume and mute before its send adds it to the target (Master), so with Master muted (the bridge's `mute`, delve's `--nosound`) Master reads -200 and `Effects` still reads its level | Confirmed from source | `audio_server.cpp` L598-670 |
| Effects are pre-fader | Bus effects (an `AudioEffectCapture` included) process the buffer before volume and mute | Confirmed | `audio_server.cpp` L566-596, L624-634 |
| A voice | `play()` instantiates a new `AudioStreamPlayback` and appends it to the player's list (`play_basic`); `get_stream_playback()` returns the last one, `has_stream_playback()` whether any exists, paused or not; past `max_polyphony` the oldest is stopped | Confirmed | `scene/audio/audio_stream_player_internal.cpp` L140-179, L87-92, L331-337; `AudioStreamPlayer.xml` L28-37 |
| `playing` is false while paused | `is_playing` is true only while a playback's state is `PLAYING`; a paused playback (`FADE_OUT_TO_PAUSE`, `PAUSED`) reads false, while `has_stream_playback` stays true and `stream_paused` reads true | Confirmed | `audio_server.cpp` L1443-1460, L1409-1427; `audio_stream_player_internal.cpp` L287-294, L191-197 |
| A voice ends when the mixer runs out | The mixer marks a playback for deletion when its `mix` returns fewer frames than asked and deletes it in the same step; the player's next internal process drops it and emits `finished` (not emitted on `stop()`, nor on leaving the tree) | Confirmed | `audio_server.cpp` L426-440, L534-538; `audio_stream_player_internal.cpp` L66-85; `AudioStreamPlayer.xml` L101-103 |
| The tree's pause pauses voices | A player that cannot process on `NOTIFICATION_PAUSED` pauses its playbacks (`set_stream_paused(true)`), and resumes them on `NOTIFICATION_UNPAUSED`; a `PROCESS_MODE_ALWAYS` player keeps playing. The mixer skips paused playbacks, so their position does not advance | Confirmed | `audio_stream_player_internal.cpp` L118-136; `audio_server.cpp` L397-401 |
| A freed player | `NOTIFICATION_PREDELETE` stops every playback; leaving the tree pauses them | Confirmed | `audio_stream_player_internal.cpp` L103-116 |
| 2D and 3D players | `AudioStreamPlayer2D.play` creates the playback at once and starts it at the next physics tick; `is_playing` reads true meanwhile. Its volume comes from its distance to the listener each physics tick: beyond `max_distance` it plays at zero volume, still `playing`. `AudioStreamPlayer3D` does the same, its `max_distance` of 0 meaning no limit | Confirmed | `scene/2d/audio_stream_player_2d.cpp` L59-77, L124-171, L240-279; `scene/3d/audio_stream_player_3d.cpp` L458-470, L677-678 |
| `time_scale` | `Engine.time_scale` does not change how fast audio plays; `AudioServer.playback_speed_scale` does, independently | Confirmed from docs | `AudioServer.xml` L389-391 |
| Finding players | `Node.find_children(pattern, type, recursive, owned)` matches `type` by `is_class`, and with `owned` true (the default) skips nodes without an owner, which every player made in code is (delve's `Sounds` players, `Sounds.cs` L41-48) | Confirmed | `Node.xml` L301-315 |
| An empty player | `get_stream_playback()` on a player with no playback fails with an error message ("Player is inactive…"), which would reach the error feed every frame | Confirmed | `audio_stream_player_internal.cpp` L335-337 |
| Web only: samples | A `Sample` playback is not mixed by the `AudioServer` (no peaks); the default playback type is `Stream` on every platform but Web | Confirmed | `audio_server.cpp` L403-405; `ProjectSettings.xml` L480-487 |
| The driver's name | `AudioServer.get_driver_name()` says which driver runs (the quiet-run itest already reads it) | Confirmed | `AudioServer.xml` L121-125; `tests/GodotMcp.IntegrationTests/QuietTests.cs` L21-33 |

## 3. Design

Two track kinds join the watch's `tracks` (decision 29 lists properties, expressions, signals and monitors; the ruling for this design amends that list). Everything else (`start`, `stop`, `run`, the window, `options.call`, paused frames, deadlines, the one-watch-per-session rule, `batch_drive`) is the watch's, unchanged.

### The tracks (questions 1, 2, 3)

```
tracks: {
  ...,
  audio: {
    players?: {under?: "<node>"},          // voices of every AudioStreamPlayer, 2D and 3D at or under the node (default the root)
    buses?: ["Effects", "Music"] | ["*"],  // bus levels and settings, by name; "*" is every bus
    thresholdDb?: -60                      // a bus counts as heard above this peak (-100 to 0, default -60, Godot's channel_disable_threshold_db default)
  }
}
```

At least one of `players` and `buses`. The server checks the shape and ranges; the bridge refuses an unknown bus name ("no audio bus 'Sfx'; the buses are Master, Effects, Music") and an `under` node that does not exist, with the not-found text every tool uses.

**Voices.** At `start` the bridge finds the players under `under` with `find_children("*", <class>, true, false)` for each of the three classes (owned false, so players made in code count), the bridge's own nodes left out, and follows players that enter the tree during the window through `SceneTree.node_added`, filtered by class (question 3). Each sample (the watch's `process_frame`, decision 29) reads each player: `has_stream_playback()`; when true, the instance id of `get_stream_playback()` (the Ref dropped at once, so no playback is kept alive) and `stream_paused`. It records:

- `start` when the latest playback's id differs from the last seen one: one per `play()`, a polyphonic restart included (two `play()` calls in one frame read as one start; `voiceCounts` says so only by the player's `finished` count);
- `pause` and `resume` when `stream_paused` flips with a playback held;
- `end` when the player holds no playback any more, with `reason`: `finished` when its `finished` signal (connected per player) fired since the last sample, `freed` when the player was freed, else `stopped`.

A `start` carries the stream (`resource_path`, or `{class}` for a built-in or generated stream: an `AudioStreamRandomizer`'s pick is not visible), the bus, `max_polyphony` when above 1, and for a 2D or 3D player its global position.

**Buses.** Each sample reads, for each named bus, the largest of `get_bus_peak_volume_left_db` and `_right_db` over its channels, plus `volume_db` and `is_bus_mute`; the summary is built as the watch builds a monitor's (decision 29: the bridge summarises, the server shapes).

### What `start` answers (question 1)

The watch's `start` reply already gives each track's resolved form; for audio that form is the snapshot the survey asked for (L141), so no separate read tool is needed: `audio: {driver, mixRate, bridgeMuted, players: {count, playing: [{path, stream, bus, position}]}, buses: [{name, index, volumeDb, mute, solo, send, effects: [class]}]}`. `watch {action: "run", tracks: {audio: {players: {}, buses: ["*"]}}, window: {frames: 1}}` is the one-call read of what plays now and how the buses stand.

### Result shape (questions 2, 4)

```
audio: {
  voices: [[frame, gameMs, event, path, {stream?, bus?, reason?, position?}]],   // event: start | pause | resume | end
  voiceCounts: {"<path>": {starts, ends}}, voicesDropped?,
  buses: [{name, peakMaxDb, peakMaxAt, heard: [[fromFrame, toFrame]], heardFrames, sampledFrames,
           volumeDb: {first, last, changes}, mute: {first, last, changes}}],
  playersFollowed?, playersSkipped?, warning?
}
```

- `frame` and `gameMs` are the watch's: 0 at the first sample, `startFrame` the process frame there, so voices line up with property changes, signal `events` and `record_mark` frames (which are process frames, ARCHITECTURE.md Recording).
- A `start` at frame N means `play()` ran in frame N-1's processing: the one-frame offset decision 29 documents for property tracks. An `end` is late by up to one mix burst plus a frame in a quiet run (about 93 ms + 17 ms), by the driver's period in a watched run (unmeasured), and by one frame in a recording.
- `heard` lists the ranges of sampled frames whose peak was above `thresholdDb`; `heardFrames` of `sampledFrames` says how much of the window had sound. In a quiet run a range starts and ends on the 93 ms burst grid, and a sound shorter than about 81 ms can be missed (section 2): voices, not peaks, answer whether a sound fired.
- Budgets, as the watch's: at most 300 voice events kept, shared fairly across players (each player's earliest first), `voiceCounts` counting all; at most 50 `heard` ranges per bus, the middle cut with a count; every value already short.

### Paused frames, `frame_control` and the bridge's mute (questions 5, 6)

- **Paused frames.** Voice events are read in paused frames too, as the watch records a signal emitted in one (decision 29, "a signal emitted in a paused frame is still recorded"), so a pause menu's click on a `PROCESS_MODE_ALWAYS` player is caught; bus levels are not sampled in paused frames, as property tracks are not, and the watch's `paused` ranges say which frames those were.
- **`frame_control pause`** pauses every player that cannot process (section 2): the timeline shows `pause` events on the pause's frame and `resume` on the resume's, never an `end`, and their audio does not advance meanwhile. A `PROCESS_MODE_ALWAYS` player plays on in wall time and can end while the game is paused.
- **`frame_control step`** unpauses for its frames, so paused voices resume for the step and pause after it. In a quiet run audio advances by the wall time the step took, in 93 ms bursts, so a one-frame step may move a voice by 0 or 93 ms: stepping through a sound frame by frame needs a recording, where each stepped frame mixes exactly 1/60 s. TOOLS.md says so.
- **`time_scale`** does not change audio speed, so a voice's length in `gameMs` is its length in seconds times `Engine.time_scale`; a watch with audio tracks under a scale other than 1 adds a `warning` saying so.
- **The bridge's mute** (`mute` on a watched run, an attach or an arm; ARCHITECTURE.md Bridge, the `Audio` child) mutes Master, so Master's peak reads -200 while every other bus's still reads (section 2). The start reply's `bridgeMuted` says so, and a watch naming Master under it adds a `warning` pointing at the child buses. The watch never changes the mute, a bus or a player, so decision 10's rule (a quiet run is silenced by the Dummy driver, never by touching the game's buses) and the `mute` option's Master-only mute stand as they are, and a game's own mute (delve's `--nosound`, mute-in-background on an unfocused quiet window) shows as the bus's `mute` track.

### Under Movie Maker recording

A recording's audio is mixed in step with its frames (section 2), so it is the most exact place to watch sound: a voice's `start` and `end` fall on the frames the clip holds them, a `heard` range covers the frames whose 1/60 s of audio passed the threshold, and `record_mark`'s frame numbers line up with `startFrame + frame`. The game runs uncapped and `gameMs` is clip time (TOOLS.md Recording), so voice lengths read in clip time, as a listener of the clip hears them. Nothing plays out loud whatever `quiet` says, since the Dummy driver is forced; a recording with `mute: true` records silence on Master, and the start reply's `bridgeMuted` says so. The watch adds no `warning` for audio in a recording, unlike its `frame_ms` warning (timeline-watch.md section 4).

### Where it lives, and other tools

- **Bridge**: a new `bridge/godot_mcp_watch_audio.gd` (a `RefCounted` the `Watch` child makes per watch with audio tracks): player discovery and following, the voice poll, the bus reads, the start snapshot. `godot_mcp_watch.gd` calls it from its sampler and its paused-frame branch, a few lines, so the watch module keeps its distance from gdlint's 1000 lines.
- **Server**: `WatchTracks.cs` gains the `audio` record (`WatchAudioTrack`: `Players {Under}`, `Buses`, `ThresholdDb`) and its checks; `WatchTimeline.cs` (or `WatchAudioTimeline.cs` beside it, if it nears the length limits) the voice budget, fair shares and the `heard` cut, unit-testable in C#.
- **Annotations**: unchanged; the watch is already destructive with a `read` class override (decision 29), and audio tracks run no game code beyond native getters.
- **`batch_drive`, `wait_for`, `capture_frames`, the error feed**: as the watch has them; no new `wait_for` condition in the first version (question 7). The bridge reads `has_stream_playback()` before `get_stream_playback()`, so no read logs an error into the feed.

## 4. Costs and risks

- **Per frame.** One native call per idle player, three per player holding a playback, two per bus channel pair; the spike measures them (the watch's spike measured 4.6 µs a C# property read and under 0.5 µs a monitor, timeline-watch.md section 8). The cap is 200 players and 16 buses; past 200, players found or followed are counted in `playersSkipped`. The watch's own 2 ms-a-frame `warning` covers a slow sampler.
- **Following.** `node_added` costs one class check per node added anywhere while the watch runs. A screen that rebuilds hundreds of nodes a frame pays it each time.
- **Wire.** Voices are bounded at 300 events and buses at 50 ranges each, so an audio track adds a few kilobytes at most to the watch's 40000-character result.
- **What a careless agent can do.** Watch `under` the root with `players` in a game that spawns a player per bullet: the 200-player cap and the event cap hold, and the result says what it dropped. Read Master under the bridge's mute and conclude nothing played: the warning names the child buses. Read `heard` in a quiet run as proof a short sound did not fire: TOOLS.md says voices answer that, peaks only say a bus was loud. None of it changes the game: the track only reads.
- **Misleading by design.** A voice beyond a 2D or 3D player's `max_distance` is a `start` with no level; a voice on a muted bus is a `start` too. The voice says the game asked for the sound; the bus says whether it reached the mix.

## 5. Alternatives considered

- **A new `get_audio` read tool** (the survey's shape, tugcantopaloglu's `game_get_audio`): a snapshot at the call. It misses any sound that starts and ends between two calls, which is most effects (delve's blow, heal and downing, `FightSoundsScratch.cs` L84-95), and it cannot line a sound up with the input or signal that caused it. The watch's start reply gives the same snapshot (question 1 (b) keeps it as a separate tool).
- **No new track kind, recipes only**: property tracks on named players' `playing` and expression tracks on `Engine.get_singleton("AudioServer").get_bus_peak_volume_left_db(i, 0)`. It works once the watch lands, for free; but `playing` reads a pause as a stop and a polyphonic restart as nothing, fire-and-forget players cannot be named ahead, the stream of a voice is not reported, and a bus is named by index. Kept as TOOLS.md's fallback for a game with more than 200 players.
- **A `wait_for {audio}` condition** (the survey's second shape): one sound, one wait. For a named monophonic player `wait_for {node, property: "playing", equals: true}` already does it; the rest is the watch's start, act, stop. Question 7.
- **An `AudioEffectCapture` on each watched bus** for exact levels: it hears every mixed frame, so no short sound is missed. But it changes the game's bus layout during the watch (a game that saves `generate_bus_layout()` would save it), it is pre-fader (section 2), so a muted bus still shows level, and it copies every frame of audio on the main thread. Question 4 (b).
- **Listening to the recording afterwards**: ffmpeg's `silencedetect` or `ebur128` on the clip's audio. Only in a recording, only after the run ends, and with no player, stream or bus named. A game that wants a loudness check can still run it on a clip.
- **Hooking `play()`**: GDScript cannot intercept a native method, and a game-side hook is the hand-written sampler delve already has.

## 6. Steps

Each step lands with its tests, docs and changelog bullet, and passes the build, `gdlint`, `gdcomplexity`, `gdtest`, the unit tests and its itest class. Steps 2 and 3 reuse the summary and budget code of the watch's step 4 (monitor tracks, 42f1184).

1. **Spike** (no bridge edit; copies of InputProbe through `run.ps1 drive`, sampler nodes installed by `run_script`, sounds built in the script as `AudioStreamWAV` PCM tones on a bus added with `AudioServer.add_bus`): (i) in a quiet run, a 250 ms and a 50 ms tone: the frame `has_stream_playback` turns true and false against the tone's length, `finished`, and how many frames read Effects above -60 dB; the Dummy thread's burst period and drift over 60 s; (ii) the same in a watched run with `mute: true` (Master at -200, Effects reading); (iii) the same in a recording: end frames exact to one frame, peaks per frame; (iv) `frame_control` pause, a 1-frame and a 10-frame step, resume: `playing`, `stream_paused` and position each frame, and a `PROCESS_MODE_ALWAYS` player beside; (v) a `max_polyphony = 2` player played twice 100 ms apart: the playback id changes and `playing` does not; (vi) cost a frame of polling 200 idle and 20 playing players and 16 buses; (vii) a 2D player beyond `max_distance`; (viii) a fire-and-forget player added by `node_added`. Results into section 2's Status column, and the 93 ms and 81 ms figures of section 3 replaced by measured ones.
2. **Voice tracks**: `tracks.audio.players` with `under`, discovery and following (per question 3), the voice poll, `start`/`pause`/`resume`/`end` with reasons, paused-frame reads (per question 5), the start snapshot, `voiceCounts`, the fair-share budget. Tests: `tests/bridge/test_watch_audio.gd` (gdtest: headless runs the Dummy driver, so a generated tone on a player gives a real `start` and `end`; polyphonic restart; a freed player's `freed`; a paused player's `pause` without `end`); unit `WatchAudioValidationTests` (shape, `under` without `players`, ranges) and `WatchAudioTimelineTests` (fair shares, counts, the cut); itest `WatchAudioTests` on the shared InputProbe session (a tone played by `click` on a button whose handler plays it, `start` then `click` then `stop`; a pause across `frame_control`; a removed bus restored in cleanup), in the `time` group of `run.ps1` `$itestGroups`, with an `$itestRules` row for `godot_mcp_watch_audio.gd`. Docs: TOOLS.md's watch section (audio tracks, the offsets, the quiet-run caveat), the "Which tool for which job" row "Check that a sound played", SKILL.md, ARCHITECTURE.md (the Bridge row, the watch's row), the glossary terms below, README (a new capability), CHANGELOG.
3. **Bus tracks**: `buses`, `thresholdDb`, the peak summary and `heard` ranges, `volumeDb` and `mute` change points, `bridgeMuted` and its warning, the `time_scale` warning. Tests: `WatchAudioTimelineTests` (ranges, the cut), `WatchAudioTests` (Effects heard while a tone plays, unheard at volume 0 and when muted, Master under `mute`), and a recording case in `RecordingTests` (the `recording` group: a 500 ms tone's `start` and `end` 30 frames apart, give or take one, and its `heard` range).
4. **After the release that carries them**: delve's `docs/DEVELOPMENT.md` L88 (a quiet run is not deaf to a watch) and the scriptable halves of `docs/manual-tests.md` L47, L51 and L52 rewritten through `godot-mcp-docs-sync`; replacing `FightSoundsScratch`'s and `MusicScratch`'s samplers with a watch is the game's choice, filed as a suggestion in its plan. Then the rulings move into `docs/DECISIONS.md` and this file is archived.

## 7. Open questions

Ruled by the owner: 1 (a), voice and bus track kinds of the `watch`; 3 (a), players found at start plus those added during the window. Settled by the recommended option under CLAUDE.md's autonomy rule: 2 (a), voice events in their own `audio.voices` list; 4 (a), peaks read each frame as `heard` ranges plus volume and mute changes; 5 (a), voices read in paused frames, levels not sampled; 6 (a), the bridge's Master mute reported as `bridgeMuted` with a warning; 7 (a), no wait on a sound in the first version. Every question is ruled.

1. **Where audio observation lives.** (a, recommended) Two track kinds of the `watch`, `audio.players` and `audio.buses`, with the start reply as the "what plays now" snapshot. How: section 3; `watch run` with a one-frame window is the one-call read. Worst case: a plain "what is playing and how are the buses set" question goes through the watch's vocabulary (tracks, a window) and waits for GMCP-19's steps 2 to 4. (b) The tracks of (a) plus a `get_audio` read tool for the snapshot. How: a `RuntimeTools` partial calling the same bridge module once. Worst case: a second tool for the same reads, and agents reaching for it to check a short sound that has already ended between calls. (c) A standalone `get_audio` tool only, as the survey wrote it. Worst case: delve's blow, heal and downing (each well under a call's round trip) are never seen, which is the need. (d) No code: TOOLS.md recipes with property and expression tracks. Worst case: pauses read as stops, polyphonic restarts vanish, fire-and-forget players cannot be named, and no voice says its stream.
2. **What a voice reports.** (a, recommended) `start`, `pause`, `resume` and `end` (with `finished`, `stopped` or `freed`) per player, the stream, bus and position on `start`, in an `audio.voices` list of its own. How: the playback-id poll of section 3. Worst case: to line a sound up with a signal the agent reads two lists by frame number, as it does for property changes. (b) The same events merged into the watch's signal `events` list as pseudo-signals (`audio:start`). How: one list in emission order. Worst case: a busy fight's sounds take the signal list's fair share and push out the emissions the watch was started for. (c) `playing` changes only. Worst case: a pause reads as a stop and a polyphonic restart as nothing, delve's two-voice players' case.
3. **Which players.** (a, recommended) Every `AudioStreamPlayer`, 2D and 3D at or under `under` at start, plus those added during the window, followed through `SceneTree.node_added` filtered by class. How: one class check per added node. Worst case: a screen that rebuilds hundreds of nodes a frame pays a check per node, and a bullet-hell's per-bullet players hit the 200-player cap. This differs from decision 29's signal tracks, which rejected `node_added`: a signal track connects a handler per node and reconnects on every rebuild, where this only tests the class of an added node. (b) Resolved at start only, decision 29's signal rule. Worst case: a fire-and-forget player made for one sound, the common Godot pattern, is never seen. (c) Named players only (`players: [paths]`). Worst case: the agent must know every player's path, and delve's `Sounds` children, one per kind (`Sounds.cs` L12-13), are 17 paths to list (`SoundKind`, `Scripts/Screens/SoundTable.cs`).
4. **Bus levels.** (a, recommended) Read each bus's peak every frame (`get_bus_peak_volume_*_db`) and summarise it as a maximum, `heard` ranges above `thresholdDb`, and `volumeDb`/`mute` change points. How: section 3. Worst case: in a quiet run a sound under about 81 ms can fall between reads, so `heard` can miss a short click a voice `start` shows. (b) Insert an `AudioEffectCapture` at the end of each watched bus's chain for the watch's length and take the exact peak of everything mixed since the last frame. Worst case: the game's bus layout changes while the watch runs (a settings screen that saves `generate_bus_layout()` saves the effect), the level is pre-fader so a muted bus reads loud, and every frame of audio is copied on the main thread. (c) No levels: bus settings (`volumeDb`, `mute`, `solo`) as change points only. Worst case: "Effects at 0 silences it" is checked by the slider's setting, never by what reached the mix.
5. **Audio in paused frames.** (a, recommended) Voices read in paused frames (events, as signals are recorded), bus levels not sampled (as property tracks are not). How: the paused-frame branch calls the voice poll only. Worst case: a pause menu's music level is not in `heard`; the agent watches it with the game resumed or reads the start snapshot. (b) Both read in paused frames. Worst case: one track kind breaks the watch's rule that paused frames are not sampled, and a `heard` range spans frames the watch lists as paused. (c) Neither. Worst case: a pause menu's click sound, on a `PROCESS_MODE_ALWAYS` player, is never seen.
6. **The bridge's own Master mute.** (a, recommended) Report it (`bridgeMuted`) and warn when a watch names Master under it, changing nothing. How: the `Audio` child's `muted`. Worst case: an agent watching only Master in a muted watched run sees silence and must re-run naming a child bus. (b) Lift the mute for the watch's length. Worst case: a watched run or an attached game the user asked to keep silent plays sound out loud. (c) Refuse Master as a bus while the bridge mutes it. Worst case: a refusal for a read that still answers its settings half.
7. **A wait on a sound.** (a, recommended) None in the first version: `watch start`, the action, `watch stop`, or `wait_for {node, property: "playing", equals: true}` for a named player. How: no new condition. Worst case: "wait until the hit sound fires, then screenshot" takes a watch plus a `wait_for {frames}`, or a poll. (b) `wait_for {audio: {stream | player | under}}`, met at a voice `start`. Worst case: a second voice detector in the `Time` module, kept in step with the watch's. (c) A `batch_drive` assertion `{assert: "voice", stream}` over a watch's held result. Worst case: an assertion that reads another step's result, which `batch_drive` does not do today (every step's arguments are fixed and checked before any step runs, ARCHITECTURE.md's `batch_drive` row).

New terms for the glossary when the design is adopted:

- new term: Voice (one playback a player started with `play()`; a polyphonic player holds several)
- new term: Fire-and-forget player (a player made to play one sound and freed when it ends)
- new term: Heard range (the frames a bus's peak passed the watch's `thresholdDb`)
- new term: Mix burst (the 4096 frames the Dummy driver mixes at once, about 93 ms, in a quiet or headless run)
