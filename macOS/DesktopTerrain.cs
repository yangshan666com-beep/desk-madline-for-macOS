using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;

namespace DeskMadeline.Mac;

internal enum WindowMaterial { Solid, Dream, Water }

/// <summary>Turns front-to-back WindowServer geometry into the same hollow Solids used by the Windows pet.</summary>
internal sealed class DesktopTerrain
{
    internal List<Solid> BaseSolids { get; private set; } = new List<Solid>();
    public static readonly IntPtr Floor = new IntPtr(-991);
    // WindowServer can report a snap, maximize or Space transition as a new
    // rectangle for the same window. Only a tracked pointer drag can carry
    // a rider through a large translation between two samples.
    const float MaxUntrackedStep = 32f, DragTolerance = 12f;
    internal readonly record struct PointerSample(PointF Position, bool PrimaryDown);
    readonly record struct WindowMotion(PointF Shift, PointF Lift);
    PointerSample previousPointer;
    Native.WindowInfo[] windows = new Native.WindowInfo[64];
    Native.WindowInfo[] previousWindows = new Native.WindowInfo[64];
    Native.DisplayInfo[] displays = new Native.DisplayInfo[8];
    Native.DisplayInfo[] previousDisplays = new Native.DisplayInfo[8];
    int windowCount, displayCount, previousFrontmost, previousScale;
    WindowMaterial previousMaterial;
    bool moving;
    Dictionary<IntPtr, RectangleF> last = new Dictionary<IntPtr, RectangleF>();
    Dictionary<IntPtr, RectangleF> next = new Dictionary<IntPtr, RectangleF>();
    readonly Dictionary<IntPtr, WindowMotion> translations = new Dictionary<IntPtr, WindowMotion>();
    readonly List<RectangleF> occluders = new List<RectangleF>();
    readonly List<RectangleF> hidersOnly = new List<RectangleF>();
    readonly List<RectangleF> workAreas = new List<RectangleF>();
    readonly List<RectangleF> pieces = new List<RectangleF>();
    readonly List<RectangleF> scratch = new List<RectangleF>();

    public readonly List<RectangleF> Monitors = new List<RectangleF>();
    public readonly List<KeyValuePair<IntPtr, RectangleF>> Windows = new List<KeyValuePair<IntPtr, RectangleF>>();
    public uint FullscreenDisplay { get; private set; }
    public RectangleF CourseArena { get; set; }
    RectangleF previousCourseArena;

    // Monocle Actor and SlipCheck use integer game pixels. Round halves up, including left
    // of the primary display, so a one-pixel wall cannot collapse at even-numbered edges.
    internal static float ToGame(float points, int scale) => (float)Math.Floor(points / (double)scale + 0.5);

    public unsafe void Poll(Player player, int scale, WindowMaterial material, float dt, Action prepareItems = null)
    {
        int displaySize, count, frontmost;
        fixed (Native.DisplayInfo* ptr = displays) displaySize = Native.Displays(ptr, displays.Length);
        if (displaySize > displays.Length)
        {
            Array.Resize(ref displays, displaySize);
            fixed (Native.DisplayInfo* ptr = displays) displaySize = Native.Displays(ptr, displays.Length);
        }

        fixed (Native.WindowInfo* ptr = windows) count = Native.Windows(ptr, windows.Length, out frontmost);
        if (count > windows.Length)
        {
            Array.Resize(ref windows, count);
            fixed (Native.WindowInfo* ptr = windows) count = Native.Windows(ptr, windows.Length, out frontmost);
        }
        PointerSample pointer = default;
#if DESK_MACOS
        Native.Cursor(out float x, out float y);
        pointer = new PointerSample(new PointF(x, y), Native.PrimaryButtonDown() != 0);
#endif
        Apply(player, displays.AsSpan(0, Math.Min(displaySize, displays.Length)),
            windows.AsSpan(0, Math.Min(count, windows.Length)), frontmost, scale, material, dt, pointer, prepareItems);
    }

