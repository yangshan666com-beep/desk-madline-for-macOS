using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;

namespace DeskMadeline.Mac;

internal sealed class DesktopHeart(int id, DesktopItemPlacement placement, SpriteBloom bloom, SoundEffects audio = null)
    : DesktopItemWithEffects(id, placement, 72)
{
    const string Sound = "event:/game/general/crystalheart_";
    readonly Bitmap[] frames = Frames("heartgem/", 14);
    readonly Bitmap[] white = Frames("heartgem/white", 14);
    readonly DesktopHeartPickup pickup = new DesktopHeartPickup();
    int spin;
    float animationTime, time, wiggle, moveWiggle, bounceSoundDelay, collectedTime;
    PointF moveDirection;
    bool collected;
    public bool Completed => collected;
    public bool PickupActive => pickup.Active;
    public bool PlayerInFront => pickup.PlayerInFront;
    static readonly PType Shine = new PType
    {
        Tex = new[] { "refillParticle" }, Color = Color.FromArgb(0x5c, 0xae, 0xfa), Color2 = Color.White,
        BlinkColor = true, FadeInAndOut = true, Size = 1f, TextureScale = true,
        SpeedMin = 5f, SpeedMax = 10f, LifeMin = .6f, LifeMax = 1f,
    };

    public override bool Available => frames.All(image => image != null) && white.All(image => image != null) && pickup.Available;
    protected override int Depth => 2;

    public override void Touch(Player player, Action<string> play, Action<RumbleStrength, RumbleLength> rumble)
    {
        if (collected || player.BeingDragged || !player.HurtboxOverlaps(Anchor.X - 8f, Anchor.Y - 8f, Anchor.X + 8f, Anchor.Y + 8f)) return;
        if (player.IsDashAttacking)
        {
            if (audio == null) play(Sound + "blue_get");
            else audio.PlayKept(this, Sound + "blue_get");
            collected = true;
            collectedTime = 0f;
            pickup.Start(Anchor);
            Particles.Clear();
            return;
        }
        if (bounceSoundDelay <= 0f)
        {
            play(Sound + "bounce");
            bounceSoundDelay = .1f;
        }
        player.PointBounce(Anchor);
        float x = Anchor.X - player.Center.X, y = Anchor.Y - player.Center.Y;
        float length = MathF.Sqrt(x * x + y * y);
        moveDirection = length > 0f ? new PointF(x / length, y / length) : new PointF(0f, 1f);
        wiggle = .5f;
        moveWiggle = .8f;
        rumble(RumbleStrength.Medium, RumbleLength.Medium);
    }

    public override bool Advance(DesktopItemFrame frame)
    {
        float dt = frame.Dt;
        time += dt;
        bounceSoundDelay -= dt;
        wiggle = Math.Max(0f, wiggle - dt);
        moveWiggle = Math.Max(0f, moveWiggle - dt);
        if (collected)
        {
            if (!pickup.Active && !Generated && (collectedTime += dt) >= 3f)
            {
                collected = false;
                spin = 0;
                animationTime = 0f;
            }
            return false;
        }
        if ((animationTime += dt) >= .1f)
        {
            animationTime -= .1f;
            if (++spin == 23)
            {
                spin = 0;
                wiggle = .5f;
                frame.Play(Sound + "pulse");
            }
        }
        if (frame.OnInterval(.1f))
            Particles.Emit(Shine, 0f, 0f, -MathF.PI / 2f, 1.3962634f / 2f, 1, 8f, 8f);
        return false;
    }

    protected override Rectangle Paint()
    {
        if (collected && !pickup.WhiteHeart && !Held) return Rectangle.Empty;
        Rectangle area;
        float wave = moveWiggle / .8f * MathF.Sin((.8f - moveWiggle) * 2f * MathF.Tau);
        float x = Center - 8f * moveDirection.X * wave, y = Center + 2f * MathF.Sin(time * 2f) - 8f * moveDirection.Y * wave;
        float size = 1f + wiggle / .5f * .25f * MathF.Sin((.5f - wiggle) * 4f * MathF.Tau);
        using (var g = new Graphics(Body))
        {
            g.Clear();
            int frame = Math.Max(0, spin - 9);
            DrawCentered(g, frames[frame], x, y, size, size);
            if (pickup.WhiteHeart) DrawCentered(g, white[frame], x, y, size, size);
            area = g.Dirty;
        }
        return bloom.Apply(Body, area, x, y, 1f, opacity: .75f);
    }

    public float AdvancePickup(float dt, Player player, RectangleF monitor, Action<RumbleStrength, RumbleLength> rumble)
    {
        float timeRate = pickup.Advance(dt, player, monitor,
            () => rumble(RumbleStrength.Strong, RumbleLength.Medium), EndSound);
        if (player.IsDead) collected = false;
        return timeRate;
    }

    void EndSound() => audio?.FinishKept(this, "end", 1f);

    public void CancelPickup()
    {
        pickup.Cancel();
        EndSound();
    }

    public override void Render(List<Native.ItemLayer> layers, int scale)
    {
        base.Render(layers, scale);
        pickup.Render(layers, scale);
    }

    public override void Dispose()
    {
        audio?.StopLoop(this);
        pickup.Dispose();
        base.Dispose();
    }
}

