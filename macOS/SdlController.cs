using System;
using System.IO;
using System.Runtime.InteropServices;
using static DeskMadeline.Mac.Native;

namespace DeskMadeline.Mac;

/// <summary>Uses the SDL runtime from the installed Celeste for DualSense input and both rumble motors.</summary>
internal sealed unsafe class SdlController : IDisposable
{
    const uint GameControllerSubsystem = 0x2000;
    const int PlayStation5 = 7;
    const int AxisLeftX = 0, AxisLeftY = 1, AxisLeftTrigger = 4, AxisRightTrigger = 5;

    // SDL_GameControllerButton enum order. Stick clicks and menu buttons have no gameplay binding.
    static readonly (int Id, GamepadButtons Button)[] Buttons =
    {
        (0, GamepadButtons.Cross), (1, GamepadButtons.Circle),
        (2, GamepadButtons.Square), (3, GamepadButtons.Triangle),
        (9, GamepadButtons.L1), (10, GamepadButtons.R1),
        (11, GamepadButtons.Up), (12, GamepadButtons.Down),
        (13, GamepadButtons.Left), (14, GamepadButtons.Right),
    };

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    delegate IntPtr OpenFile([MarshalAs(UnmanagedType.LPUTF8Str)] string path,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string mode);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    delegate int SetHint([MarshalAs(UnmanagedType.LPUTF8Str)] string name,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string value, int priority);

    readonly delegate* unmanaged[Cdecl]<uint, void> quit;
    readonly delegate* unmanaged[Cdecl]<void> update;
    readonly delegate* unmanaged[Cdecl]<int> count;
    readonly delegate* unmanaged[Cdecl]<int, int> isGameController, typeForIndex;
    readonly delegate* unmanaged[Cdecl]<int, IntPtr> open;
    readonly delegate* unmanaged[Cdecl]<IntPtr, int> attached;
    readonly delegate* unmanaged[Cdecl]<IntPtr, void> close;
    readonly delegate* unmanaged[Cdecl]<IntPtr, int, short> axis;
    readonly delegate* unmanaged[Cdecl]<IntPtr, int, byte> button;
    readonly delegate* unmanaged[Cdecl]<IntPtr, ushort, ushort, uint, int> rumble;
    readonly delegate* unmanaged[Cdecl]<IntPtr> getError;
    IntPtr library, controller;
    uint connection;
    long nextScan;
    bool reportedError;

    SdlController(IntPtr library, string mappings)
    {
        IntPtr Export(string name) => NativeLibrary.GetExport(library, "SDL_" + name);
        quit = (delegate* unmanaged[Cdecl]<uint, void>)Export("QuitSubSystem");
        update = (delegate* unmanaged[Cdecl]<void>)Export("GameControllerUpdate");
        count = (delegate* unmanaged[Cdecl]<int>)Export("NumJoysticks");
        isGameController = (delegate* unmanaged[Cdecl]<int, int>)Export("IsGameController");
        typeForIndex = (delegate* unmanaged[Cdecl]<int, int>)Export("GameControllerTypeForIndex");
        open = (delegate* unmanaged[Cdecl]<int, IntPtr>)Export("GameControllerOpen");
        attached = (delegate* unmanaged[Cdecl]<IntPtr, int>)Export("GameControllerGetAttached");
        close = (delegate* unmanaged[Cdecl]<IntPtr, void>)Export("GameControllerClose");
        axis = (delegate* unmanaged[Cdecl]<IntPtr, int, short>)Export("GameControllerGetAxis");
        button = (delegate* unmanaged[Cdecl]<IntPtr, int, byte>)Export("GameControllerGetButton");
        rumble = (delegate* unmanaged[Cdecl]<IntPtr, ushort, ushort, uint, int>)Export("GameControllerRumble");
        getError = (delegate* unmanaged[Cdecl]<IntPtr>)Export("GetError");

        var init = (delegate* unmanaged[Cdecl]<uint, int>)Export("InitSubSystem");
        var controllerEvents = (delegate* unmanaged[Cdecl]<int, int>)Export("GameControllerEventState");
        var joystickEvents = (delegate* unmanaged[Cdecl]<int, int>)Export("JoystickEventState");
        ((delegate* unmanaged[Cdecl]<void>)Export("SetMainReady"))();
        if (File.Exists(mappings))
        {
            var openFile = Marshal.GetDelegateForFunctionPointer<OpenFile>(Export("RWFromFile"));
            var addMappings = (delegate* unmanaged[Cdecl]<IntPtr, int, int>)Export("GameControllerAddMappingsFromRW");
            IntPtr file = openFile(mappings, "rb");
            if (file != IntPtr.Zero) addMappings(file, 1);
        }

        var hint = Marshal.GetDelegateForFunctionPointer<SetHint>(Export("SetHintWithPriority"));
        hint("SDL_JOYSTICK_ALLOW_BACKGROUND_EVENTS", "1", 1);
        hint("SDL_GAMECONTROLLER_USE_BUTTON_LABELS", "0", 2);
#if DESK_WINDOWS
        hint("SDL_JOYSTICK_HIDAPI_PS5_RUMBLE", "1", 1);
#endif
        if (init(GameControllerSubsystem) != 0)
            throw new InvalidOperationException(Marshal.PtrToStringUTF8(getError()));
        // The desktop host owns event dispatch, so poll controllers without queueing SDL events.
        controllerEvents(0);
        joystickEvents(0);
        this.library = library;
    }

