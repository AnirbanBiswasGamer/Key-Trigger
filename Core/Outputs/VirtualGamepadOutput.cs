using System;
using Nefarius.ViGEm.Client;
using Nefarius.ViGEm.Client.Exceptions;
using Nefarius.ViGEm.Client.Targets;
using Nefarius.ViGEm.Client.Targets.Xbox360;

namespace KeyTrigger.Core.Outputs
{
    public class VirtualGamepadOutput : IThrottleOutput
    {
        private ViGEmClient? _client;
        private IXbox360Controller? _controller;
        private bool _isInitialized;
        private bool _isAvailable;
        private string _statusMessage = "Not initialized";
        private readonly object _syncLock = new();

        private byte _lastRt = 0;
        private byte _lastLt = 0;
        private short _lastSteer = 0;
        private long _lastReportTimestamp = 0;

        private bool _lsUp = false;
        private bool _lsDown = false;
        private bool _rsUp = false;
        private bool _rsDown = false;
        private bool _rsLeft = false;
        private bool _rsRight = false;

        private static readonly System.Collections.Generic.Dictionary<string, Xbox360Button> ButtonLookup = new(StringComparer.OrdinalIgnoreCase)
        {
            { "A", Xbox360Button.A },
            { "B", Xbox360Button.B },
            { "X", Xbox360Button.X },
            { "Y", Xbox360Button.Y },
            { "LB", Xbox360Button.LeftShoulder },
            { "RB", Xbox360Button.RightShoulder },
            { "Start", Xbox360Button.Start },
            { "Back", Xbox360Button.Back },
            { "LS", Xbox360Button.LeftThumb },
            { "RS", Xbox360Button.RightThumb },
            { "Up", Xbox360Button.Up },
            { "Down", Xbox360Button.Down },
            { "Left", Xbox360Button.Left },
            { "Right", Xbox360Button.Right },
            { "Guide", Xbox360Button.Guide }
        };

        public OutputMode Mode => OutputMode.VirtualGamepad;
        public string Name => "Virtual Xbox Controller (ViGEm 1000Hz)";
        public bool IsAvailable => _isAvailable;
        public string StatusMessage => _statusMessage;

        public void Initialize()
        {
            lock (_syncLock)
            {
                if (_isInitialized && _isAvailable) return;

                try
                {
                    _client?.Dispose();
                    _client = new ViGEmClient();
                    _controller = _client.CreateXbox360Controller();
                    _controller.Connect();
                    int userIdx = -1;
                    try { userIdx = _controller.UserIndex; } catch { }
                    _isAvailable = true;
                    if (userIdx > 0)
                    {
                        _statusMessage = $"Virtual Controller connected as Player {userIdx + 1} (Slot {userIdx}). Warning: Games usually only listen to Player 1!";
                    }
                    else if (userIdx == 0)
                    {
                        _statusMessage = "Connected as Virtual Xbox 360 Controller (Player 1, 1000Hz 1ms Polling)";
                    }
                    else
                    {
                        _statusMessage = "Connected as Virtual Xbox 360 Controller (1000Hz 1ms Polling)";
                    }
                    _isInitialized = true;
                }
                catch (VigemBusNotFoundException)
                {
                    _isAvailable = false;
                    _statusMessage = "ViGEmBus driver not installed on this PC. Please install ViGEmBus driver.";
                    _client = null;
                    _controller = null;
                }
                catch (VigemBusAccessFailedException)
                {
                    _isAvailable = false;
                    _statusMessage = "Access to ViGEmBus failed (check permissions or reboot).";
                    _client = null;
                    _controller = null;
                }
                catch (Exception ex)
                {
                    _isAvailable = false;
                    _statusMessage = $"Virtual Gamepad error: {ex.Message}";
                    _client = null;
                    _controller = null;
                }
            }
        }

        public void Update(double throttle, double brake, double steer = 0.0)
        {
            if (!_isAvailable || _controller == null) return;

            lock (_syncLock)
            {
                try
                {
                    byte rt = (byte)Math.Clamp((int)Math.Round(throttle * 255.0), 0, 255);
                    byte lt = (byte)Math.Clamp((int)Math.Round(brake * 255.0), 0, 255);
                    short steerVal = (short)Math.Clamp((int)Math.Round(steer * 32767.0), -32768, 32767);

                    long now = System.Diagnostics.Stopwatch.GetTimestamp();

                    // Ultra-low latency: Submit immediately on any value change, or at least every 50ms heartbeat
                    if (rt != _lastRt || lt != _lastLt || steerVal != _lastSteer || (now - _lastReportTimestamp) > System.Diagnostics.Stopwatch.Frequency / 20)
                    {
                        _lastRt = rt;
                        _lastLt = lt;
                        _lastSteer = steerVal;
                        _lastReportTimestamp = now;

                        _controller.SetSliderValue(Xbox360Slider.RightTrigger, rt);
                        _controller.SetSliderValue(Xbox360Slider.LeftTrigger, lt);
                        _controller.SetAxisValue(Xbox360Axis.LeftThumbX, steerVal);
                        _controller.SubmitReport();
                    }
                }
                catch
                {
                    // Ignore transient write errors if device unplugged
                }
            }
        }

