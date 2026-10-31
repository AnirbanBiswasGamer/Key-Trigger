using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using KeyTrigger.Core.Utils;
using KeyTrigger.Core.Win32;

namespace KeyTrigger.Core.Hooks
{
    public delegate void KeyInterceptEventHandler(int vkCode, bool isDown, ref bool swallowKey);

    // Low-level keyboard hook that runs on its own thread and keeps itself at the head of the hook chain.
    //
    // Why it is built this way (measured with HookProbe against GRID Autosport, an EGO-engine game):
    //  - While an EGO game window has focus, Windows does not call any WH_KEYBOARD_LL hook installed before the game
    //    took focus; a hook installed after that works normally. Keys then bypass KeyTrigger completely (WASD do
    //    nothing, or a key-up is lost and the virtual pad stays held). So the hook re-installs itself on every
    //    foreground change (then every 50 ms for 2 s, as the game installs its own hook shortly after activating)
    //    and once a second otherwise.
    //  - Windows calls a low-level hook by sending a message to the thread that installed it. On the WPF UI thread
    //    every layout or render stall counts against LowLevelHooksTimeout, and a hook that times out is silently
    //    removed. A dedicated thread with its own message loop keeps the callback fast.
    //  - Raw Input (RIDEV_INPUTSINK) is the backstop: keys the hook swallows never reach Raw Input (or GetAsyncKeyState),
    //    so a mapped key that shows up there slipped past the hook, and the engine applies it so no key-up is ever lost.
    public class GlobalKeyboardHook : IDisposable
    {
        private const string WindowClassName = "KeyTrigger_KeyboardHookWindow";
        private const uint PeriodicRehookMs = 1000;
        private const uint FocusRehookMs = 50;
        private const int FocusRehookWindowMs = 2000;
        // Longer than Windows lets a few hook calls take (LowLevelHooksTimeout is capped at 1 s per hook), so a key
        // stuck behind a couple of slow hooks still finds one of ours. Anything slower is caught by the Raw Input backstop.
        private const int RetireAfterMs = 3000;
        private static readonly IntPtr TimerPeriodic = new IntPtr(1);
        private static readonly IntPtr TimerFocus = new IntPtr(2);

        private IntPtr _hookHandle = IntPtr.Zero;
        private readonly System.Collections.Generic.List<(IntPtr Handle, long SupersededAt)> _supersededHooks = new();
        private readonly NativeMethods.LowLevelKeyboardProc _proc;
        private readonly NativeMethods.WndProc _wndProc;
        private readonly NativeMethods.WinEventDelegate _winEventProc;
        private Thread? _thread;
        private uint _threadId;
        private IntPtr _window = IntPtr.Zero;
        private IntPtr _winEventHook = IntPtr.Zero;
        private IntPtr _rawBuffer = IntPtr.Zero;
        private long _focusRehookUntil;
        private ManualResetEventSlim? _started;
        private bool _isDisposed;
        private volatile bool _isKeyCaptureActive;
        private int _rehookCount;
        private int _removedByWindowsCount;

        // Hook-thread only: the last event handled (to answer a duplicate from an older hook the same way), the time and
        // direction of the last hook event per key (to drop Raw Input events that are older than it), and the key code
        // the hook last reported per non-extended scan code (to translate numpad keys the way the hook does)
        private NativeMethods.KBDLLHOOKSTRUCT _lastEvent;
        private bool _lastEventSwallowed;
        private bool _hasLastEvent;
        private readonly uint[] _lastHookTime = new uint[256];
        private readonly bool[] _lastHookDown = new bool[256];
        private readonly bool[] _hookSeen = new bool[256];
        private readonly int[] _vkByScanCode = new int[256];

        public event KeyInterceptEventHandler? KeyIntercepted;

        // --diag only: which keys to trace per event (the engine passes "is this a mapped key"), so the log never
        // records anything Key Trigger doesn't map
        public Func<int, bool>? IsTracedKey { get; set; }
        public event Action<int>? KeyCapturedForBinding;

        // Raised on the hook thread for every key Raw Input reports (vkCode, isDown), using the same left/right
        // modifier codes the hook uses. A key the hook swallowed never arrives here, so a mapped key arriving here
        // means the hook missed it.
        public event Action<int, bool>? RawKeyObserved;

