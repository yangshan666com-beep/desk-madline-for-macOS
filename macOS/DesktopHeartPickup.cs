using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Numerics;

namespace DeskMadeline.Mac;

internal sealed class DesktopHeartPickup : IDisposable
{
    const float GatherTime = 2f, ShowTime = 1.8f, FadeTime = .5f;
    const int Resolution = 2, TileSize = 128;
    const int BackdropDepth = 3, PoemDepth = 6, FlashDepth = 7;
    static readonly Color PoemColor = Color.FromArgb(0x8c, 0xc7, 0xfa);
    readonly Bitmap orb = Sprites.Get("heartgem/orb", false);
    readonly Bitmap snow = Sprites.Get("heartpoem/snow", false);
    readonly Bitmap[] heart = Enumerable.Range(0, 11)
        .Select(i => Sprites.Get(FormattableString.Invariant($"heartpoem/{i:00}"), false)).ToArray();
    readonly AbsorbOrb[] orbs = new AbsorbOrb[10];
    readonly Streak[] streaks = new Streak[80];
    readonly Bitmap dimPixel = new Bitmap(1, 1), flashPixel = new Bitmap(1, 1);
    Bitmap foreground;
    Rectangle[] foregroundTiles = Array.Empty<Rectangle>();
    int tileColumns;
    readonly Action<Rectangle> recordForeground;
    RectangleF view;
    Vector2 origin;
    float elapsed, poemTime, dim, flash;
    Phase phase;
    enum Phase { Idle, Prime, Freeze, Gather, Poem, Fade }

    public bool Active => phase != Phase.Idle;
    public bool WhiteHeart => phase is Phase.Prime or Phase.Freeze;
    public bool PlayerInFront => phase >= Phase.Gather;
    public bool ShowingHeart => phase is Phase.Poem or Phase.Fade;
    public bool Available => orb != null && snow != null && heart.All(frame => frame != null);
    internal int OrbCount => phase == Phase.Gather ? orbs.Count(p => p.Visible) : 0;

    public DesktopHeartPickup() => recordForeground = RecordForeground;

    public void Start(PointF position)
    {
        origin = new Vector2(position.X, position.Y);
        phase = Phase.Prime;
        elapsed = poemTime = flash = dim = 0f;
    }

    public void Cancel() => phase = Phase.Idle;

    public float Advance(float dt, Player player, RectangleF monitor, Action show, Action fade)
    {
        if (player.IsDead)
        {
            if (Active) fade();
            phase = Phase.Idle;
            return 1f;
        }
        if (phase >= Phase.Gather) SetView(monitor);
        switch (phase)
        {
            case Phase.Prime:
                // CollectRoutine adds the white sprite and yields before the freeze.
                player.ApplyFreeze(.2f);
                phase = Phase.Freeze;
                return 1f;
            case Phase.Freeze:
                if (player.IsHitStopped) return 1f;
                BeginGather(monitor);
                show();
                return .5f;
            case Phase.Gather:
                elapsed = Math.Min(GatherTime, elapsed + dt);
                float timeRate = .5f - .25f * elapsed;
                dim = Approach(dim, 1f, dt * 3f);
                flash = Approach(flash, 0f, dt * timeRate * 3f);
                PointF target = player.Center;
                for (int i = 0; i < orbs.Length; i++)
                    orbs[i].Advance(dt, new Vector2(target.X, target.Y));
                if (elapsed < GatherTime) return timeRate;
                phase = Phase.Poem;
                elapsed = 0f;
                for (int i = 0; i < streaks.Length; i++)
                    streaks[i] = Streak.Create(Random.Shared.NextSingle());
                return 0f;
            case Phase.Poem:
                AdvanceStreaks(dt);
                if ((elapsed += dt) >= ShowTime)
                {
                    // A desktop has no chapter confirmation screen. Hold the
                    // original heart reveal briefly, then release automatically.
                    phase = Phase.Fade;
                    elapsed = 0f;
                    fade();
                }
                return 0f;
            case Phase.Fade:
                AdvanceStreaks(dt);
                dim = Approach(dim, 0f, dt * 3f);
                if ((elapsed += dt) < FadeTime) return 0f;
                phase = Phase.Idle;
                return 1f;
            default:
                return 1f;
        }
    }

