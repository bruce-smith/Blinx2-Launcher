# Blinx 2 setup (launcher)

`Blinx2Setup.exe` sets up the [Blinx 2 static recompilation](https://github.com/bruce-smith/blinx-2-recompile-full)
on a Windows PC from your own disc image, for people who would rather not
run the build by hand. You choose your Blinx 2 `.iso`/`.xiso` and a folder,
and it does the rest:

1. **Checks the tools the build needs** and lists what is missing:
   Git, CMake 3.20+, Visual Studio 2022 or newer with the C++ tools and a
   Windows SDK, Python 3.10+ (3.12 recommended), and the Visual C++
   Redistributable that the game needs to start. Nothing is installed
   without asking. When you agree, it installs the missing ones with winget
   (Windows' package manager), and Windows may ask for permission (UAC).
   Tools that are already installed are used as they are. A Visual Studio
   2022 or newer that already has the C++ tools is used, whatever its
   edition. Otherwise it installs the free Visual Studio 2022 Build Tools.
2. **Extracts the game files** from your disc image into
   `<install>\game_files` with [xdvdfs](https://github.com/antangelo/xdvdfs).
   It downloads xdvdfs v0.8.3 from its GitHub releases and checks the
   download against a SHA-256 built into the setup. It also checks that the
   disc is the North American (NTSC-U) Blinx 2, the version the project
   supports.
3. **Downloads the project** with `git clone --recurse-submodules`:
   [blinx-2-recompile-full](https://github.com/bruce-smith/blinx-2-recompile-full)
   and its submodules.
4. **Runs the recompiler pipeline** exactly as the project documents it
   (`blinx2-recomp/README.md`, "Generating the recompiled C"): five
   `python -m tools.*` steps in `xboxrecomp`, with the extra code sections,
   seeds, coalescing, yield points and loop heads. Python runs in a private
   environment (`<install>\python`, with `capstone`), so nothing is
   installed into your own Python.
5. **Builds the game** (CMake + MSBuild, Release).
6. **Points the game at its files** (`game_dir` under `[paths]` in
   `blinx2_settings.ini` next to the exe) and makes **shortcuts**: "Blinx
   2" on the desktop and in the Start menu, plus "Blinx 2 Setup (update or
   repair)" in the Start menu.

Nothing of the game is downloaded or included. The game files come only
from your own disc image, and the recompiled code is generated from them on
your PC.

## Using it

Double-click `Blinx2Setup.exe`. It needs no .NET installation.

- **Time:** about 30 minutes on a recent PC (measured on a 32-thread PC:
  25 minutes; about 7 for the recompiler, 17 for the compile). Slower PCs
  take longer. Add 15-30 minutes if Visual Studio has to be installed first. Each step shows its progress and an estimate of
  the time left. You can leave the PC alone, and it will not go to sleep
  while the setup runs.
- **Disk:** about 4.5 GB (measured: game files 3.2 GB, generated C and
  build 0.9 GB), plus about 6 GB if Visual Studio has to be installed. The setup checks the free space first.
- **Internet:** needed for the tools, the source, and the libraries the
  build downloads (Vulkan headers, glslang, volk, ImGui).
- **Interrupted?** Run it again with the same folder and it continues at
  the step that did not finish. `<install>\setup_state.json` records the
  steps that are done.
- **Something went wrong?** The window shows the error. "Copy log" copies
  the tool versions, the steps and the end of the log for a bug report, and
  the full log is `<install>\setup.log`.
- **Updating:** run "Blinx 2 Setup (update or repair)" from the Start menu
  and tick "Update to the latest version". It pulls the newest project
  commits, then reruns the recompiler and the build if anything changed.
  Your saves stay in `<install>\game_files\UDATA`.

Install folder layout:

```
<install>\
  game_files\                 your extracted disc (and your saves, UDATA)
  blinx-2-recompile-full\     the project (xboxrecomp, blinx2-recomp)
    blinx2-recomp\build\Release\blinx_2_recomp.exe   the game
  python\                     private Python environment for the recompiler
  tools\xdvdfs\               the disc extractor
  setup.log, setup_state.json, Blinx2Setup.exe
```

The folder path must be plain ASCII and short: the game reads `game_dir`
with narrow-character file functions and takes at most 95 characters. The
default is `%USERPROFILE%\Blinx2`, or `C:\Blinx2` when the profile path does
not fit those limits.

### Command line

The same setup runs without the window:

```
Blinx2Setup.exe --cli --iso <file or folder> [--dir <install folder>]
                [--yes] [--dry-run] [--no-shortcuts] [--update]
```

- `--yes` installs missing tools without asking.
- `--dry-run` only reports what is installed, what it would install (the
  exact winget commands) and which steps would run.
- `--update` pulls the newest project commits first.

For testing the install path on a PC that already has everything:
`BLINX2SETUP_PRETEND_MISSING=git,vs,cmake,python,vcredist` reports those as
missing, `BLINX2SETUP_DRY_INSTALL=1` logs the winget commands instead of
running them, and `BLINX2SETUP_SHORTCUT_DIR=<dir>` puts the shortcuts there.

Exit codes: 0 done, 1 a step failed, 2 bad arguments, 3 tools missing,
4 not enough disk space, 5 cancelled.

## Building the setup itself

It needs the .NET 10 SDK on Windows.

```
dotnet publish src\Blinx2Launcher -c Release -o dist
```

The result is `dist\Blinx2Setup.exe`, a single self-contained file (about
50 MB). `dotnet build src\Blinx2Launcher` builds a normal debug version.

Code map (`src/Blinx2Launcher`):

- `MainForm.cs`: the window (three pages: choose, tools, progress).
- `Program.cs`: the entry point, the `--cli` mode and the folder checks.
- `Deps.cs`: finding Git, CMake, Visual Studio (through vswhere, with the
  matching CMake generator), Python, the Windows SDK and the VC++ runtime.
- `DepInstaller.cs`: the winget commands.
- `Setup.cs`: the steps. The pipeline flags live here; keep them in step
  with the project's README.
- `SetupState.cs`: resuming. A step reruns when its key changes, so after an
  update the pipeline and the build run again.
- `ProcRunner.cs`: child processes in a job object. Cancel and close stop
  only the setup's own processes.

## Licence

MIT, see [LICENSE](LICENSE). xdvdfs is MIT-licensed as well; the setup
downloads it from its official releases. Blinx 2 is © its owners. This
project includes no part of it.