        public bool IsKeyCaptureActive
        {
            get => _isKeyCaptureActive;
            set => _isKeyCaptureActive = value;
        }

        public bool IsInstalled => _hookHandle != IntPtr.Zero;
        public int RehookCount => _rehookCount;
        public int RemovedByWindowsCount => _removedByWindowsCount;

        public GlobalKeyboardHook()
        {
            // Root the delegates so the Garbage Collector never collects them
            _proc = HookCallback;
            _wndProc = WindowProc;
            _winEventProc = OnForegroundChanged;
        }

        public void Install()
        {
            if (_thread != null) return;

            _started = new ManualResetEventSlim(false);
            _thread = new Thread(HookThreadMain)
            {
                IsBackground = true,
                Priority = ThreadPriority.Highest,
                Name = "KeyTrigger_KeyboardHook"
            };
            _thread.Start();

            // Wait for the first install attempt so IsInstalled is meaningful when this returns
            _started.Wait(2000);
        }

        public void Uninstall()
        {
            if (_thread == null) return;

            NativeMethods.PostThreadMessage(_threadId, NativeMethods.WM_QUIT, IntPtr.Zero, IntPtr.Zero);
            _thread.Join(1000);
            _thread = null;
        }

        private void HookThreadMain()
        {
            _threadId = NativeMethods.GetCurrentThreadId();

            // Create this thread's message queue before anyone can post to it
            NativeMethods.PeekMessage(out _, IntPtr.Zero, 0, 0, NativeMethods.PM_NOREMOVE);

            try
            {
                Rehook();
                if (!IsInstalled)
                {
                    int error = Marshal.GetLastWin32Error();
                    Debug.WriteLine($"Low-level keyboard hook warning: {error}. Falling back to 1000Hz hardware polling.");
                    DiagLog.Write($"!! SetWindowsHookEx failed (error {error}) - falling back to polling");
                }

                CreateMessageWindow();

                // Delivered to this thread's message loop, independent of the UI thread
                _winEventHook = NativeMethods.SetWinEventHook(
                    NativeMethods.EVENT_SYSTEM_FOREGROUND,
                    NativeMethods.EVENT_SYSTEM_FOREGROUND,
                    IntPtr.Zero,
                    _winEventProc,
                    0,
                    0,
                    NativeMethods.WINEVENT_OUTOFCONTEXT);
            }
            catch (Exception ex)
            {
                DiagLog.Write($"!! keyboard hook thread setup failed: {ex.Message}");
            }
            finally
            {
                _started?.Set();
            }

            while (NativeMethods.GetMessage(out var msg, IntPtr.Zero, 0, 0) > 0)
            {
                NativeMethods.TranslateMessage(ref msg);
                NativeMethods.DispatchMessage(ref msg);
            }

            if (_winEventHook != IntPtr.Zero)
            {
                NativeMethods.UnhookWinEvent(_winEventHook);
                _winEventHook = IntPtr.Zero;
            }
            if (_window != IntPtr.Zero)
            {
                NativeMethods.DestroyWindow(_window);
                _window = IntPtr.Zero;
            }
            foreach (var superseded in _supersededHooks)
            {
                NativeMethods.UnhookWindowsHookEx(superseded.Handle);
            }
            _supersededHooks.Clear();
            if (_hookHandle != IntPtr.Zero)
            {
                NativeMethods.UnhookWindowsHookEx(_hookHandle);
                _hookHandle = IntPtr.Zero;
            }
            if (_rawBuffer != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(_rawBuffer);
                _rawBuffer = IntPtr.Zero;
            }
        }

        private void CreateMessageWindow()
        {
            IntPtr hInstance = NativeMethods.GetModuleHandle(null);
            var wc = new NativeMethods.WNDCLASSEX
            {
                cbSize = (uint)Marshal.SizeOf<NativeMethods.WNDCLASSEX>(),
                lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProc),
                hInstance = hInstance,
                lpszClassName = WindowClassName
            };
            NativeMethods.RegisterClassEx(ref wc); // Fails harmlessly if a previous Install already registered it

            _window = NativeMethods.CreateWindowEx(0, WindowClassName, string.Empty, 0, 0, 0, 0, 0, NativeMethods.HWND_MESSAGE, IntPtr.Zero, hInstance, IntPtr.Zero);
            if (_window == IntPtr.Zero)
            {
                DiagLog.Write($"!! could not create the hook message window (error {Marshal.GetLastWin32Error()}) - no periodic re-hook or Raw Input backstop");
                return;
            }

