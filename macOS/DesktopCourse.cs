using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;

namespace DeskMadeline.Mac;

internal sealed record DesktopCourse(RectangleF Arena, PointF Start, int Direction,
    DesktopItemPlacement[] Placements, PointF[] Ledges)
{
    const float Dt = 1f / 60f;
    static readonly DesktopItemKind[] Platforms =
    {
        DesktopItemKind.DreamBlock, DesktopItemKind.CrumblePlatform, DesktopItemKind.Cloud, DesktopItemKind.PinkCloud,
    };
    static readonly DesktopItemKind[] Bonuses =
    {
        DesktopItemKind.Refill, DesktopItemKind.DoubleRefill, DesktopItemKind.Strawberry,
        DesktopItemKind.Bumper, DesktopItemKind.Feather, DesktopItemKind.BoosterGreen, DesktopItemKind.BoosterRed,
    };

    public static DesktopCourse Generate(DesktopTerrain desktop, int scale, PointF near, Random random,
        IReadOnlyList<DesktopItemPlacement> placed, Predicate<DesktopItemKind> available)
    {
        if (!available(DesktopItemKind.Heart) || !available(DesktopItemKind.DreamBlock)) return null;
        var platforms = Platforms.Where(kind => available(kind)).ToArray();
        var bonuses = Bonuses.Where(kind => available(kind)).ToArray();
        var occupied = placed.Select(item =>
        {
            PointF from = item.ToGame(scale);
            PointF to = new PointF(from.X + item.RouteX / scale, from.Y + item.RouteY / scale);
            float routeX = Math.Clamp(to.X, from.X - 128f, from.X + 128f), routeY = Math.Clamp(to.Y, from.Y - 128f, from.Y + 128f);
            return RectangleF.FromLTRB(Math.Min(from.X, routeX) - 22f, Math.Min(from.Y, routeY) - 23f,
                Math.Max(from.X, routeX) + 22f, Math.Max(from.Y, routeY) + 23f);
        }).ToArray();
        var arenas = FindArenas(desktop, near, occupied).ToArray();
        for (int attempt = 0; attempt < 18 && arenas.Length != 0; attempt++)
        {
            var area = arenas[attempt % arenas.Length];
            DesktopCourse course = LayOut(area, scale, random, platforms, bonuses, includeExtras: attempt < 14);
            if (course.CanFinish(scale)) return course;
        }
        return null;
    }

    static IEnumerable<RectangleF> FindArenas(DesktopTerrain desktop, PointF near, RectangleF[] occupied)
    {
        var candidates = new List<(RectangleF Area, float Score)>();
        foreach (RectangleF monitor in desktop.Monitors)
        {
            float height = Math.Min(104f, monitor.Height - 12f);
            if (height < 65f) continue;
            foreach (float width in new[] { Math.Min(355f, monitor.Width - 12f), 248f, 195f, 138f }.Distinct())
            {
                if (width < 123f || width > monitor.Width - 12f) continue;
                foreach (float u in new[] { .5f, .12f, .88f, 0f, 1f })
                foreach (float v in new[] { .43f, .12f, .8f, 0f, 1f })
                {
                    var area = new RectangleF(MathF.Round(monitor.Left + 6f + (monitor.Width - width - 12f) * u),
                        MathF.Round(monitor.Top + 6f + (monitor.Height - height - 12f) * v), width, height);
                    if (occupied.Any(area.IntersectsWith)) continue;
                    float penalty = 0f;
                    foreach (Solid solid in desktop.BaseSolids)
                    {
                        if (solid.OffScreen) continue;
                        var overlap = RectangleF.Intersect(area, RectangleF.FromLTRB(solid.L, solid.T, solid.R, solid.B));
                        penalty += overlap.Width * overlap.Height;
                    }
                    float distance = Math.Abs(area.Left + width / 2f - near.X) + Math.Abs(area.Top + height / 2f - near.Y);
                    candidates.Add((area, width - penalty * .02f - distance * .04f));
                }
            }
        }
        return candidates.OrderByDescending(candidate => candidate.Score).Select(candidate => candidate.Area).Distinct().Take(8);
    }

    static DesktopCourse LayOut(RectangleF area, int scale, Random random, DesktopItemKind[] platforms,
        DesktopItemKind[] bonuses, bool includeExtras)
    {
        int direction = random.Next(2) == 0 ? 1 : -1;
        int count = Math.Clamp((int)((area.Width - 76f) / 49f) + 1, 2, 6);
        var route = new PointF[count];
        var props = new List<DesktopItemPlacement>();
        float x = direction > 0 ? area.Left + 25f : area.Right - 25f;
        float y = area.Bottom - 19f - random.Next(0, 6);
        float spacing = (area.Width - 76f) / (count - 1);
        bool rolling = random.Next(3) == 0;
        for (int index = 0; index < count; index++)
        {
            route[index] = new PointF(x, y);
            DesktopItemKind kind = index == 0 || index == count - 1 ? DesktopItemKind.DreamBlock :
                platforms[random.Next(platforms.Length)];
            float centerY = kind == DesktopItemKind.DreamBlock ? y + 8f :
                kind == DesktopItemKind.CrumblePlatform ? y + 4f : y;
            Add(kind, x, centerY);
            if (index == count - 1) break;
            float nextX = x + direction * (spacing + random.Next(-2, 3));
            float rise = rolling ? random.Next(-6, 11) : random.Next(2, 12);
            float nextY = Math.Clamp(y - rise, area.Top + 38f, area.Bottom - 18f);
            if (includeExtras && bonuses.Length > 0)
                Add(bonuses[random.Next(bonuses.Length)], MathF.Round((x + nextX) / 2f),
                    Math.Max(area.Top + 12f, Math.Min(y, nextY) - random.Next(25, 40)));
            x = nextX;
            y = nextY;
        }
        Add(DesktopItemKind.Heart, route[^1].X + direction * 31f, route[^1].Y - 8f);
        return new DesktopCourse(area, route[0], direction, props.ToArray(), route);

        void Add(DesktopItemKind kind, float x, float y) =>
            props.Add(new DesktopItemPlacement(kind, x * scale, y * scale));
    }

    // Verify every proposed course against the same player and prop update
    // order as the app. This runs on fresh actors, without keyboard injection,
    // sound, or mutations to the live desktop.
    internal bool CanFinish(int scale)
    {
        var terrain = new DesktopTerrain();
        terrain.Monitors.Add(Arena);
        var player = new Player { MinX = Arena.Left, MaxX = Arena.Right };
        using var items = new DesktopItems(_ => { }, (_, _) => { });
        if (!items.ReplaceCourse(this)) return false;
        player.ResetTo(Start);
        player.Facing = Direction;
        float time = 0f;
        Action<Player> touch = items.CheckCollisions;

        for (int index = 1; index < Ledges.Length; index++)
        {
            PointF from = Ledges[index - 1], to = Ledges[index];
            bool jumped = false, landed = false;
            for (int tick = 0; tick < 110 && !landed; tick++)
            {
                float ahead = Direction * (player.Pos.X - from.X);
                float remaining = Direction * (to.X - player.Pos.X);
                if (!jumped && ahead >= 9f && player.Pos.Y >= from.Y - 1f && player.Speed.Y >= 0f)
                {
                    player.BufferJump();
                    jumped = true;
                }
                int move = remaining > 4f ? Direction : remaining < -8f ? -Direction : 0;
                Step(move, jump: true, dash: false);
                if (player.IsDead || player.Pos.Y > Arena.Bottom) return false;
                landed = Math.Abs(player.Pos.Y - to.Y) < .7f && Math.Abs(player.Pos.X - to.X) < 15f &&
                    player.Speed.Y >= 0f;
            }
            if (!landed) return false;
        }
        // The final landing is wide enough to line up a normal horizontal dash.
        for (int tick = 0; tick < 40 && !items.CourseComplete; tick++)
            Step(Direction, jump: false, dash: tick == 1 || tick == 20);
        return items.CourseComplete;

        void Step(int move, bool jump, bool dash)
        {
            bool frozen = player.IsHitStopped;
            if (!frozen) time += Dt;
            if (dash) player.BufferDash();
            var input = new PetInput { MoveX = move, AimX = move, JumpHeld = jump, JumpPressed = player.HasJumpBuffer, DashPressed = player.HasDashBuffer };
            items.Prepare(player, terrain, scale);
            if (!frozen) items.BeforePlayer(player, Dt, time, input);
            player.Update(Dt, input, time, touch);
            if (!frozen && items.AfterPlayer(player, Dt, time, input)) player.ApplyFreeze(DesktopRefill.PickupFreeze);
        }
    }
}

internal sealed partial class DesktopItems
{
    DesktopCourse course;
    DesktopHeart courseHeart;
    public DesktopCourse Course => course;
    public bool CourseComplete => courseHeart?.Completed == true;

    public bool ReplaceCourse(DesktopCourse replacement)
    {
        var staged = new List<DesktopItem>(replacement.Placements.Length);
        foreach (var placement in replacement.Placements)
        {
            DesktopItem item = Create(nextId++, placement);
            if (!item.Available)
            {
                item.Dispose();
                foreach (DesktopItem partial in staged) partial.Dispose();
                return false;
            }
            item.Generated = true;
            staged.Add(item);
        }
        ClearCourse();
        course = replacement;
        courseHeart = staged.OfType<DesktopHeart>().Single();
        items.AddRange(staged);
        return true;
    }

    public void ClearCourse()
    {
        for (int i = items.Count - 1; i >= 0; i--)
            if (items[i].Generated) Remove(items[i].Id, generated: true);
        ClearCourseState();
    }

    void ClearCourseState()
    {
        courseHeart = null;
        course = null;
        courseMechanisms.Reset();
    }

}
