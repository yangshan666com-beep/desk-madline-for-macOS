using System;
using System.Drawing;
using System.Collections.Generic;
using System.Numerics;

namespace DeskMadeline.Mac;

internal sealed class DesktopDreamBlock(int id, DesktopItemPlacement placement, SpriteBloom bloom)
    : DesktopResizableBlock(id, placement, new Size(40, 16))
{
    readonly Bitmap particles = Art("dreamblock/particles");
    readonly List<Star> stars = new List<Star>();
    float animTimer, wobble;
    float from = Random.Shared.NextSingle() * MathF.Tau, to = Random.Shared.NextSingle() * MathF.Tau;
    float paintedAt = float.NaN;
    Size paintedSize;
    Rectangle paintedArea;
    readonly record struct Star(float X, float Y, int Layer, float Time, Color Color);
    static readonly Color[][] Colors =
    {
        new[] { Color.FromArgb(0xff, 0xef, 0x11), Color.FromArgb(0xff, 0x00, 0xd0), Color.FromArgb(0x08, 0xa3, 0x10) },
        new[] { Color.FromArgb(0x5f, 0xcd, 0xe4), Color.FromArgb(0x7f, 0xb2, 0x5e), Color.FromArgb(0xe0, 0x56, 0x4c) },
        new[] { Color.FromArgb(0x5b, 0x6e, 0xe1), Color.FromArgb(0xcc, 0x3b, 0x3b), Color.FromArgb(0x7d, 0xaa, 0x64) },
    };

    void EnsureStars(int count)
    {
        var random = Random.Shared;
        ReadOnlySpan<int> layers = [0, 1, 1, 2, 2, 2];
        while (stars.Count < count)
        {
            int layer = layers[random.Next(layers.Length)];
            stars.Add(new Star(random.NextSingle(), random.NextSingle(), layer, random.NextSingle(),
                Colors[layer][random.Next(3)]));
        }
    }

    public override bool Available => particles != null;
    protected override bool Dream => true;
    protected override int SurfaceSoundIndex => 11;
    protected override int Depth => -1;

    public override bool Advance(DesktopItemFrame frame)
    {
        animTimer += frame.Dt * 6f;
        wobble += frame.Dt * 2f;
        if (wobble > 1f)
        {
            wobble = 0f;
            from = to;
            to = Random.Shared.NextSingle() * MathF.Tau;
        }
        return false;
    }

    protected override Rectangle Paint()
    {
        Size size = BlockSize;
        if (paintedAt == animTimer && paintedSize == size) return paintedArea;
        // The original outline can extend three pixels, with bloom beyond it.
        EnsureBlockCanvas(padding: 16);
        // DreamBlock.Setup adds 0.7 particles per 8×8 tile.
        int count = (int)(Width / 8f * (Height / 8f) * .7f);
        EnsureStars(count);
        Rectangle area;
        using (var g = new Graphics(Body))
        {
            g.Clear();
            float left = Center - Width / 2f, top = CenterY - Height / 2f;
            g.FillRectangle(Color.Black, left, top, Width, Height);
            for (int i = 0; i < count; i++)
            {
                Star star = stars[i];
                int tile = star.Layer == 0 ? 3 - (int)((star.Time * 4f + animTimer) % 4f) :
                    star.Layer == 1 ? 1 + (int)((star.Time * 2f + animTimer) % 2f) : 2;
                int source = tile == 0 ? 14 : tile == 2 ? 0 : 7;
                // With a fixed desktop camera, the original particle positions
                // are already wrapped into the block's current dimensions.
                float x = star.X * Width, y = star.Y * Height;
                if (x >= 2f && y >= 2f && x < Width - 2f && y < Height - 2f)
                    g.DrawRegion(particles, new Rectangle(source, 0, 7, 7), left + x - 3.5f, top + y - 3.5f, tint: star.Color);
            }
            var upperLeft = new Vector2(left, top);
            var upperRight = new Vector2(left + Width, top);
            var lowerRight = new Vector2(left + Width, top + Height);
            var lowerLeft = new Vector2(left, top + Height);
            WobbleLine(upperLeft, upperRight, 0f);
            WobbleLine(upperRight, lowerRight, .7f);
            WobbleLine(lowerRight, lowerLeft, 1.5f);
            WobbleLine(lowerLeft, upperLeft, 2.5f);
            g.FillRectangle(Color.White, left, top, 2f, 2f);
            g.FillRectangle(Color.White, upperRight.X - 2f, top, 2f, 2f);
            g.FillRectangle(Color.White, left, lowerLeft.Y - 2f, 2f, 2f);
            g.FillRectangle(Color.White, upperRight.X - 2f, lowerRight.Y - 2f, 2f, 2f);
            area = g.Dirty;

            // DreamBlock.WobbleLine uses outward 16-pixel segments, two
            // backing strokes and a white edge, joined by 2×2 corner caps.
            void WobbleLine(Vector2 start, Vector2 end, float phase)
            {
                const int Segment = 16;
                int length = (int)Vector2.Distance(start, end);
                Vector2 direction = (end - start) / length;
                var outward = new Vector2(direction.Y, -direction.X);
                float previous = 0f;
                for (int i = 2; i < length - 2; i += Segment)
                {
                    float a = LineAmplitude(from + phase, i), b = LineAmplitude(to + phase, i);
                    float next = i + Segment >= length ? 0f : a + (b - a) * wobble;
                    int count = Math.Min(Segment, length - 2 - i);
                    float slope = (next - previous) / count;
                    int runStart = i, offset = Snap(previous + .5f * slope);
                    // Rasterize the original segment at pixel centres. Adjacent
                    // rotated quads can leave a single uncovered row in Skia.
                    for (int j = 1; j < count; j++)
                    {
                        int pixel = Snap(previous + (j + .5f) * slope);
                        if (pixel == offset) continue;
                        DrawRun(runStart, i + j, offset);
                        runStart = i + j;
                        offset = pixel;
                    }
                    DrawRun(runStart, i + count, offset);
                    previous = next;
                }

                void DrawRun(int begin, int endExclusive, int offset)
                {
                    Vector2 p = start + direction * begin + outward * offset;
                    Vector2 q = start + direction * endExclusive + outward * offset;
                    float x = Math.Min(p.X, q.X) - Math.Max(0f, outward.X);
                    float y = Math.Min(p.Y, q.Y) - Math.Max(0f, outward.Y);
                    float width = Math.Max(1f, Math.Abs(q.X - p.X));
                    float height = Math.Max(1f, Math.Abs(q.Y - p.Y));
                    g.FillRectangle(Color.Black, x - outward.X, y - outward.Y, width, height);
                    g.FillRectangle(Color.Black, x - 2f * outward.X, y - 2f * outward.Y, width, height);
                    g.FillRectangle(Color.White, x, y, width, height);
                }

                static int Snap(float amplitude) => (int)MathF.Floor(amplitude + .5f);
            }
        }
        paintedArea = bloom.Apply(Body, area, Center, CenterY, 0f, ambient: .5f);
        paintedSize = size;
        paintedAt = animTimer;
        return paintedArea;
    }

    static float LineAmplitude(float seed, float index) =>
        (float)(Math.Sin(seed + index / 16f + Math.Sin(seed * 2f + index / 32f) * MathF.Tau) + 1.0) * 1.5f;
}

