using System;
using System.Drawing;

namespace DeskMadeline.Mac;

internal abstract class DesktopSolidItem(int id, DesktopItemPlacement placement, Size size, int canvasSize = 56)
    : DesktopItem(id, placement, canvasSize)
{
    protected virtual int Width => size.Width;
    protected virtual int Height => size.Height;
    protected virtual int SurfaceSoundIndex => 0;
    protected virtual bool Dream => false;
    public virtual bool Collidable => true;
    public Solid Collider => BlockAt(Position);

    protected Solid BlockAt(PointF position) => new Solid
    {
        Id = SolidId, L = position.X - Width / 2f, R = position.X + Width / 2f,
        T = position.Y - Height / 2f, B = position.Y + Height / 2f, SurfaceSoundIndex = SurfaceSoundIndex, Dream = Dream,
    };

    protected void MoveSolid(Player player, PointF old, PointF target, PointF lift)
    {
        int dx = (int)(target.X - old.X), dy = (int)(target.Y - old.Y);
        bool active = !player.IsDead && !player.IsRespawning && !player.BeingDragged;
        Solid previous = player.Solids[PlatformIndex];
        bool rider = active && player.IsRiding(previous);
        Solid horizontal = BlockAt(new PointF(target.X, old.Y));
        horizontal.LiftX = lift.X;
        horizontal.LiftY = lift.Y;
        player.Solids[PlatformIndex] = horizontal;
        if (active && dx != 0)
        {
            if (player.OverlapsHitbox(horizontal))
                player.PushH(player.ClearanceX(horizontal, Math.Sign(dx)), horizontal);
            else if (rider)
                player.MoveWithPlatform(dx, 0, SolidId);
            if (rider) player.LiftSpeed = lift;
        }

        Solid vertical = BlockAt(target);
        vertical.LiftX = lift.X;
        vertical.LiftY = lift.Y;
        rider = active && !player.IsDead && player.IsRiding(horizontal);
        player.Solids[PlatformIndex] = vertical;
        if (active && !player.IsDead && dy != 0)
        {
            if (player.OverlapsHitbox(vertical))
                player.PushV(player.ClearanceY(vertical, Math.Sign(dy)), vertical);
            else if (rider)
                player.MoveWithPlatform(0, dy, SolidId);
            if (rider) player.LiftSpeed = lift;
        }
    }

    protected bool BlockedAt(Player player, PointF position)
    {
        Solid target = BlockAt(position);
        foreach (Solid solid in player.Solids)
            if (solid.Id != SolidId && target.L < solid.R && target.R > solid.L &&
                target.T < solid.B && target.B > solid.T) return true;
        return false;
    }

    protected static float Approach(float value, float target, float distance) =>
        value < target ? Math.Min(value + distance, target) : Math.Max(value - distance, target);

    protected void DrawFrameTiles(Graphics g, Bitmap texture, float left, float top, bool interior = false)
    {
        for (int x = 0; x < Width / 8; x++)
        for (int y = 0; y < Height / 8; y++)
        {
            int tileX = x == 0 ? 0 : x == Width / 8 - 1 ? 2 : 1;
            int tileY = y == 0 ? 0 : y == Height / 8 - 1 ? 2 : 1;
            if (interior || tileX != 1 || tileY != 1)
                g.DrawRegion(texture, new Rectangle(tileX * 8, tileY * 8, 8, 8), left + x * 8f, top + y * 8f);
        }
    }
}

internal abstract class DesktopResizableBlock(int id, DesktopItemPlacement placement, Size size)
    : DesktopSolidItem(id, placement, size)
{
    const int Tile = 8, Minimum = 2 * Tile, Maximum = 1024, Padding = 12;
    protected override int Width => Placement.Width > 0 ? Snap(Placement.Width) : base.Width;
    protected override int Height => Placement.Height > 0 ? Snap(Placement.Height) : base.Height;
    public Size BlockSize => new Size(Width, Height);
    protected virtual float HitOffsetY => 0f;

    protected void EnsureBlockCanvas(int padding = Padding) => ResizeBody(Width + padding, Height + padding);

    public override void Render(System.Collections.Generic.List<Native.ItemLayer> layers, int scale)
    {
        Solid collider = Collider;
        var bounds = new Native.DesktopRect(collider.L * scale, (collider.T + HitOffsetY) * scale, Width * scale, Height * scale);
        Rectangle area = Paint();
        AddLayer(layers, Body, area, Position, Center, CenterY, scale, Depth, Id, bounds, Native.ItemHitMode.Resize);
    }

    static int Snap(float length) =>
        (int)MathF.Round(Math.Clamp(length, Minimum, Maximum) / Tile, MidpointRounding.AwayFromZero) * Tile;

    public static DesktopItemPlacement Resize(DesktopItemPlacement start, Size size, Native.ResizeEdge edges,
        int scale, float dx, float dy)
    {
        int horizontal = (edges & Native.ResizeEdge.Left) != 0 ? -1 :
            (edges & Native.ResizeEdge.Right) != 0 ? 1 : 0;
        int vertical = (edges & Native.ResizeEdge.Top) != 0 ? -1 :
            (edges & Native.ResizeEdge.Bottom) != 0 ? 1 : 0;
        int width = Snap(size.Width + horizontal * dx / scale);
        int height = Snap(size.Height + vertical * dy / scale);
        float x = start.X + horizontal * (width - size.Width) * scale / 2f;
        float y = start.Y + vertical * (height - size.Height) * scale / 2f;
        return start with
        {
            X = x, Y = y,
            Width = width == size.Width ? start.Width : width,
            Height = height == size.Height ? start.Height : height,
        };
    }
}
