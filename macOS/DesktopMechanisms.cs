using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Numerics;

namespace DeskMadeline.Mac;

internal interface IDesktopJumpThru
{
    bool Collidable { get; }
    Solid Collider { get; }
}

internal sealed partial class DesktopItems
{
    readonly List<IPetHoldable> holdables = new List<IPetHoldable>();
    readonly Dictionary<IntPtr, IDesktopDashSolid> dashSolids = new Dictionary<IntPtr, IDesktopDashSolid>();
    Func<IntPtr, PointF, DashCollisionResults> solidDashHandler;
    Func<IntPtr, bool> hasDashHandler;
    Action<PointF> dashHandler;
    readonly MechanismState manualMechanisms = new MechanismState(), courseMechanisms = new MechanismState();

    DesktopItem CreateAdvancedItem(int id, DesktopItemPlacement placement) => placement.Kind switch
    {
        DesktopItemKind.Bumper => new DesktopBumper(id, placement, bloom, audio),
        DesktopItemKind.DreamBlock => new DesktopDreamBlock(id, placement, bloom),
        DesktopItemKind.CrumblePlatform => new DesktopCrumblePlatform(id, placement),
        DesktopItemKind.SwapBlock => new DesktopSwapBlock(id, placement, bloom, audio),
        DesktopItemKind.KevinBlock => new DesktopKevinBlock(id, placement, bloom, audio),
        DesktopItemKind.Puffer => new DesktopPuffer(id, placement, bloom, audio),
        DesktopItemKind.Jellyfish => new DesktopJellyfish(id, placement, bloom, audio),
        DesktopItemKind.Strawberry => new DesktopStrawberry(id, placement, bloom),
        DesktopItemKind.TouchSwitch => new DesktopTouchSwitch(id, placement, bloom),
        DesktopItemKind.SwitchGate => new DesktopSwitchGate(id, placement, bloom),
        DesktopItemKind.CassetteBlue or DesktopItemKind.CassettePink or DesktopItemKind.CassetteYellow or DesktopItemKind.CassetteGreen
            => new DesktopCassetteBlock(id, placement, bloom),
        DesktopItemKind.CoreBlock => new DesktopCoreBlock(id, placement, bloom),
        DesktopItemKind.CoreToggle => new DesktopCoreToggle(id, placement, bloom, () => ToggleCore(id)),
        DesktopItemKind.Heart => new DesktopHeart(id, placement, bloom, audio),
        _ => throw new ArgumentOutOfRangeException(nameof(placement)),
    };

    DashCollisionResults OnSolidDash(IntPtr id, PointF direction) =>
        dashSolids.TryGetValue(id, out IDesktopDashSolid item)
            ? item.OnDashCollide(direction, play, rumble) : DashCollisionResults.NormalOverride;

    void OnDash(PointF direction)
    {
        foreach (DesktopItem item in items)
            if (item is DesktopSwapBlock swap && !swap.Held)
                swap.OnDash(play);
    }

    bool ToggleCore(int id)
    {
        bool generated = items.Any(item => item.Id == id && item.Generated);
        MechanismState group = generated ? courseMechanisms : manualMechanisms;
        group.Cold = !group.Cold;
        return group.Cold;
    }

    void AdvanceMechanisms(DesktopItemFrame frame)
    {
        AdvanceGroup(manualMechanisms, generated: false, frame);
        if (course != null) AdvanceGroup(courseMechanisms, generated: true, frame);
    }

