# Port17 — RimWorld 1.7 on iOS (unofficial)

[![Build](https://github.com/Kamisorara/rimworld-ios-port/actions/workflows/m2-host.yml/badge.svg)](https://github.com/Kamisorara/rimworld-ios-port/actions/workflows/m2-host.yml)
[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](LICENSE)

**English** | [简体中文](README.zh-CN.md)

> **Unofficial fan project — not affiliated with Ludeon Studios.**
> Port17 ships no game files and sells nothing. It runs only the files of a copy of RimWorld that **you already own on Steam**; without them there is nothing to run. See [Legal](#legal).

Port17 is an open-source compatibility layer that runs the Windows (Steam) build of **RimWorld 1.7** — Ludeon's Waylith engine, .NET code over a Vulkan renderer — on iPhone and iPad. No jailbreak, and no repackaged game: you copy the files over from your own Steam installation, and Port17 supplies the rest: its own .NET runtime, Vulkan on Metal, touch controls, mod support and a Steam Workshop downloader.

**Current build: 0.4.44** (26 November 2026). The project is in active development; [docs/PLAN.md](docs/PLAN.md) is the round-by-round record.

## Status

Tested on an iPad (M4, iOS 27).

| Area | State |
|---|---|
| Game | Boots to the menu, generates new worlds, runs colony maps, saves and loads — the base game plus the DLCs you own |
| Speed | .NET code is JIT-compiled on the device (see [How it works](#how-it-works)) |
| Controls | Touch: tap, double tap, long-press for right-click, two-finger zoom, tap assist, inertial scrolling, on-screen left/right mouse buttons and a key wheel (a ring of keyboard keys) |
| Language | The game's own languages; a Simplified Chinese font and an emoji fallback font are bundled so that CJK text renders |
| Mods | Load with the game, Harmony mods included; import a mod as a `.zip` or a folder; a community load-order rule set is shipped |
| Steam Workshop | Sign in with your Steam account (QR code or password), browse your subscriptions, download and install mods |
| Saves | Import save files (`.rws`); back up saves and settings to a zip |

### Not there yet

- **Every launch needs a JIT session.** iOS does not let an ordinary app create executable memory, so Port17 has to be started through StikDebug (see [Install](#install)). Opened the normal way it stops with a notice and the game does not start.
- **No Steam integration.** Achievements and the other Steam features are unavailable; Steam is used only to download Workshop mods.
- **No file watchers.** Mods that wait for files to change are never notified.
- **Memory.** iOS kills the app outright when it passes its memory limit, and nothing is tuned for that yet. Since 0.4.44 the app keeps a memory ledger (see `port17-status.txt`) and the Android trimmer runs on iOS too, but what to release and when is still undecided, so long sessions with large mod lists are untested.
- **A few PC-only APIs are missing.** External programs cannot be started (links open in Safari instead); a mod that needs one of them may fail — report it with `coreclr-stderr.log`.

Port17's own launcher screen is in Chinese (`导入 zip` = *Import zip*, `导出` = *Export*).

## How it works

- **Runtime.** A self-built hybrid .NET 11 runtime (`dotnet/runtime` v11.0.0): CoreCLR with the RyuJIT compiler linked in plus an interpreter. Two source patches go on top: [`patches/dualjit-arena.patch`](patches/dualjit-arena.patch) lets the JIT work from a pre-blessed arena, and [`patches/waylithclr-corelib.patch`](patches/waylithclr-corelib.patch) re-creates the `WaylithClr.*` runtime switches the engine relies on. Microsoft's own iOS runtime is interpreter-only.
- **Executable memory.** At start-up the app raises a `brk` request. A debug session running [`probe/stikdebug/rw17-jit26.js`](probe/stikdebug/rw17-jit26.js) allocates a 128 MiB executable arena on the app's behalf, complete with a writable alias, hands over its address and detaches; from then on the runtime allocates from that arena.
- **Graphics and audio.** Vulkan on Metal through MoltenVK. The engine's windowing and input code talks to a GLFW-API shim with a UIKit back end ([`host/glfw/`](host/glfw/)); audio goes through Core Audio.
- **On-device set-up.** The first run patches the game's own assemblies in place with Mono.Cecil ([`setup/`](setup/)): machine type, OS detection and a short list of APIs that do not exist on iOS. No PC-side tool is needed.
- **Managed layer.** [`hooks/`](hooks/) and [`mobile/`](mobile/) load with the game as a start-up hook: runtime shims for the engine, touch input, mod import and preparation, Harmony/MonoMod support, and small per-app fixes for iOS.
- **Workshop.** [`workshop/`](workshop/) is a Steam Workshop downloader built on SteamKit2 that runs inside the game.
- **Build.** The IPA is produced entirely on GitHub Actions (a macOS runner) by [`host/build-ipa.sh`](host/build-ipa.sh).

## Install

### 1. Get the app

- **Latest build:** GitHub Actions → [M2 Host](https://github.com/Kamisorara/rimworld-ios-port/actions/workflows/m2-host.yml) → latest green run → artifact `port17host-m2-ipa`. Downloading an artifact needs a GitHub account.
- The [Releases](https://github.com/Kamisorara/rimworld-ios-port/releases) page only carries milestone builds (currently v0.3.3), which are older than the artifacts.

### 2. Sideload it

Install the IPA with SideStore, AltStore, LiveContainer or any other tool that signs apps with your Apple ID. No Apple developer account is needed.

### 3. Copy your game files

From **your own** RimWorld installation (Steam → `steamapps/common/RimWorld`, version 1.7.x), Port17 needs `Assets.zip`, `Version.txt`, `Data/`, `Rendering/` and the game's managed assemblies. The easiest way is to put them into one zip. Zipping every `.dll` in the folder is fine; the `.exe`, `steam_api64*` and the `.vdf` / `.html` files are not needed. Include the folders of DLCs only for DLCs you own.

Get the zip (or the files) onto the device by any of these:

- the `导入 zip` button on Port17's screen — the system file picker; works with iCloud Drive, AirDrop and other apps' folders;
- the Files app: *On My iPad/iPhone → Port17 → Documents*;
- USB, with Finder;
- *Share → Open with → Port17* on a `port17…zip` in the Files app.

The first launch rewrites those game files in place for iOS (about half a minute) and then asks you to launch once more.

### 4. Launch it (JIT session)

iOS gives an ordinary app no executable memory, and this runtime needs it for JIT code. Every launch therefore goes through a debug session that hands Port17 an executable arena:

1. **Once:** install StikDebug and import its pairing file; install a VPN app that provides JIT (for example LocalDevVPN), switch it on and stay on the same Wi-Fi network; AirDrop [`rw17-jit26.js`](probe/stikdebug/rw17-jit26.js) to the device and import it into StikDebug.
2. **Every time:** in StikDebug run `rw17-jit26` with **Port17** as the target. Port17 waits for the arena (up to 120 seconds) and then starts the game; the script window can be closed once it reports `detached`.

### 5. Mods and saves

- **Mods:** import a `.zip` or a folder through Port17, or use the Workshop window on the game's Mods screen.
- **Workshop sign-in:** by QR code or password; a Steam Guard code is asked for when Steam requires it. The sign-in is kept in the app's private storage and survives updates. Neither your password nor the sign-in token is written to any log or export.
- **Saves:** `导出` shares a zip of your saves and settings together with the log files.

## Troubleshooting

Port17 keeps its logs in *Files → On My iPad/iPhone → Port17 → Documents*:

| File | What it holds |
|---|---|
| `port17-ui.log` | Port17's launcher: install, set-up and engine start-up |
| `coreclr-stderr.log` | Game and .NET runtime output — the last run only |
| `engine-trace.log` | Engine timing marks, plus one line per memory warning and the session's memory peak |
| `port17-status.txt` | One-line status of the last session: JIT arena, execution mode, and since 0.4.44 the memory footprint, the allowance left and its peak (`mem_*`) |
| `port17-workshop.log` | Workshop sign-in, downloads and installs |

When you report a problem, attach these files. For a black screen, start with `port17-ui.log` and `coreclr-stderr.log`.

## Build from source

The iOS app is built in the cloud; nothing Apple-specific is needed on your machine.

- **IPA:** push to `main` (changes under `host/`) or run the **M2 Host** workflow by hand; the artifact is `port17host-m2-ipa`. The other workflows build the patched runtime (**m1-probe**, also published as a release asset), the interpreter baseline (**interp-probe**) and a macOS debug build (**mac-debug**).
- **Managed layer:** `python scripts/build-portlayers.py` compiles `hooks/`, `mobile/` and `workshop/` into `prebuilt/` and rewrites the runtime's file-watcher stub. It needs the .NET SDK, Mono.Cecil, the Harmony assembly, a local RimWorld 1.7 installation and network access on the first run to fetch the Workshop's NuGet dependencies (same pinned versions and package hashes as `host/build-ipa.sh`; paths can be overridden with environment variables, `WORKSHOP_PKG` for the package cache). The result is byte-identical to the committed DLLs.

## Repository layout

| Path | What it is |
|---|---|
| `host/` | Native iOS host: UIKit app and launcher, GLFW-API shim, Vulkan forwarder to MoltenVK, audio, stand-ins for Windows libraries, and `build-ipa.sh` |
| `hooks/`, `mobile/` | Managed phone layer: start-up hook, runtime shims, touch input, mod support |
| `setup/` | On-device set-up that patches the game's assemblies |
| `workshop/` | Steam Workshop downloader |
| `scripts/` | Build and patch tools (`build-portlayers.py`, `check-facades.py`, `patch-harmony`, `patch-platform`, `unstub-watcher`) |
| `patches/` | CoreCLR source patches |
| `probe/` | Runtime probes and the StikDebug launch scripts |
| `runtime-out-hybrid/`, `prebuilt/` | Runtime pieces, managed binaries and facades copied into the IPA |
| `docs/` | Decision log and guides (Chinese) |

## Legal

**You must own the game.** This repository and its installers contain no RimWorld game files: no code, data, art, audio or text from the game is stored here or distributed with Port17. The app only runs files copied from a copy of RimWorld that you have bought on Steam, and it adapts that copy on your own device. Nothing here lets anyone play without buying the game first — Port17 depends on that purchase; it does not replace it.

**Why we consider this legitimate.** Port17 is a compatibility tool for owners of the game, in the same sense as a translation layer that lets an existing program run on another system. It is free and open source, it is not sold, and it redistributes nothing of RimWorld. The only source of game content is the player's own purchase.

**Trademarks.** RimWorld and the RimWorld logo are trademarks of Ludeon Studios, Inc. Steam and the Steam logo are trademarks of Valve Corporation. This is an unofficial, fan-made project: it is not affiliated with, endorsed by or sponsored by Ludeon Studios or Valve. The Workshop features sign in with your own Steam account and are subject to your Steam Subscriber Agreement.

If you are a rights holder and have a concern, please open an issue — it will be answered.

## License

Port17's own code is released under the [MIT License](LICENSE).

Everything else keeps its own license: the .NET runtime and CoreCLR (MIT), MoltenVK (Apache-2.0), Harmony and MonoMod (MIT), SteamKit2 (LGPL-2.1-only), protobuf-net (Apache-2.0), QRCoder and ZstdSharp (MIT — see [workshop/LICENSES.txt](workshop/LICENSES.txt)), Noto Sans SC ([OFL](host/app/fonts/LICENSE-OFL.txt)), and the further libraries fetched or compiled by the build (`host/build-ipa.sh`, `host/fetch-*.sh`).

## Documentation

The project's documentation is written in Chinese.

- [docs/PLAN.md](docs/PLAN.md) — decisions, every round of work, device verdicts and package records
- [docs/GAME-IMPORT.md](docs/GAME-IMPORT.md) — game files, import and directory layout in detail
- [docs/ENGINE-NATIVE-CONTRACT.md](docs/ENGINE-NATIVE-CONTRACT.md) — native ABI between engine and host
- [docs/NET11-COMPARISON.md](docs/NET11-COMPARISON.md), [docs/PLAN-JIT26.md](docs/PLAN-JIT26.md), [docs/PERF-30FPS-PLAN.md](docs/PERF-30FPS-PLAN.md) — runtime study, JIT arena design, performance plan