    public static SdlController OpenCeleste(string resources)
    {
        if (resources == null) return null;
        string[] paths =
#if DESK_WINDOWS
        {
            Path.Combine(AppContext.BaseDirectory, "SDL2.dll"),
            Path.Combine(resources, "SDL2.dll"),
        };
#else
        {
            Path.Combine(resources, "../MacOS/osx", "libSDL2-2.0.0.dylib"),
            Path.Combine(resources, "../MacOS", "libSDL2-2.0.0.dylib"),
            Path.Combine(resources, "osx", "libSDL2-2.0.0.dylib"),
        };
#endif
        foreach (string candidate in paths)
        {
            string path = Path.GetFullPath(candidate);
            if (!File.Exists(path) || !NativeLibrary.TryLoad(path, out IntPtr handle)) continue;
            try
            {
                var gamepad = new SdlController(handle, Path.Combine(resources, "gamecontrollerdb.txt"));
                PetWindow.Log("DualSense rumble uses SDL at " + path);
                return gamepad;
            }
            catch (Exception error)
            {
                NativeLibrary.Free(handle);
                PetWindow.Log("SDL controller unavailable: " + error.Message);
            }
        }
        return null;
    }

    // A handle's identity changes on reconnect, even if SDL recycles its pointer.
    public uint Connection => controller == IntPtr.Zero ? 0 : connection;

    public GamepadState Poll()
    {
        update();
        if (controller != IntPtr.Zero && attached(controller) == 0)
            CloseController();

        if (controller == IntPtr.Zero && Environment.TickCount64 >= nextScan)
        {
            nextScan = Environment.TickCount64 + 500;
            int devices = count();
            for (int i = 0; i < devices; i++)
            {
                if (isGameController(i) == 0 || typeForIndex(i) != PlayStation5) continue;
                controller = open(i);
                if (controller == IntPtr.Zero) continue;
                connection = connection == uint.MaxValue ? 1u : connection + 1;
                reportedError = false;
                break;
            }
        }

        if (controller == IntPtr.Zero) return default;
        GamepadButtons buttons = 0;
        foreach (var binding in Buttons)
            if (button(controller, binding.Id) != 0) buttons |= binding.Button;
        return new GamepadState(1, buttons, axis(controller, AxisLeftX) / 32767f,
            axis(controller, AxisLeftY) / -32767f, axis(controller, AxisLeftTrigger) / 32767f,
            axis(controller, AxisRightTrigger) / 32767f);
    }

    internal static ushort MotorValue(float strength) => (ushort)(Math.Clamp(strength, 0f, 1f) * 65535f);

    public void Vibrate(float strength)
    {
        if (controller == IntPtr.Zero) return;
        // FNA sets both motors to the same value and Monocle sends zero on expiry.
        ushort motor = MotorValue(strength);
        if (rumble(controller, motor, motor, 0) != 0 && !reportedError)
        {
            reportedError = true;
            PetWindow.Log("DualSense rumble: " + Marshal.PtrToStringUTF8(getError()));
        }
    }

    void CloseController()
    {
        if (controller == IntPtr.Zero) return;
        Vibrate(0f);
        close(controller);
        controller = IntPtr.Zero;
    }

    public void Dispose()
    {
        if (library == IntPtr.Zero) return;
        CloseController();
        quit(GameControllerSubsystem);
        NativeLibrary.Free(library);
        library = IntPtr.Zero;
    }
}
