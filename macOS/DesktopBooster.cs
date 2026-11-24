using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Numerics;

namespace DeskMadeline.Mac;

internal sealed class DesktopBooster(int id, DesktopItemPlacement placement, SpriteBloom bloom,
    Action<string> play, SoundEffects audio) : DesktopItemWithEffects(id, placement, 48, effectRadius: 160), IBooster
{
    const string Green = "event:/game/04_cliffside/greenbooster_";
    const string RedPrefix = "event:/game/05_mirror_temple/redbooster_";
    internal static string Sound(bool red, string action) => (red ? RedPrefix : Green) + action;
    readonly Bitmap[] frames = Frames(Prefix(placement.Kind), placement.Kind == DesktopItemKind.BoosterRed ? 35 : 26);
    readonly Bitmap outline = Art("booster/outline");
    Bitmap outlineImage;
    readonly PType burst = BurstType(placement.Kind == DesktopItemKind.BoosterRed);
    readonly PType appear = AppearType(placement.Kind == DesktopItemKind.BoosterRed);
    Player rider;
    PointF offset, launchDirection;
    float cannotUse, respawn, wiggle, animationTime;
    int frameIndex;
    bool flip, disposed;
    Phase phase;
    enum Phase { Idle, Inside, Spin, Pop, Empty }
    public bool Red => Placement.Kind == DesktopItemKind.BoosterRed;
    PointF IBooster.Center => new PointF(Anchor.X, Anchor.Y + 2f);
    protected override PointF Position => new PointF(Anchor.X + MathF.Floor(offset.X), Anchor.Y + MathF.Floor(offset.Y));
    protected override PointF EffectView => Position;
    protected override int Depth => 2;
    protected override int EffectDepth => -2;
    public override bool Available => outline != null && Art("blob") != null && Art("refillParticle") != null &&
        frames.All(image => image != null);
    string MoveLoopKey => FormattableString.Invariant($"desktop.booster.{Id}");

    static string Prefix(DesktopItemKind kind) => kind == DesktopItemKind.BoosterRed ? "booster/boosterRed" : "booster/booster";

    static PType BurstType(bool red) => new PType
    {
        Tex = new[] { "blob" }, Color = red ? Color.FromArgb(0x94, 0x2c, 0x3e) : Color.FromArgb(0x2c, 0x95, 0x6e),
        FadeOut = false, LifeMin = .5f, LifeMax = .8f, Size = .7f, SizeRange = .25f,
        TextureScale = true, ScaleOut = true, SpeedMin = 10f, SpeedMax = 20f,
        SpeedMultiplier = .01f, GravY = 90f,
    };

    static PType AppearType(bool red) => new PType
    {
        Tex = new[] { "refillParticle" }, Color = red ? Color.FromArgb(0xff, 0x59, 0x4a) : Color.FromArgb(0x4a, 0xcf, 0xc6),
        Size = 1f, TextureScale = true, LifeMin = .6f, LifeMax = 1f, SpeedMin = 40f, SpeedMax = 50f,
        SpeedMultiplier = .25f, LateFade = true,
    };

    public override void Touch(Player player, Action<string> sound, Action<RumbleStrength, RumbleLength> rumble)
    {
        if (cannotUse > 0f || respawn > 0f || phase == Phase.Spin || !Touches(player, hurtbox: true) ||
            !player.EnterBooster(this)) return;
        rider = player;
        cannotUse = .45f;
        play(Sound(Red, "enter"));
        SetPhase(Phase.Inside, 5);
        wiggle = .5f;
        flip = player.Facing < 0;
    }

    bool Touches(Player player, bool hurtbox)
    {
        float l, t, r, b;
        if (hurtbox) player.GetHurtbox(out l, out t, out r, out b);
        else player.GetHitbox(out l, out t, out r, out b);
        float dx = Anchor.X - Math.Clamp(Anchor.X, l, r);
        float y = Anchor.Y + 2f, dy = y - Math.Clamp(y, t, b);
        return dx * dx + dy * dy < 100f;
    }

    public void Launch(Player player, PointF direction)
    {
        play(Sound(Red, "dash"));
        if (Red) audio?.PlayKept(MoveLoopKey, Sound(true, "move"));
        rider = player;
        launchDirection = direction;
        SetPhase(Phase.Spin, 18);
        wiggle = .5f;
        flip = player.Facing < 0;
    }

    public void Release(Player player)
    {
        if (rider != player) return;
        rider = null;
        audio?.StopLoop(MoveLoopKey);
        if (disposed) return;
        play(Sound(Red, "end"));
        SetPhase(Phase.Pop, 9);
        cannotUse = 0f;
        respawn = 1f;
        wiggle = 0f;
    }

    public override void OnGrabbed() => rider?.CancelBooster(this);

    public override bool Advance(DesktopItemFrame state)
    {
        float dt = state.Dt;
        UpdateAnimation(dt);
        cannotUse = Math.Max(0f, cannotUse - dt);
        wiggle = Math.Max(0f, wiggle - dt);
        if (respawn > 0f)
        {
            respawn -= dt;
            if (respawn <= 0f)
            {
                play(Sound(Red, "reappear"));
                offset = PointF.Empty;
                SetPhase(Phase.Idle, 0);
                wiggle = .5f;
                for (int i = 0; i < 12; i++)
                    Particles.Emit(appear, 0f, 2f, i * MathF.PI / 6f, MathF.PI / 60f, 1, 2f, 2f);
            }
        }
        if (phase == Phase.Spin)
        {
            PointF center = state.Player.Center;
            offset = new PointF(center.X - Anchor.X, center.Y - 2f - Anchor.Y);
            if (state.OnInterval(.02f))
                Particles.Emit(burst, offset.X - launchDirection.X * 3f, offset.Y - launchDirection.Y * 3f,
                    MathF.Atan2(-launchDirection.Y, -launchDirection.X), .17453292f / 2f, 2, 3f, 3f);
        }
        else if (respawn <= 0f)
        {
            bool touching = Touches(state.Player, hurtbox: false);
            PointF center = state.Player.Center;
            var target = touching ? new Vector2(center.X - Anchor.X, center.Y - 2f - Anchor.Y) : Vector2.Zero;
            Vector2 delta = target - new Vector2(offset.X, offset.Y);
            float length = delta.Length();
            if (length > 80f * dt) delta *= 80f * dt / length;
            offset = new PointF(offset.X + delta.X, offset.Y + delta.Y);
            if (phase == Phase.Inside && !touching) SetPhase(Phase.Idle, 0);
        }
        return false;
    }

    void SetPhase(Phase next, int first)
    {
        phase = next;
        frameIndex = first;
        animationTime = 0f;
    }

    void UpdateAnimation(float dt)
    {
        if (phase == Phase.Empty) return;
        float delay = phase == Phase.Spin ? .06f : phase == Phase.Pop ? .08f : .1f;
        animationTime += dt;
        if (animationTime < delay) return;
        animationTime -= delay;
        frameIndex++;
        switch (phase)
        {
            case Phase.Idle when frameIndex == 5: frameIndex = 0; break;
            case Phase.Inside when frameIndex == 9: frameIndex = 5; break;
            case Phase.Spin when frameIndex == 26: frameIndex = 18; break;
            case Phase.Pop when frameIndex == 18: SetPhase(Phase.Empty, 0); break;
        }
    }

    protected override Rectangle Paint()
    {
        if (phase == Phase.Empty) return Rectangle.Empty;
        Rectangle area;
        using (var g = new Graphics(Body))
        {
            g.Clear();
            Bitmap frame = flip ? Sprites.Get(FormattableString.Invariant($"{Prefix(Placement.Kind)}{frameIndex:00}"), true) : frames[frameIndex];
            float size = 1f + .25f * (wiggle / .5f) * MathF.Cos(MathF.Tau * 4f * (.5f - wiggle));
            if (phase != Phase.Pop) DrawOutline(g, frame, Center, Center, size, size);
            DrawCentered(g, frame, Center, Center, size, size);
            area = g.Dirty;
        }
        PointF pos = Position;
        // Chapter 4's green booster also sits in ambient bloom. The red one
        // uses the point at its source, as in Mirror Temple.
        return bloom.Apply(Body, area, Center + Anchor.X - pos.X, Center + Anchor.Y - pos.Y,
            1f, opacity: .1f, ambient: Red ? 0f : .25f);
    }

    public override void Render(List<Native.ItemLayer> layers, int scale)
    {
        if (phase is Phase.Spin or Phase.Pop or Phase.Empty)
        {
            if (outlineImage == null)
            {
                outlineImage = new Bitmap(outline.Width, outline.Height);
                using var g = new Graphics(outlineImage);
                g.DrawImage(outline, 0, 0, outline.Width, outline.Height, .75f);
            }
            AddLayer(layers, outlineImage, new Rectangle(0, 0, outlineImage.Width, outlineImage.Height),
                Anchor, outlineImage.Width / 2, outlineImage.Height / 2, scale, -1, Id);
        }
        base.Render(layers, scale);
    }

    public override void Dispose()
    {
        disposed = true;
        rider?.CancelBooster(this);
        outlineImage?.Dispose();
        base.Dispose();
    }
}