    void BeginGather(RectangleF monitor)
    {
        SetView(monitor);
        for (int i = 0; i < orbs.Length; i++) orbs[i] = new AbsorbOrb(origin);
        phase = Phase.Gather;
        elapsed = 0f;
        flash = 1f;
    }

    void SetView(RectangleF monitor)
    {
        // FormationBackdrop and Level.Flash fill the viewport. On the desktop
        // that is the entire display where the heart was collected.
        view = monitor.IsEmpty ? new RectangleF(origin.X - 160f, origin.Y - 90f, 320f, 180f) : monitor;
        int width = (int)Math.Ceiling(view.Width * Resolution), height = (int)Math.Ceiling(view.Height * Resolution);
        if (foreground != null && foreground.Width == width && foreground.Height == height) return;
        foreground?.Dispose();
        foreground = new Bitmap(width, height);
        tileColumns = (width + TileSize - 1) / TileSize;
        foregroundTiles = new Rectangle[tileColumns * ((height + TileSize - 1) / TileSize)];
    }

    void RecordForeground(Rectangle bounds)
    {
        // Transfer only the occupied portion of each tile to AppKit. A streak
        // near the edge no longer expands one bitmap to the whole display.
        for (int y = bounds.Top / TileSize; y <= (bounds.Bottom - 1) / TileSize; y++)
            for (int x = bounds.Left / TileSize; x <= (bounds.Right - 1) / TileSize; x++)
            {
                var tile = new Rectangle(x * TileSize, y * TileSize, TileSize, TileSize);
                Rectangle touched = Rectangle.Intersect(tile, bounds);
                ref Rectangle dirty = ref foregroundTiles[y * tileColumns + x];
                dirty = dirty.IsEmpty ? touched : Rectangle.Union(dirty, touched);
            }
    }

    void AdvanceStreaks(float dt)
    {
        poemTime += dt;
        for (int i = 0; i < streaks.Length; i++)
        {
            ref Streak streak = ref streaks[i];
            streak.Percent += dt / streak.Duration;
            if (streak.Percent > 1f) streak = Streak.Create(0f);
        }
    }

    public void Render(List<Native.ItemLayer> layers, int scale)
    {
        if (phase < Phase.Gather) return;
        var topLeft = new PointF(view.X * Resolution, view.Y * Resolution);
        AddFill(dimPixel, Color.Black, .85f * dim, BackdropDepth);

        using var front = new Graphics(foreground, recordForeground);
        for (int i = 0; i < foregroundTiles.Length; i++)
        {
            front.Clear(foregroundTiles[i]);
            foregroundTiles[i] = Rectangle.Empty;
        }
        if (phase == Phase.Gather)
        {
            foreach (AbsorbOrb particle in orbs)
            {
                if (!particle.Visible) continue;
                Draw(front, orb, (particle.Position.X - view.X) * Resolution, (particle.Position.Y - view.Y) * Resolution,
                    particle.Scale.X * Resolution, particle.Scale.Y * Resolution, particle.Angle, Color.White, particle.Alpha);
            }
        }
        else
        {
            float opacity = phase == Phase.Fade ? MathF.Pow(Math.Max(0f, 1f - elapsed / FadeTime), 3f) :
                1f - MathF.Pow(1f - Math.Clamp(elapsed, 0f, 1f), 3f);
            DrawPoem(front, opacity);
        }
        foreach (Rectangle tile in foregroundTiles)
            DesktopItem.AddLayer(layers, foreground, tile, topLeft, 0, 0, scale / (float)Resolution, PoemDepth);
        // Level.Flash composites over the world, including Madeline and the orbs.
        if (phase == Phase.Gather) AddFill(flashPixel, Color.White, flash, FlashDepth);

        void AddFill(Bitmap pixel, Color color, float alpha, int depth)
        {
            if (alpha <= 0f) return;
            pixel.SetPixel(0, 0, Color.FromArgb((int)MathF.Round(alpha * 255f), color));
            var bounds = new Native.DesktopRect(view.X * scale, view.Y * scale, view.Width * scale, view.Height * scale);
            layers.Add(new Native.ItemLayer(new Native.ImageLayer(pixel.Address, pixel.RowBytes, 1, 1, bounds), depth, 0));
        }
    }

