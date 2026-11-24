using System;
using System.Drawing;
using System.Linq;

namespace DeskMadeline.Mac;

internal sealed class DesktopSpring(int id, DesktopItemPlacement placement) : DesktopItem(id, placement, 40)
{
    static readonly int[] BounceFrames = { 0, 1, 2, 2, 2, 2, 2, 2, 2, 2, 2, 3, 4, 5 };
    readonly Bitmap[] frames = Frames("spring/", 6);
    PointF support;
    float wiggle, animationTime;
    int bounceFrame = -1;
    bool supported;
    public override bool Available => frames.All(image => image != null);
    public override bool UpdatesBeforePlayer => true;
    protected override int Depth => -1;
    int Side => Placement.Kind == DesktopItemKind.WallSpringLeft ? 1 :
        Placement.Kind == DesktopItemKind.WallSpringRight ? -1 : 0;
    protected override PointF Position => !Held && supported ? support : Anchor;

    public override void Prepare(DesktopTerrain terrain, int scale)
    {
        // Desktop placement attaches the floor spring to the first usable top
        // below the anchor, including a window ledge or the monitor floor.
        support = Anchor;
        if (Generated) { supported = true; return; }
        float distance = float.PositiveInfinity;
        foreach (Solid solid in terrain.BaseSolids)
        {
            if (solid.Dream) continue;
            if (Side == 0)
            {
                if (Anchor.X < solid.L || Anchor.X >= solid.R || solid.T < Anchor.Y - 10f || solid.T >= distance) continue;
                distance = solid.T;
                support.Y = solid.T;
                continue;
            }
            if (solid.Id == DesktopTerrain.Floor || solid.OffScreen ||
                Anchor.Y - 8f < solid.T || Anchor.Y + 8f > solid.B) continue;
            float edge = Side > 0 ? solid.R : solid.L;
            float gap = Math.Abs(edge - Anchor.X);
            if (gap > 24f || gap >= distance) continue;
            distance = gap;
            support.X = edge;
        }
        supported = float.IsFinite(distance);
    }

    public override DesktopItemPlacement PlacementWhileHeld(int scale)
        => supported ? Placement with { X = support.X * scale, Y = support.Y * scale } : Placement;

    public override bool Advance(DesktopItemFrame frame)
    {
        wiggle = Math.Max(0f, wiggle - frame.Dt);
        StepFrames(ref bounceFrame, ref animationTime, frame.Dt, .07f, BounceFrames.Length, loop: false);
        return false;
    }

    public override void Touch(Player player, Action<string> play, Action<RumbleStrength, RumbleLength> rumble)
    {
        if (!supported || player.State == Player.StDreamDash) return;
        if (Side == 0)
        {
            if (player.Speed.Y < 0f || !player.HurtboxOverlaps(support.X - 8f, support.Y - 6f, support.X + 8f, support.Y)) return;
            player.SuperBounce(support.Y - 6f);
        }
        else
        {
            float face = support.X + Side * 6f;
            if (!player.HurtboxOverlaps(Math.Min(face, support.X), support.Y - 8f, Math.Max(face, support.X), support.Y + 8f) ||
                !player.SideBounce(Side, face, support.Y)) return;
        }
        play("event:/game/general/spring");
        bounceFrame = 0;
        animationTime = 0f;
        wiggle = 1f;
    }

    protected override Rectangle Paint()
    {
        using var g = new Graphics(Body);
        g.Clear();
        Bitmap image = frames[bounceFrame < 0 ? 0 : BounceFrames[bounceFrame]];
        float stretch = 1f + .2f * wiggle * MathF.Cos(MathF.Tau * 4f * (1f - wiggle));
        var state = g.Save();
        g.TranslateTransform(Center, Center);
        if (Side != 0) g.RotateTransform(90f * Side);
        float y = -image.Height * stretch / 2f;
        DrawOutline(g, image, 0f, y, 1f, stretch);
        DrawCentered(g, image, 0f, y, 1f, stretch);
        g.Restore(state);
        return g.Dirty;
    }
}

