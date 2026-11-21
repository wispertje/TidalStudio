<img src="tools/logo/logo.svg" width="96" alt="TidalStudio logo">

# TidalStudio

A desktop live-coding editor for [TidalCycles](https://tidalcycles.org), built with C#, .NET 10 and Avalonia 12.
It runs Tidal code the same way the VS Code extension does and adds Strudel-style visual feedback.

## Features

- **Editor** with Tidal syntax highlighting, including separate colours for mini-notation inside strings.
- **Tabs**: open as many files as you like; double-clicking a file in the explorer opens it in a new tab (or
  switches to it). `Ctrl+W` closes a tab (middle-click works too), `Ctrl+Tab` / `Ctrl+Shift+Tab` switch tabs, and
  right-click a tab for Close others / Close all. Modified tabs show a dot; open tabs are reopened next time.
- **Autocomplete** while typing (or with `Ctrl+Space`):
  - Tidal functions with their type signatures, read from your installed Tidal when it starts, plus short
    descriptions of the common ones. After `#`, effects such as `room` and `lpf` are preferred.
  - Sample names inside `s "…"` / `sound "…"`, from your Dirt-Samples folder and every folder your SuperCollider
    `startup.scd` loads with `~dirt.loadSoundFiles("…")` (with the number of samples in each), plus SuperDirt's synths.
  - Inline visuals after `-- _`, then their options (`-- _pianoroll cyc` → `cycles=`), then the allowed values
    (`rows=` → `pitch` / `sound`).

  `Enter` or `Tab` accepts a suggestion; `Enter` after a word that is already complete just starts a new line.
  Turn it off with View ▸ Autocomplete while typing.
- **Running code** like in VS Code:
  - `Shift+Enter`: run the current line
  - `Ctrl+Enter`: run the block around the cursor (lines between blank lines), or the selection
  - `Ctrl+.` or `Ctrl+Alt+H`: hush
  - `Ctrl+1` … `Ctrl+9`: mute or unmute `d1` … `d9`

  The code that ran flashes green (or red on an error). Errors are marked on the line they came from and shown in the
  status bar and console.
- **Mini-notation highlighting**: the characters of the mini-notation that are sounding right now light up, one
  colour per pattern. You can override the colour with `# color "orange"` or `# color "ff8800"` (hex without `#`,
  because `#` isn't allowed inside Tidal strings). Style: background, outline or underline (View ▸ Highlight style).
- **Visualizer panel**:
  - **Cycle progress**: where Tidal's clock is: position in the cycle, beat, cycle number, cps/bpm, a lane per
    pattern with its onsets, and progress through an N-cycle phrase.
  - **Punchcard / Pianoroll**: scrolling rows per sound or per pitch. Options include cycles shown, playhead
    position, labels, colours, vertical layout and folding.
  - **Spiral**: one turn per cycle, the past spiralling inward. Options include size, thickness, stretch, playhead
    and rotation.
  - **Pitchwheel**: sounding notes on a one-octave circle (flake, polygon or dots), with trails.
  - **Scope**: audio waveform with a rising-edge trigger.
  - **Spectrum**: FFT analyser with a log or linear axis, dB range, smoothing and peak hold.
  - **Rings**: one ring per pattern (or per sound) with the onsets of the current cycle joined into a shape, so
    polyrhythms and euclidean rhythms read at a glance; a hand sweeps round.
  - **Parameter plot**: one control (lpf, pan, speed, room, …) over the last few cycles, a line per pattern, so
    modulation like `# lpf (range 200 2000 sine)` becomes visible. Steps, lines or dots; optional log scale.
  - **Channels**: a mixer-like bar per pattern that jumps on every hit (height = gain) and falls back, with its sounds
    and hits per cycle.
  - **Stereo field**: each hit as a dot by pan (left to right) and pitch, gain, orbit or pattern; shows `jux` and
    `# pan sine` nicely.
  - **Spectrogram**: scrolling frequency waterfall of the audio, with inferno, ocean or mono colours.
  - **Level meter**: peak and RMS bars with peak hold, plus a loudness history.

  Every tile has an ⚙ options panel and a resize grip. **behind code** also draws that visualizer behind the
  editor text.
- **Inline visuals** (like Strudel's `_pianoroll()`): add a comment at the start of a line in a block:

  ```haskell
  d3 $ n "0 [3 7] <5 10> 12" # s "superpiano"
    -- _pianoroll cycles=2 height=120
  ```

  This works with every visualizer: `_punchcard`, `_pianoroll`, `_spiral`, `_pitchwheel`, `_scope`, `_spectrum`,
  `_cycle`, `_rings`, `_params`, `_channels`, `_stereo`, `_spectrogram` and `_meter` (e.g. `-- _params param=pan`).
  Options go after the name as `key=value` (the same keys as in the ⚙ panel, plus `width` and `height`).
  An inline visual only shows the patterns of its own block.
- **File explorer and working directory**: the explorer (View ▸ Show file explorer, `Ctrl+Shift+E`) shows the
  working directory. Double-click a file to open it. The **+** and new-folder buttons create a Tidal file or folder in
  the selected folder; right-click for New Tidal file, New folder, Delete, Set as working directory, Show in file
  manager and Copy path. **Delete** (or the Delete key) asks first, moves the file or folder to the Recycle Bin, and
  closes tabs of files inside it. The working directory is also ghci's current directory (changed live, so patterns keep
  playing), where `BootTidal.hs` is looked for if there is none next to the file, and where file dialogs open. Set it
  with File ▸ Set working directory… or the folder button in the explorer.
- **Loading screen** while Tidal boots at launch. GHC itself needs about 5 seconds to start on Windows (even
  `ghc -e "print 1"` takes that long), so this can't be made much faster; the editor is usable as soon as the screen
  closes, or press Esc to skip it.
- **Tidepool sample synth** (Synth ▸ Tidepool sample synth…, `Ctrl+Shift+Y`): design a sound and save it as samples
  SuperDirt can play, so it works in Tidal as `s "name"`.
  - Three wavetable oscillators (Basic, PWM, Harmonics, Formant, Sync, Digital, Fold, Noise) with position morphing,
    octave/semi/fine, unison up to 9 voices with detune and stereo spread, and a warp mode each (bend, sync, fold,
    crush).
  - A resonant filter (low/high/band-pass, notch) with drive, envelope and key tracking; amp, filter and mod
    envelopes; two LFOs; drive, chorus (with rate and depth, up to the wide, slow 80s ensemble sound), ping-pong delay,
    reverb and **tape** (wow and flutter, saturation and a softer top end). The mod envelope and LFOs can drive pitch,
    wavetable position, cutoff, volume, pan or warp.
  - A **signal flow** diagram above the controls shows how the parts are connected: oscillators → mixer → filter →
    amp → drive, chorus, delay, reverb, tape → output, with a wire from every envelope and LFO to what it modulates.
    Bypassed parts are dashed; click a block to jump to its controls.
  - Factory presets in five groups: basics (leads, pads, basses, a bell, a kick and a hat), **acid techno** (303-style
    saw/square lines, accent, squelch, rubber, sub, sync scream, ringing, distorted, a sweep to `chop`, dub-delay lead,
    minor and fifths stabs, bleep) and **retro**
    (chiptune square/pulse/arp, 80s brass, PWM strings, polysynth, analog bass, FM e-piano, rave hoover, rave organ,
    sci-fi sweep, lo-fi keys) and **synth pop** (brass and major-chord stabs, pluck, arp bleep, clav pluck, octave
    bass, square sub, glass bell keys, lush pad, synth choir, vibrato lead) and **synthwave** (80s retro, sound names
    `wave…`: ensemble chorus pad, slowly swelling sunset pad, string machine, poly brass, a sax-like reed lead, neon lead,
    chorus and driving basses, tape-worn keys, dream arp, glass pluck). Acid sounds are short notes: play them with `# legato 1` or `# cut 1`. Randomise, auto-preview while you turn knobs,
    and a keyboard to audition any note.
  - **Save to SuperDirt** renders each note in the Notes list (Tidal note names; `c5` is note 0) to a 24-bit WAV in
    `<samples folder>/<name>/`. With several notes, `n` picks them in order: `s "pad" # n "0 .. 5"`. With one note,
    transpose with `# note`. Your sound is also kept as a preset (★ in the list) to tweak later.
  - After saving, SuperDirt loads the new sound: one started by TidalStudio loads it immediately; one started
    elsewhere (e.g. the SuperCollider IDE) is restarted, after which TidalStudio runs it (option "Restart SuperDirt
    after saving"; turn it off to get the one-line load command instead). There is also a button that makes your `startup.scd` load your samples folder at every start (it
    uncomments the line if it is there, and keeps a backup as `startup.scd.bak`).
- **Tidepool Drums** (Synth ▸ Tidepool Drums…, `Ctrl+Shift+U`): a drum synthesizer that saves a whole kit.
  - Every drum is built from **layers**, each with its own envelope (attack, hold, decay, curve) and graph:
    - **Osc 1**: the body, a wavetable oscillator whose pitch starts *Drop* semitones high and falls to *Pitch* over
      *Drop time* (the kick's punch).
    - **Osc 2**: a second oscillator at a ratio of osc 1's pitch, mixed in and/or frequency-modulating osc 1 (FM).
    - **Noise**: white/pink/brown, filtered, with repeated hits for claps.
    - **Metal**: inharmonic square waves through a band-pass, for hats, cymbals and cowbells.
    - **Click**: the attack transient.

    Then **drive** (soft, hard, fold, crush), a **filter** with its own envelope, and a **room**. A signal-flow
    diagram shows the layers and what is on; click a block to jump to it.
  - Models (kick, snare, tom, clap, closed/open hat, cymbal, rim, cowbell, zap, shaker) are starting points: *Start
    from* sets a drum up as one of them, *Reset* goes back to it, and *Randomise* (`Ctrl+R`) varies it. There are twelve
    factory kits (analog, punchy, hard techno, acid house, lo-fi, electro; three acid techno kits: warehouse, rumble with a separate `rb` rumble drum, and 303 era; three synth pop kits: gated 80s with gated-reverb snare and big toms, electro-pop with a tambourine, and minimal wave; and two synthwave kits: `neon`, Linn-style with a huge gated snare and clap, gated toms, tambourine and syndrum, and `nightdrive`, 808-style with a booming kick, cowbell and a snare in a gated room). You can add or remove drums, and ▶ Groove
    plays a two-bar beat with the kit. Kits from the first version are converted automatically.
  - **Saved like the tidal-drum-machines kits**: each drum is its own sound named kit + drum, so a kit called `mykit`
    gives `s "mykitbd"`, `s "mykitsd"`, `s "mykithh"`, … Each drum can have several **versions** (slightly different
    tune/decay/tone, set by Spread) as `n 0`, `n 1`, …: `s "mykithh*8" # n "<0 1 2 3>"`.
  - With "Also save as one kit" the whole kit is also one sound with a drum per index, like Dirt-Samples' `drum`:
    `s "mykit" # n 0` is the first drum (bd), `n 1` the second, …
  - SuperDirt loads the kit right after saving, the same way as Tidepool; "Copy example code" gives a beat to start from.
- **Tidepool Acid** (Synth ▸ Tidepool Acid…, `Ctrl+Shift+J`): a bassline box with accent and slide, for the acid lines
  that single-note samples can't play (a slide glides into the next note without re-triggering it).
  - Eight pattern slots (A–H, keys `1`–`8`) of 1–32 sixteenth steps. The grid is a 13-note piano roll from the key up an
    octave, with rows for octave up/down, **accent** (louder and brighter, building up over consecutive accents) and
    **slide**. Click to place a note (again for a rest), drag to paint, right-click for a rest.
  - *Random* (`R`) writes a new line in the chosen scale (minor, phrygian, dorian, minor pentatonic, major, chromatic),
    *Mutate* (`M`) changes a few steps; shift, transpose, clear and copy-to-next-slot help build variations.
  - The voice: saw or square (with **pulse width**) plus an optional **sub** an octave down and a **detuned** second
    oscillator, into a driven, resonant 4-pole low-pass (cutoff, resonance, env mod, decay, accent, **key tracking**),
    slide time, gate length and swing, then drive, a tempo-synced delay, reverb, **chorus** and **tape** (their sweeps
    fit the loop, so it stays seamless). Nine acid, five synth pop and five synthwave sounds (chorused and detuned
    basses with a sub, a narrow pulse bass, echoing arps) to start from (they keep your patterns), plus seven acid demo patterns and seven synth pop patterns (octave basses,
    a two-bar chord bass, arpeggios). Space plays the pattern as a loop with a moving playhead.
  - **Saved as loops**: every slot up to the last one with notes becomes one seamless loop (the delay and reverb tails
    wrap around) in one sound, so `s "acidloop" # n 0` is pattern A, `n 1` pattern B, … Play them in time with
    `d1 $ loopAt 1 $ chop 16 $ s "acidloop" # n "<0 1 0 2>"` (32-step patterns: `loopAt 2 $ chop 32`), or at the
    box's tempo with `setcps (130/60/4)`.
- **Tidepool FM** (Synth ▸ Tidepool FM…, `Ctrl+Shift+K`): a six-operator FM synth in the spirit of the 80s digital
  keyboards, for the glassy electric pianos, bells, mallets, slap basses and brass that wavetables don't do well.
  - Six sine operators, each with a ratio (×0.5–×16 plus fine, for inharmonic bell ratios) or a fixed frequency, detune,
    level/depth, key scaling and its own ADSR. Nine **algorithms** wire them (tower, two towers, three pairs, fan out,
    organ, …); the diagram shows which operators are heard (carriers) and which modulate, and clicking one jumps to it.
    Operator 6 has a feedback loop (sine → saw → noise).
  - Global brightness (scales every modulator), key rate (higher notes decay faster), a pitch envelope (kicks, brass
    blips), an LFO with vibrato and tremolo, and the same effects as Tidepool. Rendered 2× oversampled, so bright
    sounds don't alias.
  - 28 presets in keys & mallets, bass, brass & leads, pads & bells, percussion, synth pop and synthwave (a chorused
    ballad EP, sunset bells, an FM sax, a night bass and a tape-worn glass pad; sound names start with `fm`: `fmepiano`,
    `fmsolid`, `fmbell`, `fmpopep`, `fmwaveep`, `fmsax`, …). Its effects have the same chorus rate/depth and tape. Saved like Tidepool: one sample per note, `n` picks the octave.
- **Sequencers** (🎹 Sequencer in Tidepool and Tidepool FM, 🥁 Beat sequencer in Tidepool Drums): a separate window to
  try out melodies and beats with the sound you are designing, and get them as Tidal code.
  - A **piano roll** (click to add a note and drag its length, drag to move, right-click deletes; key and scale are
    highlighted) or a **drum grid** (click to add hits, right-click for soft / normal / accent, mute per drum).
    Random, Mutate, shift, transpose, Double and undo/redo help to experiment.
  - It loops with the synth's current sound, rendered the way SuperDirt plays the saved samples (closest rendered note
    + `note`, `legato` / `cut`, `gain`, `swingBy`), and **turning knobs in the synth window is heard while it plays**.
  - The **code** below it (a `do` block with `setcps` and the pattern, on the channel you choose) can be copied, or
    inserted into the current script and run. Save the sound in the synth so SuperDirt has the samples you heard.
    It is written to be read and edited: one bar per cycle (`<[bar 1] [bar 2]>`, or `slow` when notes cross bar lines),
    beats split with `.`, rest runs as `~@n`, notes played together as `[c,e,g]`, repeating drum rows as `[bd ~ ~ ~]*4`.
    A cycle is one bar of 16 sixteenths at `setcps (bpm/60/4)`, so Tidal plays exactly what the sequencer plays.
  - Tidepool Acid has its own step editor; its "Copy code" / "Insert into script" give the loop code for a pattern.
  - **Open sequencers play in time together** (Tidepool Acid included): their loops are mixed on one shared clock, a
    loop you start joins in where it would be had it been playing all along (like a pattern in Tidal), and they share
    one tempo — change the BPM in one window and the others follow; a newly opened one takes the tempo in use.
- **VJ screen** (View ▸ VJ screen…, `Ctrl+Shift+V`): visuals for a performance. Drag the window to the projector and
  press `F11`.
  - **Driven by the music**: what Windows plays (loopback capture: bass, mid, high and beats from the kick), and every
    Tidal event as a hit (kick, snare, hat or note, from the sound name). "Tidal events only" works without audio capture.
  - **Nine scenes**: Liquid, Tunnel, Retro grid, Kaleido bloom, Plasma, Warp stars and Cells (GPU shaders), Spectrum ring
    and Pattern rain (every event as a falling block). **Eight transitions**: crossfade, wipe, iris, dissolve, glitch
    slices, zoom, luma key and flash cut. *Auto* changes scene every 1–32 cycles in time with Tidal, with a random or fixed
    transition, and drifts slowly through the scenes when nothing plays.
  - **Effects**: video-feedback trails, RGB split on the kicks, kaleidoscope, mirror, pixelate, scanlines, beat flash,
    hit rings and sparks with the sound names, seven colour palettes. *Auto FX* varies them with every scene.
  - **The code**: the program that is playing (the latest block of every `dN`), typed in as you run it, with each
    sounding mini-notation token lit up in its pattern's colour. Errors shake in red, `hush` clears it, and the block
    you are typing appears as a dim draft with a cursor. As a side panel or a big centred overlay (`C`).
  - Move the mouse for the controls (they hide by themselves). Keys: `Space` next scene, `1`–`9` scenes, `A` auto,
    `T` transition, `Q` colours, `C` code, `R`/`K`/`M`/`P`/`S`/`B` effects, `↑`/`↓` intensity, `[`/`]` trails, `?` all keys.
- **Muse: AI part suggestions** (Muse ▸ Suggest a part…, `Ctrl+Shift+G`): ideas for a melody, bassline, drums,
  chords or a whole new part that fit what is playing, as Tidal code with your own samples.
  - **Live**: put the cursor where you want the code and press `Ctrl+Shift+G`. Pick with one key (`M` melody, `B` bass,
    `D` drums, `C` chords, `P` new part), `1`/`2`/`4`/`8` bars; *busy* and *surprise* sliders, a sound or kit (or auto).
    `↑`/`↓` choose, `Space` listens on `d14`–`d16` in time with the music, `Enter` pastes the code as a new block
    (`Ctrl+Enter` pastes and runs it), `R` new ideas, `+`/`−` like/dislike, `Esc` closes.
  - **What it knows**: Tidal itself works out what plays (a small helper queries the running patterns), so Muse sees
    the actual notes, key, chords per bar and the kick. It only uses the sounds in your samples folder (the Tidepool
    ones: pitched sounds with their pitch per `n`, drum kits, loops), and writes plain Tidal: `note "<[c ~ ef g] …>"
    # s "poplead" # n 0`, drums as `s "[gatedbd*4, [~ gatedsd]*2, gatedhh*8]"`, a new part as a `do` block.
  - **The model**: a small transformer (≈15M parameters) that writes notes as tokens, never Haskell; C# turns them into
    code, so the code always works. Generation follows rules (in key, in range, the right number of bars) and your taste
    picks the best of more ideas than you see. It runs on the CPU in about a second. Without a trained model the
    suggestions come from music rules.
  - **Muse trainer** (`Ctrl+Shift+M`): *Sounds & data* (the role of every sound, import your `.tidal` files — run in a
    second, silent Tidal — and public MIDI for pretraining), *Train by ear* (A or B? keys `N` next, `A`/`B` play, `1`/`2`
    choose, `3` both good, `0` neither, with optional reasons; every choice updates your taste at once and every 50 ratings
    the model learns from them), *Training* (set up Python with PyTorch, pretrain, fine-tune on your music, learn from your
    ratings, with a live loss chart) and *Versions* (every run is kept; switch back any time).
  - Data and models live in `%LOCALAPPDATA%\TidalStudio\ml`. Command line: `TidalStudio.exe --ml roundtrip|generate|dataset …`.
- **Render all factory presets** (Synth ▸ Render all factory presets to samples…) saves every preset of the four
  synths as samples in one go (about 125 sounds, ~900 WAVs), plus `mykit`, `acidloop` and `popline` that
  the example scripts use, and loads them into SuperDirt. Sounds that already have samples are left alone. Acid box
  sounds get the names `acidclassic`, `acidslow`, `acidscreamer`, … and the synth pop sequences `seqoctbass`,
  `seqeighths`, … and the synthwave ones `seqnight`, `seqneonarp`, … (118 bpm). From the command line: `TidalStudio.exe --render-presets <samples folder> [--replace]`.
- **Example scripts** (Synth ▸ Example scripts, or the 📄 button in each synth window) open in a new tab and show
  how to play what each synth makes: Tidepool samples (`n` for the octave, `note` for pitch, chords, arpeggios, monophonic
  basses with `cut`), Tidepool Drums kits (per-drum sounds, versions, the combined kit, fills, groove) and Tidepool Acid
  loops (`loopAt`/`chop`, switching patterns, `splice`), a synth pop sketch using the first three, and Tidepool FM
  samples (EP chords, octave basses, mallets, FM percussion), and a **synthwave sketch** with the 80s presets of all
    four (major 7th and add9 chords on the chorus pad, gated drums, octave bass, poly brass, a sax solo, EP and bells,
    the Acid loops). They are also in
  [examples/](examples/). The self-test runs every block of them in Tidal.
- **SuperDirt starts automatically**: at launch and whenever you run code, TidalStudio checks that SuperDirt answers
  (the same handshake Tidal uses) and starts SuperCollider + SuperDirt if it doesn't. The status bar shows
  SuperDirt's state. If your `startup.scd` already starts SuperDirt, that is used as is. When TidalStudio starts SuperDirt it also loads every
  sound in your Tidepool samples folder that `startup.scd` doesn't. A SuperDirt started by TidalStudio is stopped again when TidalStudio closes. Turn it off under SuperCollider ▸ Start SuperDirt
  automatically; SuperCollider ▸ Start SuperDirt starts it by hand, recent files, and settings saved to
  `%APPDATA%\TidalStudio\settings.json`.

## Requirements

- GHC with the `tidal` package (`cabal install tidal --lib`). TidalStudio finds `ghci` on PATH (it re-reads PATH
  from the registry, so a PATH change after login is picked up) or under `C:\tools\ghc-*`, ghcup and similar
  locations. You can also choose it via Tidal ▸ Choose ghci….
- SuperCollider with SuperDirt.
- Windows for the audio visualizers (WASAPI loopback). Everything else is plain Avalonia and should also run on
  macOS and Linux.

## Build, run, publish

```sh
dotnet build
dotnet test
dotnet run --project src/TidalStudio.App -- path/to/file.tidal

# One self-contained TidalStudio.exe (no .NET install needed):
dotnet publish src/TidalStudio.App -c Release -r win-x64 -o publish/win-x64
```

## How it works

```
 editor ──(code, string literals wrapped as (tsctx ID "…"))──▶ ghci + Tidal ──OSC──▶ SuperDirt :57120
                                                                     │
                                                                     └──OSC (same events)──▶ TidalStudio :57130
                                                                                               │
              event store + cycle clock ◀─────────────────────────────────────────────────────┘
                     │
   highlighting, punchcard, pianoroll, spiral, pitchwheel, cycle progress
   WASAPI loopback ──▶ ring buffer ──▶ scope, spectrum
```

- **Boot file.** TidalStudio's `BootTidal.hs` (in `src/TidalStudio.Core/Resources`) is the standard one plus:
  - a second OSC target, so every event also goes to the editor on UDP 57130 as a timestamped bundle;
  - `tsVis`, applied by `p`/`d1…d16`, which copies each event's source positions into a `_ctx` parameter;
  - the `TsCtx` class (`tsctx`), which attaches positions to patterns and leaves plain strings and pattern IDs alone;
  - `color` / `colour`.

  It stays compatible with VS Code and other editors. Without TidalStudio running, the extra UDP packets are simply
  dropped.
- **Which boot file is used.** By default TidalStudio uses `BootTidal.hs` next to the open file (or in a parent
  folder), like the VS Code extension does, and otherwise its built-in one. If that file lacks the additions, the
  console says so. Tidal ▸ Boot file can switch to the built-in file, or write it next to your `.tidal` file.
- **Highlighting.** Before code is sent, each string literal becomes `(tsctx ID "…")`. Tidal shifts the literal's
  mini-notation positions to line `ID + 1`. The editor keeps a text anchor per ID, so positions follow your edits
  until you change the literal itself. If wrapping breaks a piece of code (a string used where `tsctx` has no
  instance), it is run again unchanged.
- **Clock.** Tidal owns the clock. The cycle position is derived from the `cycle`, `cps` and time tag of incoming
  events and extrapolated between them.

## Limitations

- Tidal only sends events about 50 ms ahead, so the future side of the punchcard and pianoroll shows a faint
  repeat of the last cycle rather than the real upcoming events. You can turn this off per tile.
- Audio capture records everything Windows plays, not only SuperCollider.
- Strings with escape characters (`\"`) aren't highlighted.

## Development

The logo, app icon and in-app logo are generated by one script: `dotnet run tools/logo/MakeLogo.cs` writes
`tools/logo/logo.svg`, `src/TidalStudio.App/Assets/logo.png` and `src/TidalStudio.App/Assets/TidalStudio.ico`.

`TidalStudio.exe --selftest <dir> [file.tidal]` starts Tidal, runs every block of the file (or the welcome sample),
sends real Shift+Enter / Ctrl+Enter / Ctrl+. key events, writes `selftest.log` and screenshots to `<dir>`, and exits.
Set `TIDALSTUDIO_SETTINGS` to a separate settings file (for example one whose `BootFilePath` points to a boot file
with the SuperDirt port changed) to test without sound. If another TidalStudio is open it holds UDP 57130, so also
give the test its own `EditorOscPort` and change `57130` in that boot file to match.

`--test-signal` replaces audio capture with a synthetic signal (kick, hats and a rising tone) written straight into
the analysis buffer, so the scope, spectrum, spectrogram and level meter can be checked without playing anything.