    internal void Apply(Player player, ReadOnlySpan<Native.DisplayInfo> screens,
        ReadOnlySpan<Native.WindowInfo> zOrder, int frontmost, int scale, WindowMaterial material, float dt,
        PointerSample pointer = default, Action prepareItems = null)
    {
        if (screens.IsEmpty) return;
        PointerSample previous = previousPointer;
        previousPointer = pointer;
        bool screenChanged = !screens.SequenceEqual(previousDisplays.AsSpan(0, displayCount));
        bool courseChanged = CourseArena != previousCourseArena;
        bool sameForeground = frontmost == previousFrontmost;
        if (!moving && !screenChanged && !courseChanged && scale == previousScale && material == previousMaterial &&
            sameForeground && zOrder.SequenceEqual(previousWindows.AsSpan(0, windowCount))) return;

        bool canMove = !player.IsDead && !player.IsPreDeath && !player.IsRespawning && !player.BeingDragged;
        bool wasEmbedded = canMove && player.CollidesHere;
        IntPtr rider = IntPtr.Zero;
        bool resetLift = courseChanged || scale != previousScale || material != previousMaterial ||
            screenChanged && DisplayLayoutChanged(screens);
        if (resetLift)
        {
            last.Clear();
            player.EndRide();
            player.ResetLiftSpeed();
        }
        else if (canMove && (player.Speed.Y >= 0f || player.State is Player.StClimb or Player.StDreamDash))
        {
            // Use the actual contact just before the snapshot is replaced.
            // GroundId was sampled before Player's movement in the last tick.
            foreach (Solid solid in BaseSolids)
                if (!solid.OffScreen && player.HasPlatformContact(solid, player.Pos))
                {
                    rider = solid.Id;
                    break;
                }
        }

        if (previousWindows.Length < zOrder.Length) Array.Resize(ref previousWindows, zOrder.Length);
        if (previousDisplays.Length < screens.Length) Array.Resize(ref previousDisplays, screens.Length);
        zOrder.CopyTo(previousWindows);
        screens.CopyTo(previousDisplays);
        windowCount = zOrder.Length;
        displayCount = screens.Length;
        previousFrontmost = frontmost;
        previousScale = scale;
        previousMaterial = material;
        previousCourseArena = CourseArena;

        workAreas.Clear();
        Monitors.Clear();
        foreach (Native.DisplayInfo screen in screens)
        {
            var area = screen.WorkArea.Bounds;
            if (area.Width <= 0 || area.Height <= 0) area = screen.Frame.Bounds;
            workAreas.Add(area);
            Monitors.Add(RectangleF.FromLTRB(ToGame(area.Left, scale), ToGame(area.Top, scale),
                ToGame(area.Right, scale), ToGame(area.Bottom, scale)));
        }

        var solids = new List<Solid>(zOrder.Length * 4 + workAreas.Count * 4);
        var waters = new List<Solid>();
        Windows.Clear();
        occluders.Clear();
        hidersOnly.Clear();
        next.Clear();
        translations.Clear();
        moving = false;
        FullscreenDisplay = 0;
        foreach (Native.WindowInfo window in zOrder)
        {
            var bounds = window.Frame.Bounds;
            bool visible = false;
            foreach (Native.DisplayInfo screen in screens)
                if (bounds.IntersectsWith(screen.Frame.Bounds)) { visible = true; break; }
            if (!visible) continue;

            uint fullscreen = FullscreenAt(bounds, screens);
            if (fullscreen != 0)
            {
                if (window.Owner == frontmost && FullscreenDisplay == 0)
                    FullscreenDisplay = fullscreen;
                hidersOnly.Add(bounds);
                occluders.Add(bounds);
                continue;
            }

            var id = unchecked((nint)(nuint)window.Id);
            next[id] = bounds;
            Windows.Add(new KeyValuePair<IntPtr, RectangleF>(id, new RectangleF(
                bounds.X / scale, bounds.Y / scale, bounds.Width / scale, bounds.Height / scale)));

            PointF lift = PointF.Empty;
            if (last.TryGetValue(id, out var old) && old.Location != bounds.Location && old.Size == bounds.Size)
            {
                PointF delta = Delta(old, bounds, scale);
                if (TracksPointer(old, bounds, previous, pointer) ||
                    sameForeground && Math.Abs(delta.X) <= MaxUntrackedStep && Math.Abs(delta.Y) <= MaxUntrackedStep)
                {
                    lift = new PointF(delta.X / dt, delta.Y / dt);
                    translations.Add(id, new WindowMotion(GridDelta(old, bounds, scale), lift));
                    moving = true;
                }
            }
            if (material == WindowMaterial.Solid)
            {
                // Eight backing pixels at 2× Retina. Keep at least one game pixel at every scale.
                float b = Math.Max(4, scale);
                AddVisible(RectangleF.FromLTRB(bounds.Left, bounds.Top, bounds.Right, bounds.Top + b), occluders);
                AddVisible(RectangleF.FromLTRB(bounds.Left, bounds.Bottom - b, bounds.Right, bounds.Bottom), occluders);
                AddVisible(RectangleF.FromLTRB(bounds.Left, bounds.Top + b, bounds.Left + b, bounds.Bottom - b), occluders);
                AddVisible(RectangleF.FromLTRB(bounds.Right - b, bounds.Top + b, bounds.Right, bounds.Bottom - b), occluders);
            }
            else AddVisible(bounds, hidersOnly);
            occluders.Add(bounds);

            void AddVisible(RectangleF rect, List<RectangleF> covered)
            {
                foreach (var part in Subtract(rect, covered))
                    if (TrySolid(part, id, scale, out Solid solid))
                    {
                        solid.LiftX = lift.X;
                        solid.LiftY = lift.Y;
                        solid.Dream = material == WindowMaterial.Dream;
                        AddOutsideCourse(material == WindowMaterial.Water ? waters : solids, solid);
                    }
            }
        }

        // Fill only the exposed perimeter of the work-area union. Shared display seams stay open.
        float depth = 400 * scale;
        foreach (RectangleF area in workAreas)
        {
            AddEdge(RectangleF.FromLTRB(area.Left, area.Top - depth, area.Right, area.Top));
            AddEdge(RectangleF.FromLTRB(area.Left, area.Bottom, area.Right, area.Bottom + depth));
            AddEdge(RectangleF.FromLTRB(area.Left - depth, area.Top, area.Left, area.Bottom));
            AddEdge(RectangleF.FromLTRB(area.Right, area.Top, area.Right + depth, area.Bottom));
        }

        void AddEdge(RectangleF edge)
        {
            foreach (RectangleF part in Subtract(edge, workAreas))
                if (TrySolid(part, Floor, scale, out Solid solid))
                {
                    solid.OffScreen = true;
                    solids.Add(solid);
                }
        }

        player.MinX = Monitors.Min(r => r.Left);
        player.MaxX = Monitors.Max(r => r.Right);
        BaseSolids = player.Solids = solids;
        player.Waters = waters;
        // Carry against the same walls and placed platforms the next gameplay
        // tick will see, even when a window moves onto an existing prop.
        prepareItems?.Invoke();

        if (canMove && material != WindowMaterial.Water)
        {
            if (rider != IntPtr.Zero && !FollowContact(player, rider))
            {
                player.EndRide();
                player.ResetLiftSpeed();
            }

            if (player.State != Player.StDreamDash)
            {
                foreach (Solid piece in solids)
                {
                    if (!translations.TryGetValue(piece.Id, out WindowMotion motion) ||
                        motion.Shift.IsEmpty || !player.OverlapsHitbox(piece)) continue;
                    if (!player.SweptInto(piece, motion.Shift.X, motion.Shift.Y)) break;
                }
                // New, re-ordered and snapped windows have no sweep path.
                // Only separate an overlap that this snapshot introduced.
                if (!player.IsDead && !wasEmbedded && player.CollidesHere)
                    player.DisplaceOutOfSolids();
            }
        }

        (last, next) = (next, last);
    }

