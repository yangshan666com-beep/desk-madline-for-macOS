using System;
using System.Drawing;

namespace DeskMadeline.Mac;

internal sealed class DesktopDrag
{
    const float DragDistance = 4f;
    const double MaximumClickDuration = .3;
    PointF offset, lastCursor, velocity;
    double pressedAt, lastMove;
    bool pressed, moved, canPickUp;

    static bool CanInteract(Player player) => !player.IsDead && !player.IsPreDeath && !player.IsRespawning;

    public void Begin(Player player, PointF cursor, double timestamp, int scale, bool allowDrag = true)
    {
        if (pressed || player.BeingDragged || !CanInteract(player)) return;
        pressed = true;
        canPickUp = allowDrag;
        offset = new PointF(player.Pos.X * scale - cursor.X, player.Pos.Y * scale - cursor.Y);
        lastCursor = cursor;
        pressedAt = lastMove = timestamp;
        moved = false;
        velocity = PointF.Empty;
    }

    // Decide between a tap and a drag before interrupting the player's motion.
    // A press that began during an escape can only become a tap.
    public bool Move(Player player, PointF cursor, double timestamp, int scale)
    {
        if (!pressed || cursor == lastCursor) return false;
        float dx = cursor.X - lastCursor.X, dy = cursor.Y - lastCursor.Y;
        bool started = !moved;
        if (started)
        {
            if (dx * dx + dy * dy < DragDistance * DragDistance) return false;
            moved = true;
            if (canPickUp && CanInteract(player)) player.BeginDesktopDrag();
        }
        if (!player.BeingDragged) return false;
        float dt = (float)Math.Clamp(timestamp - lastMove, 1.0 / 120, .1);
        velocity = new PointF(velocity.X * .7f + .3f * dx / dt, velocity.Y * .7f + .3f * dy / dt);
        lastCursor = cursor;
        lastMove = timestamp;
        player.Pos = new PointF(DesktopTerrain.ToGame(cursor.X + offset.X, scale),
            DesktopTerrain.ToGame(cursor.Y + offset.Y, scale));
        return started;
    }

    public bool Finish(Player player, RectangleF monitor, double timestamp, int scale, bool cancel)
    {
        if (!pressed) return false;
        pressed = false;
        if (!player.BeingDragged)
            return !cancel && !moved && timestamp - pressedAt <= MaximumClickDuration && CanInteract(player);
        player.BeingDragged = false;
        player.EndRide();
        player.ResetLiftSpeed();
        if (!monitor.IsEmpty && !monitor.Contains(player.Pos))
        {
            player.Pos = new PointF(
                Math.Clamp(player.Pos.X, monitor.Left + 4f, monitor.Right - 4f),
                Math.Clamp(player.Pos.Y, monitor.Top + player.CurrentHitHeight, monitor.Bottom));
        }
        player.DisplaceOutOfSolids();

        // Match the Windows pet's throw multiplier, minimum and maximum speed.
        float x = velocity.X * .6f / scale, y = velocity.Y * .6f / scale;
        float speed = MathF.Sqrt(x * x + y * y);
        float magnitude = cancel || timestamp - lastMove >= .1 || speed <= 30f ? 0f : Math.Min(1f, 400f / speed);
        player.Speed = new PointF(x * magnitude, y * magnitude);
        return false;
    }
}