        public void SetButton(string buttonId, bool pressed)
        {
            if (!_isAvailable || _controller == null) return;

            lock (_syncLock)
            {
                try
                {
                    // Check Left Stick Up/Down
                    if (buttonId.Equals("LS_Up", StringComparison.OrdinalIgnoreCase))
                    {
                        if (_lsUp == pressed) return;
                        _lsUp = pressed;
                        short lsY = _lsUp && !_lsDown ? (short)32767 : (_lsDown && !_lsUp ? (short)-32768 : (short)0);
                        _controller.SetAxisValue(Xbox360Axis.LeftThumbY, lsY);
                        _controller.SubmitReport();
                        return;
                    }
                    if (buttonId.Equals("LS_Down", StringComparison.OrdinalIgnoreCase))
                    {
                        if (_lsDown == pressed) return;
                        _lsDown = pressed;
                        short lsY = _lsUp && !_lsDown ? (short)32767 : (_lsDown && !_lsUp ? (short)-32768 : (short)0);
                        _controller.SetAxisValue(Xbox360Axis.LeftThumbY, lsY);
                        _controller.SubmitReport();
                        return;
                    }

                    // Check Right Stick Directions (Up, Down, Left, Right)
                    if (buttonId.Equals("RS_Up", StringComparison.OrdinalIgnoreCase))
                    {
                        if (_rsUp == pressed) return;
                        _rsUp = pressed;
                        short rsY = _rsUp && !_rsDown ? (short)32767 : (_rsDown && !_rsUp ? (short)-32768 : (short)0);
                        _controller.SetAxisValue(Xbox360Axis.RightThumbY, rsY);
                        _controller.SubmitReport();
                        return;
                    }
                    if (buttonId.Equals("RS_Down", StringComparison.OrdinalIgnoreCase))
                    {
                        if (_rsDown == pressed) return;
                        _rsDown = pressed;
                        short rsY = _rsUp && !_rsDown ? (short)32767 : (_rsDown && !_rsUp ? (short)-32768 : (short)0);
                        _controller.SetAxisValue(Xbox360Axis.RightThumbY, rsY);
                        _controller.SubmitReport();
                        return;
                    }
                    if (buttonId.Equals("RS_Left", StringComparison.OrdinalIgnoreCase))
                    {
                        if (_rsLeft == pressed) return;
                        _rsLeft = pressed;
                        short rsX = _rsRight && !_rsLeft ? (short)32767 : (_rsLeft && !_rsRight ? (short)-32768 : (short)0);
                        _controller.SetAxisValue(Xbox360Axis.RightThumbX, rsX);
                        _controller.SubmitReport();
                        return;
                    }
                    if (buttonId.Equals("RS_Right", StringComparison.OrdinalIgnoreCase))
                    {
                        if (_rsRight == pressed) return;
                        _rsRight = pressed;
                        short rsX = _rsRight && !_rsLeft ? (short)32767 : (_rsLeft && !_rsRight ? (short)-32768 : (short)0);
                        _controller.SetAxisValue(Xbox360Axis.RightThumbX, rsX);
                        _controller.SubmitReport();
                        return;
                    }

                    // Standard Buttons
                    if (ButtonLookup.TryGetValue(buttonId, out var btn))
                    {
                        _controller.SetButtonState(btn, pressed);
                        _controller.SubmitReport();
                    }
                }
                catch
                {
                    // Ignore transient write errors
                }
            }
        }

        public void ResetButtons()
        {
            if (!_isAvailable || _controller == null) return;

            lock (_syncLock)
            {
                try
                {
                    _lsUp = false;
                    _lsDown = false;
                    _rsUp = false;
                    _rsDown = false;
                    _rsLeft = false;
                    _rsRight = false;
                    _controller.SetAxisValue(Xbox360Axis.LeftThumbY, 0);
                    _controller.SetAxisValue(Xbox360Axis.RightThumbX, 0);
                    _controller.SetAxisValue(Xbox360Axis.RightThumbY, 0);

                    foreach (var btn in ButtonLookup.Values)
                    {
                        _controller.SetButtonState(btn, false);
                    }
                    _controller.SubmitReport();
                }
                catch
                {
                    // Ignore transient write errors
                }
            }
        }

        public void Reset()
        {
            _lastRt = 255; // Force state change
            _lastLt = 255;
            _lastSteer = -1;
            Update(0.0, 0.0, 0.0);
            ResetButtons();
        }

        public void Dispose()
        {
            lock (_syncLock)
            {
                try
                {
                    if (_controller != null)
                    {
                        Reset();
                        _controller.Disconnect();
                        _controller = null;
                    }

                    _client?.Dispose();
                    _client = null;
                }
                catch
                {
                    // Ignore on disposal
                }
                finally
                {
                    _isInitialized = false;
                    _isAvailable = false;
                    _statusMessage = "Disconnected";
                }
            }
        }
    }
}