    void AdvanceGroup(MechanismState group, bool generated, DesktopItemFrame frame)
    {
        // A desktop is one room for loose switches. Generated routes have a
        // separate room state so regenerating does not reset a user's puzzle.
        int switchCount = 0, activated = 0, maxCassette = -1;
        foreach (DesktopItem item in items)
        {
            if (item.Generated != generated || item.Held) continue;
            if (item is DesktopTouchSwitch touch)
            {
                switchCount++;
                if (touch.Activated) activated++;
            }
            else if (item is DesktopCassetteBlock cassette) maxCassette = Math.Max(maxCassette, cassette.Index);
        }
        bool finished = switchCount > 0 && activated == switchCount;
        if (finished && !group.Finished) frame.Play("event:/game/general/touchswitch_last_oneshot");
        group.Finished = finished;

        if (maxCassette >= 0)
        {
            int oldBeat = (int)(group.Beat * 6f);
            group.Beat += frame.Dt;
            int beat = (int)(group.Beat * 6f);
            if (beat != oldBeat)
            {
                if (beat % 8 == 0) frame.Play("event:/game/general/cassette_block_switch_2");
                else if (beat % 8 == 4) frame.Play("event:/game/general/cassette_block_switch_1");
            }
        }
        else group.Beat = 0f;
        // The room's cycle length is the highest placed color plus one.
        int colors = Math.Max(1, maxCassette + 1);
        int current = ((int)(group.Beat * 6f) / 8) % colors;
        bool warning = (int)(group.Beat * 6f) % 8 == 7;

        foreach (DesktopItem item in items)
        {
            if (item.Generated != generated || item.Held) continue;
            switch (item)
            {
                case DesktopTouchSwitch touch: touch.Finished = finished; break;
                case DesktopSwitchGate gate when finished: gate.Open(); break;
                case DesktopCassetteBlock cassette:
                    cassette.SetBeat(cassette.Index == current, warning && cassette.Index == (current + 1) % colors, frame.Player);
                    break;
                case DesktopCoreToggle toggle: toggle.SetMode(group.Cold); break;
                case DesktopCoreBlock block: block.SetMode(group.Cold); break;
            }
        }
    }

    void ClearMechanisms()
    {
        manualMechanisms.Reset();
        courseMechanisms.Reset();
    }

    sealed class MechanismState
    {
        public bool Cold, Finished;
        public float Beat;
        public void Reset() { Cold = Finished = false; Beat = 0f; }
    }
}

internal sealed class DesktopTouchSwitch(int id, DesktopItemPlacement placement, SpriteBloom bloom)
    : DesktopItemWithEffects(id, placement, 52)
{
    readonly Bitmap container = Art("touchswitch/container");
    readonly Bitmap[] icon = Frames("touchswitch/icon", 6);
    public bool Activated { get; private set; }
    public bool Finished { get; set; }
    int animation;
    float timer, pulse;
    public override bool Available => container != null && icon.All(x => x != null);

    public override void Touch(Player player, Action<string> play, Action<RumbleStrength, RumbleLength> rumble)
    {
        if (Activated || !player.HurtboxOverlaps(Anchor.X - 8f, Anchor.Y - 8f, Anchor.X + 8f, Anchor.Y + 8f)) return;
        // TouchSwitch uses a radius of 8. The overlap above excludes most actors.
        PointF center = player.Center;
        float dx = Math.Max(0f, Math.Abs(center.X - Anchor.X) - 4f);
        float dy = Math.Max(0f, Math.Abs(center.Y - Anchor.Y) - player.CurrentHitHeight / 2f);
        if (dx * dx + dy * dy > 64f) return;
        Activated = true;
        pulse = .5f;
        play("event:/game/general/touchswitch_any");
        rumble(RumbleStrength.Light, RumbleLength.Medium);
    }

    public override void OnGrabbed() => Activated = Finished = false;

    public override bool Advance(DesktopItemFrame frame)
    {
        pulse = Math.Max(0f, pulse - frame.Dt);
        StepFrames(ref animation, ref timer, frame.Dt, Finished ? .06f : .1f, 6, loop: true);
        return false;
    }

    protected override Rectangle Paint()
    {
        Rectangle area;
        using (var g = new Graphics(Body))
        {
            g.Clear();
            float scale = 1f + MathF.Sin((.5f - pulse) * MathF.Tau * 4f) * pulse * .5f;
            DrawCentered(g, container, Center, Center, scale, scale);
            DrawCentered(g, icon[animation], Center, Center, scale, scale, Finished ? Color.FromArgb(0xf1, 0x41, 0xdf)
                : Activated ? Color.White : Color.FromArgb(0x5f, 0xcd, 0xe4));
            area = g.Dirty;
        }
        return bloom.Apply(Body, area, Center, Center, 1f, radius: 12, opacity: Activated ? .5f : .2f);
    }
}

