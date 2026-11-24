using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Runtime.InteropServices;

namespace DeskMadeline.Mac;

// The order is shared with the AppKit palette and with saved placements.
internal enum DesktopItemKind
{
    Refill, ZipMover, DustBunny, Cloud, Feather, Spring,
    ZipSquare, ZipTall, BoosterGreen, BoosterRed, MoveBlockLeft, MoveBlockRight, MoveBlockUp, MoveBlockDown,
    Bumper, WallSpringLeft, WallSpringRight, DreamBlock, PinkCloud, DoubleRefill, CrumblePlatform,
    SwapBlock, KevinBlock, Puffer, Jellyfish, Strawberry, TouchSwitch, SwitchGate,
    CassetteBlue, CassettePink, CassetteYellow, CassetteGreen, CoreBlock, CoreToggle, Heart,
}

internal readonly record struct DesktopItemPlacement(DesktopItemKind Kind, float X, float Y, float RouteX = 0, float RouteY = 0,
    int Width = 0, int Height = 0)
{
    public PointF ToGame(int scale) => new PointF(DesktopTerrain.ToGame(X, scale), DesktopTerrain.ToGame(Y, scale));
    internal bool IsValid => Enum.IsDefined(Kind) && float.IsFinite(X) && float.IsFinite(Y) &&
        float.IsFinite(RouteX) && float.IsFinite(RouteY);
}

internal readonly record struct DesktopItemFrame(Player Player, float Dt, float Time,
    Action<string> Play, Action<RumbleStrength, RumbleLength> Rumble, PetInput Input = default)
{
    public bool OnInterval(float interval, float offset = 0f) =>
        (int)((Time + offset - Dt) / interval) < (int)((Time + offset) / interval);
}

internal sealed partial class DesktopItems : IDisposable
{
    readonly List<DesktopItem> items = new List<DesktopItem>();
    readonly List<DesktopItemPlacement> unavailable = new List<DesktopItemPlacement>();
    readonly List<Native.ItemLayer> layers = new List<Native.ItemLayer>();
    readonly List<Solid> solids = new List<Solid>(), jumpThrus = new List<Solid>();
    readonly SpriteBloom bloom = new SpriteBloom(Sprites.Get("refillBloom", false));
    readonly Action<string> play;
    readonly Action<RumbleStrength, RumbleLength> rumble;
    readonly SoundEffects audio;
    int nextId = 1;
    DesktopItem grabbed;
    DesktopHeart activeHeart;
    bool draggingEndpoint;
    Native.ResizeEdge resizing;
    Size resizeStartSize;
    int dragScale;
    DesktopItemPlacement startPlacement, dragOrigin;
    PointF startPointer;

    public DesktopItems(Action<string> play, Action<RumbleStrength, RumbleLength> rumble, SoundEffects audio = null)
    {
        this.play = play;
        this.rumble = rumble;
        this.audio = audio;
    }

    DesktopItem Create(int id, DesktopItemPlacement placement) => placement.Kind switch
    {
        DesktopItemKind.Refill or DesktopItemKind.DoubleRefill => new DesktopRefill(id, placement, bloom),
        DesktopItemKind.ZipMover or DesktopItemKind.ZipSquare or DesktopItemKind.ZipTall => new DesktopZipMover(id, placement, bloom),
        DesktopItemKind.DustBunny => new DesktopDustBunny(id, placement),
        DesktopItemKind.Cloud or DesktopItemKind.PinkCloud => new DesktopCloud(id, placement, bloom),
        DesktopItemKind.Feather => new DesktopFeather(id, placement, bloom),
        DesktopItemKind.Spring or DesktopItemKind.WallSpringLeft or DesktopItemKind.WallSpringRight => new DesktopSpring(id, placement),
        DesktopItemKind.BoosterGreen or DesktopItemKind.BoosterRed => new DesktopBooster(id, placement, bloom, play, audio),
        DesktopItemKind.MoveBlockLeft or DesktopItemKind.MoveBlockRight or DesktopItemKind.MoveBlockUp or DesktopItemKind.MoveBlockDown
            => new DesktopMoveBlock(id, placement, bloom, audio),
        _ => CreateAdvancedItem(id, placement),
    };

    public Bitmap CreateIcon(DesktopItemKind kind)
    {
        using DesktopItem sample = Create(0, new DesktopItemPlacement(kind, 0, 0));
        return sample.Available ? sample.CreateIcon() : null;
    }

