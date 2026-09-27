using System.Runtime.InteropServices;
using AVWorkstationToolkit.OpenAppFixture;

// A non-shipping stand-in for an ordinary Windows desktop app, used only by the integration harness's open-app
// boundary. It keeps one top-level window and answers the session-end messages Windows Restart Manager sends when it
// asks an app to close: "visible" shows the window, "hidden" never shows it, as a tray app like ShareX does, and
// "refuse" declines to close, as an app with unsaved work does.
if (args.Length != 2 || args[0] is not ("visible" or "hidden" or "refuse")) return 2;
return Fixture.Run(args[0], args[1]);

namespace AVWorkstationToolkit.OpenAppFixture
{
    internal static class Fixture
    {
        private const string ClassName = "AVWorkstationToolkitOpenAppFixture";
        private const uint WmDestroy = 0x0002;
        private const uint WmClose = 0x0010;
        private const uint WmQueryEndSession = 0x0011;
        private const uint WmEndSession = 0x0016;
        private const uint WsOverlappedWindow = 0x00CF0000;
        private const int SwShowNoActivate = 4;

        private static string mode = "visible";
        private static int exitCode = 1;
        private static WindowProcedure? procedure;

        public static int Run(string requestedMode, string readyPath)
        {
            mode = requestedMode;
            procedure = Procedure;
            var instance = GetModuleHandle(null);
            var windowClass = new WindowClass
            {
                Size = (uint)Marshal.SizeOf<WindowClass>(),
                Procedure = Marshal.GetFunctionPointerForDelegate(procedure),
                Instance = instance,
                ClassName = ClassName
            };
            if (RegisterClassEx(ref windowClass) == 0) return 3;
            var window = CreateWindowEx(0, ClassName, "AVWT open-app fixture", WsOverlappedWindow, 100, 100, 240, 120,
                IntPtr.Zero, IntPtr.Zero, instance, IntPtr.Zero);
            if (window == IntPtr.Zero) return 4;
            if (mode != "hidden") ShowWindow(window, SwShowNoActivate);
            File.WriteAllText(readyPath, "ready");
            while (GetMessage(out var message, IntPtr.Zero, 0, 0) > 0)
            {
                TranslateMessage(ref message);
                DispatchMessage(ref message);
            }
            return exitCode;
        }

        private static IntPtr Procedure(IntPtr window, uint message, IntPtr wParam, IntPtr lParam)
        {
            switch (message)
            {
                case WmQueryEndSession:
                    return mode == "refuse" ? IntPtr.Zero : new IntPtr(1);
                case WmEndSession:
                    if (wParam != IntPtr.Zero && mode != "refuse")
                    {
                        exitCode = 0;
                        PostQuitMessage(0);
                    }
                    return IntPtr.Zero;
                case WmClose when mode == "refuse":
                    return IntPtr.Zero;
                case WmDestroy:
                    exitCode = 0;
                    PostQuitMessage(0);
                    return IntPtr.Zero;
                default:
                    return DefWindowProc(window, message, wParam, lParam);
            }
        }

        private delegate IntPtr WindowProcedure(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct WindowClass
        {
            public uint Size;
            public uint Style;
            public IntPtr Procedure;
            public int ClassExtra;
            public int WindowExtra;
            public IntPtr Instance;
            public IntPtr Icon;
            public IntPtr Cursor;
            public IntPtr Background;
            public string? MenuName;
            public string ClassName;
            public IntPtr SmallIcon;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct Message
        {
            public IntPtr Window;
            public uint Value;
            public IntPtr WParam;
            public IntPtr LParam;
            public uint Time;
            public int X;
            public int Y;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr GetModuleHandle(string? moduleName);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern ushort RegisterClassEx(ref WindowClass windowClass);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr CreateWindowEx(uint extendedStyle, string className, string title, uint style, int x, int y,
            int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr parameter);

        [DllImport("user32.dll")]
        private static extern bool ShowWindow(IntPtr window, int command);

        [DllImport("user32.dll")]
        private static extern int GetMessage(out Message message, IntPtr window, uint filterMinimum, uint filterMaximum);

        [DllImport("user32.dll")]
        private static extern bool TranslateMessage(ref Message message);

        [DllImport("user32.dll")]
        private static extern IntPtr DispatchMessage(ref Message message);

        [DllImport("user32.dll")]
        private static extern void PostQuitMessage(int exitCode);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr DefWindowProc(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
    }
}
