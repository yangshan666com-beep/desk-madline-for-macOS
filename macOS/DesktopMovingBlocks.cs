using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Numerics;

namespace DeskMadeline.Mac;

internal interface IDesktopDashSolid
{
    DashCollisionResults OnDashCollide(PointF direction, Action<string> play, Action<RumbleStrength, RumbleLength> rumble);
}

internal abstract class DesktopRouteBlock(int id, DesktopItemPlacement placement, Size size, Bitmap destination)
    : DesktopSolidItem(id, placement, size, 64)
{
    protected Vector2 Route;
    PointF offset;
    Bitmap guide, marker;
    protected float Progress { get; private set; }
    protected virtual bool DestinationInGuide => false;
    protected override PointF Position => new PointF(Anchor.X + offset.X, Anchor.Y + offset.Y);
    public override bool UpdatesBeforePlayer => true;

    public override void Prepare(DesktopTerrain terrain, int scale)
    {
        Route = new Vector2(MathF.Round(Placement.RouteX / scale), MathF.Round(Placement.RouteY / scale));
        Route = Vector2.Clamp(Route, new Vector2(-128f), new Vector2(128f));
        offset = new PointF(MathF.Round(Route.X * Progress), MathF.Round(Route.Y * Progress));
    }

    public override DesktopItemPlacement PlacementWhileHeld(int scale) =>
        Placement with { X = Position.X * scale, Y = Position.Y * scale };

    public override void OnGrabbed()
    {
        Progress = 0f;
        offset = PointF.Empty;
    }

    protected void Travel(Player player, float progress, Vector2 lift)
    {
        PointF old = Position;
        Progress = progress;
        offset = new PointF(MathF.Round(Route.X * progress), MathF.Round(Route.Y * progress));
        if (PlatformIndex >= 0) MoveSolid(player, old, Position, new PointF(lift.X, lift.Y));
    }

    public override void Render(List<Native.ItemLayer> layers, int scale)
    {
        if (destination != null && Route != Vector2.Zero)
        {
            int w = Width + (int)Math.Abs(Route.X) + 4, h = Height + (int)Math.Abs(Route.Y) + 4;
            if (guide == null || guide.Width != w || guide.Height != h)
            {
                guide?.Dispose();
                guide = new Bitmap(w, h);
            }
            using var g = new Graphics(guide);
            g.Clear();
            float x = Math.Max(0f, -Route.X) + 2f, y = Math.Max(0f, -Route.Y) + 2f;
            var travel = new Rectangle(2, 2, w - 4, h - 4);
            DrawGuide(g, travel);
            var end = new PointF(Anchor.X + Route.X, Anchor.Y + Route.Y);
            var endBounds = new Native.DesktopRect((end.X - Width / 2f) * scale, (end.Y - Height / 2f) * scale,
                Width * scale, Height * scale);
            AddLayer(layers, guide, g.Dirty, Anchor, (int)(x + Width / 2f), (int)(y + Height / 2f), scale, -2,
                DestinationInGuide ? Id | RouteEndpoint : 0, endBounds,
                DestinationInGuide ? Native.ItemHitMode.Region : Native.ItemHitMode.Alpha);
            if (!DestinationInGuide)
            {
                if (marker == null)
                {
                    marker = new Bitmap(Width, Height);
                    using var m = new Graphics(marker);
                    m.Clear();
                    if (destination.Width >= 24 && destination.Height >= 24)
                        DrawFrameTiles(m, destination, 0f, 0f);
                    else DrawCentered(m, destination, Width / 2f, Height / 2f);
                }
                AddLayer(layers, marker, new Rectangle(0, 0, Width, Height), end, Width / 2, Height / 2,
                    scale, -2, Id | RouteEndpoint);
            }
        }
        base.Render(layers, scale);
    }

    protected virtual void DrawGuide(Graphics g, Rectangle travel) { }

    public override void Dispose()
    {
        guide?.Dispose();
        marker?.Dispose();
        base.Dispose();
    }
}