    public void Restore(IEnumerable<DesktopItemPlacement> placements)
    {
        foreach (var placement in placements)
        {
            if (!placement.IsValid) continue;
            if (!Add(placement)) unavailable.Add(placement);
        }
    }

    public bool Add(DesktopItemPlacement placement)
    {
        if (!placement.IsValid) return false;
        DesktopItem item = Create(nextId, placement);
        if (!item.Available) { item.Dispose(); return false; }
        nextId++;
        items.Add(item);
        return true;
    }

    public bool Place(DesktopItemKind kind, PointF cursor, DesktopTerrain terrain, int scale)
    {
        if (!Enum.IsDefined(kind)) return false;
        var placement = new DesktopItemPlacement(kind, cursor.X, cursor.Y);
        if (kind is DesktopItemKind.ZipMover or DesktopItemKind.ZipSquare or DesktopItemKind.ZipTall)
        {
            PointF anchor = placement.ToGame(scale);
            RectangleF monitor = terrain.NearestMonitor(anchor);
            var size = DesktopZipMover.Dimensions(kind);
            // The 24×24 and 16×32 blocks in Forsaken City lvl_11 travel
            // 56 px horizontally and 64 px vertically. Fit that cable on this screen.
            if (kind == DesktopItemKind.ZipTall)
            {
                float up = Math.Max(0, anchor.Y - monitor.Top - size.Height / 2f - 2f);
                float down = Math.Max(0, monitor.Bottom - anchor.Y - size.Height / 2f - 2f);
                float travel = up >= down ? -Math.Min(up, 64f) : Math.Min(down, 64f);
                placement = placement with { RouteY = travel * scale };
            }
            else
            {
                float left = Math.Max(0, anchor.X - monitor.Left - size.Width / 2f - 2f);
                float right = Math.Max(0, monitor.Right - anchor.X - size.Width / 2f - 2f);
                float length = kind == DesktopItemKind.ZipSquare ? 56f : 96f;
                float travel = right >= left ? Math.Min(right, length) : -Math.Min(left, length);
                placement = placement with { RouteX = travel * scale };
            }
        }
        else if (kind is DesktopItemKind.SwapBlock or DesktopItemKind.SwitchGate)
        {
            PointF anchor = placement.ToGame(scale);
            RectangleF monitor = terrain.NearestMonitor(anchor);
            if (kind == DesktopItemKind.SwapBlock)
            {
                float left = Math.Max(0f, anchor.X - monitor.Left - 16f), right = Math.Max(0f, monitor.Right - anchor.X - 16f);
                placement = placement with { RouteX = (right >= left ? Math.Min(64f, right) : -Math.Min(64f, left)) * scale };
            }
            else
            {
                float up = Math.Max(0f, anchor.Y - monitor.Top - 16f), down = Math.Max(0f, monitor.Bottom - anchor.Y - 16f);
                placement = placement with { RouteY = (up >= down ? -Math.Min(40f, up) : Math.Min(40f, down)) * scale };
            }
        }
        return Add(placement);
    }

    public List<DesktopItemPlacement> SavePlacements() => items.Where(item => !item.Generated)
        .Select(item => item.Placement).Concat(unavailable).ToList();

    public void BeginDrag(int id, PointF cursor, int scale, Native.ResizeEdge resize = Native.ResizeEdge.None)
    {
        if (grabbed != null) return;
        grabbed = items.Find(item => item.Id == (id & ~DesktopItem.RouteEndpoint));
        if (grabbed == null || grabbed.Generated) { grabbed = null; return; }
        draggingEndpoint = (id & DesktopItem.RouteEndpoint) != 0;
        resizing = resize;
        if (resizing != Native.ResizeEdge.None && grabbed is DesktopResizableBlock block && !draggingEndpoint)
        {
            resizeStartSize = block.BlockSize;
            dragScale = scale;
        }
        else resizing = Native.ResizeEdge.None;
        grabbed.Resizing = resizing != Native.ResizeEdge.None;
        startPlacement = grabbed.Placement;
        dragOrigin = draggingEndpoint ? startPlacement : grabbed.PlacementWhileHeld(scale);
        grabbed.Placement = dragOrigin;
        grabbed.Held = true;
        grabbed.OnGrabbed();
        startPointer = cursor;
    }