    bool DisplayLayoutChanged(ReadOnlySpan<Native.DisplayInfo> screens)
    {
        if (screens.Length != displayCount) return true;
        for (int i = 0; i < screens.Length; i++)
            if (screens[i].Id != previousDisplays[i].Id || screens[i].Frame != previousDisplays[i].Frame)
                return true;
        return false;
    }

    bool FollowContact(Player player, IntPtr rider)
    {
        bool OnSameSurface(PointF position) =>
            BaseSolids.Any(solid => solid.Id == rider && player.HasPlatformContact(solid, position));

        if (!translations.TryGetValue(rider, out WindowMotion motion))
            return last.TryGetValue(rider, out RectangleF was) && next.TryGetValue(rider, out RectangleF now) &&
                was == now && OnSameSurface(player.Pos);

        PointF before = player.Pos;
        PointF target = before + new SizeF(motion.Shift);
        if (!OnSameSurface(target))
        {
            // Opposite borders can round at different WindowServer points.
            // Recheck one pixel along the contact normal when that happens.
            PointF normal = player.State == Player.StClimb ? new PointF(1f, 0f) : new PointF(0f, 1f);
            PointF near = target - new SizeF(normal), far = target + new SizeF(normal);
            if (OnSameSurface(near)) target = near;
            else if (OnSameSurface(far)) target = far;
            else return false;
        }
        player.FollowDesktopPlatform((int)(target.X - before.X), (int)(target.Y - before.Y), rider);
        if (!OnSameSurface(player.Pos)) return false;
        player.LiftSpeed = motion.Lift;
        return true;
    }

