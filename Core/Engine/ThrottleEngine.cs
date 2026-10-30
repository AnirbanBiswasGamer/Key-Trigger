using System;
using System.Diagnostics;
using System.IO;
using System.Media;
using System.Threading;
using System.Threading.Tasks;
using KeyTrigger.Core.Hooks;
using KeyTrigger.Core.Outputs;
using KeyTrigger.Core.Win32;
using KeyTrigger.Models;

namespace KeyTrigger.Core.Engine
{
    public class ThrottleTelemetry
    {
        public bool IsActive { get; set; }
        public bool IsThrottleKeyPressed { get; set; }
        public bool IsBrakeKeyPressed { get; set; }
        public double ThrottleProgress { get; set; }
        public double OutputThrottle { get; set; }
        public double BrakeProgress { get; set; }
        public double OutputBrake { get; set; }
        public OutputMode ActiveOutputMode { get; set; }
        public bool IsOutputAvailable { get; set; }
        public string OutputStatusMessage { get; set; } = string.Empty;
    }

    public class ThrottleEngine : IDisposable
    {
        private readonly AppSettings _settings;
        private readonly GlobalKeyboardHook _keyboardHook;
        private readonly VirtualGamepadOutput _gamepadOutput;

        // Starts disabled by default per user requirement; activates manually or via GameWatcher auto-detection
        private bool _isActive = false;
        private volatile bool _isThrottleDown;
        private volatile bool _isBrakeDown;
        private volatile bool _isSteerLeftDown;
        private volatile bool _isSteerRightDown;

        private double _throttleProgress;
        private double _brakeProgress;
        private double _steerLeftProgress;
        private double _steerRightProgress;
        private double _lastEvaluatedThrottle;
        private double _lastEvaluatedBrake;
        private double _lastEvaluatedSteer;

        private Thread? _engineThread;
        private volatile bool _isRunning;
        private readonly object _stateLock = new();

        public event Action<ThrottleTelemetry>? TelemetryUpdated;
        public event Action<bool>? ActiveStateChanged;
        public event Action<ThrottleProfile>? ProfileActivated;

        public AppSettings Settings => _settings;
        public GlobalKeyboardHook KeyboardHook => _keyboardHook;
        public VirtualGamepadOutput GamepadOutput => _gamepadOutput;

        public double LastEvaluatedThrottle => _lastEvaluatedThrottle;
        public double LastEvaluatedBrake => _lastEvaluatedBrake;
        public double LastEvaluatedSteer => _lastEvaluatedSteer;
        public bool IsThrottleDown => _isThrottleDown;
        public bool IsBrakeDown => _isBrakeDown;
        public bool IsSteerLeftDown => _isSteerLeftDown;
        public bool IsSteerRightDown => _isSteerRightDown;

        public bool IsActive
        {
            get => _isActive;
            set
            {
                if (_isActive != value)
                {
                    _isActive = value;
                    if (!_isActive)
                    {
                        ResetAll();
                    }
                    ActiveStateChanged?.Invoke(_isActive);
                }
            }
        }

        public ThrottleEngine(AppSettings settings)
        {
            _settings = settings;
            _keyboardHook = new GlobalKeyboardHook
            {
                ToggleHotKeyVk = _settings.ToggleHotKeyVk
            };

            _gamepadOutput = new VirtualGamepadOutput();
            _keyboardHook.KeyIntercepted += OnKeyIntercepted;
        }

        public void Start()
        {
            if (_isRunning) return;

            // Request 1ms OS multimedia timer precision for true 1000Hz polling rate
            NativeMethods.TimeBeginPeriod(1);

            _gamepadOutput.Initialize();
            _keyboardHook.Install();

            _isRunning = true;
            _engineThread = new Thread(EngineLoop)
            {
                IsBackground = true,
                Priority = ThreadPriority.Highest,
                Name = "KeyTrigger_1000Hz_Engine"
            };
            _engineThread.Start();
        }