internal sealed class DesktopSwapBlock(int id, DesktopItemPlacement placement, SpriteBloom bloom, SoundEffects audio)
    : DesktopRouteBlock(id, placement, new Size(32, 16), Art("swapblock/target"))
{
    const string Sound = "event:/game/05_mirror_temple/swapblock_";
    readonly Bitmap red = Art("swapblock/blockRed"), green = Art("swapblock/block"), targetArt = Art("swapblock/target");
    readonly Bitmap trackH = Art("swapblock/pathH"), trackV = Art("swapblock/pathV");
    readonly Bitmap[] middle = Frames("swapblock/midBlock", 4), middleRed = Frames("swapblock/midBlockRed", 4);
    float timer, speed, redAlpha = 1f, animationTime, pathTime = Random.Shared.NextSingle();
    int target, animation;
    public override bool Available => red != null && green != null && trackH != null && trackV != null &&
        targetArt != null && middle.All(x => x != null) && middleRed.All(x => x != null);
    protected override bool DestinationInGuide => true;

    public void OnDash(Action<string> play)
    {
        float distance = Route.Length();
        if (Held || distance < 1f) return;
        target = 1;
        timer = .8f;
        float max = 360f / distance;
        speed = Progress >= .2f ? max : max * (.33333334f + .6666667f * Progress / .2f);
        audio?.StopLoop(this);
        if (Progress < 1f) audio?.StartLoop(this, Sound + "move");
        else play(Sound + "move_end");
    }

    public override bool Advance(DesktopItemFrame frame)
    {
        pathTime += frame.Dt * 4f;
        float distance = Route.Length();
        if (distance < 1f) return false;
        if (timer > 0f && (timer -= frame.Dt) <= 0f)
        {
            target = 0;
            speed = 0f;
            audio?.StartLoop(this, Sound + "return");
        }
        redAlpha = Approach(redAlpha, target == 0 ? 1f : 0f, frame.Dt * 32f);
        if (Progress == 0f && target == 0) animation = 0;
        else StepFrames(ref animation, ref animationTime, frame.Dt, .08f, 4, loop: true);
        float max = (target == 1 ? 360f : 144f) / distance;
        speed = Approach(speed, max, frame.Dt * max / (target == 1 ? .2f : 1.5f));
        float next = Approach(Progress, target, speed * frame.Dt);
        if (next != Progress)
        {
            Travel(frame.Player, next, Route * (target == 1 ? max : -speed));
            if (next == target)
            {
                audio?.StopLoop(this);
                frame.Play(Sound + (target == 1 ? "move_end" : "return_end"));
            }
        }
        return false;
    }

    protected override void DrawGuide(Graphics g, Rectangle travel)
    {
        Bitmap track = Route.X == 0f ? trackV : trackH;
        // PathRenderer covers the union of the start and destination boxes.
        for (int x = travel.Left; x < travel.Right; x += track.Width)
        for (int y = travel.Top; y < travel.Bottom; y += track.Height)
            g.DrawRegion(track, new Rectangle(0, 0, Math.Min(track.Width, travel.Right - x),
                Math.Min(track.Height, travel.Bottom - y)), x, y);
        float opacity = .5f * (.5f + (MathF.Sin(pathTime) + 1f) * .25f);
        DrawBlockStyle(g, targetArt, null, travel.X, travel.Y, travel.Width, travel.Height, opacity);
    }

    protected override Rectangle Paint()
    {
        Rectangle area;
        using (var g = new Graphics(Body))
        {
            g.Clear();
            float x = Center - Width / 2f, y = Center - Height / 2f;
            if (Progress != target && speed > 0f && Route.LengthSquared() > 0f)
            {
                Vector2 direction = Vector2.Normalize(Route) * (target == 1 ? -1f : 1f);
                float extent = 16f * speed * Route.Length() / 360f;
                for (int i = 2; i < extent; i += 2)
                    DrawBlockStyle(g, green, middle[animation], x + direction.X * i, y + direction.Y * i,
                        Width, Height, 1f - i / extent);
            }
            if (redAlpha < 1f)
                DrawBlockStyle(g, green, middle[animation], x, y, Width, Height, 1f);
            if (redAlpha > 0f)
                DrawBlockStyle(g, red, middleRed[animation], x, y, Width, Height, redAlpha);
            area = g.Dirty;
        }
        return bloom.Apply(Body, area, Center, Center, 0f, ambient: .1f);
    }

    static void DrawBlockStyle(Graphics g, Bitmap tiles, Bitmap center, float left, float top, int width, int height, float opacity)
    {
        for (int x = 0; x < width;)
        {
            int tx = x == 0 ? 0 : x == width - 8 ? 2 : 1;
            int w = tx == 1 ? Math.Min(8, width - x - 8) : 8;
            for (int y = 0; y < height;)
            {
                int ty = y == 0 ? 0 : y == height - 8 ? 2 : 1;
                int h = ty == 1 ? Math.Min(8, height - y - 8) : 8;
                g.DrawRegion(tiles, new Rectangle(tx * 8, ty * 8, w, h), left + x, top + y, opacity);
                y += h;
            }
            x += w;
        }
        if (center != null)
            g.DrawImage(center, left + (width - center.Width) / 2f, top + (height - center.Height) / 2f,
                center.Width, center.Height, opacity);
    }

    public override void OnGrabbed()
    {
        audio?.StopLoop(this);
        timer = speed = 0f;
        target = 0;
        base.OnGrabbed();
    }

    public override void Dispose() { audio?.StopLoop(this); base.Dispose(); }
}