internal sealed class DesktopCloud(int id, DesktopItemPlacement placement, SpriteBloom bloom)
    : DesktopItemWithEffects(id, placement, 72), IDesktopJumpThru
{
    // Chapter 4's A-side clouds use a 32 px JumpThru
    // with the sprite origin eight pixels below the top of the image.
    readonly Bitmap sprite = Art("clouds/cloud00");
    readonly Bitmap[] pinkFrames = placement.Kind == DesktopItemKind.PinkCloud ? Frames("clouds/fragile", 13) : Array.Empty<Bitmap>();
    bool Fragile => pinkFrames.Length > 0;
    static readonly PType Vapor = VaporType(Color.FromArgb(0x2c, 0x5f, 0xcc));
    static readonly PType PinkVapor = VaporType(Color.FromArgb(0x5e, 0x22, 0xae));
    static PType VaporType(Color color) => new PType
    {
        Tex = new[] { "cloud" }, Color = color,
        FadeOut = false, LifeMin = .25f, LifeMax = .3f, Size = .7f, SizeRange = .25f,
        TextureScale = true, ScaleOut = true, SpeedMin = 10f, SpeedMax = 20f,
        SpeedMultiplier = .01f, GravY = 90f,
    };
    enum Motion { Waiting, Bouncing, Returning, Spent }
    Motion motion;
    bool canRumble;
    int offsetY;
    float movementCounter, liftY, speed, timer = Random.Shared.NextSingle() * 4f;
    float sx = 1f, sy = 1f, bob, respawn, animationTime;
    int cloudFrame, animationStart;
    public bool Collidable => motion != Motion.Spent;

    public override bool Available => sprite != null && Art("cloud") != null && pinkFrames.All(image => image != null);
    protected override int Depth => -1;
    protected override int EffectDepth => -2;
    protected override PointF Position => new PointF(Anchor.X, Anchor.Y + offsetY);
    public Solid Collider => new Solid
    {
        Id = SolidId, L = Anchor.X - 16f, R = Anchor.X + 16f,
        T = Anchor.Y + offsetY, B = Anchor.Y + offsetY + 5f, LiftY = liftY, SurfaceSoundIndex = 4,
    };

    public override bool Advance(DesktopItemFrame frame)
    {
        float dt = frame.Dt;
        Player player = frame.Player;
        bool rider = Collidable && player.IsRidingJumpThru(Collider);
        sx = Approach(sx, 1f, dt);
        sy = Approach(sy, 1f, dt);
        timer += dt;
        bob = rider ? 0f : Approach(bob, MathF.Sin(timer * 2f), dt * 4f);
        if (animationStart == 6)
        {
            StepFrames(ref cloudFrame, ref animationTime, dt, .06f, 13, loop: false);
            if (cloudFrame < 0) { cloudFrame = 0; animationStart = 0; }
        }
        if (motion == Motion.Spent)
        {
            StepFrames(ref cloudFrame, ref animationTime, dt, .08f, 6, loop: false);
            respawn -= dt;
            if (respawn <= 0f)
            {
                offsetY = 0;
                movementCounter = liftY = speed = 0f;
                sx = sy = 1f;
                motion = Motion.Waiting;
                cloudFrame = animationStart = 6;
                animationTime = 0f;
            }
            return false;
        }

        if (motion == Motion.Waiting)
        {
            if (rider)
            {
                canRumble = true;
                speed = 180f;
                (sx, sy) = (1.3f, .7f);
                motion = Motion.Bouncing;
                frame.Play(Fragile ? "event:/game/04_cliffside/cloud_pink_boost" : "event:/game/04_cliffside/cloud_blue_boost");
            }
            return false;
        }
        if (motion == Motion.Returning)
        {
            speed = Approach(speed, 180f, 600f * dt);
            float exactY = offsetY + movementCounter;
            float next = Approach(exactY, 0f, speed * dt);
            Move(player, next - exactY, (next - exactY) / dt, rider);
            if (next == 0f)
            {
                movementCounter = speed = 0f;
                motion = Motion.Waiting;
            }
            return false;
        }

        if (speed < 0f && canRumble)
        {
            canRumble = false;
            if (rider) frame.Rumble(RumbleStrength.Medium, RumbleLength.Medium);
        }
        if (speed < 0f && frame.OnInterval(.02f))
            Particles.Emit(Fragile ? PinkVapor : Vapor, 0f, offsetY + 2f, MathF.PI / 2f, .17453292f / 2f, 1, 16f, 1f);
        if (Fragile && speed < 0f) sy = Approach(sy, 0f, dt * 4f);

        if (offsetY >= 0)
            speed -= 1200f * dt;
        else
        {
            speed += 1200f * dt;
            if (speed >= -100f)
            {
                if (rider)
                {
                    player.Speed.Y = -200f;
                    rider = false;
                }
                (sx, sy) = (.7f, 1.3f);
                motion = Fragile ? Motion.Spent : Motion.Returning;
                if (Fragile)
                {
                    respawn = 2.5f;
                    cloudFrame = animationStart = 0;
                    animationTime = 0f;
                    player.JumpThrus[PlatformIndex] = default;
                    return false;
                }
            }
        }
        Move(player, speed * dt, speed < 0f ? -220f : speed, rider);
        return false;
    }

    void Move(Player player, float displacement, float lift, bool rider)
    {
        Solid previous = Collider;
        movementCounter += displacement;
        int amount = (int)MathF.Round(movementCounter);
        movementCounter -= amount;
        liftY = lift;
        offsetY += amount;
        Solid next = Collider;
        if (PlatformIndex >= 0) player.JumpThrus[PlatformIndex] = next;
        if (amount == 0 || player.IsDead || player.IsRespawning || player.BeingDragged || player.State == Player.StDreamDash) return;

        if (rider)
            player.MoveWithPlatform(0, amount, SolidId);
        else if (amount < 0 && !player.OverlapsHitbox(previous) && player.OverlapsHitbox(next))
            player.MoveWithPlatform(0, (int)(next.T - player.CollisionBottom), SolidId);
        else
            return;
        player.LiftSpeed = new PointF(0f, liftY);
    }

    static float Approach(float value, float target, float amount)
        => value < target ? Math.Min(value + amount, target) : Math.Max(value - amount, target);

    protected override Rectangle Paint()
    {
        if (Fragile && cloudFrame < 0 && !Held) return Rectangle.Empty;
        Rectangle area;
        using (var g = new Graphics(Body))
        {
            g.Clear();
            Bitmap image = Fragile ? pinkFrames[Math.Max(0, cloudFrame)] : sprite;
            g.DrawImage(image, Center - image.Width * sx / 2f, Center + bob - 8f * sy,
                image.Width * sx, image.Height * sy);
            area = g.Dirty;
        }
        // Golden Ridge has a quarter-strength ambient bloom even on props
        // without a BloomPoint. This is visible in the white and cyan ridges.
        return bloom.Apply(Body, area, Center, Center, 0f, ambient: .25f);
    }
}