        public void UpdateKeyBindings()
        {
            lock (_stateLock)
            {
                _keyboardHook.ToggleHotKeyVk = _settings.ToggleHotKeyVk;
            }
        }

        public void SwitchToProfile(ThrottleProfile profile)
        {
            lock (_stateLock)
            {
                // Sync current state to active profile before switching
                _settings.SyncToActiveProfile();

                _settings.ActiveProfileId = profile.Id;
                _settings.SyncFromActiveProfile();
                _settings.Save();
            }

            // Immediately apply new key bindings & controller mappings
            UpdateKeyBindings();

            int index = _settings.Profiles.IndexOf(profile);
            if (index < 0) index = 0;
            PlaySubtleSwitchSound(index);

            ProfileActivated?.Invoke(profile);
        }

        private static void PlaySubtleSwitchSound(int index)
        {
            Task.Run(() =>
            {
                try
                {
                    int sampleRate = 44100;
                    int durationMs = 36;
                    int numSamples = sampleRate * durationMs / 1000;
                    double freq = 480.0 + (Math.Clamp(index, 0, 7) * 50.0);

                    byte[] wavBytes = new byte[44 + numSamples * 2];
                    using var ms = new MemoryStream(wavBytes);
                    using var writer = new BinaryWriter(ms);

                    writer.Write(new char[4] { 'R', 'I', 'F', 'F' });
                    writer.Write(36 + numSamples * 2);
                    writer.Write(new char[4] { 'W', 'A', 'V', 'E' });
                    writer.Write(new char[4] { 'f', 'm', 't', ' ' });
                    writer.Write(16);
                    writer.Write((short)1); // PCM
                    writer.Write((short)1); // Mono
                    writer.Write(sampleRate);
                    writer.Write(sampleRate * 2);
                    writer.Write((short)2);
                    writer.Write((short)16);
                    writer.Write(new char[4] { 'd', 'a', 't', 'a' });
                    writer.Write(numSamples * 2);

                    short maxAmp = 4200; // ~13% volume for a gentle, subtle haptic audio cue
                    for (int i = 0; i < numSamples; i++)
                    {
                        double t = (double)i / sampleRate;
                        double env = (i < 80) ? (i / 80.0) : Math.Exp(-(i - 80) / (double)(numSamples * 0.35));
                        short sample = (short)(Math.Sin(2.0 * Math.PI * freq * t) * maxAmp * env);
                        writer.Write(sample);
                    }

                    ms.Position = 0;
                    using var player = new SoundPlayer(ms);
                    player.PlaySync();
                }
                catch
                {
                    try { Console.Beep(480, 25); } catch { }
                }
            });
        }