    public void Drag(int id, PointF cursor)
    {
        if (grabbed == null || grabbed.Id != (id & ~DesktopItem.RouteEndpoint)) return;
        if (resizing != Native.ResizeEdge.None)
        {
            grabbed.Placement = DesktopResizableBlock.Resize(startPlacement, resizeStartSize,
                resizing, dragScale, cursor.X - startPointer.X, cursor.Y - startPointer.Y);
            return;
        }
        if (draggingEndpoint)
        {
            float x = dragOrigin.RouteX + cursor.X - startPointer.X;
            float y = dragOrigin.RouteY + cursor.Y - startPointer.Y;
            bool horizontal = Math.Abs(x) >= Math.Abs(y);
            grabbed.Placement = dragOrigin with { RouteX = horizontal ? x : 0f, RouteY = horizontal ? 0f : y };
            return;
        }
        grabbed.Placement = dragOrigin with
        {
            X = dragOrigin.X + cursor.X - startPointer.X,
            Y = dragOrigin.Y + cursor.Y - startPointer.Y,
        };
    }

    public bool EndDrag(int id, PointF cursor, bool cancel)
    {
        if (grabbed == null || grabbed.Id != (id & ~DesktopItem.RouteEndpoint)) return false;
        if (cancel || cursor == startPointer) grabbed.Placement = startPlacement;
        else Drag(id, cursor);
        bool changed = grabbed.Placement != startPlacement;
        grabbed.Held = false;
        grabbed.Resizing = false;
        grabbed = null;
        draggingEndpoint = false;
        resizing = Native.ResizeEdge.None;
        return changed;
    }

    public bool Delete(int id)
        => Remove(id & ~DesktopItem.RouteEndpoint, generated: false);

    bool Remove(int id, bool generated)
    {
        int index = items.FindIndex(item => item.Id == id);
        if (index < 0 || (items[index].Generated && !generated)) return false;
        DesktopItem item = items[index];
        if (grabbed == item) grabbed = null;
        if (activeHeart == item) activeHeart = null;
        items.RemoveAt(index);
        item.Dispose();
        return true;
    }

    public void Prepare(Player player, DesktopTerrain terrain, int scale)
    {
        solids.Clear();
        solids.AddRange(terrain.BaseSolids);
        jumpThrus.Clear();
        holdables.Clear();
        dashSolids.Clear();
        foreach (DesktopItem item in items)
        {
            item.Anchor = item.Placement.ToGame(scale);
            item.Prepare(terrain, scale);
            item.UpdatedBeforePlayer = item.UpdatesBeforePlayer;
            item.PlatformIndex = -1;
            if (item.Held) continue;
            if (item is DesktopJellyfish jellyfish) holdables.Add(jellyfish.Actor);
            if (item is IDesktopDashSolid dashSolid) dashSolids[item.SolidKey] = dashSolid;
            if (item is DesktopSolidItem { Collidable: true } movingSolid)
            {
                item.PlatformIndex = solids.Count;
                solids.Add(movingSolid.Collider);
            }
            else if (item is IDesktopJumpThru { Collidable: true } jumpThru)
            {
                item.PlatformIndex = jumpThrus.Count;
                jumpThrus.Add(jumpThru.Collider);
            }
        }
        player.Solids = solids;
        player.JumpThrus = jumpThrus;
        player.Holdables = holdables;
        player.OnDashCollide = solidDashHandler ??= OnSolidDash;
        player.HasDashCollision = hasDashHandler ??= dashSolids.ContainsKey;
        player.OnDash = dashHandler ??= OnDash;
    }

    public void BeforePlayer(Player player, float dt, float time, PetInput input = default)
    {
        var frame = new DesktopItemFrame(player, dt, time, play, rumble, input);
        AdvanceMechanisms(frame);
        foreach (DesktopItem item in items)
            if (item.UpdatedBeforePlayer && !item.Held) item.Advance(frame);
    }

    public void CheckCollisions(Player player)
    {
        foreach (DesktopItem item in items)
        {
            if (item.Held) continue;
            if (item is DesktopHeart && activeHeart != null) continue;
            item.Touch(player, play, rumble);
            if (item is DesktopHeart { PickupActive: true } heart) activeHeart = heart;
            if (player.IsDead) return;
        }
    }

    public bool HeartPickupActive => activeHeart != null;
    public bool HeartPickupForeground => activeHeart?.PlayerInFront == true;