            _rawBuffer = Marshal.AllocHGlobal(512);
            var devices = new[]
            {
                new NativeMethods.RAWINPUTDEVICE
                {
                    usUsagePage = 0x01, // Generic desktop
                    usUsage = 0x06,     // Keyboard
                    dwFlags = NativeMethods.RIDEV_INPUTSINK,
                    hwndTarget = _window
                }
            };
            if (!NativeMethods.RegisterRawInputDevices(devices, 1, (uint)Marshal.SizeOf<NativeMethods.RAWINPUTDEVICE>()))
            {
                DiagLog.Write($"!! RegisterRawInputDevices failed (error {Marshal.GetLastWin32Error()}) - no Raw Input backstop");
            }

            NativeMethods.SetTimer(_window, TimerPeriodic, PeriodicRehookMs, IntPtr.Zero);
        }

        // Install a fresh hook, which Windows puts at the head of the chain. The hooks it replaces stay installed for
        // RetireAfterMs: a key already on its way down the chain (e.g. waiting on a game's slow hook) may not have
        // reached them yet, and removing them sooner would make that key skip KeyTrigger. A key that reaches several
        // of our hooks is answered once (see HookCallback).
        private void Rehook()
        {
            IntPtr fresh = NativeMethods.SetWindowsHookEx(NativeMethods.WH_KEYBOARD_LL, _proc, NativeMethods.GetModuleHandle(null), 0);
            if (fresh == IntPtr.Zero) return;

            long now = Stopwatch.GetTimestamp();
            if (_hookHandle != IntPtr.Zero)
            {
                _supersededHooks.Add((_hookHandle, now));
            }
            _hookHandle = fresh;
            _rehookCount++;

            long retireBefore = now - Stopwatch.Frequency * RetireAfterMs / 1000;
            for (int i = _supersededHooks.Count - 1; i >= 0; i--)
            {
                if (_supersededHooks[i].SupersededAt > retireBefore) continue;

                if (!NativeMethods.UnhookWindowsHookEx(_supersededHooks[i].Handle))
                {
                    // Windows had already removed it behind our back (LowLevelHooksTimeout exceeded)
                    _removedByWindowsCount++;
                    DiagLog.Write($"!! an earlier keyboard hook had been removed by Windows (hook timeout) (#{_removedByWindowsCount})");
                }
                _supersededHooks.RemoveAt(i);
            }
        }

        private void OnForegroundChanged(IntPtr hWinEventHook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint dwEventThread, uint dwmsEventTime)
        {
            // The newly focused game may install its own hook a moment after it activates, so keep re-hooking
            // at a fast rate for a while rather than once
            Rehook();
            _focusRehookUntil = Stopwatch.GetTimestamp() + Stopwatch.Frequency * FocusRehookWindowMs / 1000;
            if (_window != IntPtr.Zero)
            {
                NativeMethods.SetTimer(_window, TimerFocus, FocusRehookMs, IntPtr.Zero);
            }

            if (DiagLog.Enabled)
            {
                DiagLog.Write($"focus -> {DescribeWindowProcess(hwnd)}");
            }
        }

        private IntPtr WindowProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
        {
            try
            {
                if (msg == NativeMethods.WM_TIMER)
                {
                    if (wParam == TimerFocus && Stopwatch.GetTimestamp() > _focusRehookUntil)
                    {
                        NativeMethods.KillTimer(hWnd, TimerFocus);
                    }
                    Rehook();
                    return IntPtr.Zero;
                }

                if (msg == NativeMethods.WM_INPUT)
                {
                    OnRawInput(lParam);
                }
            }
            catch (Exception ex)
            {
                // Never let an exception escape into the window procedure
                DiagLog.Write($"!! hook window procedure exception: {ex.GetType().Name}: {ex.Message}");
            }

            return NativeMethods.DefWindowProc(hWnd, msg, wParam, lParam);
        }

        private static readonly uint RawInputHeaderSize = (uint)Marshal.SizeOf<NativeMethods.RAWINPUTHEADER>();

