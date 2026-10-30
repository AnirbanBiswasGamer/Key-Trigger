# Key Trigger Technical Wiki & Architecture Reference

Key Trigger is a high-performance Windows input virtualization system written in C# (.NET 8 WPF) that translates discrete binary keyboard events into continuous analog Xbox 360 controller states at 1000Hz with sub-millisecond latency and near-zero CPU footprint.

---

## Table of Contents
1. [Architecture Overview](#1-architecture-overview)
2. [Input Pipeline & Low-Level Keystroke Swallowing](#2-input-pipeline--low-level-keystroke-swallowing)
3. [1000Hz Physics & Ramp Evaluation Engine](#3-1000hz-physics--ramp-evaluation-engine)
4. [Response Curves & Mathematical Models](#4-response-curves--mathematical-models)
5. [Virtual Gamepad Output Subsystem (ViGEmBus)](#5-virtual-gamepad-output-subsystem-vigembus)
6. [Profile Architecture & Storage Schema](#6-profile-architecture--storage-schema)
7. [Process Watcher & Auto-Activation](#7-process-watcher--auto-activation)
8. [Windows 11 Fluent UI & DWM Interop](#8-windows-11-fluent-ui--dwm-interop)
9. [Build & Compilation Pipeline](#9-build--compilation-pipeline)

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
 |                      WIN32 LOW-LEVEL HOOK FILTER                         |
 |                WH_KEYBOARD_LL (GlobalKeyboardHook.cs)                     |
 |  - Evaluates Virtual Key Codes (VK_W, VK_S, VK_A, VK_D, F1-F4, F8)       |
 |  - Detects MAGIC_EXTRA_INFO (0x534D5448) to prevent feedback loops       |
 |  - Swallows mapped keys (returns IntPtr(1)) to prevent double-input      |
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

## 2. Input Pipeline & Low-Level Keystroke Swallowing

### Win32 Global Hook Filter (`WH_KEYBOARD_LL`)
Key Trigger intercepts raw hardware keystrokes using the Win32 `SetWindowsHookEx` API configured for `WH_KEYBOARD_LL` (id 13).

```csharp
[DllImport("user32.dll", SetLastError = true)]
public static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);
```

### Keystroke Swallowing Mechanism
Many racing engines (such as Codemasters EGO Engine or Forza Apex) monitor both raw DirectInput keyboards and XInput gamepads simultaneously. If a player holds `W`, the game may register both the keyboard key and the controller trigger, causing rapid device thrashing, menu freezing, or steering lock.

Key Trigger eliminates this by **swallowing** mapped keys:
- When a keystroke matches a mapped driving key or remapped controller button, the hook callback returns `(IntPtr)1` instead of calling `CallNextHookEx`.
- Windows drops the event before it reaches the OS input queue, preventing the game from receiving the raw keyboard input.
- Synthetic keystrokes injected by accessibility software are identified via `MAGIC_EXTRA_INFO = 0x534D5448` (`SMTH` in ASCII hex) to prevent recursive feedback.

---

## 3. 1000Hz Physics & Ramp Evaluation Engine

### Precision Scheduling with Minimal CPU Overhead
Typical Windows threads sleeping with `Thread.Sleep(1)` sleep for 15.6 milliseconds due to default Windows timer resolution. Key Trigger invokes the Win32 Multimedia API `timeBeginPeriod(1)` to enforce 1ms resolution:

```csharp
[DllImport("winmm.dll", EntryPoint = "timeBeginPeriod")]
public static extern uint TimeBeginPeriod(uint uMilliseconds);
```

The engine loop runs on a dedicated thread (`KeyTrigger_1000Hz_Engine`) with `ThreadPriority.Highest`:

```csharp
while (_isRunning)
{
    long currentTick = sw.ElapsedMilliseconds;
    double deltaSeconds = (currentTick - lastTick) / 1000.0;
    lastTick = currentTick;

    if (deltaSeconds > 0.05) deltaSeconds = 0.05; // Prevent jump spikes

    if (_isActive)
    {
        UpdateRamp(deltaSeconds);
    }
    
    _gamepadOutput.Update(_lastEvaluatedThrottle, _lastEvaluatedBrake, _lastEvaluatedSteer);
    Thread.Sleep(1); // 1ms sleep yields CPU cleanly -> 0% CPU usage
}
```

### Dynamic Ramp Integration
For each axis (Throttle, Brake, Steering):
- **Attack Phase (Key Pressed)**:
  $$\text{progress} \leftarrow \text{progress} + \frac{\Delta t}{\text{AttackTimeSeconds}}$$
- **Release Phase (Key Released)**:
  $$\text{progress} \leftarrow \text{progress} - \frac{\Delta t}{\text{ReleaseTimeSeconds}}$$
- Clamped strictly to $[0.0, 1.0]$.

---

## 4. Response Curves & Mathematical Models

Key Trigger implements 7 response curve transformations evaluated via `ResponseCurves.Evaluate(type, t, p1x, p1y, p2x, p2y)`:

### 1. Linear
Direct proportional 1:1 mapping:
$$f(t) = t$$

### 2. SmoothStep (Cubic Hermite)
Zero 1st-order derivative at start and end for smooth starts:
$$f(t) = 3t^2 - 2t^3$$

### 3. SmootherStep (Perlin Quintic)
Zero 1st and 2nd derivatives at boundaries for ultra-smooth luxury ramp-up:
$$f(t) = 6t^5 - 15t^4 + 10t^3$$

### 4. Exponential
High initial modulation with aggressive high-end surge:
$$f(t) = \frac{e^{2.5t} - 1}{e^{2.5} - 1}$$

### 5. Progressive (Quadratic)
Gradual initial bite, ramping up toward the limit:
$$f(t) = t^2$$

### 6. Aggressive (Square Root)
Instant bite off the line for fast reaction times:
$$f(t) = \sqrt{t}$$

### 7. Custom Interactive Bézier
Evaluated via cubic Bézier parameterization with endpoints $(0,0)$ and $(1,1)$, and user-draggable control points $(P_1X, P_1Y)$ and $(P_2X, P_2Y)$:
$$B(u) = (1-u)^3 P_0 + 3(1-u)^2 u P_1 + 3(1-u) u^2 P_2 + u^3 P_3$$
Solved for parameter $u$ where $X(u) = t$ using binary search, followed by evaluating $Y(u)$ to yield the curve output.

---

## 5. Virtual Gamepad Output Subsystem (ViGEmBus)

Key Trigger communicates with the **Virtual Gamepad Emulation Bus (ViGEmBus)** kernel-mode driver through `Nefarius.ViGEm.Client`.

### Axis Mapping Specifications
- **Right Trigger (RT - Throttle)**:
  $$\text{Byte Value} = \text{round}(\text{OutputThrottle} \times 255.0)$$
- **Left Trigger (LT - Brake)**:
  $$\text{Byte Value} = \text{round}(\text{OutputBrake} \times 255.0)$$
- **Left Thumbstick X (Steering)**:
  $$\text{Short Value} = \text{round}(\text{OutputSteer} \times 32767.0)$$
  where $-32768$ is full left lock, $0$ is center, and $+32767$ is full right lock.

### Controller Button Mask Remapping
Supports full Xbox 360 controller digital buttons:
`A`, `B`, `X`, `Y`, `Left Bumper (LB)`, `Right Bumper (RB)`, `Start`, `Back`, `Left Stick Click (LS)`, `Right Stick Click (RS)`, and all 4 D-Pad directions (`Up`, `Down`, `Left`, `Right`).

---

## 6. Profile Architecture & Storage Schema

Configuration is stored in portable `settings.json` located directly alongside `KeyTrigger.exe`.

### Profile Data Model (`ThrottleProfile.cs`)
```json
{
  "Id": "60def6c2-e51b-4310-b1a5-8fe14686d3d1",
  "Name": "Sport / Track",
  "EnableThrottle": true,
  "EnableBrake": true,
  "EnableSteering": true,
  "AttackTimeSeconds": 0.35,
  "ReleaseTimeSeconds": 0.15,
  "CurveType": 4,
  "MinThrottle": 0.0,
  "MaxThrottle": 1.0,
  "HotkeyVkCode": 112,
  "HotkeyName": "F1",
  "CustomP1X": 0.35,
  "CustomP1Y": 0.15,
  "CustomP2X": 0.65,
  "CustomP2Y": 0.85,
  "BrakeAttackTimeSeconds": 0.25,
  "BrakeReleaseTimeSeconds": 0.15,
  "BrakeCurveType": 1,
  "MinBrake": 0.0,
  "MaxBrake": 1.0,
  "SteerLeftAttackTimeSeconds": 0.25,
  "SteerLeftReleaseTimeSeconds": 0.15,
  "SteerLeftCurveType": 0,
  "MinSteerLeft": 0.0,
  "MaxSteerLeft": 1.0,
  "ThrottleVkCode": 87,
  "ThrottleKeyName": "W",
  "BrakeVkCode": 83,
  "BrakeKeyName": "S",
  "SteerLeftVkCode": 65,
  "SteerLeftKeyName": "A",
  "SteerRightVkCode": 68,
  "SteerRightKeyName": "D",
  "ButtonMappings": [ ... ]
}
```

### Hot-Swapping Architecture
When switching profiles (via hotkey `F1`–`F4`, chip click, or auto-activation):
1. `_settings.SyncToActiveProfile()` commits current UI sliders and control points.
2. `ActiveProfileId` switches to the new profile ID.
3. `_settings.SyncFromActiveProfile()` unpacks all keys, curves, and limits into engine cache.
4. `_engine.UpdateKeyBindings()` updates hook filters without stopping the background 1000Hz thread.
5. Windows sound synthesizes subtle audio feedback corresponding to profile index.

---

## 7. Process Watcher & Auto-Activation

`GameWatcher.cs` polls active window handles via `GetForegroundWindow` and `GetWindowThreadProcessId` on a lightweight 1-second timer.
- When an active process matches an entry in `AutoActivateGames`, Key Trigger activates automatically.
- When the game process exits, Key Trigger returns to stand-by mode, preventing key interception during desktop workflows.

---

## 8. Windows 11 Fluent UI & DWM Interop

### Native Acrylic & Mica Composition
Key Trigger utilizes Windows Desktop Window Manager (DWM) composition without GPU-heavy shader layers:
- `DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, 1)` enables dark window title and border styling.
- `DwmSetWindowAttribute(hwnd, DWMWA_SYSTEMBACKDROP_TYPE, DWMSBT_TRANSIENTWINDOW)` activates Windows 11 Acrylic frosted-glass backdrop with zero CPU rendering load.
- Glass margin expansion: `DwmExtendFrameIntoClientArea(hwnd, ref margins)` with `margins = -1` allows the client canvas to receive the native desktop backdrop.

---

## 9. Build & Compilation Pipeline

### Prerequisites
- Windows 10 / 11 (64-bit)
- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)
- [ViGEmBus Driver](https://github.com/nefarius/ViGEmBus/releases)

### CLI Build Commands
```bash
# Debug build
dotnet build KeyTrigger.csproj

# Release compilation
dotnet build KeyTrigger.csproj -c Release

# Standalone Publish (Zero dependencies required on client)
dotnet publish KeyTrigger.csproj -c Release -r win-x64 --self-contained false -o ./bin/Publish
```