    public float AdvanceHeartPickup(Player player, DesktopTerrain terrain, float dt)
    {
        if (activeHeart == null) return 1f;
        float rate = activeHeart.AdvancePickup(dt, player, terrain.NearestDisplayFrame(activeHeart.Anchor), rumble);
        if (!activeHeart.PickupActive) activeHeart = null;
        return rate;
    }

    public void CancelHeartPickup()
    {
        activeHeart?.CancelPickup();
        activeHeart = null;
    }

    public bool AfterPlayer(Player player, float dt, float time, PetInput input = default)
    {
        var frame = new DesktopItemFrame(player, dt, time, play, rumble, input);
        bool freeze = false;
        foreach (DesktopItem item in items)
        {
            if (item.Held) continue;
            if (!item.UpdatedBeforePlayer) freeze |= item.Advance(frame);
            item.AfterFrame(dt);
        }
        return freeze;
    }

    public ReadOnlySpan<Native.ItemLayer> Render(int scale, DesktopBadeline chase = null)
    {
        layers.Clear();
        foreach (DesktopItem item in items)
        {
            int first = layers.Count;
            item.Render(layers, scale);
            if (!item.Generated) continue;
            for (int i = first; i < layers.Count; i++)
                layers[i] = layers[i] with { ItemId = 0 };
        }
        chase?.Render(layers, scale);
        return CollectionsMarshal.AsSpan(layers);
    }

    public void Clear()
    {
        foreach (DesktopItem item in items) item.Dispose();
        items.Clear();
        unavailable.Clear();
        grabbed = null;
        activeHeart = null;
        ClearCourseState();
        ClearMechanisms();
    }

    public void Dispose() => Clear();
}

internal abstract class DesktopItem : IDisposable
{
    protected Bitmap Body { get; private set; }
    protected int Center => Body.Width / 2;
    protected int CenterY => Body.Height / 2;
    public int Id { get; }
    internal const int RouteEndpoint = 1 << 30;
    protected IntPtr SolidId => new IntPtr(-1000L - Id);
    internal IntPtr SolidKey => SolidId;
    public bool Generated { get; set; }
    public DesktopItemPlacement Placement;
    public PointF Anchor;
    public bool Held, Resizing, UpdatedBeforePlayer;
    public int PlatformIndex;
    public abstract bool Available { get; }
    public virtual bool UpdatesBeforePlayer => false;
    protected virtual int Depth => 1;
    protected virtual PointF Position => Anchor;

    protected DesktopItem(int id, DesktopItemPlacement placement, int canvasSize = 48)
    {
        Id = id;
        Placement = placement;
        Body = new Bitmap(canvasSize, canvasSize);
    }

    public virtual void Prepare(DesktopTerrain terrain, int scale) { }
    public virtual DesktopItemPlacement PlacementWhileHeld(int scale) => Placement;
    public virtual void OnGrabbed() { }
    public virtual bool Advance(DesktopItemFrame frame) => false;
    public virtual void AfterFrame(float dt) { }
    public virtual void Touch(Player player, Action<string> play, Action<RumbleStrength, RumbleLength> rumble) { }
    protected abstract Rectangle Paint();

    protected void ResizeBody(int width, int height)
    {
        if (Body.Width == width && Body.Height == height) return;
        Bitmap next = new Bitmap(width, height);
        Body.Dispose();
        Body = next;
    }

    public Bitmap CreateIcon()
    {
        Rectangle painted = Paint();
        Rectangle area = Body.OpaqueBounds(painted);
        if (area.IsEmpty) return null;
        area.Inflate(1, 1);
        return Body.Extract(area, area.Size, Point.Empty);
    }

    public virtual void Render(List<Native.ItemLayer> layers, int scale)
    {
        Rectangle area = Paint();
        AddLayer(layers, Body, area, Position, Center, CenterY, scale, Depth, Id);
    }

    internal static void AddLayer(List<Native.ItemLayer> layers, Bitmap bitmap, Rectangle area,
        PointF position, int centerX, int centerY, float scale, int depth, int itemId = 0,
        Native.DesktopRect hitBounds = default, Native.ItemHitMode hitMode = Native.ItemHitMode.Alpha, float opacity = 1f)
    {
        if (area.IsEmpty) return;
        var image = new Native.ImageLayer(bitmap.Address + area.Y * bitmap.RowBytes + area.X * 4,
            bitmap.RowBytes, area.Width, area.Height, new Native.DesktopRect(
                (position.X + area.X - centerX) * scale, (position.Y + area.Y - centerY) * scale,
                area.Width * scale, area.Height * scale));
        layers.Add(new Native.ItemLayer(image, depth, itemId, hitBounds, hitMode, opacity));
    }