        // Reads the structs straight from the buffer: no allocation per key on this thread
        private unsafe void OnRawInput(IntPtr hRawInput)
        {
            if (_rawBuffer == IntPtr.Zero) return;

            uint headerSize = RawInputHeaderSize;
            uint size = 512;
            if (NativeMethods.GetRawInputData(hRawInput, NativeMethods.RID_INPUT, _rawBuffer, ref size, headerSize) == unchecked((uint)-1)) return;

            var header = *(NativeMethods.RAWINPUTHEADER*)_rawBuffer;
            if (header.dwType != NativeMethods.RIM_TYPEKEYBOARD) return;

            var keyboard = *(NativeMethods.RAWKEYBOARD*)(_rawBuffer + (int)headerSize);
            if (keyboard.ExtraInformation == NativeMethods.MAGIC_EXTRA_INFO.ToUInt32()) return;

            int vkCode = ToHookVirtualKey(keyboard);
            if (vkCode == 0) return;

            // Windows hands this thread pending hook calls before posted WM_INPUT, so a Raw Input event can arrive after
            // the hook already handled a later event for the same key. Applying it then would undo that later event.
            // The tick count only moves every ~16 ms, so on a tie only drop it if it repeats the hook's last direction:
            // a release tied with a hooked press is a real release the hook missed.
            bool isDown = (keyboard.Flags & NativeMethods.RI_KEY_BREAK) == 0;
            uint time = unchecked((uint)NativeMethods.GetMessageTime());
            if (_hookSeen[vkCode])
            {
                int hookIsNewerBy = unchecked((int)(_lastHookTime[vkCode] - time));
                if (hookIsNewerBy > 0 || (hookIsNewerBy == 0 && (isDown || !_lastHookDown[vkCode])))
                {
                    if (DiagLog.Enabled && IsTracedKey?.Invoke(vkCode) == true)
                    {
                        DiagLog.Write($"raw  {vkCode:X2} {(isDown ? "down" : "up  ")} t={time} DROPPED as stale (hook last saw t={_lastHookTime[vkCode]} {(_lastHookDown[vkCode] ? "down" : "up")})");
                    }
                    return;
                }
            }

            if (DiagLog.Enabled && IsTracedKey?.Invoke(vkCode) == true)
            {
                DiagLog.Write($"raw  {vkCode:X2} {(isDown ? "down" : "up  ")} t={time} passed to engine");
            }
            RawKeyObserved?.Invoke(vkCode, isDown);
        }

        // Raw Input reports generic modifier codes (VK_SHIFT/VK_CONTROL/VK_MENU) and ignores NumLock; the hook, and so
        // every saved binding, uses left/right modifier codes and VK_NUMPADn. Returns 0 for events the backstop should ignore.
        private int ToHookVirtualKey(NativeMethods.RAWKEYBOARD keyboard)
        {
            int vk = keyboard.VKey;
            bool e0 = (keyboard.Flags & NativeMethods.RI_KEY_E0) != 0;

            // 0xFF is used for fake keys (e.g. the E1 prefix of Pause)
            if (vk == 0 || vk >= 0xFF) return 0;

            switch (vk)
            {
                case 0x10: // VK_SHIFT: an E0 shift is the fake shift Windows wraps around some navigation keys
                    return e0 ? 0 : (int)NativeMethods.MapVirtualKey(keyboard.MakeCode, NativeMethods.MAPVK_VSC_TO_VK_EX);
                case 0x11: // VK_CONTROL
                    return e0 ? 0xA3 : 0xA2;
                case 0x12: // VK_MENU
                    return e0 ? 0xA5 : 0xA4;
            }

            // Without E0 these navigation codes come from the numpad, which the hook reports as VK_NUMPADn while NumLock
            // is on. Use whatever the hook last reported for that physical key; if it never saw it, ignore the event
            // rather than risk pressing an arrow binding by mistake.
            bool isNavigationKey = vk == 0x0C || (vk >= 0x21 && vk <= 0x28) || vk == 0x2D || vk == 0x2E;
            if (isNavigationKey && !e0) return _vkByScanCode[keyboard.MakeCode & 0xFF];

            return vk;
        }