        private void OnKeyIntercepted(int vkCode, bool isDown, ref bool swallowKey)
        {
            // Check global toggle hotkey
            if (vkCode == _settings.ToggleHotKeyVk && isDown)
            {
                IsActive = !IsActive;
                swallowKey = true;
                return;
            }

            // Check profile hotkey
            if (isDown && _settings.Profiles != null)
            {
                var matched = _settings.Profiles.FirstOrDefault(p => p.HotkeyVkCode != 0 && p.HotkeyVkCode == vkCode);
                if (matched != null)
                {
                    SwitchToProfile(matched);
                    swallowKey = true;
                    return;
                }
            }

            if (!IsActive) return;

            // Check Throttle Key
            if (vkCode == _settings.ThrottleVkCode)
            {
                _isThrottleDown = isDown;
                if (_settings.BlockNativeKey)
                {
                    swallowKey = true;
                }

                if (!_settings.EnableThrottle)
                {
                    // Instant Direct Digital mode (0ms to MaxThrottle RT, 0.0 on release)
                    _lastEvaluatedThrottle = isDown ? _settings.MaxThrottle : 0.0;
                    _throttleProgress = isDown ? 1.0 : 0.0;
                    _gamepadOutput.Update(_lastEvaluatedThrottle, _lastEvaluatedBrake, _lastEvaluatedSteer);
                    return;
                }

                if (isDown && _throttleProgress <= 0.0001 && _settings.MinThrottle > 0)
                {
                    _throttleProgress = 0.001;
                    _lastEvaluatedThrottle = _settings.MinThrottle;
                    _gamepadOutput.Update(_lastEvaluatedThrottle, _lastEvaluatedBrake, _lastEvaluatedSteer);
                }
                return;
            }

            // Check Brake Key
            if (vkCode == _settings.BrakeVkCode)
            {
                _isBrakeDown = isDown;
                if (_settings.BlockNativeKey)
                {
                    swallowKey = true;
                }

                if (!_settings.EnableBrake)
                {
                    // Instant Direct Digital mode (0ms to MaxBrake LT, 0.0 on release)
                    _lastEvaluatedBrake = isDown ? _settings.MaxBrake : 0.0;
                    _brakeProgress = isDown ? 1.0 : 0.0;
                    _gamepadOutput.Update(_lastEvaluatedThrottle, _lastEvaluatedBrake, _lastEvaluatedSteer);
                    return;
                }

                if (isDown && _brakeProgress <= 0.0001 && _settings.MinBrake > 0)
                {
                    _brakeProgress = 0.001;
                    _lastEvaluatedBrake = _settings.MinBrake;
                    _gamepadOutput.Update(_lastEvaluatedThrottle, _lastEvaluatedBrake, _lastEvaluatedSteer);
                }
                return;
            }

            // Check Steer Left Key
            if (vkCode == _settings.SteerLeftVkCode)
            {
                _isSteerLeftDown = isDown;
                if (_settings.BlockNativeKey)
                {
                    swallowKey = true;
                }

                if (!_settings.EnableSteering)
                {
                    // Instant Direct Digital Steer Left
                    double dir = isDown ? -_settings.MaxSteer : (_isSteerRightDown ? _settings.MaxSteer : 0.0);
                    _lastEvaluatedSteer = dir;
                    _steerLeftProgress = isDown ? 1.0 : 0.0;
                    _gamepadOutput.Update(_lastEvaluatedThrottle, _lastEvaluatedBrake, _lastEvaluatedSteer);
                }
                return;
            }

            // Check Steer Right Key
            if (vkCode == _settings.SteerRightVkCode)
            {
                _isSteerRightDown = isDown;
                if (_settings.BlockNativeKey)
                {
                    swallowKey = true;
                }

                if (!_settings.EnableSteering)
                {
                    // Instant Direct Digital Steer Right
                    double dir = isDown ? _settings.MaxSteer : (_isSteerLeftDown ? -_settings.MaxSteer : 0.0);
                    _lastEvaluatedSteer = dir;
                    _steerRightProgress = isDown ? 1.0 : 0.0;
                    _gamepadOutput.Update(_lastEvaluatedThrottle, _lastEvaluatedBrake, _lastEvaluatedSteer);
                }
                return;
            }

            // Check Remapped Controller Buttons (A, B, X, Y, LB, RB, Start, Back, D-Pad, LS, RS)
            if (_settings.ButtonMappings != null)
            {
                var mapping = _settings.ButtonMappings.FirstOrDefault(m => m.VkCode != 0 && m.VkCode == vkCode);
                if (mapping != null)
                {
                    if (_settings.BlockNativeKey)
                    {
                        swallowKey = true;
                    }
                    _gamepadOutput.SetButton(mapping.ButtonId, isDown);
                    return;
                }
            }
        }

        private bool _lastF8Down = false;
        private int _lastProfileHotkeyDown = 0;