internal sealed class DesktopCassetteBlock(int id, DesktopItemPlacement placement, SpriteBloom bloom)
    : DesktopResizableBlock(id, placement, new Size(32, 16))
{
    readonly Bitmap active = Art("cassetteblock/solid");
    readonly Bitmap inactive = Art(FormattableString.Invariant($"cassetteblock/pressed{(int)placement.Kind - (int)DesktopItemKind.CassetteBlue:00}"));
    bool on = placement.Kind == DesktopItemKind.CassetteBlue, pulse;
    Size drawnSize;
    Rectangle drawnArea;
    int drawnState = -1;
    public int Index => (int)Placement.Kind - (int)DesktopItemKind.CassetteBlue;
    public override bool Available => active != null && inactive != null;
    public override bool Collidable => on;
    protected override int SurfaceSoundIndex => 35;
    protected override int Depth => on ? 1 : -1;
    bool Lit => Id == 0 || (Held && !Resizing) || on;
    protected override float HitOffsetY => Lit || pulse ? 0f : 2f;
    static readonly Color[] Colors = {
        Color.FromArgb(0x49, 0xaa, 0xf0), Color.FromArgb(0xf0, 0x49, 0xbe),
        Color.FromArgb(0xfc, 0xdc, 0x3a), Color.FromArgb(0x38, 0xe0, 0x4e),
    };

    public void SetBeat(bool selected, bool warning, Player player)
    {
        pulse = warning;
        if (!selected) on = false;
        else if (!on && !player.OverlapsHitbox(Collider) && !BlockedAt(player, Position)) on = true;
    }

    protected override Rectangle Paint()
    {
        bool lit = Lit;
        int state = lit ? 2 : pulse ? 1 : 0;
        if (drawnState == state && drawnSize == BlockSize) return drawnArea;
        EnsureBlockCanvas();
        Rectangle area;
        using (var g = new Graphics(Body))
        {
            g.Clear();
            Bitmap tile = lit ? active : inactive;
            float left = Center - Width / 2f, top = CenterY - Height / 2f + HitOffsetY;
            for (int x = 0; x < Width / 8; x++)
            for (int y = 0; y < Height / 8; y++)
            {
                int tx = x == Width / 8 - 1 ? 2 : x == 0 ? 0 : 1;
                int ty = y == Height / 8 - 1 ? 2 : y == 0 ? 0 : 1;
                g.DrawRegion(tile, new Rectangle(tx * 8, ty * 8, 8, 8), left + x * 8, top + y * 8,
                    tint: Colors[Index]);
            }
            area = g.Dirty;
        }
        drawnState = state;
        drawnSize = BlockSize;
        drawnArea = bloom.Apply(Body, area, Center, CenterY, 0f, ambient: lit ? .15f : 0f);
        return drawnArea;
    }
}

