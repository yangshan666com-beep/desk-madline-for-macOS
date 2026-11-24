using System;
using System.Runtime.InteropServices;

namespace DeskMadeline.Mac;

internal static partial class Native
{
#if !DESK_WINDOWS
    const string Library = "libDeskMadelineMac.dylib";
#endif
    const CallingConvention C = CallingConvention.Cdecl;

    [StructLayout(LayoutKind.Sequential)]
    public readonly record struct DesktopRect(float X, float Y, float Width, float Height)
    {
        public System.Drawing.RectangleF Bounds => new System.Drawing.RectangleF(X, Y, Width, Height);
    }

    [StructLayout(LayoutKind.Sequential)]
    public readonly record struct WindowInfo(uint Id, int Owner, DesktopRect Frame);

    [StructLayout(LayoutKind.Sequential)]
    public readonly record struct DisplayInfo(uint Id, DesktopRect Frame, DesktopRect WorkArea);

    [StructLayout(LayoutKind.Sequential)]
    public readonly record struct ImageLayer(IntPtr Pixels, int Stride, int Width, int Height, DesktopRect Bounds);

    [StructLayout(LayoutKind.Sequential)]
    public readonly record struct PortraitLayer(ImageLayer Border, ImageLayer Artwork, ImageLayer Tail, float Opacity);

    [StructLayout(LayoutKind.Sequential)]
    public readonly record struct ItemLayer(ImageLayer Image, int Depth, int ItemId,
        DesktopRect HitBounds = default, ItemHitMode HitMode = ItemHitMode.Alpha, float Opacity = 1f);

    public enum ItemHitMode { Alpha, Region, Resize }

    // Match AppKit's NSCursorFrameResizePosition.
    [Flags]
    public enum ResizeEdge { None = 0, Top = 1, Left = 2, Bottom = 4, Right = 8 }

    [Flags]
    public enum GamepadButtons : uint
    {
        Up = 1 << 0, Down = 1 << 1, Left = 1 << 2, Right = 1 << 3,
        Cross = 1 << 4, Circle = 1 << 5, Square = 1 << 6, Triangle = 1 << 7,
        L1 = 1 << 8, R1 = 1 << 9,
    }

    [StructLayout(LayoutKind.Sequential)]
    public readonly record struct GamepadState(int Connected, GamepadButtons Buttons,
        float LeftX, float LeftY, float L2, float R2);

    public enum MouseAction { Down, Drag, Up, Cancel, PlaceItem, DeleteItem }

    [UnmanagedFunctionPointer(C)]
    public delegate int Start();
    [UnmanagedFunctionPointer(C)]
    public delegate void Frame(double elapsed);
    [UnmanagedFunctionPointer(C)]
    public delegate void Key(int key, int down);
    [UnmanagedFunctionPointer(C)]
    public delegate void Mouse(MouseAction action, float x, float y, double timestamp, int subject, ResizeEdge resize);
    [UnmanagedFunctionPointer(C)]
    public delegate void Command(int command, int argument);
    [UnmanagedFunctionPointer(C)]
    public delegate void Stop();

#if !DESK_WINDOWS
    [DllImport(Library, CallingConvention = C, EntryPoint = "dm_run")]
    public static extern int Run(Start start, Frame frame, Key key, Mouse mouse, Command command, Stop stop);

    [DllImport(Library, CallingConvention = C, EntryPoint = "dm_gamepad")]
    public static extern void Gamepad(out GamepadState state);

    [DllImport(Library, CallingConvention = C, EntryPoint = "dm_windows")]
    public static extern unsafe int Windows(WindowInfo* windows, int capacity, out int frontmost);

    [DllImport(Library, CallingConvention = C, EntryPoint = "dm_displays")]
    public static extern unsafe int Displays(DisplayInfo* screens, int capacity);

    [DllImport(Library, CallingConvention = C, EntryPoint = "dm_present")]
    public static extern void Present(IntPtr pixels, int rowBytes, int width, int height,
        float x, float y, int scale, uint fullscreenDisplay, in PortraitLayer portrait, int heartForeground);

    [DllImport(Library, CallingConvention = C, EntryPoint = "dm_present_items")]
    public static extern unsafe void PresentItems(ItemLayer* layers, int count, uint fullscreenDisplay);

    [DllImport(Library, CallingConvention = C, EntryPoint = "dm_item_icon")]
    public static extern void ItemIcon(int kind, IntPtr pixels, int rowBytes, int width, int height);

    [DllImport(Library, CallingConvention = C, EntryPoint = "dm_hit_target")]
    public static extern void HitTarget(IntPtr pixels, int rowBytes, int width, int height, float x, float y, int scale);

    [DllImport(Library, CallingConvention = C, EntryPoint = "dm_status")]
    public static extern void Status(int scale, int autonomy, int sound, int audioAvailable, int terrain, int rumble, int showExpressions);

    [DllImport(Library, CallingConvention = C, EntryPoint = "dm_assist_status")]
    public static extern void AssistStatus(int badelineCount, int badelineAvailable, int infiniteStamina, int dashAssist);

    [DllImport(Library, EntryPoint = "dm_course_status", CallingConvention = C)]
    public static extern void CourseStatus(int state, [MarshalAs(UnmanagedType.LPUTF8Str)] string message, int focus);

    [DllImport(Library, CallingConvention = C, EntryPoint = "dm_cursor")]
    public static extern void Cursor(out float x, out float y);

    [DllImport(Library, CallingConvention = C, EntryPoint = "dm_primary_button_down")]
    public static extern int PrimaryButtonDown();

    [DllImport(Library, CallingConvention = C, EntryPoint = "dm_pick_game")]
    static extern IntPtr PickGamePath();

    [DllImport(Library, CallingConvention = C, EntryPoint = "dm_free")]
    static extern void Free(IntPtr buffer);

    public static string PickGame()
    {
        IntPtr path = PickGamePath();
        if (path == IntPtr.Zero) return null;
        try { return Marshal.PtrToStringUTF8(path); }
        finally { Free(path); }
    }

    [DllImport(Library, CallingConvention = C, EntryPoint = "dm_alert")]
    public static extern void Alert([MarshalAs(UnmanagedType.LPUTF8Str)] string title,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string message);

    [DllImport(Library, CallingConvention = C, EntryPoint = "dm_quit")]
    public static extern void Quit();
#endif
}