        private void PollHardwareInputs()
        {
            // 1. Hardware F8 Master Toggle hotkey
            if (_settings.ToggleHotKeyVk != 0)
            {
                bool isF8Down = (NativeMethods.GetAsyncKeyState(_settings.ToggleHotKeyVk) & 0x8000) != 0;
                if (isF8Down && !_lastF8Down)
                {
                    IsActive = !IsActive;
                }
                _lastF8Down = isF8Down;
            }

            // 2. Hardware Profile switch hotkeys (F1-F4)
            if (_settings.Profiles != null)
            {
                foreach (var profile in _settings.Profiles)
                {
                    if (profile.HotkeyVkCode != 0)
                    {
                        bool isKeyDown = (NativeMethods.GetAsyncKeyState(profile.HotkeyVkCode) & 0x8000) != 0;
                        if (isKeyDown && _lastProfileHotkeyDown != profile.HotkeyVkCode)
                        {
                            _lastProfileHotkeyDown = profile.HotkeyVkCode;
                            SwitchToProfile(profile);
                            break;
                        }
                        else if (!isKeyDown && _lastProfileHotkeyDown == profile.HotkeyVkCode)
                        {
                            _lastProfileHotkeyDown = 0;
                        }
                    }
                }
            }

            if (!_isActive)
            {
                _isThrottleDown = false;
                _isBrakeDown = false;
                _isSteerLeftDown = false;
                _isSteerRightDown = false;
                return;
            }

            // 3. Hardware state for Throttle, Brake, Steering Left & Right
            if (_settings.ThrottleVkCode != 0)
            {
                _isThrottleDown = (NativeMethods.GetAsyncKeyState(_settings.ThrottleVkCode) & 0x8000) != 0;
            }

            if (_settings.BrakeVkCode != 0)
            {
                _isBrakeDown = (NativeMethods.GetAsyncKeyState(_settings.BrakeVkCode) & 0x8000) != 0;
            }

            if (_settings.SteerLeftVkCode != 0)
            {
                _isSteerLeftDown = (NativeMethods.GetAsyncKeyState(_settings.SteerLeftVkCode) & 0x8000) != 0;
            }

            if (_settings.SteerRightVkCode != 0)
            {
                _isSteerRightDown = (NativeMethods.GetAsyncKeyState(_settings.SteerRightVkCode) & 0x8000) != 0;
            }

            // 4. Controller Button Mappings (A, B, X, Y, LB, RB, Start, Back, D-Pad, LS, RS)
            if (_settings.ButtonMappings != null)
            {
                for (int i = 0; i < _settings.ButtonMappings.Count; i++)
                {
                    var mapping = _settings.ButtonMappings[i];
                    if (mapping.VkCode != 0)
                    {
                        bool isDown = (NativeMethods.GetAsyncKeyState(mapping.VkCode) & 0x8000) != 0;
                        _gamepadOutput.SetButton(mapping.ButtonId, isDown);
                    }
                }
            }
        }