internal sealed class DesktopCoreToggle(int id, DesktopItemPlacement placement, SpriteBloom bloom, Func<bool> toggle)
    : DesktopItem(id, placement, 56)
{
    readonly Bitmap[] frames = Frames("coreFlipSwitch/switch", 13);
    bool cold, changing;
    float cooldown, animationTime;
    int animation;
    public override bool Available => frames.All(x => x != null);

    public void SetMode(bool value)
    {
        if (value == cold) return;
        cold = value;
        changing = true;
        animation = cold ? 2 : 10;
        animationTime = 0f;
    }

    public override void Touch(Player player, Action<string> play, Action<RumbleStrength, RumbleLength> rumble)
    {
        if (cooldown > 0f || !player.HurtboxOverlaps(Anchor.X - 8f, Anchor.Y - 12f, Anchor.X + 8f, Anchor.Y + 12f)) return;
        SetMode(toggle());
        play(cold ? "event:/game/09_core/switch_to_cold" : "event:/game/09_core/switch_to_hot");
        cooldown = 1f;
    }

    public override bool Advance(DesktopItemFrame frame)
    {
        cooldown -= frame.Dt;
        if ((animationTime += frame.Dt) < .08f) return false;
        animationTime -= .08f;
        if (changing)
        {
            animation += cold ? 1 : -1;
            if (cold ? animation >= 12 : animation <= 0) changing = false;
        }
        else animation = cold ? (animation == 11 ? 12 : 11) : (animation == 0 ? 1 : 0);
        return false;
    }

    protected override Rectangle Paint()
    {
        Rectangle area;
        using (var g = new Graphics(Body))
        {
            g.Clear();
            DrawCentered(g, frames[animation], Center, Center);
            area = g.Dirty;
        }
        return bloom.Apply(Body, area, Center, Center, 1f, radius: 18, opacity: .35f);
    }
}

