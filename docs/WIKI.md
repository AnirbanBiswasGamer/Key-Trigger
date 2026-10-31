# Key Trigger Technical Wiki & Architecture Reference

Key Trigger is a high-performance Windows input virtualization system written in C# (.NET 8 WPF) that translates discrete binary keyboard events into continuous analog Xbox 360 controller states at 1000Hz with sub-millisecond latency and near-zero CPU footprint.

---

## Table of Contents
1. [Architecture Overview](#1-architecture-overview)
2. [Input Pipeline & Low-Level Hook Subsystem](#2-input-pipeline--low-level-hook-subsystem)
3. [1000Hz Physics & Ramp Evaluation Engine](#3-1000hz-physics--ramp-evaluation-engine)
4. [Response Curves & Mathematical Models](#4-response-curves--mathematical-models)
5. [Virtual Gamepad Output Subsystem (ViGEmBus)](#5-virtual-gamepad-output-subsystem-vigembus)
6. [Controller Button Remapping & Key Capture Pipeline](#6-controller-button-remapping--key-capture-pipeline)
7. [Profile Architecture & Storage Schema](#7-profile-architecture--storage-schema)
8. [Process Watcher & Auto-Activation](#8-process-watcher--auto-activation)
9. [Windows 11 Fluent UI & DWM Interop](#9-windows-11-fluent-ui--dwm-interop)
10. [EGO Engine & Exclusive Device Compatibility (GRID Autosport)](#10-ego-engine--exclusive-device-compatibility-grid-autosport)
11. [Build, Packaging & Distribution Pipeline](#11-build-packaging--distribution-pipeline)

---

## 1. Architecture Overview

```
 +--------------------------------------------------------------------------+
 |                            PHYSICAL HARDWARE                             |
 |                Mechanical Keyboard (USB / PS/2 Polling)                  |
 +-------------------------------------+------------------------------------+
                                       |
                                       v
 +--------------------------------------------------------------------------+
 |              WIN32 ISOLATED HOOK THREAD & RAW INPUT BACKSTOP             |
 |              (GlobalKeyboardHook.cs - Dedicated Message Loop)             |
 |  - Dedicated background thread avoids WPF UI thread message stalls       |
 |  - Periodic re-hooking (50ms on focus shift, 1s heartbeat) maintains      |
 |    front-of-chain priority against aggressive game hooks (Codemasters)   |
 |  - Evaluates Virtual Key Codes (VK_W, VK_S, VK_A, VK_D, Remaps, F1-F4, F8)|
 |  - Swallows mapped keys (returns IntPtr(1)) to prevent double-input      |
 |  - Raw Input sink (RIDEV_INPUTSINK) backstop catches dropped hook events |
 +-------------------------------------+------------------------------------+
                                       |
                                       v
 +--------------------------------------------------------------------------+
 |                     1000Hz ENGINE EVALUATION LOOP                        |
 |                         (ThrottleEngine.cs)                              |
 |  - Dedicated HighPriority background thread with timeBeginPeriod(1)      |
 |  - Per-axis Attack / Release time integration (dt evaluation)            |
 |  - Clamping & Deadzone transformation (MinThrottle / MaxThrottle)        |
 |  - Real-time mathematical response curve evaluation                      |
 +-------------------------------------+------------------------------------+
                                       |
                                       v
 +--------------------------------------------------------------------------+
 |                    OUTPUT SUBSYSTEM (VIRTUAL GAMEPAD)                    |
 |                     (VirtualGamepadOutput.cs)                            |
 |  - Nefarius.ViGEm.Client C# wrapper to ViGEmBus Kernel Driver            |
 |  - Batched report submissions (AutoSubmitReport = false)                 |
 |  - Neutral calibration nudge on startup eliminates stick drift           |
 |  - 50ms heartbeat refresh guarantees driver sync                         |
 |  - Maps 0.0..1.0 float -> Byte (0..255) for Triggers (LT / RT)           |
 |  - Maps -1.0..+1.0 float -> Short (-32768..+32767) for Left Stick X      |
 |  - Dispatches controller button masks (A, B, X, Y, LB, RB, D-Pad, etc.)  |
 +-------------------------------------+------------------------------------+
                                       |
                                       v
 +--------------------------------------------------------------------------+
 |                             RACING GAME                                  |
 |       (GRID Autosport, Forza Horizon/Motorsport, Assetto Corsa, F1)       |
 |              Receives native Xbox 360 controller telemetry               |
 +--------------------------------------------------------------------------+
```

---

## 2. Input Pipeline & Low-Level Hook Subsystem

### Dedicated Hook Thread Architecture
Windows calls low-level hooks (`WH_KEYBOARD_LL`) by dispatching messages to the specific thread that installed the hook. In earlier builds, installing the hook on the WPF UI thread made hook callbacks susceptible to UI layout or rendering stalls, triggering Windows' silent `LowLevelHooksTimeout` unhooking.

Key Trigger isolates the hook on a dedicated high-priority thread (`KeyTrigger_KeyboardHook`) with its own Win32 message pump:

```csharp
private void HookThreadMain()
{
    _threadId = NativeMethods.GetCurrentThreadId();
    NativeMethods.PeekMessage(out _, IntPtr.Zero, 0, 0, NativeMethods.PM_NOREMOVE);
    Rehook();
    CreateMessageWindow();
    
    while (NativeMethods.GetMessage(out var msg, IntPtr.Zero, 0, 0) > 0)
    {
        NativeMethods.TranslateMessage(ref msg);
        NativeMethods.DispatchMessage(ref msg);
    }
}
```

### Dynamic Hook Reinstallation & Aging
Games using aggressive keyboard monitoring (such as Codemasters EGO Engine) install unhandled hooks upon gaining focus, causing Windows to skip pre-existing hooks in the chain.

Key Trigger overcomes this with active hook rotation:
- Installs WinEvent foreground tracking (`EVENT_SYSTEM_FOREGROUND`).
- Rehooks every 50ms for 2 seconds after any foreground window shift, ensuring Key Trigger remains at the head of the hook chain.
- Maintains superseded hooks in a retired list for at least 3 seconds so in-flight keystrokes already propagating down the chain are never dropped.
- De-duplicates identical events across hook generations.

### Raw Input Backstop (`RIDEV_INPUTSINK`)
A hidden message window listens to Raw Input keyboard reports. Because swallowed keys never reach Raw Input, any mapped key that appears in the Raw Input sink bypassed the hook (e.g. during a focus transition). The engine immediately applies it, guaranteeing that a physical key release is never missed and triggers/steering never get stuck.

---

## 3. 1000Hz Physics & Ramp Evaluation Engine

### Precision Scheduling with Minimal CPU Overhead
Standard Windows thread scheduling sleeps for ~15.6ms. Key Trigger activates the Win32 Multimedia timer service via `timeBeginPeriod(1)` to enable genuine 1ms scheduler resolution:

```csharp
[DllImport("winmm.dll", EntryPoint = "timeBeginPeriod")]
public static extern uint TimeBeginPeriod(uint uMilliseconds);
```

The engine thread runs at `ThreadPriority.Highest`:
- **Active / Ramping State**: Loops at 1ms resolution (`ActiveTickMs = 1`), calculating delta time ($\Delta t$) via `Stopwatch.GetTimestamp()` with zero latency.
- **Idle State**: Automatically sleeps for 25ms (`IdleTickMs = 25`) using an auto-reset wait handle (`_wake`). Pressing any key or changing active profiles signals `_wake.Set()`, awakening the engine immediately with 0ms delay.

### Dynamic Ramp Integration
For each analog axis (Throttle, Brake, Steering):
- **Attack Phase (Key Held)**:
  $$\text{progress} \leftarrow \text{progress} + \frac{\Delta t}{\text{AttackTimeSeconds}}$$
- **Release Phase (Key Released)**:
  $$\text{progress} \leftarrow \text{progress} - \frac{\Delta t}{\text{ReleaseTimeSeconds}}$$
- Progress is bounded strictly within $[0.0, 1.0]$ and transformed through the configured response curve before reaching the output subsystem.

---

## 4. Response Curves & Mathematical Models

Key Trigger implements 7 mathematical response curve transformations evaluated via `ResponseCurves.Evaluate(type, t, p1x, p1y, p2x, p2y)`:

| Curve Type | Mathematical Model | Characteristics |
|---|---|---|
| **Linear** | $f(t) = t$ | Pure 1:1 proportional response |
| **SmoothStep** | $f(t) = 3t^2 - 2t^3$ | Cubic Hermite curve with zero 1st derivatives at endpoints |
| **SmootherStep** | $f(t) = 6t^5 - 15t^4 + 10t^3$ | Perlin quintic polynomial with zero 1st and 2nd derivatives |
| **Exponential** | $f(t) = \frac{e^{2.5t} - 1}{e^{2.5} - 1}$ | Soft initial bite, ramping aggressively toward max output |
| **Progressive** | $f(t) = t^2$ | Quadratic curve for controlled traction off the line |
| **Aggressive** | $f(t) = \sqrt{t}$ | Fast initial bite for rapid corner turn-in |
| **Custom Bézier** | Parametric Cubic Bézier | Solved via binary search on user-defined control points $(P_1, P_2)$ |

---

## 5. Virtual Gamepad Output Subsystem (ViGEmBus)

Key Trigger communicates with the **Virtual Gamepad Emulation Bus (ViGEmBus)** kernel driver using `Nefarius.ViGEm.Client`.

### Batched Single-Report Output
By default, the ViGEm client transmits a separate HID report on every `Set*` property modification (`AutoSubmitReport = true`). Updating throttle, brake, and steering would generate 3 partial reports, presenting the game with desynchronized intermediate inputs.

Key Trigger enforces atomic batched submissions:
```csharp
_controller.AutoSubmitReport = false;
_controller.SetSliderValue(Xbox360Slider.RightTrigger, rt);
_controller.SetSliderValue(Xbox360Slider.LeftTrigger, lt);
_controller.SetAxisValue(Xbox360Axis.LeftThumbX, steerVal);
_controller.SubmitReport(); // Transmits one cohesive report
```

### Neutral Calibration & Stick Centering
Until an explicit report changes state, XInput reports slight non-zero offsets on uninitialized thumbstick axes (~10% left/right drift). Upon initialization, Key Trigger performs an immediate micro-nudge followed by an explicit neutral submission (`ResetReport()`), calibrating all sticks and triggers precisely to zero.

### 50ms State Heartbeat
If inputs remain completely static, Key Trigger automatically resends the gamepad state every 50ms. This prevents games from timing out virtual controller inputs during long straights or steady-state cruising.

---

## 6. Controller Button Remapping & Key Capture Pipeline

### Hybrid Dual-Route Key Capture (`v1.1.0`)
When rebinding keys inside the application GUI:
1. **Direct WPF Preview Pipeline**: The main window implements `OnPreviewKeyDown`, capturing keys at the root preview stage via `KeyInterop.VirtualKeyFromKey`. Keystrokes are captured instantaneously without waiting for OS message routing, and marked `e.Handled = true` to prevent focused buttons from interpreting `Space` or `Enter` as clicks.
2. **Global Hook Fallback**: Keystrokes delivered from outside the focused window are captured by `KeyCapturedForBinding` on the hook thread.

### Rebind Toggle & Cancellation
Clicking a button currently waiting in `[ PRESS KEY... ]` mode immediately toggles it off, returning it to its existing binding and disarming capture mode.

### Tab Navigation Teardown
Switching pages via the sidebar automatically calls `CancelCapture()` and sets `IsKeyCaptureActive = false`. Keystrokes are never trapped in capture mode or bound to hidden buttons when navigating between menus.

---

## 7. Profile Architecture & Storage Schema

Configuration is persisted in portable `settings.json` located directly in the application directory.

### Profile Schema & Bidirectional Synchronization
Each profile independently encapsulates timing, curves, limits, hotkeys, and button mappings:
```json
{
  "Profiles": [
    {
      "Id": "60def6c2-e51b-4310-b1a5-8fe14686d3d1",
      "Name": "Sport / Track",
      "AttackTimeSeconds": 0.35,
      "ReleaseTimeSeconds": 0.15,
      "CurveType": 4,
      "MinThrottle": 0.0,
      "MaxThrottle": 1.0,
      "HotkeyVkCode": 112,
      "HotkeyName": "F1",
      "ButtonMappings": [
        {
          "ButtonId": "A",
          "DisplayName": "A Button",
          "Category": "Face Buttons",
          "DefaultRole": "Handbrake / Select",
          "VkCode": 32,
          "KeyName": "Spacebar"
        }
      ]
    }
  ],
  "ActiveProfileId": "60def6c2-e51b-4310-b1a5-8fe14686d3d1"
}
```

### Atomic Disk Persistence
To prevent corrupt settings during unexpected system reboots, `Save()` serializes to a `.tmp` file using `FileStream.Flush(true)` and replaces `settings.json` atomically via `File.Move(..., overwrite: true)`.

---

## 8. Process Watcher & Auto-Activation

`GameWatcher.cs` monitors active foreground processes using `GetForegroundWindow` and `GetWindowThreadProcessId` on a 1-second interval:
- When a recognized racing executable (e.g. `GRIDAutosport_avx.exe`, `ForzaHorizon5.exe`, `acs.exe`) gains focus, Key Trigger automatically activates.
- When focus leaves the game, Key Trigger pauses or deactivates, returning native keyboard functionality for desktop typing.

---

## 9. Windows 11 Fluent UI & DWM Interop

Key Trigger utilizes Desktop Window Manager (DWM) composition without GPU overhead:
- `DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, 1)` activates dark window frames.
- `DwmSetWindowAttribute(hwnd, DWMWA_SYSTEMBACKDROP_TYPE, DWMSBT_TRANSIENTWINDOW)` activates native Windows 11 Acrylic frosted-glass composition with 0% GPU rendering overhead.
- `DwmExtendFrameIntoClientArea(hwnd, ref margins)` with `margins = -1` expands the glass area across the entire window canvas.

---

## 10. EGO Engine & Exclusive Device Compatibility (GRID Autosport)

Codemasters EGO Engine games (GRID series, F1 series, Dirt series) employ strict single-device control models:
1. **Device Priority**: While a Keyboard preset is selected in `Options > Controls`, EGO games discard all XInput controller packets entirely. Users must select the **Xbox 360 Controller** preset in the game's control settings.
2. **Hook Displacement**: EGO engine binaries install their own `WH_KEYBOARD_LL` hook upon activation. Key Trigger's foreground-shift re-hooking ensures our hook stays ahead of the game's hook, preventing `WASD` leakage.

---

## 11. Build, Packaging & Distribution Pipeline

### Compilation Commands
```bash
# Debug build
dotnet build KeyTrigger.csproj

# Release compilation
dotnet build KeyTrigger.csproj -c Release
```

### Lightweight Distribution Binaries (`v1.1.0`)
Key Trigger is packaged in two lightweight formats:
1. **Portable Release Folder** (~1.1 MB total / ~578 KB zipped):
   ```bash
   dotnet publish -c Release -o ./bin/Publish
   ```
2. **Single-File Lightweight Executable** (~1.05 MB single `.exe` file):
   ```bash
   dotnet publish -c Release -r win-x64 --no-self-contained -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o ./bin/Publish-SingleFile
   ```
