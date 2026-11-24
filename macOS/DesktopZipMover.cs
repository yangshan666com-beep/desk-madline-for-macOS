using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Numerics;

namespace DeskMadeline.Mac;

internal sealed class DesktopZipMover(int id, DesktopItemPlacement placement, SpriteBloom bloom)
    : DesktopSolidItem(id, placement, Dimensions(placement.Kind))
{
    const int TrackMargin = 25;
    internal const string MoveSound = "event:/game/01_forsaken_city/zip_mover";
    static readonly Color Rope = Color.FromArgb(0x66, 0x39, 0x31), RopeLight = Color.FromArgb(0x9b, 0x61, 0x57);
    static readonly PType Sparks = new PType
    {
        Tex = new[] { "refillParticle" }, Color = Color.FromArgb(0xff, 0xf5, 0x38), FadeOut = false,
        TextureScale = true, Size = 1f, LifeMin = .15f, LifeMax = .25f, SpeedMin = 15f, SpeedMax = 20f,
    };
    static readonly PType Scrape = new PType
    {
        Tex = new[] { "smoke0", "smoke1", "smoke2", "smoke3" }, Color = Color.White, FadeOut = false,
        TextureScale = true, Size = .7f, SizeRange = .2f, ScaleOut = true, LifeMin = .3f, LifeMax = .1f,
        SpeedMin = 5f, SpeedMax = 15f, RandomRotation = true, GravY = 4f,
    };

    readonly Bitmap block = Art("zipmover/block"), cog = Art("zipmover/cog");
    readonly Bitmap[] lights = Frames("zipmover/light", 4), cogs = Frames("zipmover/innercog", 12);
    readonly ParticleSystem particles = new ParticleSystem();
    Bitmap track;
    Vector2 route, shake;
    float percent, at, wait, shakeTimer;
    int light = 1;
    Motion motion;
    enum Motion { Waiting, Arming, Outbound, AtEnd, Returning, AtStart }

    public override bool Available => block != null && cog != null && Art("refillParticle") != null &&
        lights.All(x => x != null) && cogs.All(x => x != null);
    protected override int Depth => 2;
    protected override int SurfaceSoundIndex => 7;
    protected override PointF Position => new PointF(Anchor.X + MathF.Round(route.X * percent), Anchor.Y + MathF.Round(route.Y * percent));

    internal static Size Dimensions(DesktopItemKind kind) => kind switch
    {
        DesktopItemKind.ZipSquare => new Size(24, 24),
        DesktopItemKind.ZipTall => new Size(16, 32),
        _ => new Size(32, 16),
    };

    public override void Prepare(DesktopTerrain terrain, int scale)
    {
        // Both ends are in desktop points, so moving or resizing the pet
        // keeps the cable's endpoints in their placed positions.
        route.X = Math.Clamp(DesktopTerrain.ToGame(Placement.X + Placement.RouteX, scale) - Anchor.X, -512, 512);
        route.Y = Math.Clamp(DesktopTerrain.ToGame(Placement.Y + Placement.RouteY, scale) - Anchor.Y, -512, 512);
    }

    public override bool Advance(DesktopItemFrame frame)
    {
        float dt = frame.Dt;
        if (shakeTimer > 0f)
        {
            shakeTimer -= dt;
            if (shakeTimer <= 0f) shake = Vector2.Zero;
            else if (frame.OnInterval(.04f)) shake = new Vector2(Random.Shared.Next(-1, 2), Random.Shared.Next(-1, 2));
        }

        switch (motion)
        {
            case Motion.AtStart:
                if (!OverInterval(ref wait, dt)) break;
                motion = Motion.Waiting;
                goto case Motion.Waiting;
            case Motion.Waiting:
                if (route == Vector2.Zero || frame.Player.BeingDragged || !frame.Player.IsRiding(Collider)) break;
                frame.Play(MoveSound);
                frame.Rumble(RumbleStrength.Medium, RumbleLength.Short);
                shakeTimer = wait = .1f;
                motion = Motion.Arming;
                break;
            case Motion.Arming:
                if (!OverInterval(ref wait, dt)) break;
                light = 3;
                at = 0f;
                motion = Motion.Outbound;
                break;
            case Motion.Outbound:
                at = Math.Min(1f, at + 2f * dt);
                Move(frame, Ease(at), outbound: true);
                if (at < 1f) break;
                shakeTimer = .2f;
                frame.Rumble(RumbleStrength.Strong, RumbleLength.Medium);
                wait = .5f;
                motion = Motion.AtEnd;
                break;
            case Motion.AtEnd:
                if (!OverInterval(ref wait, dt)) break;
                light = 2;
                at = 0f;
                motion = Motion.Returning;
                break;
            case Motion.Returning:
                at = Math.Min(1f, at + .5f * dt);
                Move(frame, 1f - Ease(at), outbound: false);
                if (at < 1f) break;
                light = 1;
                shakeTimer = .2f;
                wait = .5f;
                motion = Motion.AtStart;
                break;
        }
        return false;
    }

    static float Ease(float value) => value >= 1f ? 1f : 1f - MathF.Cos(value * MathF.PI / 2f);

    void Move(DesktopItemFrame frame, float nextPercent, bool outbound)
    {
        Player player = frame.Player;
        PointF old = Position;
        Vector2 delta = route * (nextPercent - percent);
        Vector2 lift = delta / frame.Dt;
        percent = nextPercent;
        PointF target = Position;
        if (outbound && frame.OnInterval(.1f)) EmitSparks();
        if (outbound && frame.OnInterval(.03f)) EmitScrapes(frame.Player, old, delta);
        MoveSolid(player, old, target, new PointF(lift.X, lift.Y));
    }

    void EmitSparks()
    {
        Vector2 unit = Vector2.Normalize(-route);
        Vector2 offset = new Vector2(-unit.Y, unit.X) * 5f;
        float angle = MathF.Atan2(unit.Y, unit.X);
        ParticlesAt(Vector2.Zero + offset, angle + MathF.PI / 8);
        ParticlesAt(Vector2.Zero - offset, angle - MathF.PI / 8);
        ParticlesAt(route + offset, angle + MathF.PI - MathF.PI / 8);
        ParticlesAt(route - offset, angle + MathF.PI + MathF.PI / 8);
    }

    void ParticlesAt(Vector2 position, float direction)
        => particles.Emit(Sparks, position.X, position.Y, direction, 1.3962634f / 2f, 1, 1f, 1f);

    void EmitScrapes(Player player, PointF old, Vector2 delta)
    {
        if (delta.X == 0f && delta.Y != 0f)
        {
            int vertical = Math.Sign(delta.Y);
            float top = old.Y - Height / 2f;
            float yEdge = old.Y + vertical * Height / 2f;
            int first = vertical == 1 ? 4 : 0, last = vertical == 1 ? Height - 12 : Math.Max(16, Height - 12);
            for (int side = -1; side <= 1; side += 2)
            {
                float edge = old.X + side * Width / 2f;
                if (!TouchesSolid(player, edge + side * 2f, yEdge - vertical * 2f)) continue;
                float spray = MathF.Atan2(-vertical, side);
                for (int i = first; i < last; i += 8)
                    particles.Emit(Scrape, edge + side - Anchor.X, top + i - Anchor.Y, spray, .25f, 1);
            }
            return;
        }
        if (delta.X == 0f || delta.Y != 0f) return;
        int dir = Math.Sign(delta.X);
        float x = old.X + dir * Width / 2f;
        float start = dir == 1 ? 12f : 0f, stop = dir == 1 ? Width : Width - 12f;
        for (int side = -1; side <= 1; side += 2)
        {
            float y = old.Y + side * Height / 2f;
            if (!TouchesSolid(player, x - dir * 2f, y + side * 2f)) continue;
            float angle = dir == 1 ? (side == -1 ? -MathF.PI / 4f : MathF.PI / 4f)
                : (side == -1 ? -3f * MathF.PI / 4f : 3f * MathF.PI / 4f);
            for (float i = start; i < stop; i += 8f)
                particles.Emit(Scrape, old.X - Width / 2f + i - Anchor.X, y + side - Anchor.Y, angle, .25f, 1);
        }
    }

    bool TouchesSolid(Player player, float x, float y) => player.Solids.Any(s => s.Id != SolidId &&
        x >= s.L && x < s.R && y >= s.T && y < s.B);

    public override void AfterFrame(float dt) => particles.Update(dt);

    protected override Rectangle Paint()
    {
        float left = Center - Width / 2f + shake.X, top = Center - Height / 2f + shake.Y;
        Rectangle area;
        using (var g = new Graphics(Body))
        {
            g.Clear();
            g.FillRectangle(Color.Black, left - 1f, top - 1f, Width + 2f, Height + 2f);
            int sign = 1;
            float angle = 0f;
            for (int y = 4; y <= Height - 4; y += 8)
            {
                int rowSign = sign;
                for (int x = 4; x <= Width - 4; x += 8)
                {
                    float phase = (angle + sign * percent * MathF.PI * 4f) / (MathF.PI / 2f);
                    int index = (int)((phase - MathF.Floor(phase)) * cogs.Length);
                    Bitmap texture = cogs[index];
                    int clipX = x == 4 ? 2 : 0, clipY = y == 4 ? 2 : 0;
                    int w = texture.Width - (x == 4 || x == Width - 4 ? 2 : 0);
                    int h = texture.Height - (y == 4 || y == Height - 4 ? 2 : 0);
                    float offX = x == 4 ? 2 : x == Width - 4 ? -2 : 0;
                    float offY = y == 4 ? 2 : y == Height - 4 ? -2 : 0;
                    g.DrawRegion(texture, new Rectangle(clipX, clipY, w, h),
                        left + x + offX - w / 2f, top + y + offY - h / 2f, sign < 0 ? .5f : 1f);
                    sign = -sign;
                    angle += MathF.PI / 3f;
                }
                if (rowSign == sign) sign = -sign;
            }
            DrawFrameTiles(g, block, left, top);
            g.DrawImage(lights[light], left + (Width - lights[light].Width) / 2f, top, lights[light].Width, lights[light].Height);
            area = g.Dirty;
        }
        return bloom.Apply(Body, area, Center + shake.X, top + light * 3f, 1f, radius: 6, opacity: 1f);
    }

    public override void Render(List<Native.ItemLayer> layers, int scale)
    {
        if (route != Vector2.Zero) DrawTrack(layers, scale);
        base.Render(layers, scale);
    }

    void DrawTrack(List<Native.ItemLayer> layers, int scale)
    {
        int width = (int)MathF.Abs(route.X) + 2 * TrackMargin;
        int height = (int)MathF.Abs(route.Y) + 2 * TrackMargin;
        if (track == null || track.Width != width || track.Height != height)
        {
            track?.Dispose();
            track = new Bitmap(width, height);
        }
        var from = new Vector2(TrackMargin - Math.Min(0f, route.X), TrackMargin - Math.Min(0f, route.Y));
        Vector2 to = from + route;
        Vector2 direction = Vector2.Normalize(route), normal = new Vector2(-direction.Y, direction.X);
        float distance = route.Length();
        float angle = MathF.Atan2(direction.Y, direction.X);
        using var g = new Graphics(track);
        g.Clear();
        for (int shadow = 1; shadow >= 0; shadow--)
        {
            Vector2 offset = new Vector2(0f, shadow);
            Line(g, from + normal * 3f + offset, angle, distance, shadow == 1 ? Color.Black : Rope);
            Line(g, from - normal * 4f + offset, angle, distance, shadow == 1 ? Color.Black : Rope);
            for (float tick = 4f - percent * MathF.PI * 8f % 4f; tick < distance; tick += 4f)
            {
                Line(g, from + normal * 4f + direction * tick + offset, angle, 2f, shadow == 1 ? Color.Black : RopeLight);
                Line(g, to - normal * 4f - direction * tick + offset, angle + MathF.PI, 2f, shadow == 1 ? Color.Black : RopeLight);
            }
            DrawRotated(g, cog, from.X, from.Y + shadow, percent * MathF.Tau, shadow == 1 ? Color.Black : null);
            DrawRotated(g, cog, to.X, to.Y + shadow, percent * MathF.Tau, shadow == 1 ? Color.Black : null);
        }
        particles.Draw(g, -from.X, -from.Y, Anchor.X, Anchor.Y);
        AddLayer(layers, track, g.Dirty, Anchor, (int)from.X, (int)from.Y, scale, -3);
    }

    static void Line(Graphics g, Vector2 from, float angle, float length, Color color)
    {
        var state = g.Save();
        g.TranslateTransform(from.X, from.Y);
        g.RotateTransform(angle * 180f / MathF.PI);
        g.FillRectangle(color, 0f, -.5f, length, 1f);
        g.Restore(state);
    }

    public override void Dispose() { track?.Dispose(); base.Dispose(); }
}
