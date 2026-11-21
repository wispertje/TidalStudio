# Tidepool Muse — AI part suggestions: plan and progress

Read this first when continuing. Keep it up to date after every step.

## Goal (from the user)
A deep learning model that suggests new parts (melody, bassline, drums, chords, a new part of the track) as Tidal
code, using only the samples in `C:\Data\Tidal\samples` (made by the Tidepool synths). Runs with TidalStudio:
- a **training panel** in TidalStudio (data, train by ear = like/dislike, fine-tune, versions);
- **live use**: Ctrl+Shift+G opens a popup at the cursor, pick a part, audition, Enter pastes the code into the editor.

## Architecture (decided)
- The model never writes Haskell. It generates **music tokens**; C# writes the Tidal code.
- **Context** comes from Tidal itself: the helper `tsDump a b` / `tsDumpNow n` (defined at runtime via
  `GhciHost.QueryAsync`, see `TidalDump.cs`) queries the playing patterns (`sPMapMV tidal`, `queryArc`) and prints
  `EV|pattern|start|dur|s|n|note|gain|legato` lines. Works live and offline (import of .tidal files through a
  second, silent ghci).
- **Sample catalog** (`SampleCatalog.cs`) scans the samples folder: pitched (`name_NN_c4.wav`), drums
  (`kitbd/kitbd_NN.wav`), loops (`name_NN_A.wav`, bpm from length). Roles from name rules + user overrides.
- **Tokenizer** (C#, single source of truth, vocab exported as JSON) is used for MIDI import, Tidal import and
  generation. Everything is transposed to C (major/minor mode token).
- **Training** in Python/PyTorch (`ml/` in the repo, venv in `%LOCALAPPDATA%\TidalStudio\ml\venv`), started by
  TidalStudio; progress as JSON lines on stdout. Model: small GPT (~10–20M params). Export to ONNX.
- **Inference** in TidalStudio with ONNX Runtime + grammar/key/range-constrained sampling.
- **Train by ear**: ratings stored as JSONL; instant taste model (C#, logistic on features) reranks candidates;
  DPO fine-tune of the generator in Python every ~50 ratings.
- **Rule-based generator** = baseline + fallback when no model is trained.

## Steps
1. [x] Core: SampleCatalog, music types, theory (key/chords), TidalWriter, dump parser (`#EV|` lines — `EV|`
       was eaten by GhciHost's prompt filter), tokenizer + grammar, MIDI reader, Dataset writer, rule generator,
       TidalRunner (silent ghci), MlCli (`--ml roundtrip`, `--ml dataset`). Round trip 24/24: written code plays
       exactly the intended notes in Tidal.
2. [x] App: MuseService (live context via tsDumpNow, audition on d14–16), SuggestPopup (Ctrl+Shift+G, Muse menu),
       MuseTrainerWindow (Ctrl+Shift+M: sounds & data, train by ear A/B, training runs, versions), MainWindow
       paste (InsertSuggestionAsync). Settings: AppSettings.Muse.
3. [x] Python: venv at %LOCALAPPDATA%\TidalStudio\mlenv (torch 2.6 cu124, works on the 4070). ml/train.py,
       ml/dpo.py, ml/muse/{model,data}.py, ml/export.py. Pretrained 24k steps (4h13m, val 0.619) →
       models61117-094322-pretrain; fine-tuned on own data (400 steps, own-share 0.5, val 0.629) →
       models61117-135649-finetune (newest, so active). Both have model.onnx + model-step.onnx.
4. [x] ONNX inference with KV cache (model-step.onnx), CPU default (DirectML slower here), `--ml generate`
       check: 0.4–2 s per 4 ideas; all generated code compiles in Tidal.
5. [x] "New part" (Suggester.NewParts: missing roles + a variation, as a do-block).
6. [x] Tests (159 pass), README section, Muse self-test with the final model (scratchpad\muse2, ML copy in
       scratchpad\ml2): 0.2–1.5 s per 4 ideas, paste OK; screenshots sent to the user.
ALL STEPS DONE. Optional later: more own music → re-fine-tune; DPO after ~50 ratings (automatic).
## Log
- 2026-11-17: plan written; tsDump helper prototyped and verified in ghci (Tidal 1.10.3).
- 2026-11-17 09:40: steps 1–2 done, Python smoke test OK, pretrain data building.
- 10:02: KV cache, CPU inference, self-test with popup+trainer OK, DPO smoke test OK. Waiting for pretraining.
- 10:10: everything except the real model is done (README section added, own-share fine-tune). Pretraining at step 1700/24000, ~0.52 s/step → done ≈13:15. Then: export.py → fine-tune (Muse trainer args: --own-share 0.5 --steps 400 --lr 1e-4) → --ml generate → self-test screenshots → summary.
- 10:50 (auto-continue): pretrain at step 5850 (val 0.716 at 5000), ETA ≈14:05. Previewed checkpoint 5000: code OK, bass follows chords, melodies repetitive → added Suggester.Variety prior + count+2 oversampling. Next unchanged (wait for pretraining).
- 12:47 (auto-continue): pretrain at step 17350/24000 (val 0.636 at 17000), ETA ≈14:00. Waiting; next unchanged.
- 14:02: pretraining done (val 0.619), export.py OK (cache diff 1e-5), fine-tune 35 s (val 0.629). --ml generate: all compile, 0.2–2.4 s (new part 4 bars 6 s). Self-test with final model OK, screenshots sent. Everything done.