internal sealed class DesktopSwitchGate(int id, DesktopItemPlacement placement, SpriteBloom bloom)
    : DesktopRouteBlock(id, placement, new Size(24, 32), Art("switchgate/border"))
{
    readonly Bitmap tile = Art("switchgate/stars"), border = Art("switchgate/border");
    readonly Bitmap[] icon = Frames("switchgate/icon", 6);
    bool opening, opened;
    float elapsed, animationTimer;
    int animation;
    public override bool Available => tile != null && border != null && icon.All(x => x != null);
    public void Open() => opening = !opened;
    public bool IsOpen => opened;

    public override bool Advance(DesktopItemFrame frame)
    {
        if (!opening) return false;
        float previous = elapsed;
        elapsed += frame.Dt;
        if (previous < .1f && elapsed >= .1f) frame.Play("event:/game/general/touchswitch_gate_open");
        StepFrames(ref animation, ref animationTimer, frame.Dt, .08f, 6, loop: true);
        if (elapsed > .7f)
        {
            float progress = Math.Clamp((elapsed - .7f) / 2f, 0f, 1f);
            float eased = 1f - MathF.Pow(1f - progress, 3f);
            Travel(frame.Player, eased, Vector2.Zero);
            if (progress >= 1f)
            {
                opening = false;
                opened = true;
                frame.Play("event:/game/general/touchswitch_gate_finish");
            }
        }
        return false;
    }

    public override void OnGrabbed()
    {
        base.OnGrabbed();
        opened = opening = false;
        elapsed = 0f;
    }

    protected override Rectangle Paint()
    {
        Rectangle area;
        using (var g = new Graphics(Body))
        {
            g.Clear();
            float shake = opening && elapsed >= .1f && elapsed < .6f ? MathF.Round(MathF.Sin(elapsed * 75f)) : 0f;
            DrawFrameTiles(g, tile, Center - Width / 2f + shake, Center - Height / 2f, interior: true);
            Color color = opened ? Color.FromArgb(0xf1, 0x41, 0xdf) :
                opening ? Color.White : Color.FromArgb(0x5f, 0xcd, 0xe4);
            DrawCentered(g, icon[animation], Center + shake, Center, tint: color);
            area = g.Dirty;
        }
        return bloom.Apply(Body, area, Center, Center, 0f, ambient: .25f);
    }
}

