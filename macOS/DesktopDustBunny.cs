using System;
using System.Drawing;
using System.Linq;
using System.Numerics;

namespace DeskMadeline.Mac;

internal sealed class DesktopDustBunny(int id, DesktopItemPlacement placement) : DesktopItem(id, placement, 48)
{
    static readonly Vector2[] Quadrants =
    {
        Vector2.Normalize(new Vector2(-1, -1)), Vector2.Normalize(new Vector2(1, -1)),
        Vector2.Normalize(new Vector2(-1, 1)), Vector2.Normalize(new Vector2(1, 1)),
    };
    static readonly Color[] Edges = { Color.FromArgb(0xf2, 0x5a, 0x10), Color.Red, Color.FromArgb(0xf2, 0x10, 0x67) };
    readonly Bitmap center = Art("dustcreature/center00");
    readonly Bitmap[] bases = Frames("dustcreature/base", 3), overlays = Frames("dustcreature/overlay", 3);
    readonly Bitmap[] eyes = Frames("dustcreature/eyes", 3);
    readonly Bitmap deadEyes = Art("dustcreature/deadEyes");
    readonly Random random = new Random(827 + id);
    readonly byte[] alpha = new byte[48 * 48];
    readonly Vector2[] nodes = Quadrants.ToArray();
    readonly bool[] enabled = { true, true, true, true };
    Vector2 positionOffset, eyeDirection = new Vector2(0, -1), eyeTarget = new Vector2(0, -1);
    Vector2 lookLimit, shake;
    float time, rotation, shakeTimer, blinkWait = 2f;
    int blinkStage;
    bool eyesShutByDeath;

    public override bool Available => center != null && deadEyes != null &&
        bases.All(x => x != null) && overlays.All(x => x != null) && eyes.All(x => x != null);

    public override void Prepare(DesktopTerrain terrain, int scale)
    {
        if (Held) return;
        Vector2 open = Vector2.Zero;
        positionOffset = Vector2.Zero;
        int openCount = 0;
        for (int i = 0; i < nodes.Length; i++)
        {
            Vector2 side = Quadrants[i];
            int x = Math.Sign(side.X), y = Math.Sign(side.Y);
            enabled[i] = !HasSolid(terrain, Anchor.X + Math.Min(0, x * 8), Anchor.Y + Math.Min(0, y * 8), 8, 8);
            float spreadX = HasSolid(terrain, Anchor.X - 4 + x * 16, Anchor.Y - 4 + y * 4, 8, 8) ? 5f : 1f;
            float spreadY = HasSolid(terrain, Anchor.X - 4 + x * 4, Anchor.Y - 4 + y * 16, 8, 8) ? 5f : 1f;
            nodes[i] = side * new Vector2(spreadX, spreadY);
            if (!enabled[i]) continue;
            open += side;
            openCount++;
        }
        if (enabled[0] || enabled[2]) positionOffset.X--;
        if (enabled[1] || enabled[3]) positionOffset.X++;
        if (enabled[0] || enabled[1]) positionOffset.Y--;
        if (enabled[2] || enabled[3]) positionOffset.Y++;
        lookLimit = openCount < 4 && open != Vector2.Zero ? Vector2.Normalize(open) : Vector2.Zero;
    }

    static bool HasSolid(DesktopTerrain terrain, float x, float y, float w, float h)
    {
        foreach (Solid solid in terrain.BaseSolids)
            if (x < solid.R && x + w > solid.L && y < solid.B && y + h > solid.T) return true;
        return false;
    }

    public override bool Advance(DesktopItemFrame frame)
    {
        float dt = frame.Dt;
        time += dt;
        rotation += .5f * dt;
        // This palette places the eye-bearing Chapter 3 variant. Its eyes turn
        // toward Madeline and are limited by the exposed side of a wall.
        Vector2 delta = eyeTarget - eyeDirection;
        if (delta.LengthSquared() > 0f)
            eyeDirection += delta * Math.Min(1f, (lookLimit == Vector2.Zero ? 12f : 8f) * dt / delta.Length());
        Vector2 toward = new Vector2(frame.Player.Pos.X - Anchor.X, frame.Player.Pos.Y - Anchor.Y);
        if (toward != Vector2.Zero)
        {
            eyeTarget = Vector2.Normalize(toward);
            if (lookLimit != Vector2.Zero)
            {
                float angle = MathF.Atan2(lookLimit.Y, lookLimit.X);
                float target = MathF.Atan2(toward.Y, toward.X);
                angle += Math.Clamp(MathF.IEEERemainder(target - angle, MathF.Tau), -MathF.PI / 4f, MathF.PI / 4f);
                eyeTarget = new Vector2(MathF.Cos(angle), MathF.Sin(angle));
            }
        }
        if (shakeTimer > 0f)
        {
            shakeTimer -= dt;
            if (shakeTimer <= 0f) shake = Vector2.Zero;
            else if (frame.OnInterval(.05f)) shake = new Vector2(random.Next(-1, 2), random.Next(-1, 2));
        }
        if (eyesShutByDeath && !frame.Player.IsDead && !frame.Player.IsRespawning)
        {
            // The game reloads the room after death. The desktop keeps placed
            // entities, so restore the normal eyes when Madeline returns.
            eyesShutByDeath = false;
            blinkStage = 0;
            blinkWait = 2f + random.NextSingle() * 1.5f;
        }
        if (!eyesShutByDeath && OverInterval(ref blinkWait, dt))
        {
            blinkStage = (blinkStage + 1) % 3;
            blinkWait = blinkStage switch { 1 => .02f + random.NextSingle() * .05f, 2 => .25f, _ => 2f + random.NextSingle() * 1.5f };
        }
        return false;
    }