    void AddOutsideCourse(List<Solid> destination, Solid solid)
    {
        RectangleF source = RectangleF.FromLTRB(solid.L, solid.T, solid.R, solid.B);
        RectangleF cut = RectangleF.Intersect(CourseArena, source);
        if (cut.IsEmpty)
        {
            destination.Add(solid);
            return;
        }
        // The course occupies a small clear part of the desktop. Remove only
        // the window collision inside its bounds while the route is active.
        Add(solid.L, solid.T, solid.R, cut.Top);
        Add(solid.L, cut.Bottom, solid.R, solid.B);
        Add(solid.L, cut.Top, cut.Left, cut.Bottom);
        Add(cut.Right, cut.Top, solid.R, cut.Bottom);

        void Add(float left, float top, float right, float bottom)
        {
            if (left >= right || top >= bottom) return;
            Solid piece = solid;
            (piece.L, piece.T, piece.R, piece.B) = (left, top, right, bottom);
            destination.Add(piece);
        }
    }

    static uint FullscreenAt(RectangleF rect, ReadOnlySpan<Native.DisplayInfo> screens)
    {
        foreach (Native.DisplayInfo screen in screens)
        {
            RectangleF bounds = screen.Frame.Bounds;
            if (Math.Abs(rect.Left - bounds.Left) <= 2f && Math.Abs(rect.Top - bounds.Top) <= 2f &&
                Math.Abs(rect.Right - bounds.Right) <= 2f && Math.Abs(rect.Bottom - bounds.Bottom) <= 2f)
                return screen.Id;
        }
        return 0;
    }

    static PointF Delta(RectangleF was, RectangleF now, int scale) =>
        new PointF((now.X - was.X) / scale, (now.Y - was.Y) / scale);

    static PointF GridDelta(RectangleF was, RectangleF now, int scale) =>
        new PointF(ToGame(now.X, scale) - ToGame(was.X, scale), ToGame(now.Y, scale) - ToGame(was.Y, scale));

    static bool TracksPointer(RectangleF was, RectangleF now, PointerSample previous, PointerSample current) =>
        current.PrimaryDown && was.Contains(previous.Position) && now.Contains(current.Position) &&
        Math.Abs((current.Position.X - now.X) - (previous.Position.X - was.X)) <= DragTolerance &&
        Math.Abs((current.Position.Y - now.Y) - (previous.Position.Y - was.Y)) <= DragTolerance;

    static bool TrySolid(RectangleF rect, IntPtr id, int scale, out Solid solid)
    {
        solid = new Solid { Id = id, L = ToGame(rect.Left, scale), T = ToGame(rect.Top, scale),
            R = ToGame(rect.Right, scale), B = ToGame(rect.Bottom, scale) };
        return solid.L < solid.R && solid.T < solid.B;
    }