internal sealed class DesktopKevinBlock(int id, DesktopItemPlacement placement, SpriteBloom bloom, SoundEffects audio)
    : DesktopSolidItem(id, placement, new Size(32, 32), 64), IDesktopDashSolid
{
    const string Sound = "event:/game/06_reflection/crushblock_";
    readonly Bitmap tile = Art("crushblock/block03"), idle = Art("crushblock/idle_face");
    readonly Bitmap[] hit = Frames("crushblock/hit", 3), hurt = Frames("crushblock/hurt", 13);
    readonly Bitmap[] lit = new[] { Art("crushblock/lit_left"), Art("crushblock/lit_right"),
        Art("crushblock/lit_top"), Art("crushblock/lit_bottom") };
    readonly List<Vector2> waypoints = new List<Vector2>();
    Vector2 offset, direction;
    float speed, counter, timer, animationTimer;
    int animation;
    Phase phase;
    enum Phase { Idle, Windup, Attack, Impact, Return, Wait }
    protected override PointF Position => new PointF(Anchor.X + offset.X, Anchor.Y + offset.Y);
    public override bool Available => tile != null && idle != null && hit.All(x => x != null) &&
        hurt.All(x => x != null) && lit.All(x => x != null);
    public override bool UpdatesBeforePlayer => true;

    public DashCollisionResults OnDashCollide(PointF dash, Action<string> play, Action<RumbleStrength, RumbleLength> rumble)
    {
        var next = new Vector2(-dash.X, -dash.Y);
        if (phase == Phase.Windup || phase == Phase.Attack && direction == next)
            return DashCollisionResults.NormalCollision;
        if (waypoints.Count == 0 || waypoints[^1] != offset) waypoints.Add(offset);
        direction = next;
        timer = .4f;
        animation = 0;
        animationTimer = speed = counter = 0f;
        phase = Phase.Windup;
        play(Sound + "activate");
        audio?.StopLoop(this);
        audio?.StartLoop(this, Sound + "move_loop");
        rumble(RumbleStrength.Strong, RumbleLength.Medium);
        return DashCollisionResults.Rebound;
    }

    public override bool Advance(DesktopItemFrame frame)
    {
        switch (phase)
        {
            case Phase.Windup:
                if (OverInterval(ref timer, frame.Dt)) { animation = 0; phase = Phase.Attack; }
                else StepFrames(ref animation, ref animationTimer, frame.Dt, .08f, 3, loop: true);
                break;
            case Phase.Attack:
                speed = Approach(speed, 240f, 500f * frame.Dt);
                counter += speed * frame.Dt;
                int amount = (int)MathF.Round(counter);
                counter -= amount;
                for (int i = 0; i < amount; i++)
                {
                    if (TryAttackStep(frame.Player)) continue;
                    phase = Phase.Impact;
                    timer = .4f;
                    animation = 0;
                    animationTimer = speed = counter = 0f;
                    frame.Play(Sound + "impact");
                    frame.Rumble(RumbleStrength.Medium, RumbleLength.Medium);
                    audio?.StopLoop(this);
                    audio?.StartLoop(this, Sound + "return_loop");
                    break;
                }
                break;
            case Phase.Impact:
                StepFrames(ref animation, ref animationTimer, frame.Dt, .08f, 13, loop: true);
                if (OverInterval(ref timer, frame.Dt)) phase = Phase.Return;
                break;
            case Phase.Return:
                speed = Approach(speed, 60f, 160f * frame.Dt);
                counter += speed * frame.Dt;
                float step = MathF.Round(counter);
                counter -= step;
                Vector2 target = waypoints[^1];
                Vector2 next = new Vector2(Approach(offset.X, target.X, step), Approach(offset.Y, target.Y, step));
                MoveTo(frame.Player, next);
                if (next == target)
                {
                    waypoints.RemoveAt(waypoints.Count - 1);
                    if (waypoints.Count == 0)
                    {
                        phase = Phase.Idle;
                        audio?.StopLoop(this);
                        frame.Play(Sound + "rest");
                    }
                    else
                    {
                        phase = Phase.Wait;
                        timer = .2f;
                        speed = counter = 0f;
                        frame.Play(Sound + "rest_waypoint");
                    }
                }
                break;
            case Phase.Wait:
                if (OverInterval(ref timer, frame.Dt)) phase = Phase.Return;
                break;
        }
        return false;
    }

    bool TryAttackStep(Player player)
    {
        Vector2 next = offset + direction;
        if (!BlockedAt(player, At(next))) { MoveTo(player, next); return true; }
        var perpendicular = new Vector2(direction.Y, direction.X);
        for (int i = 1; i <= 4; i++)
        for (int side = 1; side >= -1; side -= 2)
        {
            Vector2 around = perpendicular * i * side;
            if (BlockedAt(player, At(next + around))) continue;
            MoveTo(player, offset + around);
            MoveTo(player, next + around);
            return true;
        }
        return false;
    }

    PointF At(Vector2 position) => new PointF(Anchor.X + position.X, Anchor.Y + position.Y);
    void MoveTo(Player player, Vector2 next)
    {
        PointF old = Position;
        offset = next;
        MoveSolid(player, old, Position, PointF.Empty);
    }

    protected override Rectangle Paint()
    {
        Rectangle area;
        using (var g = new Graphics(Body))
        {
            g.Clear();
            float shake = phase is Phase.Windup or Phase.Impact ? MathF.Round(MathF.Sin(timer * 94f)) : 0f;
            float x = Center - Width / 2f + shake, y = Center - Height / 2f;
            g.FillRectangle(Color.FromArgb(0x62, 0x22, 0x2b), x + 2f, y + 2f, Width - 4f, Height - 4f);
            for (int i = 0; i < Width / 8; i++)
            for (int j = 0; j < Height / 8; j++)
                if (i == 0 || j == 0 || i == Width / 8 - 1 || j == Height / 8 - 1)
                    g.DrawRegion(tile, new Rectangle(i * 8, j * 8, 8, 8), x + i * 8, y + j * 8);
            Bitmap face = phase == Phase.Windup ? hit[animation] : phase == Phase.Idle ? idle :
                phase == Phase.Attack ? (Art("crushblock/hit_" + DirectionName() + "01") ?? hit[^1]) : hurt[animation % hurt.Length];
            DrawCentered(g, face, Center + shake, Center);
            if (phase is Phase.Windup or Phase.Attack)
            {
                int edge = direction.X < 0 ? 0 : direction.X > 0 ? 1 : direction.Y < 0 ? 2 : 3;
                Bitmap light = lit[edge];
                g.DrawImage(light, x + (edge == 1 ? Width - light.Width : 0f),
                    y + (edge == 3 ? Height - light.Height : 0f), light.Width, light.Height);
            }
            area = g.Dirty;
        }
        return bloom.Apply(Body, area, Center, Center, 0f, ambient: .2f);
    }

    string DirectionName() => direction.X < 0 ? "left" : direction.X > 0 ? "right" : direction.Y < 0 ? "up" : "down";

    public override DesktopItemPlacement PlacementWhileHeld(int scale) =>
        Placement with { X = Position.X * scale, Y = Position.Y * scale };
    public override void OnGrabbed()
    {
        waypoints.Clear();
        phase = Phase.Idle;
        offset = Vector2.Zero;
        audio?.StopLoop(this);
    }
    public override void Dispose() { audio?.StopLoop(this); base.Dispose(); }
}