        private void EngineLoop()
        {
            var sw = Stopwatch.StartNew();
            long lastTick = sw.ElapsedTicks;
            long lastTelemetryTick = 0;
            double invFrequency = 1.0 / Stopwatch.Frequency;

            while (_isRunning)
            {
                long currentTick = sw.ElapsedTicks;
                double deltaSeconds = (currentTick - lastTick) * invFrequency;
                lastTick = currentTick;

                if (deltaSeconds > 0.05) deltaSeconds = 0.05; // Prevent jump spikes

                // Bulletproof direct hardware keyboard polling at 1000Hz:
                // Guarantees keys are never missed even if WH_KEYBOARD_LL hook is timed out or blocked by Windows
                PollHardwareInputs();

                if (_isActive)
                {
                    UpdateRamp(deltaSeconds);
                }
                else
                {
                    _throttleProgress = 0.0;
                    _brakeProgress = 0.0;
                    _steerLeftProgress = 0.0;
                    _steerRightProgress = 0.0;
                    _lastEvaluatedThrottle = 0.0;
                    _lastEvaluatedBrake = 0.0;
                    _lastEvaluatedSteer = 0.0;
                }

                // Ultra-responsive Virtual Gamepad Output
                try
                {
                    _gamepadOutput.Update(_lastEvaluatedThrottle, _lastEvaluatedBrake, _lastEvaluatedSteer);
                }
                catch
                {
                    // Ignore dispatch errors
                }

                // Publish UI telemetry at a clean ~30 FPS (every 33ms) to use minimal CPU resources
                long currentMs = sw.ElapsedMilliseconds;
                if (currentMs - lastTelemetryTick >= 33)
                {
                    lastTelemetryTick = currentMs;

                    if (TelemetryUpdated != null)
                    {
                        var telemetry = new ThrottleTelemetry
                        {
                            IsActive = _isActive,
                            IsThrottleKeyPressed = _isThrottleDown,
                            IsBrakeKeyPressed = _isBrakeDown,
                            ThrottleProgress = _throttleProgress,
                            OutputThrottle = _lastEvaluatedThrottle,
                            BrakeProgress = _brakeProgress,
                            OutputBrake = _lastEvaluatedBrake,
                            ActiveOutputMode = OutputMode.VirtualGamepad,
                            IsOutputAvailable = _gamepadOutput.IsAvailable,
                            OutputStatusMessage = _gamepadOutput.StatusMessage
                        };

                        TelemetryUpdated.Invoke(telemetry);
                    }
                }

                // 1ms kernel-level sleep yielding CPU (enabled by TimeBeginPeriod(1)) -> 0% CPU usage!
                Thread.Sleep(1);
            }
        }