    List<RectangleF> Subtract(RectangleF rect, List<RectangleF> covers)
    {
        pieces.Clear();
        pieces.Add(rect);
        var rest = pieces;
        var other = scratch;
        foreach (RectangleF cover in covers)
        {
            other.Clear();
            foreach (RectangleF part in rest)
            {
                RectangleF hit = RectangleF.Intersect(part, cover);
                if (hit.Width <= 0 || hit.Height <= 0) { other.Add(part); continue; }
                if (part.Top < hit.Top) other.Add(RectangleF.FromLTRB(part.Left, part.Top, part.Right, hit.Top));
                if (part.Bottom > hit.Bottom) other.Add(RectangleF.FromLTRB(part.Left, hit.Bottom, part.Right, part.Bottom));
                if (part.Left < hit.Left) other.Add(RectangleF.FromLTRB(part.Left, hit.Top, hit.Left, hit.Bottom));
                if (part.Right > hit.Right) other.Add(RectangleF.FromLTRB(hit.Right, hit.Top, part.Right, hit.Bottom));
            }
            (rest, other) = (other, rest);
            if (rest.Count == 0) break;
        }
        return rest;
    }

    public RectangleF NearestMonitor(PointF pos)
    {
        int index = NearestMonitorIndex(pos);
        return index < 0 ? RectangleF.Empty : Monitors[index];
    }

    internal RectangleF NearestDisplayFrame(PointF pos)
    {
        int index = NearestMonitorIndex(pos);
        if (index < 0) return RectangleF.Empty;
        RectangleF area = previousDisplays[index].Frame.Bounds;
        return new RectangleF(area.X / previousScale, area.Y / previousScale,
            area.Width / previousScale, area.Height / previousScale);
    }

    int NearestMonitorIndex(PointF pos)
    {
        int nearest = -1;
        float distance = float.PositiveInfinity;
        for (int i = 0; i < Monitors.Count; i++)
        {
            RectangleF screen = Monitors[i];
            float dx = pos.X - Math.Clamp(pos.X, screen.Left, screen.Right);
            float dy = pos.Y - Math.Clamp(pos.Y, screen.Top, screen.Bottom);
            float square = dx * dx + dy * dy;
            if (square < distance) { nearest = i; distance = square; }
        }
        return nearest;
    }

    internal static bool TryStandIn(Player player, RectangleF area, PointF requested, out PointF position)
    {
        position = PointF.Empty;
        float left = area.Left + Player.HitHalfWidth, right = area.Right - Player.HitHalfWidth;
        float top = area.Top + Player.StandingHitHeight, bottom = area.Bottom;
        if (left > right || top > bottom) return false;
        PointF target = new PointF(Math.Clamp(requested.X, left, right), Math.Clamp(requested.Y, top, bottom));
        if (player.CanStandAt(target))
        {
            position = target;
            return true;
        }

        // In position space each solid excludes one rectangle for the standing
        // hitbox. The closest legal position can lie on one of its edges.
        var xs = new HashSet<float> { target.X, left, right };
        var ys = new HashSet<float> { target.Y, top, bottom };
        foreach (Solid solid in player.Solids)
        {
            if (solid.L >= area.Right || solid.R <= area.Left || solid.T >= area.Bottom || solid.B <= area.Top)
                continue;
            xs.Add(Math.Clamp(solid.L - Player.HitHalfWidth, left, right));
            xs.Add(Math.Clamp(solid.R + Player.HitHalfWidth, left, right));
            ys.Add(Math.Clamp(solid.T, top, bottom));
            ys.Add(Math.Clamp(solid.B + Player.StandingHitHeight, top, bottom));
        }

        float best = float.PositiveInfinity;
        float[] orderedY = ys.OrderBy(y => Math.Abs(y - target.Y)).ThenBy(y => y).ToArray();
        foreach (float x in xs.OrderBy(x => Math.Abs(x - target.X)))
        {
            float dx = x - target.X;
            if (dx * dx >= best) break;
            foreach (float y in orderedY)
            {
                float dy = y - target.Y;
                float distance = dx * dx + dy * dy;
                if (distance >= best) break;
                if (!player.CanStandAt(new PointF(x, y))) continue;
                position = new PointF(x, y);
                best = distance;
                break;
            }
        }
        return float.IsFinite(best);
    }
}