    protected static Bitmap Art(string id) => Sprites.Get(id, false);
    protected static Bitmap[] Frames(string prefix, int count) => Enumerable.Range(0, count)
        .Select(i => Art(FormattableString.Invariant($"{prefix}{i:00}"))).ToArray();

    protected static void DrawCentered(Graphics g, Bitmap image, float x, float y,
        float scaleX = 1f, float scaleY = 1f, Color? tint = null, bool silhouette = false)
    {
        float width = image.Width * scaleX, height = image.Height * scaleY;
        g.DrawImage(image, x - width / 2f, y - height / 2f, width, height, tint: tint, silhouette: silhouette);
    }

    protected static void DrawOutline(Graphics g, Bitmap image, float x, float y, float scaleX, float scaleY)
    {
        for (int dx = -1; dx <= 1; dx++)
            for (int dy = -1; dy <= 1; dy++)
                if (dx != 0 || dy != 0)
                    DrawCentered(g, image, x + dx, y + dy, scaleX, scaleY, Color.Black, silhouette: true);
    }

    protected static void DrawRotated(Graphics g, Bitmap image, float x, float y, float angle, Color? tint = null)
    {
        var state = g.Save();
        g.TranslateTransform(x, y);
        g.RotateTransform(angle * (180f / MathF.PI));
        DrawCentered(g, image, 0f, 0f, tint: tint);
        g.Restore(state);
    }

    protected static bool OverInterval(ref float timer, float dt)
    {
        if (timer <= 0f) return true;
        timer -= dt;
        return false;
    }

    protected static void StepFrames(ref int frame, ref float timer, float dt, float delay, int length, bool loop)
    {
        if (frame < 0 || (timer += dt) < delay) return;
        timer -= delay;
        if (++frame == length) frame = loop ? 0 : -1;
    }

    public virtual void Dispose() => Body.Dispose();
}

internal abstract class DesktopItemWithEffects(int id, DesktopItemPlacement placement, int canvasSize = 48, int effectRadius = 72)
    : DesktopItem(id, placement, canvasSize)
{
    Bitmap effects;
    readonly Bitmap[] slash = Frames("slash", 4);
    protected readonly ParticleSystem Particles = new ParticleSystem();
    int slashFrame = -1;
    float slashAngle, slashDistance, slashTimer;
    protected bool HasSlash => slash.All(frame => frame != null);
    protected virtual int EffectDepth => 2;
    protected virtual PointF EffectOrigin => Anchor;
    protected virtual PointF EffectView => EffectOrigin;

    protected void BurstSlash(float angle)
    {
        slashFrame = 0;
        slashAngle = angle;
        slashDistance = slashTimer = 0f;
    }

    protected void AdvanceSlash(float dt)
    {
        if (slashFrame < 0) return;
        slashDistance += 8f * dt;
        StepFrames(ref slashFrame, ref slashTimer, dt, .1f, slash.Length, loop: false);
    }

    public override void AfterFrame(float dt) => Particles.Update(dt);

    public override void Render(List<Native.ItemLayer> layers, int scale)
    {
        base.Render(layers, scale);
        if (Particles.Count == 0 && slashFrame < 0) return;
        effects ??= new Bitmap(2 * effectRadius, 2 * effectRadius);
        using var g = new Graphics(effects);
        g.Clear();
        if (slashFrame >= 0)
        {
            float x = effectRadius + slashDistance * MathF.Cos(slashAngle);
            float y = effectRadius + slashDistance * MathF.Sin(slashAngle);
            float rotation = MathF.Abs(slashAngle - MathF.PI) > .01f ? slashAngle : 0f;
            DrawRotated(g, slash[slashFrame], x, y, rotation);
        }
        PointF origin = EffectOrigin;
        PointF view = EffectView;
        Particles.Draw(g, view.X - origin.X - effectRadius, view.Y - origin.Y - effectRadius, origin.X, origin.Y);
        AddLayer(layers, effects, g.Dirty, view, effectRadius, effectRadius, scale, EffectDepth);
    }

    public override void Dispose() { effects?.Dispose(); base.Dispose(); }
}