internal sealed class DesktopFeather(int id, DesktopItemPlacement placement, SpriteBloom bloom)
    : DesktopItemWithEffects(id, placement, 52)
{
    internal const string GetSound = "event:/game/06_reflection/feather_get";
    internal const string RenewSound = "event:/game/06_reflection/feather_renew";
    internal const string ReturnSound = "event:/game/06_reflection/feather_reappear";

    static readonly PType Collect = FeatherType(60f, 70f, .1f);
    internal static readonly PType Boost = FeatherType(20f, 80f, .2f);
    internal static readonly PType Flying = new PType
    {
        Tex = new[] { "refillParticle" }, Color = Player.FlyPowerHairColor, Color2 = Color.FromArgb(0xff, 0xf2, 0x0f),
        BlinkColor = true, LateFade = true, LifeMin = .4f, LifeMax = .8f, Size = 1f, TextureScale = true,
        SpeedMin = 60f, SpeedMax = 70f, SpeedMultiplier = .25f,
    };
    static readonly PType Regen = new PType
    {
        Tex = new[] { "refillParticle" }, Color = Color.FromArgb(0xff, 0xdc, 0xa4), Color2 = Color.FromArgb(0xff, 0xe9, 0x5e),
        BlinkColor = true, LateFade = true, LifeMin = .4f, LifeMax = .6f, Size = 1f, TextureScale = true,
        SpeedMin = 30f, SpeedMax = 40f, SpeedMultiplier = .2f,
    };

    static PType FeatherType(float minimumSpeed, float maximumSpeed, float speedMultiplier) => new PType
    {
        Tex = new[] { "feather" }, Color = Player.FlyPowerHairColor, Color2 = Color.FromArgb(0xff, 0xf2, 0x0f),
        BlinkColor = true, LateFade = true, LifeMin = .6f, LifeMax = .9f, Size = 1f, TextureScale = true,
        SpeedMin = minimumSpeed, SpeedMax = maximumSpeed, SpeedMultiplier = speedMultiplier,
        RandomRotation = true, SpinFlippedChance = true, SpinMin = MathF.PI / 6f, SpinMax = 1.3962634f,
    };

    // Sprites.xml defines 21 idle frames followed by 21 flash frames.
    readonly Bitmap[] animation = Frames("flyFeather/idle", 21).Concat(Frames("flyFeather/flash", 21)).ToArray();
    readonly Bitmap outline = Art("flyFeather/outline");
    int animationFrame;
    float animationTimer, sine = Random.Shared.NextSingle() * MathF.Tau;
    float light = 1f, wiggle, respawnTimer, burstWait;
    bool collidable = true, spent, pendingHide, pendingBurst;
    PointF incomingSpeed;

    public override bool Available => outline != null && HasSlash && Art("feather") != null &&
        Art("refillParticle") != null && animation.All(image => image != null);

    public override void Touch(Player player, Action<string> play, Action<RumbleStrength, RumbleLength> rumble)
    {
        if (!collidable || !player.HurtboxOverlaps(Anchor.X - 10f, Anchor.Y - 10f, Anchor.X + 10f, Anchor.Y + 10f)) return;
        incomingSpeed = player.Speed;
        bool renew = player.State == Player.StStarFly;
        if (!player.StartStarFly()) return;
        play(renew ? RenewSound : GetSound);
        collidable = false;
        pendingHide = true;
        respawnTimer = 3f;
    }

    public override bool Advance(DesktopItemFrame frame)
    {
        float dt = frame.Dt;
        StepFrames(ref animationFrame, ref animationTimer, dt, .06f, animation.Length, loop: true);
        AdvanceSlash(dt);
        sine = (sine + MathF.Tau * .6f * dt) % MathF.Tau;
        wiggle = Math.Max(0f, wiggle - dt);
        if (pendingHide)
        {
            spent = true;
            pendingHide = false;
            pendingBurst = true;
            burstWait = .05f;
        }
        else if (pendingBurst && OverInterval(ref burstWait, dt))
        {
            pendingBurst = false;
            PointF from = frame.Player.Center;
            PointF direction = incomingSpeed.IsEmpty ? new PointF(Anchor.X - from.X, Anchor.Y - from.Y) : incomingSpeed;
            Particles.Emit(Collect, 0f, 0f, 0f, MathF.PI, 10, 6f, 6f);
            BurstSlash(MathF.Atan2(direction.Y, direction.X));
        }
        if (respawnTimer > 0f)
        {
            respawnTimer -= dt;
            if (respawnTimer <= 0f)
            {
                spent = false;
                collidable = true;
                wiggle = 1f;
                frame.Play(ReturnSound);
                Particles.Emit(Regen, 0f, 0f, 0f, MathF.PI, 16, 2f, 2f);
            }
        }
        light = Math.Clamp(light + (spent ? -4f : 4f) * dt, 0f, 1f);
        return false;
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
                DrawCentered(g, animation[animationFrame], Center, y, size, size);
            }
            area = g.Dirty;
        }
        return bloom.Apply(Body, area, Center, y, light, radius: 20, opacity: .8f, ambient: .2f);
    }
}