        private void UpdateRamp(double dt)
        {
            // --- Throttle Ramp ---
            if (_settings.EnableThrottle)
            {
                double attackTime = Math.Max(0.01, _settings.AttackTimeSeconds);
                double releaseTime = Math.Max(0.01, _settings.ReleaseTimeSeconds);

                if (_isThrottleDown)
                {
                    _throttleProgress += dt / attackTime;
                    if (_throttleProgress > 1.0) _throttleProgress = 1.0;
                }
                else
                {
                    _throttleProgress -= dt / releaseTime;
                    if (_throttleProgress < 0.0) _throttleProgress = 0.0;
                }

                if (_throttleProgress > 0.0001)
                {
                    double curved = ResponseCurves.Evaluate(_settings.CurveType, _throttleProgress, _settings.CustomP1X, _settings.CustomP1Y, _settings.CustomP2X, _settings.CustomP2Y);
                    double minT = Math.Clamp(_settings.MinThrottle, 0.0, 0.9);
                    double maxT = Math.Clamp(_settings.MaxThrottle, minT + 0.05, 1.0);
                    _lastEvaluatedThrottle = minT + curved * (maxT - minT);
                }
                else
                {
                    _lastEvaluatedThrottle = 0.0;
                }
            }
            else
            {
                // Direct digital controller output: instant max throttle when pressed, 0.0 when released
                _lastEvaluatedThrottle = _isThrottleDown ? _settings.MaxThrottle : 0.0;
                _throttleProgress = _isThrottleDown ? 1.0 : 0.0;
            }

            // --- Brake Ramp ---
            if (_settings.EnableBrake)
            {
                double bAttackTime = Math.Max(0.01, _settings.BrakeAttackTimeSeconds);
                double bReleaseTime = Math.Max(0.01, _settings.BrakeReleaseTimeSeconds);

                if (_isBrakeDown)
                {
                    _brakeProgress += dt / bAttackTime;
                    if (_brakeProgress > 1.0) _brakeProgress = 1.0;
                }
                else
                {
                    _brakeProgress -= dt / bReleaseTime;
                    if (_brakeProgress < 0.0) _brakeProgress = 0.0;
                }

                if (_brakeProgress > 0.0001)
                {
                    double curved = ResponseCurves.Evaluate(_settings.BrakeCurveType, _brakeProgress, _settings.BrakeCustomP1X, _settings.BrakeCustomP1Y, _settings.BrakeCustomP2X, _settings.BrakeCustomP2Y);
                    double minB = Math.Clamp(_settings.MinBrake, 0.0, 0.9);
                    double maxB = Math.Clamp(_settings.MaxBrake, minB + 0.05, 1.0);
                    _lastEvaluatedBrake = minB + curved * (maxB - minB);
                }
                else
                {
                    _lastEvaluatedBrake = 0.0;
                }
            }
            else
            {
                // Direct digital controller output: instant max brake when pressed, 0.0 when released
                _lastEvaluatedBrake = _isBrakeDown ? _settings.MaxBrake : 0.0;
                _brakeProgress = _isBrakeDown ? 1.0 : 0.0;
            }

            // --- Steering (Unified Left & Right) ---
            if (_settings.EnableSteering)
            {
                double sAttack = Math.Max(0.01, _settings.SteerAttackTimeSeconds);
                double sRelease = Math.Max(0.01, _settings.SteerReleaseTimeSeconds);
                double minS = Math.Clamp(_settings.MinSteer, 0.0, 0.5);
                double maxS = Math.Clamp(_settings.MaxSteer, minS + 0.05, 1.0);

                // Left Turn
                double leftAmount = 0.0;
                if (_isSteerLeftDown)
                {
                    _steerLeftProgress += dt / sAttack;
                    if (_steerLeftProgress > 1.0) _steerLeftProgress = 1.0;
                }
                else
                {
                    _steerLeftProgress -= dt / sRelease;
                    if (_steerLeftProgress < 0.0) _steerLeftProgress = 0.0;
                }

                if (_steerLeftProgress > 0.0001)
                {
                    double curved = ResponseCurves.Evaluate(_settings.SteerCurveType, _steerLeftProgress, _settings.SteerCustomP1X, _settings.SteerCustomP1Y, _settings.SteerCustomP2X, _settings.SteerCustomP2Y);
                    leftAmount = minS + curved * (maxS - minS);
                }

                // Right Turn
                double rightAmount = 0.0;
                if (_isSteerRightDown)
                {
                    _steerRightProgress += dt / sAttack;
                    if (_steerRightProgress > 1.0) _steerRightProgress = 1.0;
                }
                else
                {
                    _steerRightProgress -= dt / sRelease;
                    if (_steerRightProgress < 0.0) _steerRightProgress = 0.0;
                }

                if (_steerRightProgress > 0.0001)
                {
                    double curved = ResponseCurves.Evaluate(_settings.SteerCurveType, _steerRightProgress, _settings.SteerCustomP1X, _settings.SteerCustomP1Y, _settings.SteerCustomP2X, _settings.SteerCustomP2Y);
                    rightAmount = minS + curved * (maxS - minS);
                }

                _lastEvaluatedSteer = Math.Clamp(rightAmount - leftAmount, -1.0, 1.0);
            }
            else
            {
                // Direct digital controller steering: instant max steering when pressed, 0.0 when released
                double dir = 0.0;
                if (_isSteerLeftDown && !_isSteerRightDown) dir = -_settings.MaxSteer;
                else if (_isSteerRightDown && !_isSteerLeftDown) dir = _settings.MaxSteer;
                _lastEvaluatedSteer = dir;
                _steerLeftProgress = _isSteerLeftDown ? 1.0 : 0.0;
                _steerRightProgress = _isSteerRightDown ? 1.0 : 0.0;
            }
        }

        public void ResetAll()
        {
            _throttleProgress = 0.0;
            _brakeProgress = 0.0;
            _steerLeftProgress = 0.0;
            _steerRightProgress = 0.0;
            _lastEvaluatedThrottle = 0.0;
            _lastEvaluatedBrake = 0.0;
            _lastEvaluatedSteer = 0.0;
            _gamepadOutput.Reset();
            _gamepadOutput.ResetButtons();
        }

        public void Dispose()
        {
            _isRunning = false;
            try
            {
                _engineThread?.Join(300);
            }
            catch { }

            NativeMethods.TimeEndPeriod(1);
            _keyboardHook.Dispose();
            _gamepadOutput.Dispose();
        }
    }
}
