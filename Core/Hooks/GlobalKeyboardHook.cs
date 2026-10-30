using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using KeyTrigger.Core.Win32;

namespace KeyTrigger.Core.Hooks
{
    public delegate void KeyInterceptEventHandler(int vkCode, bool isDown, ref bool swallowKey);

    public class GlobalKeyboardHook : IDisposable
    {
        private IntPtr _hookHandle = IntPtr.Zero;
        private readonly NativeMethods.LowLevelKeyboardProc _proc;
        private bool _isDisposed;

        public event KeyInterceptEventHandler? KeyIntercepted;
        public event Action<int>? KeyCapturedForBinding;

        public bool IsKeyCaptureActive { get; set; }
        public int ToggleHotKeyVk { get; set; } = 0x77; // VK_F8

        public GlobalKeyboardHook()
        {
            // Root the delegate so the Garbage Collector never collects it
            _proc = HookCallback;
        }

        public void Install()
        {
            if (_hookHandle != IntPtr.Zero) return;

            IntPtr moduleHandle = NativeMethods.GetModuleHandle(null);

            _hookHandle = NativeMethods.SetWindowsHookEx(
                NativeMethods.WH_KEYBOARD_LL,
                _proc,
                moduleHandle,
                0);

            if (_hookHandle == IntPtr.Zero)
            {
                int error = Marshal.GetLastWin32Error();
                System.Diagnostics.Debug.WriteLine($"Low-level keyboard hook warning: {error}. Falling back to 1000Hz hardware polling.");
            }
        }

        public void Uninstall()
        {
            if (_hookHandle != IntPtr.Zero)
            {
                NativeMethods.UnhookWindowsHookEx(_hookHandle);
                _hookHandle = IntPtr.Zero;
            }
        }

        private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode >= 0)
            {
                var hookStruct = Marshal.PtrToStructure<NativeMethods.KBDLLHOOKSTRUCT>(lParam);

                // If this is an injected keystroke from our own PWM engine, let it pass freely
                if (hookStruct.dwExtraInfo == NativeMethods.MAGIC_EXTRA_INFO)
                {
                    return NativeMethods.CallNextHookEx(_hookHandle, nCode, wParam, lParam);
                }

                int msg = wParam.ToInt32();
                bool isDown = (msg == NativeMethods.WM_KEYDOWN || msg == NativeMethods.WM_SYSKEYDOWN);
                bool isUp = (msg == NativeMethods.WM_KEYUP || msg == NativeMethods.WM_SYSKEYUP);

                if (isDown || isUp)
                {
                    int vkCode = (int)hookStruct.vkCode;

                    // If user is currently rebinding a key in the GUI:
                    if (IsKeyCaptureActive && isDown)
                    {
                        IsKeyCaptureActive = false;
                        KeyCapturedForBinding?.Invoke(vkCode);
                        return (IntPtr)1; // Swallow the capture keystroke
                    }

                    bool swallowKey = false;
                    KeyIntercepted?.Invoke(vkCode, isDown, ref swallowKey);

                    if (swallowKey)
                    {
                        return (IntPtr)1; // Prevent key from propagating to OS / active window
                    }
                }
            }

            return NativeMethods.CallNextHookEx(_hookHandle, nCode, wParam, lParam);
        }

        public void Dispose()
        {
            if (!_isDisposed)
            {
                Uninstall();
                _isDisposed = true;
            }
        }
    }
}