    void DrawPoem(Graphics g, float alpha)
    {
        const float guiScale = Resolution / 6f;
        float x = foreground.Width / 2f, y = foreground.Height / 2f;
        foreach (Streak streak in streaks)
        {
            float progress = 1f - MathF.Cos(streak.Percent * MathF.PI / 2f);
            float distance = (1f - progress) * 1920f * guiScale;
            Draw(g, snow, x + streak.Direction.X * distance, y + streak.Direction.Y * distance,
                (1f + 2f * progress) * guiScale, .25f * (1f - .75f * progress) * guiScale,
                MathF.Atan2(-streak.Direction.Y, -streak.Direction.X), PoemColor, alpha * (1f - progress));
        }
        int frame = Math.Max(0, (int)(poemTime / .08f) % 20 - 9);
        Draw(g, heart[frame], x, y, guiScale, guiScale, 0f, Color.White, .6f * alpha);
    }

    static void Draw(Graphics g, Bitmap texture, float x, float y, float sx, float sy, float angle, Color tint, float alpha)
    {
        float w = texture.Width * sx, h = texture.Height * sy;
        int state = g.Save();
        g.TranslateTransform(x, y);
        if (angle != 0f) g.RotateTransform(angle * (180f / MathF.PI));
        g.DrawImage(texture, -w / 2f, -h / 2f, w, h, alpha, tint);
        g.Restore(state);
    }

    static float Approach(float value, float target, float step) =>
        value < target ? Math.Min(target, value + step) : Math.Max(target, value - step);

    static Vector2 Unit(float angle) => new Vector2(MathF.Cos(angle), MathF.Sin(angle));

    public void Dispose()
    {
        dimPixel.Dispose();
        flashPixel.Dispose();
        foreground?.Dispose();
    }

    struct Streak
    {
        public Vector2 Direction;
        public float Percent, Duration;
        public static Streak Create(float percent) => new Streak
        {
            Direction = Unit(Random.Shared.NextSingle() * MathF.Tau),
            Percent = percent, Duration = .5f + Random.Shared.NextSingle() * .5f,
        };
    }

    struct AbsorbOrb
    {
        readonly Vector2 direction;
        Vector2 start, control;
        float consumeDelay, speed, percent, duration;
        public Vector2 Position, Scale;
        public float Angle, Alpha;
        public bool Visible => percent < 1f;

        public AbsorbOrb(Vector2 origin)
        {
            Position = origin;
            consumeDelay = .7f + Random.Shared.NextSingle() * .3f;
            speed = 80f + Random.Shared.NextSingle() * 40f;
            Angle = Random.Shared.NextSingle() * MathF.Tau;
            direction = Unit(Angle);
            Alpha = 1f;
            Stretch(speed);
        }

        public void Advance(float dt, Vector2 target)
        {
            if (!Visible) return;
            if (consumeDelay > 0f)
            {
                Position += direction * speed * dt;
                speed = Approach(speed, 0f, dt * 120f);
                Stretch(speed);
                consumeDelay -= dt;
                if (consumeDelay <= 0f)
                {
                    start = Position;
                    Vector2 delta = target - start;
                    var cross = new Vector2(-delta.Y, delta.X) * (.05f + Random.Shared.NextSingle() * .45f);
                    if (Math.Abs(delta.X) > Math.Abs(delta.Y) && Math.Sign(cross.X) != Math.Sign(delta.X))
                        cross = -cross;
                    control = (start + target) / 2f + cross;
                    duration = .3f + Random.Shared.NextSingle() * .25f;
                }
                return;
            }
            percent = Math.Min(1f, percent + dt / duration);
            float progress = percent * percent * percent;
            Position = Curve(target, progress);
            float ahead = percent + .01f;
            Vector2 tangent = Curve(target, ahead * ahead * ahead) - Position;
            Angle = MathF.Atan2(tangent.Y, tangent.X);
            float length = 0f;
            Vector2 last = start;
            for (int i = 1; i <= 10; i++)
            {
                Vector2 next = Curve(target, i / 10f);
                length += Vector2.Distance(last, next);
                last = next;
            }
            Stretch(2f * Math.Min(progress, 1f - progress) * length);
            Alpha = 1f - progress;
        }

        Vector2 Curve(Vector2 target, float t)
        {
            float u = 1f - t;
            return start * (u * u) + control * (2f * u * t) + target * (t * t);
        }

        void Stretch(float amount) => Scale = new Vector2(Math.Min(2f, .5f + amount * .02f),
            Math.Max(.05f, .5f - amount * .004f));
    }
}
