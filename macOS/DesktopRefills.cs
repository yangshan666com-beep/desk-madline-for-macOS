using System;
using System.Drawing;
using System.Linq;

namespace DeskMadeline.Mac;

internal sealed class DesktopRefill(int id, DesktopItemPlacement placement, SpriteBloom bloom)
    : DesktopItemWithEffects(id, placement, 40)
{
    internal const string TouchSound = "event:/game/general/diamond_touch";
    internal const string ReturnSound = "event:/game/general/diamond_return";
    internal const string DoubleTouchSound = "event:/new_content/game/10_farewell/pinkdiamond_touch";
    internal const string DoubleReturnSound = "event:/new_content/game/10_farewell/pinkdiamond_return";
    internal const float PickupFreeze = .05f;
    static readonly PType Glow = GlowType(4f, 8f, 1f);
    static readonly PType Regen = GlowType(30f, 40f, .2f);
    static readonly PType PinkGlow = GlowType(4f, 8f, 1f, pink: true);
    static readonly PType PinkRegen = GlowType(40f, 60f, .2f, pink: true);
    static readonly PType Shatter = ShatterType(pink: false), PinkShatter = ShatterType(pink: true);
    static PType ShatterType(bool pink) => new PType
    {
        Tex = new[] { "triangle" }, Color = pink ? Color.FromArgb(0xff, 0xd3, 0xf9) : Color.FromArgb(0xd3, 0xff, 0xd4),
        Color2 = pink ? Color.FromArgb(0xef, 0x94, 0xe3) : Color.FromArgb(0x85, 0xfc, 0x87), LateFade = true, LifeMin = .25f, LifeMax = .4f,
        Size = 1f, TextureScale = true, SpeedMin = 140f, SpeedMax = 210f, SpeedMultiplier = .005f,
        RandomRotation = true, SpinMin = MathF.PI / 2f, SpinMax = 4.712389f, SpinFlippedChance = true,
    };

    static PType GlowType(float speedMin, float speedMax, float speedMultiplier, bool pink = false) => new PType
    {
        Tex = new[] { "refillParticle" }, Color = pink ? Color.FromArgb(0xff, 0xa5, 0xaa) : Color.FromArgb(0xa5, 0xff, 0xf7),
        Color2 = pink ? Color.FromArgb(0xdd, 0x6c, 0xca) : Color.FromArgb(0x6d, 0xe0, 0x81), BlinkColor = true, LateFade = true,
        LifeMin = .4f, LifeMax = .6f, Size = 1f, TextureScale = true,
        SpeedMin = speedMin, SpeedMax = speedMax, SpeedMultiplier = speedMultiplier,
    };

    static string ArtPrefix(DesktopItemPlacement placement) => placement.Kind == DesktopItemKind.DoubleRefill ? "refillTwo/" : "refill/";
    readonly Bitmap[] idle = Frames(ArtPrefix(placement) + "idle", placement.Kind == DesktopItemKind.DoubleRefill ? 13 : 5);
    readonly Bitmap[] flash = Frames(ArtPrefix(placement) + "flash", 6);
    readonly Bitmap outline = Art(ArtPrefix(placement) + "outline");
    bool TwoDashes => Placement.Kind == DesktopItemKind.DoubleRefill;
    bool collidable = true, spent;
    Pickup routine;
    float sine = Random.Shared.NextSingle() * MathF.Tau;
    float lightAlpha = 1f, respawnTimer, shatterWait, wiggle, idleTimer, flashTimer;
    int idleFrame, flashFrame = -1;
    enum Pickup { None, Start, Hide, Shatter }

    public override bool Available => outline != null && idle.All(image => image != null) &&
        flash.All(image => image != null) && HasSlash && Art("triangle") != null && Art("refillParticle") != null;
    public override bool UpdatesBeforePlayer => spent;
    protected override int Depth => spent ? -1 : 1;

    public override void Touch(Player player, Action<string> play, Action<RumbleStrength, RumbleLength> rumble)
    {
        if (!collidable || !player.HurtboxOverlaps(Anchor.X - 8f, Anchor.Y - 8f, Anchor.X + 8f, Anchor.Y + 8f) ||
            !player.UseRefill(TwoDashes)) return;
        play(TwoDashes ? DoubleTouchSound : TouchSound);
        rumble(RumbleStrength.Medium, RumbleLength.Medium);
        collidable = false;
        routine = Pickup.Start;
        respawnTimer = 2.5f;
    }

    public override bool Advance(DesktopItemFrame frame)
    {
        float dt = frame.Dt;
        StepFrames(ref idleFrame, ref idleTimer, dt, .1f, idle.Length, loop: true);
        StepFrames(ref flashFrame, ref flashTimer, dt, .05f, flash.Length, loop: false);
        AdvanceSlash(dt);
        sine = (sine + MathF.Tau * .6f * dt) % MathF.Tau;
        wiggle = Math.Max(0f, wiggle - dt);

        bool freeze = false;
        switch (routine)
        {
            case Pickup.Start:
                freeze = true; // RefillRoutine: freeze, then yield one scene update.
                routine = Pickup.Hide;
                break;
            case Pickup.Hide:
                spent = true;
                flashFrame = -1;
                shatterWait = .05f;
                routine = Pickup.Shatter;
                break;
            case Pickup.Shatter:
                if (!OverInterval(ref shatterWait, dt)) break;
                PointF speed = frame.Player.Speed;
                float angle = MathF.Atan2(speed.Y, speed.X);
                PType shatter = TwoDashes ? PinkShatter : Shatter;
                Particles.Emit(shatter, 0, 0, angle - MathF.PI / 2f, .87266463f / 2f, 5, 4, 4);
                Particles.Emit(shatter, 0, 0, angle + MathF.PI / 2f, .87266463f / 2f, 5, 4, 4);
                BurstSlash(angle);
                routine = Pickup.None;
                break;
        }

        if (respawnTimer > 0f)
        {
            respawnTimer -= dt;
            if (respawnTimer <= 0f)
            {
                collidable = true;
                spent = false;
                wiggle = 1f;
                frame.Play(TwoDashes ? DoubleReturnSound : ReturnSound);
                Particles.Emit(TwoDashes ? PinkRegen : Regen, 0, 0, 0, MathF.PI, 16, 2, 2);
            }
        }
        else if (frame.OnInterval(.1f))
            Particles.Emit(TwoDashes ? PinkGlow : Glow, 0, 0, 0, MathF.PI, 1, 5, 5);

        lightAlpha = Math.Clamp(lightAlpha + (spent ? -4f : 4f) * dt, 0f, 1f);
        if (!spent && frame.OnInterval(2f))
        {
            flashFrame = 0;
            flashTimer = 0f;
        }
        // The desktop has no ambient darkness, mirror surface, or camera to shake.
        return freeze;
    }

    protected override Rectangle Paint()
    {
        float y = Center + 2f * MathF.Sin(sine);
        Rectangle area;
        using (var g = new Graphics(Body))
        {
            g.Clear();
            if (spent) DrawCentered(g, outline, Center, Center);
            else
            {
                float size = 1f + .2f * wiggle * MathF.Cos(MathF.Tau * 4f * (1f - wiggle));
                Bitmap image = idle[idleFrame];
                DrawOutline(g, image, Center, y, size, size);
                DrawCentered(g, image, Center, y, size, size);
                if (flashFrame >= 0) DrawCentered(g, flash[flashFrame], Center, y, size, size);
            }
            area = g.Dirty;
        }
        return bloom.Apply(Body, area, Center, y, lightAlpha, ambient: TwoDashes ? .5f : 0f);
    }
}