internal sealed class DesktopStrawberry(int id, DesktopItemPlacement placement, SpriteBloom bloom)
    : DesktopItem(id, placement, 48)
{
    readonly Bitmap[] frames = Frames("strawberry/normal", 12);
    float time, animation, followDelay, collectTimer, pickedTimer;
    int idle, pickup = -1;
    PointF followPosition;
    bool following, spent;
    public override bool Available => frames.All(image => image != null);
    protected override PointF Position => following && !Held ? followPosition : Anchor;
    protected override int Depth => 2;
    public bool Following => following;

    public override void OnGrabbed()
    {
        following = spent = false;
        pickup = -1;
    }

    public override void Touch(Player player, Action<string> play, Action<RumbleStrength, RumbleLength> rumble)
    {
        if (following || spent || player.BeingDragged || !player.HurtboxOverlaps(Anchor.X - 7f, Anchor.Y - 7f, Anchor.X + 7f, Anchor.Y + 7f)) return;
        following = true;
        followPosition = Anchor;
        followDelay = .3f;
        collectTimer = 0f;
        play("event:/game/general/strawberry_touch");
    }

    public override bool Advance(DesktopItemFrame frame)
    {
        time += frame.Dt;
        if (spent)
        {
            if ((pickedTimer += frame.Dt) >= 3f) { spent = following = false; pickup = -1; }
            else if (pickup >= 0) StepFrames(ref pickup, ref animation, frame.Dt, .07f, 5, loop: false);
            return false;
        }
        StepFrames(ref idle, ref animation, frame.Dt, .1f, 36, loop: true);
        if (!following) return false;
        if (frame.Player.IsDead || frame.Player.IsRespawning)
        {
            following = false;
            return false;
        }
        followDelay -= frame.Dt;
        if (followDelay <= 0f)
        {
            PointF center = frame.Player.Center;
            var target = new PointF(center.X - frame.Player.Facing * 12f, center.Y - 3f);
            float amount = 1f - MathF.Pow(.01f, frame.Dt);
            followPosition.X += (target.X - followPosition.X) * amount;
            followPosition.Y += (target.Y - followPosition.Y) * amount;
            collectTimer = frame.Player.onGround && !frame.Player.BeingDragged ? collectTimer + frame.Dt : 0f;
            if (collectTimer > .15f)
            {
                spent = true;
                pickup = 0;
                pickedTimer = animation = 0f;
                frame.Play("event:/game/general/strawberry_get");
                frame.Rumble(RumbleStrength.Medium, RumbleLength.Medium);
            }
        }
        return false;
    }

    protected override Rectangle Paint()
    {
        if (spent && pickup < 0 && !Held) return Rectangle.Empty;
        Rectangle area;
        float y = Center + MathF.Sin(time * 4f) * 2f;
        using (var g = new Graphics(Body))
        {
            g.Clear();
            DrawCentered(g, frames[pickup >= 0 ? 7 + pickup : idle == 35 ? 7 : idle % 7], Center, y);
            area = g.Dirty;
        }
        return bloom.Apply(Body, area, Center, y, 1f, radius: 12);
    }
}