    public override void Touch(Player player, Action<string> play, Action<RumbleStrength, RumbleLength> rumble)
    {
        if (player.Invincible) return;
        player.GetHurtbox(out float l, out float t, out float r, out float b);
        float dx = Anchor.X - Math.Clamp(Anchor.X, l, r);
        float dy = Anchor.Y - Math.Clamp(Anchor.Y, t, b);
        // DustStaticSpinner's Circle(6) plus the wide 16×4 collider.
        if (dx * dx + dy * dy >= 36f && !player.HurtboxOverlaps(Anchor.X - 8, Anchor.Y - 3, Anchor.X + 8, Anchor.Y + 1)) return;
        player.DieFromDust(Anchor);
        shakeTimer = .6f;
        eyesShutByDeath = true;
    }

    protected override Rectangle Paint()
    {
        Vector2 p = new Vector2(Center, Center) + positionOffset + shake;
        Rectangle area;
        using (var g = new Graphics(Body))
        {
            g.Clear();
            for (int i = 0; i < nodes.Length; i++)
            {
                if (!enabled[i]) continue;
                Vector2 q = p + nodes[i];
                float angle = rotation + i * 1.78f + Id * .23f;
                DrawRotated(g, bases[(i + Id) % 3], q.X, q.Y, angle);
                DrawRotated(g, overlays[(i * 2 + Id) % 3], q.X, q.Y, -angle);
            }
            DrawRotated(g, center, p.X, p.Y, time * .6f);
            area = g.Dirty;
        }
        Rectangle edgeArea = PaintEdges(area);
        using (var g = new Graphics(Body))
        {
            Vector2 normal = new Vector2(-eyeDirection.Y, eyeDirection.X);
            if (normal != Vector2.Zero) normal = Vector2.Normalize(normal);
            Vector2 eyeCenter = p + eyeDirection * 5f;
            Bitmap image = eyesShutByDeath ? deadEyes : eyes[Id % eyes.Length];
            Vector2 left = eyeCenter + normal * 3f, right = eyeCenter - normal * 3f;
            if (eyesShutByDeath || blinkStage == 0) DrawCentered(g, image, left.X, left.Y, tint: Color.Red);
            if (eyesShutByDeath || blinkStage != 2) DrawCentered(g, image, right.X, right.Y, tint: Color.Red);
            return Rectangle.Union(edgeArea, g.Dirty.IsEmpty ? edgeArea : g.Dirty);
        }
    }

    Rectangle PaintEdges(Rectangle area)
    {
        // The level's Dust shader samples the shared scene mask and animated
        // noise. On the desktop, sample the spinner's own mask and use the
        // same Chapter 3 palette in the one-pixel fringe.
        Span<byte> pixels = Body.Pixels;
        int stride = Body.RowBytes, width = Body.Width;
        Rectangle fringe = Rectangle.Intersect(Rectangle.Inflate(area, 1, 1), new Rectangle(1, 1, width - 2, Body.Height - 2));
        Rectangle sample = Rectangle.Intersect(Rectangle.Inflate(area, 2, 2), new Rectangle(0, 0, width, Body.Height));
        for (int y = sample.Top; y < sample.Bottom; y++)
            for (int x = sample.Left; x < sample.Right; x++) alpha[y * width + x] = pixels[y * stride + x * 4 + 3];
        int flicker = (int)(time * 8f);
        for (int y = fringe.Top; y < fringe.Bottom; y++)
        for (int x = fringe.Left; x < fringe.Right; x++)
        {
            int offset = y * width + x;
            if (alpha[offset] != 0) continue;
            int strength = Math.Max(Math.Max(alpha[offset - 1], alpha[offset + 1]),
                Math.Max(alpha[offset - width], alpha[offset + width]));
            if (strength == 0) continue;
            int hash = unchecked(x * 198491317 ^ y * 6542989 ^ (Id + flicker) * 357239) & int.MaxValue;
            Color color = Edges[hash % Edges.Length];
            // Resort footage shows saturated red and pink along the fringe.
            // Let the brightest samples reach full alpha, with gaps between them.
            int noise = (hash >> 10) % 256;
            if (noise < 64) continue;
            int a = strength * Math.Min(255, (noise - 64) * 2) / 255;
            int index = y * stride + x * 4;
            pixels[index] = (byte)(color.B * a / 255);
            pixels[index + 1] = (byte)(color.G * a / 255);
            pixels[index + 2] = (byte)(color.R * a / 255);
            pixels[index + 3] = (byte)a;
        }
        return Rectangle.Union(area, fringe);
    }
}