internal sealed class DesktopCoreBlock(int id, DesktopItemPlacement placement, SpriteBloom bloom)
    : DesktopSolidItem(id, placement, new Size(16, 16), 72)
{
    const string Sound = "event:/game/09_core/";
    readonly Bitmap hot = Art("bumpblocknew/fire00"), ice = Art("bumpblocknew/ice00");
    readonly Bitmap[] hotCenter = Frames("bumpblocknew/fire_center", 6), iceCenter = Frames("bumpblocknew/ice_center", 6);
    readonly Bitmap[] hotRubble = Frames("bumpblocknew/fire_rubble", 4), iceRubble = Frames("bumpblocknew/ice_rubble", 4);
    Vector2 offset, direction, lift, breakOffset;
    float timer, speed, animationTime;
    int animation;
    bool cold, requestedCold;
    Phase phase;
    enum Phase { Waiting, Windup, Bounce, BounceEnd, Broken }
    public override bool Available => hot != null && ice != null && hotCenter[0] != null && iceCenter[0] != null &&
        hotRubble.All(x => x != null) && iceRubble.All(x => x != null);
    public override bool Collidable => phase != Phase.Broken;
    public override bool UpdatesBeforePlayer => true;
    protected override PointF Position => new PointF(Anchor.X + MathF.Round(offset.X), Anchor.Y + MathF.Round(offset.Y));
    public void SetMode(bool value) => requestedCold = value;

    public override bool Advance(DesktopItemFrame frame)
    {
        Player player = frame.Player;
        bool rider = player.IsRiding(Collider);
        if (phase == Phase.Waiting)
        {
            cold = requestedCold;
            if (!rider) return false;
            direction = Direction(player);
            phase = Phase.Windup;
            speed = 80f;
            frame.Play(Sound + (cold ? "iceblock_touch" : "bounceblock_touch"));
            frame.Rumble(RumbleStrength.Medium, RumbleLength.Medium);
        }
        else if (phase == Phase.Broken)
        {
            if (OverInterval(ref timer, frame.Dt) && !player.OverlapsHitbox(Collider) && !BlockedAt(player, Anchor))
            {
                phase = Phase.Waiting;
                cold = requestedCold;
                offset = Vector2.Zero;
                frame.Play(Sound + (cold ? "iceblock_reappear" : "bounceblock_reappear"));
            }
            return false;
        }

        StepFrames(ref animation, ref animationTime, frame.Dt, .08f, 6, loop: true);
        if (phase == Phase.Windup)
        {
            if (rider) direction = Direction(player);
            speed = Approach(speed, cold ? 35f : 40f, 600f * frame.Dt);
            if (MoveToward(player, -direction * (cold ? 16f : 10f), speed * (cold ? .333f : 1f), frame.Dt, false))
            {
                speed = 0f;
                if (cold) Break(frame);
                else phase = Phase.Bounce;
            }
        }
        else if (phase == Phase.Bounce)
        {
            speed = Approach(speed, 140f, 800f * frame.Dt);
            bool end = MoveToward(player, direction * 24f, speed, frame.Dt, true);
            if (end || !rider)
            {
                if (rider) player.BounceBlockLaunch(new PointF(lift.X, lift.Y));
                phase = Phase.BounceEnd;
                timer = .05f;
                frame.Rumble(RumbleStrength.Medium, RumbleLength.Medium);
            }
        }
        else if (phase == Phase.BounceEnd && OverInterval(ref timer, frame.Dt)) Break(frame);
        return false;
    }

    Vector2 Direction(Player player)
    {
        if (cold) return -Vector2.UnitY;
        Vector2 vector = new Vector2(player.Center.X - Position.X, player.Center.Y - Position.Y);
        return vector.LengthSquared() > 0f ? Vector2.Normalize(vector) : -Vector2.UnitY;
    }

    bool MoveToward(Player player, Vector2 target, float velocity, float dt, bool bounce)
    {
        PointF old = Position;
        Vector2 delta = target - offset;
        float distance = delta.Length();
        Vector2 dir = distance > 0f ? delta / distance : Vector2.Zero;
        offset += dir * Math.Min(distance, velocity * dt);
        lift = dir * (bounce ? Math.Min(velocity * 3f, 200f) : velocity);
        lift.X *= .75f;
        MoveSolid(player, old, Position, new PointF(lift.X, lift.Y));
        return distance <= Math.Max(velocity * dt, 1.414f);
    }

    void Break(DesktopItemFrame frame)
    {
        breakOffset = offset;
        offset = Vector2.Zero;
        phase = Phase.Broken;
        timer = 1.6f;
        if (!cold) frame.Play(Sound + "bounceblock_break");
        frame.Rumble(RumbleStrength.Light, RumbleLength.Medium);
    }

    protected override Rectangle Paint()
    {
        Rectangle area;
        using (var g = new Graphics(Body))
        {
            g.Clear();
            if (phase == Phase.Broken && !Held)
            {
                float age = 1.6f - timer;
                float alpha = Math.Clamp(1f - age * 1.5f, 0f, 1f);
                for (int i = 0; i < 4 && alpha > 0f; i++)
                {
                    float x = Center + breakOffset.X + (i % 2 == 0 ? -1 : 1) * (7f + age * 32f);
                    float y = Center + breakOffset.Y + (i / 2 == 0 ? -5f : 3f) + age * age * 100f;
                    Bitmap bit = (cold ? iceRubble : hotRubble)[i];
                    g.DrawImage(bit, x - 4f, y - 4f, 8f, 8f, alpha);
                }
            }
            else
            {
                Bitmap tile = cold ? ice : hot;
                g.DrawRegion(tile, new Rectangle(0, 0, 8, 8), Center - 8, Center - 8);
                g.DrawRegion(tile, new Rectangle(56, 0, 8, 8), Center, Center - 8);
                g.DrawRegion(tile, new Rectangle(0, 56, 8, 8), Center - 8, Center);
                g.DrawRegion(tile, new Rectangle(56, 56, 8, 8), Center, Center);
                Bitmap[] centerFrames = cold ? iceCenter : hotCenter;
                DrawCentered(g, centerFrames[animation] ?? centerFrames[0], Center, Center);
            }
            area = g.Dirty;
        }
        return bloom.Apply(Body, area, Center, Center, 1f, radius: 12, opacity: .3f);
    }
}
