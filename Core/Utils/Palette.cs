using System.Windows.Media;

namespace KeyTrigger.Core.Utils
{
    // Constant colours used by the code-behind UI. Created once and frozen, so list rebuilds, toggles and curve
    // drags don't allocate a brush per element and the render thread doesn't track a new resource for each one.
    // Nothing may mutate or animate these: they are shared. An "A##" suffix is the hex alpha.
    internal static class Palette
    {
        public static readonly SolidColorBrush Slate300 = Make(0xFF, 0xCB, 0xD5, 0xE1);
        public static readonly SolidColorBrush Slate400 = Make(0xFF, 0x94, 0xA3, 0xB8);
        public static readonly SolidColorBrush Slate500 = Make(0xFF, 0x64, 0x74, 0x8B);
        public static readonly SolidColorBrush Slate900 = Make(0xFF, 0x0F, 0x17, 0x2A);
        public static readonly SolidColorBrush Gray = Make(0xFF, 0x8B, 0x94, 0x9E);
        public static readonly SolidColorBrush Gray500 = Make(0xFF, 0x6B, 0x72, 0x80);
        public static readonly SolidColorBrush Silver = Make(0xFF, 0xD0, 0xD0, 0xD0);

        public static readonly SolidColorBrush Sky400 = Make(0xFF, 0x38, 0xBD, 0xF8);
        public static readonly SolidColorBrush Sky400A40 = Make(0x40, 0x38, 0xBD, 0xF8);
        public static readonly SolidColorBrush Sky400A78 = Make(0x78, 0x38, 0xBD, 0xF8);
        public static readonly SolidColorBrush Sky400AB4 = Make(0xB4, 0x38, 0xBD, 0xF8);
        public static readonly SolidColorBrush Indigo400 = Make(0xFF, 0x81, 0x8C, 0xF8);
        public static readonly SolidColorBrush Indigo400A30 = Make(0x30, 0x81, 0x8C, 0xF8);
        public static readonly SolidColorBrush Indigo400A78 = Make(0x78, 0x81, 0x8C, 0xF8);
        public static readonly SolidColorBrush Cyan = Make(0xFF, 0x00, 0xE5, 0xFF);
        public static readonly SolidColorBrush Blue = Make(0xFF, 0x44, 0x8A, 0xFF);
        public static readonly SolidColorBrush Purple = Make(0xFF, 0xB3, 0x88, 0xFF);

        public static readonly SolidColorBrush Green = Make(0xFF, 0x00, 0xE6, 0x76);
        public static readonly SolidColorBrush Emerald = Make(0xFF, 0x10, 0xB9, 0x81);
        public static readonly SolidColorBrush EmeraldA25 = Make(0x25, 0x10, 0xB9, 0x81);
        public static readonly SolidColorBrush EmeraldA60 = Make(0x60, 0x10, 0xB9, 0x81);
        public static readonly SolidColorBrush Amber = Make(0xFF, 0xFF, 0xB3, 0x00);
        public static readonly SolidColorBrush AmberA78 = Make(0x78, 0xFF, 0xB3, 0x00);
        public static readonly SolidColorBrush Amber500 = Make(0xFF, 0xF5, 0x9E, 0x0B);
        public static readonly SolidColorBrush Coral = Make(0xFF, 0xFF, 0x52, 0x52);
        public static readonly SolidColorBrush CoralA78 = Make(0x78, 0xFF, 0x52, 0x52);
        public static readonly SolidColorBrush Red = Make(0xFF, 0xEF, 0x44, 0x44);

        public static readonly SolidColorBrush WhiteA10 = Make(0x10, 0xFF, 0xFF, 0xFF);
        public static readonly SolidColorBrush WhiteA14 = Make(0x14, 0xFF, 0xFF, 0xFF);
        public static readonly SolidColorBrush WhiteA1C = Make(0x1C, 0xFF, 0xFF, 0xFF);
        public static readonly SolidColorBrush WhiteA20 = Make(0x20, 0xFF, 0xFF, 0xFF);
        public static readonly SolidColorBrush WhiteA25 = Make(0x25, 0xFF, 0xFF, 0xFF);
        public static readonly SolidColorBrush WhiteA32 = Make(0x32, 0xFF, 0xFF, 0xFF);
        public static readonly SolidColorBrush WhiteA35 = Make(0x35, 0xFF, 0xFF, 0xFF);
        public static readonly SolidColorBrush WhiteA45 = Make(0x45, 0xFF, 0xFF, 0xFF);
        public static readonly SolidColorBrush WhiteA50 = Make(0x50, 0xFF, 0xFF, 0xFF);
        public static readonly SolidColorBrush WhiteA70 = Make(0x70, 0xFF, 0xFF, 0xFF);
        public static readonly SolidColorBrush WhiteA78 = Make(0x78, 0xFF, 0xFF, 0xFF);
        public static readonly SolidColorBrush WhiteA80 = Make(0x80, 0xFF, 0xFF, 0xFF);

        // Cards, badges and chips
        public static readonly SolidColorBrush CardBg = Make(0xFF, 0x16, 0x1B, 0x26);
        public static readonly SolidColorBrush CardBorder = Make(0xFF, 0x22, 0x29, 0x38);
        public static readonly SolidColorBrush ProfileCardBorder = Make(0xFF, 0x22, 0x2A, 0x3A);
        public static readonly SolidColorBrush RenameBoxBg = Make(0xFF, 0x0E, 0x12, 0x1B);
        public static readonly SolidColorBrush ActivePillBg = Make(0xFF, 0x14, 0x2A, 0x3B);
        public static readonly SolidColorBrush ChipBg = Make(0x30, 0x1E, 0x27, 0x38);
        public static readonly SolidColorBrush ChipBorder = Make(0xFF, 0x29, 0x34, 0x48);
        public static readonly SolidColorBrush NavSelectedBg = Make(0xFF, 0x19, 0x20, 0x2E);
        public static readonly SolidColorBrush NavSelectedBorder = Make(0xFF, 0x2E, 0x3A, 0x52);
        public static readonly SolidColorBrush GreenBadgeBg = Make(0xFF, 0x12, 0x2C, 0x20);
        public static readonly SolidColorBrush AmberBadgeBg = Make(0xFF, 0x2A, 0x24, 0x14);
        public static readonly SolidColorBrush OffBadgeBg = Make(0xFF, 0x23, 0x29, 0x36);
        public static readonly SolidColorBrush OffBadgeBorder = Make(0xFF, 0x4F, 0x58, 0x6E);
        public static readonly SolidColorBrush IdleBadgeBg = Make(0xFF, 0x1C, 0x22, 0x30);
        public static readonly SolidColorBrush IdleBadgeBorder = Make(0xFF, 0x35, 0x40, 0x57);
        public static readonly SolidColorBrush OkBadgeBg = Make(0x30, 0x1A, 0x38, 0x28);
        public static readonly SolidColorBrush WarnBadgeBg = Make(0x30, 0x3F, 0x2E, 0x1A);
        public static readonly SolidColorBrush ErrorBadgeBg = Make(0x30, 0x3F, 0x1A, 0x1A);

        // Dash pattern for the curve editors' grid and tangent lines
        public static readonly DoubleCollection Dash3 = MakeDash(3, 3);

        private static SolidColorBrush Make(byte a, byte r, byte g, byte b)
        {
            var brush = new SolidColorBrush(Color.FromArgb(a, r, g, b));
            brush.Freeze();
            return brush;
        }

        private static DoubleCollection MakeDash(double on, double off)
        {
            var dash = new DoubleCollection { on, off };
            dash.Freeze();
            return dash;
        }
    }
}