internal sealed class DesktopCrumblePlatform(int id, DesktopItemPlacement placement)
    : DesktopSolidItem(id, placement, new Size(32, 8), 88)
{
    readonly Bitmap tiles = Art("crumbleBlock/default"), outline = Art("crumbleBlock/outline");
    readonly float[] tileDelays = { 0f, .05f, .1f, .15f };
    float timer, shakeX, shakeY, outlineAlpha, elapsed;
    bool fromTop;
    State state;
    enum State { Idle, Shake, Fallen, Return }

    public override bool Available => tiles != null && outline != null;
    public override bool Collidable => state != State.Fallen;
    protected override int SurfaceSoundIndex => 8;
    protected override int Depth => 1;

    public override bool Advance(DesktopItemFrame frame)
    {
        var player = frame.Player;
        bool onTop = player.Speed.Y >= 0f && Math.Abs(player.CollisionBottom - Collider.T) < .99f &&
            player.Pos.X + 4f > Collider.L && player.Pos.X - 4f < Collider.R;
        if (state == State.Idle && player.IsRiding(Collider))
        {
            fromTop = onTop;
            timer = onTop ? .6f : 1f;
            elapsed = 0f;
            state = State.Shake;
            frame.Play("event:/game/general/platform_disintegrate");
            frame.Rumble(RumbleStrength.Medium, RumbleLength.Medium);
        }
        if (state == State.Shake)
        {
            elapsed += frame.Dt;
            if (frame.OnInterval(.05f))
            {
                shakeX = Random.Shared.Next(-1, 2);
                shakeY = Random.Shared.Next(-1, 2);
            }
            if (OverInterval(ref timer, frame.Dt) || (fromTop && elapsed >= .2f && !onTop))
            {
                state = State.Fallen;
                timer = 2f;
                shakeX = shakeY = 0f;
                elapsed = 0f;
            }
        }
        else if (state == State.Fallen)
        {
            elapsed += frame.Dt;
            if (OverInterval(ref timer, frame.Dt) && !player.OverlapsHitbox(Collider) && !BlockedAt(player, Position))
            {
                state = State.Return;
                elapsed = 0f;
                frame.Play("event:/game/general/platform_return");
            }
        }
        else if (state == State.Return && (elapsed += frame.Dt) >= .35f)
            state = State.Idle;
        outlineAlpha = Approach(outlineAlpha, state == State.Fallen ? 1f : 0f, frame.Dt * 2f);
        return false;
    }

    protected override Rectangle Paint()
    {
        Rectangle area;
        using (var g = new Graphics(Body))
        {
            g.Clear();
            float x = Center - Width / 2f, y = Center - Height / 2f;
            if (outlineAlpha > 0f) g.DrawImage(outline, x, y, Width, Height, outlineAlpha);
            for (int i = 0; i < tileDelays.Length; i++)
            {
                float t = elapsed - tileDelays[i];
                if (state == State.Fallen && t > .2f || state == State.Return && t < 0f) continue;
                float dy = state == State.Fallen ? 24f * MathF.Pow(Math.Max(0f, t) / .2f, 3f) : 0f;
                g.DrawRegion(tiles, new Rectangle(i * 8, 0, 8, 8), x + i * 8 + shakeX, y + shakeY + dy,
                    state == State.Fallen ? Math.Clamp(1f - Math.Max(0f, t) / .2f, 0f, 1f) : 1f);
            }
            area = g.Dirty;
        }
        return area;
    }
}