        private unsafe IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode >= 0)
            {
                try
                {
                    var hookStruct = *(NativeMethods.KBDLLHOOKSTRUCT*)lParam; // No allocation per key

                    // If this is an injected keystroke from our own PWM engine, let it pass freely
                    if (hookStruct.dwExtraInfo == NativeMethods.MAGIC_EXTRA_INFO)
                    {
                        return NativeMethods.CallNextHookEx(_hookHandle, nCode, wParam, lParam);
                    }

                    // Right after a re-hook the same event can reach the previous hook too: answer it the same way
                    if (_hasLastEvent &&
                        hookStruct.vkCode == _lastEvent.vkCode &&
                        hookStruct.scanCode == _lastEvent.scanCode &&
                        hookStruct.flags == _lastEvent.flags &&
                        hookStruct.time == _lastEvent.time)
                    {
                        if (DiagLog.Enabled && IsTracedKey?.Invoke((int)hookStruct.vkCode & 0xFF) == true)
                        {
                            DiagLog.Write($"hook {hookStruct.vkCode:X2} flags={hookStruct.flags:X2} t={hookStruct.time} repeat of last event -> {(_lastEventSwallowed ? "swallowed" : "passed")}");
                        }
                        return _lastEventSwallowed ? (IntPtr)1 : NativeMethods.CallNextHookEx(_hookHandle, nCode, wParam, lParam);
                    }

                    int msg = wParam.ToInt32();
                    bool isDown = (msg == NativeMethods.WM_KEYDOWN || msg == NativeMethods.WM_SYSKEYDOWN);
                    bool isUp = (msg == NativeMethods.WM_KEYUP || msg == NativeMethods.WM_SYSKEYUP);

                    bool swallowKey = false;
                    if (isDown || isUp)
                    {
                        int vkCode = (int)hookStruct.vkCode & 0xFF;
                        if ((hookStruct.flags & NativeMethods.LLKHF_EXTENDED) == 0)
                        {
                            _vkByScanCode[hookStruct.scanCode & 0xFF] = vkCode;
                        }

                        // If user is currently rebinding a key in the GUI:
                        if (IsKeyCaptureActive && isDown)
                        {
                            IsKeyCaptureActive = false;
                            KeyCapturedForBinding?.Invoke(vkCode);
                            swallowKey = true; // Swallow the capture keystroke
                        }
                        else
                        {
                            KeyIntercepted?.Invoke(vkCode, isDown, ref swallowKey);

                            // Swallowed keys never update GetAsyncKeyState, so if Windows thinks this key is down its
                            // key-down got past us to the game. Let the key-up through as well, or the game keeps it held.
                            if (swallowKey && isUp && (NativeMethods.GetAsyncKeyState(vkCode) & 0x8000) != 0)
                            {
                                swallowKey = false;
                            }
                        }

                        // Recorded only once the event was handled, so an exception above leaves Raw Input free to report it
                        _lastHookTime[vkCode] = hookStruct.time;
                        _lastHookDown[vkCode] = isDown;
                        _hookSeen[vkCode] = true;

                        if (DiagLog.Enabled && IsTracedKey?.Invoke(vkCode) == true)
                        {
                            DiagLog.Write($"hook {vkCode:X2} {(isDown ? "down" : "up  ")} flags={hookStruct.flags:X2} t={hookStruct.time} -> {(swallowKey ? "swallowed" : "passed")}");
                        }
                    }

                    _lastEvent = hookStruct;
                    _lastEventSwallowed = swallowKey;
                    _hasLastEvent = true;

                    if (swallowKey)
                    {
                        return (IntPtr)1; // Prevent key from propagating to OS / active window
                    }
                }
                catch (Exception ex)
                {
                    // Prevent any exception from crashing hook callback or causing Windows to unhook
                    DiagLog.Write($"!! hook callback exception: {ex.GetType().Name}: {ex.Message}");
                }
            }

            return NativeMethods.CallNextHookEx(_hookHandle, nCode, wParam, lParam);
        }

        private static string DescribeWindowProcess(IntPtr hwnd)
        {
            NativeMethods.GetWindowThreadProcessId(hwnd, out uint pid);
            try
            {
                using var process = Process.GetProcessById((int)pid);
                return $"{process.ProcessName} (pid {pid})";
            }
            catch
            {
                return $"pid {pid}";
            }
        }

        public void Dispose()
        {
            if (!_isDisposed)
            {
                Uninstall();
                _started?.Dispose();
                _isDisposed = true;
            }
        }
    }
}
