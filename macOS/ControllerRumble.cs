using System;

namespace DeskMadeline.Mac;

internal enum RumbleAmount { Off, Half, On }

/// <summary>Celeste.Input.Rumble and Monocle.MInput.GamePadData's priority and countdown.</summary>
internal sealed class ControllerRumble(Action<float> vibrate)
{
    static readonly float[] Strengths = { .15f, .4f, 1f, .05f };
    static readonly float[] Lengths = { .1f, .25f, .5f, 1f, 2f };
    float strength, remaining;
    uint device;
    bool controlled;

    public RumbleAmount Amount { get; private set; } = RumbleAmount.On;

    public void SetAmount(RumbleAmount amount)
    {
        Amount = amount;
        if (amount == RumbleAmount.Off) Stop();
    }

    public void Advance(float dt, uint currentDevice, bool hasControl)
    {
        // The transport stops the old handle on disconnect or replacement.
        if (currentDevice != device)
        {
            device = currentDevice;
            remaining = 0f;
        }
        controlled = device != 0 && hasControl;
        if (!controlled)
        {
            Stop();
            return;
        }
        // MInput runs even when the scene is held by a dash's freeze.
        if (remaining <= 0f) return;
        remaining -= dt;
        if (remaining <= 0f) vibrate(0f);
    }

    public void Request(RumbleStrength level, RumbleLength length)
    {
        if (!controlled || Amount == RumbleAmount.Off) return;
        float next = Strengths[(int)level] * (Amount == RumbleAmount.Half ? .5f : 1f);
        float time = Lengths[(int)length];
        if (remaining > 0f && (next < strength || (next == strength && time <= remaining))) return;
        vibrate(next);
        strength = next;
        remaining = time;
    }

    public void Stop()
    {
        if (remaining > 0f) vibrate(0f);
        controlled = false;
        remaining = 0f;
    }
}
