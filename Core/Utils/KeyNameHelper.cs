using System;
using System.Windows.Input;

namespace KeyTrigger.Core.Utils
{
    public static class KeyNameHelper
    {
        public static string GetKeyName(int vkCode)
        {
            try
            {
                var key = KeyInterop.KeyFromVirtualKey(vkCode);
                return key switch
                {
                    Key.None => $"VK_{vkCode:X2}",
                    Key.Up => "Up Arrow",
                    Key.Down => "Down Arrow",
                    Key.Left => "Left Arrow",
                    Key.Right => "Right Arrow",
                    Key.Space => "Spacebar",
                    Key.LeftShift => "Left Shift",
                    Key.RightShift => "Right Shift",
                    Key.LeftCtrl => "Left Ctrl",
                    Key.RightCtrl => "Right Ctrl",
                    Key.LeftAlt => "Left Alt",
                    Key.RightAlt => "Right Alt",
                    Key.D0 => "0",
                    Key.D1 => "1",
                    Key.D2 => "2",
                    Key.D3 => "3",
                    Key.D4 => "4",
                    Key.D5 => "5",
                    Key.D6 => "6",
                    Key.D7 => "7",
                    Key.D8 => "8",
                    Key.D9 => "9",
                    _ => key.ToString()
                };
            }
            catch
            {
                return $"0x{vkCode:X2}";
            }
        }
    }
}
