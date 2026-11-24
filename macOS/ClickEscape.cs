using System;
using System.Collections.Generic;
using System.Drawing;

namespace DeskMadeline.Mac;

/// <summary>Supplies ordinary Celeste inputs for Madeline's mouse reaction.</summary>
internal sealed class ClickEscape(Random random)
{
    internal enum Technique { Dash, Super, Wavedash }
    enum Phase { Idle, Rising, Dashing, Jumping }

    // Farewell's wavedashppt recording begins its down-diagonal dash
    // 20 game pixels above the platform. Use that height for the input cue.
    const int WavedashHeight = 20;
    const double AttemptWindow = .85;
    readonly Queue<(float CursorX, double ExpiresAt)> pending = new();
    Phase phase;
    double clock;
    float takeoffY;
    int landingsBeforeDash;
    Point aim;

    public bool Active => phase != Phase.Idle || pending.Count != 0;
    internal Technique Selected { get; private set; }

    public void Request(float cursorX) => pending.Enqueue((cursorX, clock + AttemptWindow));

    public void Cancel()
    {
        phase = Phase.Idle;
        pending.Clear();
        clock = 0;
    }

    public PetInput Sample(Player player, float dt)
    {
        if (!Active) return default;
        if (player.IsDead || player.IsPreDeath || player.IsRespawning || player.BeingDragged)
        {
            Cancel();
            return default;
        }

        clock += dt;
        while (pending.TryPeek(out var attempt) && attempt.ExpiresAt < clock) pending.Dequeue();
        switch (phase)
        {
            case Phase.Idle:
                // Let the current move finish and normal refill rules supply a spent dash.
                if (pending.Count == 0 || player.State is not (Player.StNormal or Player.StSwim) ||
                    player.Dashes == 0 || player.IsHitStopped) return default;
                Choose(player, pending.Dequeue().CursorX);
                if (Selected != Technique.Wavedash) return BeginDash(player);
                takeoffY = player.Pos.Y;
                phase = Phase.Rising;
                player.BufferJump();
                return Input(player, Point.Empty, jumpHeld: true);

            case Phase.Rising:
                if (player.State != Player.StNormal) break;
                if (player.IsHitStopped) return Input(player, Point.Empty, jumpHeld: true);
                if (takeoffY - player.Pos.Y >= WavedashHeight) return BeginDash(player);
                if (player.Speed.Y < 0f) return Input(player, Point.Empty, jumpHeld: true);
                // A moving ceiling can cut the setup jump short.
                Selected = Technique.Dash;
                aim.Y = 0;
                return BeginDash(player);

            case Phase.Dashing:
                if (player.State == Player.StDreamDash) return Input(player, aim);
                if (player.State != Player.StDash) break;
                // DashCoroutine reads the aim after hitstop. Wait until it has
                // done so before giving the ground jump to DashUpdate.
                if (player.IsFrozen) return Input(player, aim);
                bool super = Selected == Technique.Super && player.DashDir.X != 0f && player.DashDir.Y == 0f;
                bool wavedash = Selected == Technique.Wavedash && player.WavedashCount != landingsBeforeDash;
                if (super || wavedash)
                {
                    phase = Phase.Jumping;
                    player.BufferJump();
                    return Input(player, new Point(aim.X, 0), jumpHeld: true);
                }
                return Input(player, aim);

            case Phase.Jumping:
                // A moving obstruction can make the game reject the jump.
                // Keep the mouse released until the resulting dash ends.
                if (player.State is Player.StDash or Player.StDreamDash) return Input(player, aim);
                // Hold the jump through the escape's rise.
                if (player.State == Player.StNormal && player.Speed.Y < 0f)
                    return Input(player, new Point(aim.X, 0), jumpHeld: true);
                break;
        }
        phase = Phase.Idle;
        return default;
    }

    PetInput BeginDash(Player player)
    {
        player.BufferDash();
        landingsBeforeDash = player.WavedashCount;
        phase = Phase.Dashing;
        return Input(player, aim);
    }

    void Choose(Player player, float clickX)
    {
        int away = Math.Sign(player.Pos.X - clickX);
        if (away == 0) away = player.Facing;
        int awayRoom = HorizontalRoom(player, away), otherRoom = HorizontalRoom(player, -away);
        int direction = otherRoom > awayRoom ? -away : away;
        int room = Math.Max(awayRoom, otherRoom);
        var forward = new Point(direction, 0);
        var dashAim = room == 0 && !player.CollidesAt(player.Pos.X, player.Pos.Y - 1f)
            ? new Point(0, -1) : forward;

        Span<(Technique Move, Point Aim)> choices = stackalloc (Technique, Point)[3];
        int count = 1;
        choices[0] = (Technique.Dash, dashAim);
        PointF at = player.Pos;
        if (player.State == Player.StNormal && player.Speed.Y >= 0f && player.CheckGroundAt(at.X, at.Y))
        {
            float step = at.X + direction * Player.HitHalfWidth;
            if (room >= Player.HitHalfWidth * 4 && player.CheckGroundAt(step, at.Y) &&
                !player.CollidesAt(step, at.Y - 1f))
                choices[count++] = (Technique.Super, forward);
            if (room >= WavedashHeight && WavedashPathClear(player, direction))
                choices[count++] = (Technique.Wavedash, new Point(direction, 1));
        }

        (Selected, aim) = choices[random.Next(count)];
    }

    static int HorizontalRoom(Player player, int direction)
    {
        int room = 0;
        int limit = (int)(Player.HitHalfWidth * 8);
        while (room < limit && !player.CollidesAt(player.Pos.X + (room + 1) * direction, player.Pos.Y))
            room++;
        return room;
    }

    static bool WavedashPathClear(Player player, int direction)
    {
        PointF at = player.Pos;
        for (int i = 1; i <= WavedashHeight; i++)
            if (player.CollidesAt(at.X, at.Y - i) ||
                player.CollidesAt(at.X + direction * i, at.Y - WavedashHeight + i))
                return false;
        // Leave one dash step of landing room for the original integer movement.
        float landing = at.X + direction * WavedashHeight;
        return player.CheckGroundAt(landing, at.Y) &&
            player.CheckGroundAt(landing + direction * Player.HitHalfWidth, at.Y);
    }

    static PetInput Input(Player player, Point direction, bool jumpHeld = false) => new PetInput
    {
        MoveX = direction.X, MoveY = direction.Y, GliderMoveY = direction.Y,
        AimX = direction.X, AimY = direction.Y, FeatherX = direction.X, FeatherY = direction.Y,
        JumpHeld = jumpHeld, JumpPressed = player.HasJumpBuffer, DashPressed = player.HasDashBuffer,
    };
}
