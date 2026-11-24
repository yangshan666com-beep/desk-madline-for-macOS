using System;
using System.Drawing;
using static DeskMadeline.Mac.Native;

namespace DeskMadeline.Mac;

/// <summary>Celeste's default bindings over AppKit keys and the Game Controller profile.</summary>
internal sealed class DesktopInput
{
    internal const int Left = 123, Right = 124, Down = 125, Up = 126;
    internal const int A = 0, S = 1, D = 2, W = 13;
    internal const int Jump = 8, Dash = 7, GrabZ = 6, GrabV = 9, LeftShift = 56;
    internal const int Capture = -2;
    const float MoveXThreshold = .3f, MoveYThreshold = .7f, GliderYThreshold = .3f;
    const float AimThreshold = .25f, TriggerThreshold = .2f;
    const float FnaLeftDeadZone = .23953247f, FnaTriggerDeadZone = .11764706f;

    readonly bool[] held = new bool[128], pressed = new bool[128];
    readonly IntegerAxis horizontal = new IntegerAxis(), vertical = new IntegerAxis();
    readonly IntegerAxis gliderVertical = new IntegerAxis();
    PointF joystick;
    bool aimHorizontalTurned, aimVerticalTurned;
    GamepadButtons previousButtons;

    public bool Capturing { get; private set; }

    public void Receive(int key, int down)
    {
        if (key == Capture)
        {
            Capturing = down != 0;
            Array.Clear(held);
            Array.Clear(pressed);
            ClearAxes();
            return;
        }
        if (!Capturing || (uint)key >= held.Length) return;
        if (down != 0 && !held[key]) pressed[key] = true;
        held[key] = down != 0;
    }

    void ClearAxes()
    {
        horizontal.Update(false, false);
        vertical.Update(false, false);
        gliderVertical.Update(false, false);
        joystick = PointF.Empty;
        aimHorizontalTurned = aimVerticalTurned = false;
    }

    public PetInput Sample(Player player, GamepadState gamepad = default, bool acceptInput = true)
    {
        GamepadButtons buttons = gamepad.Connected != 0 ? gamepad.Buttons : 0;
        GamepadButtons down = buttons & ~previousButtons;
        previousButtons = buttons;
        if (!Capturing || !acceptInput)
        {
            Array.Clear(pressed);
            ClearAxes();
            return default;
        }

        float stickX = gamepad.Connected != 0 ? ExcludeDeadZone(gamepad.LeftX, FnaLeftDeadZone) : 0f;
        float stickY = gamepad.Connected != 0 ? ExcludeDeadZone(-gamepad.LeftY, FnaLeftDeadZone) : 0f;
        float LeftAxis(float threshold) => Axis(held[A] || held[Left], -stickX, GamepadButtons.Left, threshold);
        float RightAxis(float threshold) => Axis(held[D] || held[Right], stickX, GamepadButtons.Right, threshold);
        float UpAxis(float threshold) => Axis(held[W] || held[Up], -stickY, GamepadButtons.Up, threshold);
        float DownAxis(float threshold) => Axis(held[S] || held[Down], stickY, GamepadButtons.Down, threshold);
        float Axis(bool key, float stick, GamepadButtons dpad, float threshold)
        {
            // Binding.Axis returns the first nonzero binding: keyboard, stick, then D-pad.
            if (key) return 1f;
            if (stick > 0f && stick >= threshold) return stick;
            return (buttons & dpad) != 0 ? 1f : 0f;
        }

        // VirtualJoystick resolves opposing analog magnitudes, then applies a radial
        // deadzone. Movement's VirtualIntegerAxes compare each direction independently.
        joystick.X = JoystickAxis(LeftAxis(0), RightAxis(0), joystick.X, ref aimHorizontalTurned);
        joystick.Y = JoystickAxis(UpAxis(0), DownAxis(0), joystick.Y, ref aimVerticalTurned);
        if (joystick.X * joystick.X + joystick.Y * joystick.Y < AimThreshold * AimThreshold)
            joystick = PointF.Empty;
        var aim = SnapDashAim(joystick);

        const GamepadButtons jump = GamepadButtons.Cross | GamepadButtons.Triangle;
        const GamepadButtons dash = GamepadButtons.Square | GamepadButtons.Circle;
        const GamepadButtons shoulder = GamepadButtons.L1 | GamepadButtons.R1;
        if (pressed[Jump] || (down & jump) != 0) player.BufferJump();
        if (pressed[Dash] || (down & dash) != 0) player.BufferDash();
        var input = new PetInput
        {
            MoveX = horizontal.Update(LeftAxis(MoveXThreshold) > 0f, RightAxis(MoveXThreshold) > 0f),
            MoveY = vertical.Update(UpAxis(MoveYThreshold) > 0f, DownAxis(MoveYThreshold) > 0f),
            GliderMoveY = gliderVertical.Update(UpAxis(GliderYThreshold) > 0f, DownAxis(GliderYThreshold) > 0f),
            AimX = aim.X, AimY = aim.Y,
            FeatherX = joystick.X, FeatherY = joystick.Y,
            JumpHeld = held[Jump] || (buttons & jump) != 0,
            GrabHeld = held[GrabZ] || held[GrabV] || held[LeftShift] || (buttons & shoulder) != 0 ||
                (gamepad.Connected != 0 && (TriggerHeld(gamepad.L2) || TriggerHeld(gamepad.R2))),
            JumpPressed = player.HasJumpBuffer,
            DashPressed = player.HasDashBuffer,
        };
        Array.Clear(pressed);
        return input;
    }

    static bool TriggerHeld(float value) => ExcludeDeadZone(value, FnaTriggerDeadZone) >= TriggerThreshold;

    static float ExcludeDeadZone(float axis, float deadZone)
    {
        float magnitude = Math.Min(1f, MathF.Abs(axis));
        return MathF.CopySign(Math.Max(0f, magnitude - deadZone) / (1f - deadZone), axis);
    }

    static float JoystickAxis(float negative, float positive, float last, ref bool turned)
    {
        if (negative != positive || positive == 0f)
        {
            turned = false;
            return positive > negative ? positive : -negative;
        }
        if (!turned)
        {
            turned = true;
            return -Math.Sign(last) * positive;
        }
        return Math.Sign(last) * positive;
    }

    // Celeste.Input.GetAimVector uses a narrower cardinal cone when aiming upward.
    // A neutral aim is left to Player's facing fallback.
    static Point SnapDashAim(PointF value)
    {
        if (value.IsEmpty) return Point.Empty;
        float angle = MathF.Atan2(value.Y, value.X);
        float cardinal = MathF.PI / 8f - (angle < 0f ? .08726646f : 0f);
        if (MathF.Abs(angle) < cardinal) return new Point(1, 0);
        if (MathF.PI - MathF.Abs(angle) < cardinal) return new Point(-1, 0);
        if (MathF.Abs(angle + MathF.PI / 2f) < cardinal) return new Point(0, -1);
        if (MathF.Abs(angle - MathF.PI / 2f) < cardinal) return new Point(0, 1);
        return new Point(Math.Sign(value.X), Math.Sign(value.Y));
    }
}
