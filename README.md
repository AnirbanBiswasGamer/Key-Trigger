<div align="center">

<img src="Assets/app_icon.png" alt="Key Trigger Logo" width="128" height="128" />

# Key Trigger

### Drive like you have a controller — using just your keyboard!

A smooth, progressive controller emulator designed specifically for racing and driving games.

---

</div>

## What is Key Trigger?

In most racing games, pressing a keyboard key is either **0% or 100%**. That causes wheelspin off the start line, locked brakes into corners, and jerky steering that makes you spin out.

**Key Trigger fixes this.** It smoothly ramps up your keyboard inputs like pulling a real controller trigger or gently steering an analog thumbstick.

---

## Features

- **Smooth Acceleration**: Gently build throttle for clean launches without instant wheelspin.
- **Progressive Braking**: Brake smoothly into turns without locking your wheels.
- **Natural Steering**: Steer into turns smoothly and let the wheel center naturally without sudden snaps.
- **Custom Response Curves**: Choose how your car responds — smooth, aggressive, linear, or custom.
- **Profiles for Every Car**: Save different setups for track racing, drifting, rain, or trucks, and switch between them instantly with hotkeys (`F1`–`F4`).
- **Full Controller Remapping**: Bind all controller buttons (handbrake, camera, gears, pause) to your favorite keyboard keys.
- **Auto-Detection**: Automatically activates when your game starts and sleeps when you close it.
- **One-Key Toggle**: Turn emulation on or off anytime with a single key (`F8`).

---

## Quick Start Guide

1. **Install ViGEmBus Driver**:
   - Key Trigger uses the standard free **ViGEmBus driver** to create the virtual controller.
   - If not installed yet, the app gives you a direct one-click install button right on the Home screen.

2. **Launch Key Trigger**:
   - Open `KeyTrigger.exe`.
   - Select your preferred profile (e.g. *Sport / Track*, *Balanced*, or *Rain*).

3. **Play**:
   - Press **`F8`** (or click **ACTIVATE**) to turn on Key Trigger.
   - Jump into your favorite racing game and enjoy smooth driving!

---

## Default Controls

| Action | Keyboard Key | Virtual Controller |
| :--- | :---: | :--- |
| **Throttle / Accelerate** | `W` | Right Trigger (RT) |
| **Brake / Reverse** | `S` | Left Trigger (LT) |
| **Steer Left** | `A` | Left Stick Left |
| **Steer Right** | `D` | Left Stick Right |
| **Handbrake** | `Spacebar` | A Button |
| **Shift Down** | `Q` | Left Bumper (LB) |
| **Shift Up** | `E` | Right Bumper (RB) |
| **Change Camera** | `C` | Y Button |
| **Look Back** | `B` | B Button |
| **Pause / Menu** | `Escape` | Start Button |
| **Master Toggle** | `F8` | Emulator On / Off |
| **Switch Profiles** | `F1` – `F4` | Instant Profile Switch |

> *All controls, sensitivity timings, and buttons can be customized directly in the app.*

---

## Technical Documentation

For in-depth architecture diagrams, 1000Hz engine mechanics, response curve mathematical formulas, Win32 hook filtering, and developer guides, visit our [Technical Wiki](https://github.com/AnirbanBiswasGamer/Key-Trigger/wiki) or read the local copy in [docs/WIKI.md](docs/WIKI.md).

---

## Dependencies & Attribution

Key Trigger is made possible thanks to the following open-source projects and technologies:

- **ViGEmBus** — Virtual Gamepad Emulation Bus driver by Benjamin Höglinger-Stelzer ([Nefarius](https://github.com/nefarius)).  
  Repository: [https://github.com/nefarius/ViGEmBus](https://github.com/nefarius/ViGEmBus)  
  ViGEmBus provides the Windows kernel-mode driver framework enabling high-performance Xbox 360 controller emulation.

- **Nefarius.ViGEm.Client** — Official .NET client library for the ViGEmBus driver by Benjamin Höglinger-Stelzer ([Nefarius](https://github.com/nefarius)).  
  Repository: [https://github.com/nefarius/ViGEm.NET](https://github.com/nefarius/ViGEm.NET)  
  Provides the C# API bindings for virtual gamepad management and state feeds.

- **Microsoft .NET 8 / WPF** — Modern Windows desktop application framework powering the user interface and high-precision input dispatch.

Special thanks to Benjamin Höglinger-Stelzer (Nefarius) and the open-source community for developing the virtual controller emulation infrastructure.

---

## Disclaimer

This application was vibecoded.
