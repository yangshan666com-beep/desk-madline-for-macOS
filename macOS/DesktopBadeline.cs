using System;
using System.Collections.Generic;
using System.Drawing;

namespace DeskMadeline.Mac;

/// <summary>The Old Site chaser uses past player poses, with no collision against the terrain.</summary>
internal sealed class DesktopBadeline : IDisposable
{
    public const int MaxCount = 6;
    const float FollowDelay = 1.55f, Spacing = .4f, EntryTime = FollowDelay - .1f;
    const float TrailInterval = .1f, TrailDuration = 1f;
    const string MadelineSound = "event:/char/madeline/";
    const string BadelineSound = "event:/char/badeline/";
    // Four seconds of Player.ChaserState at the desktop's fixed 60 Hz.
    readonly Pose[] history = new Pose[242];
    readonly List<PlayerSoundEvent> frameSounds = new(5);
    readonly SpriteBank bank;
    readonly IReadOnlyList<string> bangs;
    readonly SoundEffects sound;
    Chaser[] chasers = Array.Empty<Chaser>();
    float clock;
    int first, length;
    PointF? resumeAt;
    bool started, waitForMovement;

    readonly record struct Pose(PointF Position, string Animation, int Facing, float Time, PlayerSoundEvent[] Sounds);

    public DesktopBadeline(SpriteBank bank, SoundEffects sound)
    {
        this.bank = bank;
        this.sound = sound;
        bangs = AtlasLookup.Subtextures(Sprites.Atlas, "characters/player/bangs");
    }

    public bool Available => bank.Has("badeline") && bangs.Count != 0;
    internal int Count => chasers.Length;

    public void SetCount(int count)
    {
        count = Available ? Math.Clamp(count, 0, MaxCount) : 0;
        if (count == chasers.Length) return;
        DisposeChasers();
        chasers = new Chaser[count];
        for (int i = 0; i < count; i++)
            chasers[i] = new Chaser(bank, i);
        Reset();
    }

    public void Reset()
    {
        if (started)
        {
            foreach (var chaser in chasers) chaser.Hide();
            Array.Clear(history);
        }
        clock = 0f;
        first = length = 0;
        frameSounds.Clear();
        started = false;
        resumeAt = null;
        waitForMovement = false;
    }

    public void RecordSound(PlayerSoundEvent e)
    {
        if (started && frameSounds.Count < 5 && !e.IsStop &&
            (e.Path?.StartsWith(MadelineSound, StringComparison.Ordinal) == true ||
             e.Path == "event:/game/general/assist_screenbottom"))
            frameSounds.Add(e);
    }

    public void Advance(Player player, RectangleF monitor, float dt)
    {
        if (chasers.Length == 0) return;
        // A desktop has no room transition. Clear expired routes at respawn or
        // after picking her up, and give the player the first move on the new route.
        if (player.BeingDragged || player.IsRespawning)
        {
            Reset();
            waitForMovement = true;
            return;
        }
        if (!started)
        {
            if (player.IsDead || monitor.IsEmpty) return;
            if (waitForMovement)
            {
                resumeAt ??= player.Pos;
                if (resumeAt == player.Pos) return;
            }
            float x = player.Pos.X - 56f * player.Facing;
            if (x < monitor.Left + 12f || x > monitor.Right - 12f)
                x = player.Pos.X + 56f * player.Facing;
            var entry = new PointF(Math.Clamp(x, monitor.Left, monitor.Right),
                Math.Clamp(player.Pos.Y - 16f, monitor.Top, monitor.Bottom));
            foreach (var chaser in chasers) chaser.Begin(entry, player.Pos);
            started = true;
        }

        int previousTrail = (int)(clock / TrailInterval);
        clock += dt;
        bool trail = (int)(clock / TrailInterval) != previousTrail;
        if (!player.IsDead) RecordPose(player);
        else frameSounds.Clear();
        foreach (var chaser in chasers)
        {
            Pose? pose = null;
            if (!player.IsDead && clock >= chaser.Delay + EntryTime && FindPose(FollowDelay + chaser.Delay, out var past))
                pose = past;
            chaser.Advance(clock, dt, player.IsDead, pose, trail, sound, bangs);
        }
    }

    void RecordPose(Player player)
    {
        while (length > 0 && (clock - history[first].Time > 4f || length == history.Length))
        {
            history[first] = default;
            first = (first + 1) % history.Length;
            length--;
        }
        var sounds = frameSounds.Count == 0 ? null : frameSounds.ToArray();
        frameSounds.Clear();
        history[(first + length++) % history.Length] = new Pose(player.Pos, player.AnimId, player.Facing, clock, sounds);
    }

    bool FindPose(float delay, out Pose pose)
    {
        for (int i = 0; i < length; i++)
        {
            Pose candidate = history[(first + i) % history.Length];
            float age = clock - candidate.Time;
            if (age > delay) continue;
            if (i == 0 && delay - age >= .02f) break;
            pose = candidate;
            return true;
        }
        pose = default;
        return false;
    }

    public void CheckCollision(Player player)
    {
        if (player.IsDead || player.IsRespawning || player.BeingDragged) return;
        player.GetHurtbox(out float left, out float top, out float right, out float bottom);
        foreach (var chaser in chasers)
        {
            PointF p = chaser.Position;
            if (!chaser.Collidable || left >= p.X + 3f || right <= p.X - 3f ||
                top >= p.Y - 1f || bottom <= p.Y - 7f) continue;
            float dx = player.Pos.X - p.X, dy = player.Pos.Y - p.Y;
            float magnitude = MathF.Sqrt(dx * dx + dy * dy);
            player.Die(magnitude > 0f ? new PointF(dx / magnitude, dy / magnitude) : PointF.Empty);
            return;
        }
    }

    public void Render(List<Native.ItemLayer> layers, int scale)
    {
        foreach (var chaser in chasers) chaser.Render(layers, scale, clock);
    }

    void DisposeChasers()
    {
        foreach (var chaser in chasers) chaser.Dispose();
        chasers = Array.Empty<Chaser>();
    }

    public void Dispose() => DisposeChasers();

    sealed class Chaser : IDisposable
    {
        const int Size = 64, Anchor = Size / 2;
        const int TrailSlots = 10;
        // The AppKit overlay uses larger z positions nearer the viewer.
        const int TrailDepth = 1, ActorDepth = 2;
        readonly Player visual = new();
        readonly Stamp body = new();
        readonly Stamp[] trails = new Stamp[TrailSlots];
        readonly int index;
        readonly Color hair;
        PointF from, target;
        float hover, opacity, lastPoseTime;
        int nextTrail;
        bool visible, laughing;

        public float Delay => index * Spacing;
        public PointF Position { get; private set; }
        public bool Collidable { get; private set; }

        public Chaser(SpriteBank bank, int index)
        {
            this.index = index;
            Color purple = Player.NormalBadelineHairColor;
            int Tint(int channel) => (int)(channel + (255 - channel) * index / 6f);
            hair = Color.FromArgb(Tint(purple.R), Tint(purple.G), Tint(purple.B));
            visual.ResetSprite(bank, "badeline", Player.ModeBadeline, null);
        }

        public void Begin(PointF entry, PointF destination)
        {
            Position = from = entry;
            target = destination;
            visual.Facing = destination.X < entry.X ? -1 : 1;
            visual.PlaySprite("fallSlow", restart: true);
            visual.Hair.Reset(new PointF(entry.X - 200f * visual.Facing, entry.Y + 200f), visual.Facing);
        }

        public void Hide()
        {
            visible = Collidable = laughing = false;
            opacity = hover = lastPoseTime = 0f;
            nextTrail = 0;
            foreach (Stamp trail in trails)
                if (trail != null) trail.Time = float.NegativeInfinity;
        }

        public void Advance(float time, float dt, bool dead, Pose? pose, bool leaveTrail,
            SoundEffects sound, IReadOnlyList<string> bangs)
        {
            if (!visible && (time < Delay || dead)) return;
            if (!visible)
            {
                visible = true;
                sound?.Play(BadelineSound + "level_entry", "chaser_count", index);
            }

            float age = time - Delay;
            float appear = Math.Clamp(age / .5f, 0f, 1f);
            opacity = visual.SpriteScaleX = visual.SpriteScaleY = appear * appear * appear;
            float wobbleX = 0f, wobbleY = 0f;
            if (dead)
            {
                if (!laughing) visual.PlaySprite("laugh");
                laughing = true;
                Collidable = false;
                wobbleX = MathF.Sin(hover) * 4f;
                hover += dt * 3f;
                wobbleY = MathF.Sin(hover * 2f) * 4f;
            }
            else if (age < EntryTime)
            {
                float progress = age / EntryTime;
                float eased = progress * progress * progress;
                Position = new PointF(from.X + (target.X - from.X) * eased,
                    from.Y + (target.Y - from.Y) * eased);
            }
            else
            {
                if (!Collidable) Position = target;
                Collidable = true;
                if (pose is Pose sample)
                {
                    Position = Approach(Position, sample.Position, 500f * dt);
                    visual.Facing = sample.Facing;
                    if (!string.IsNullOrEmpty(sample.Animation)) visual.PlaySprite(sample.Animation);
                    if (sample.Time > lastPoseTime)
                    {
                        lastPoseTime = sample.Time;
                        if (sample.Sounds != null && sound != null)
                        {
                            foreach (var e in sample.Sounds)
                            {
                                string path = e.Path.StartsWith(MadelineSound, StringComparison.Ordinal)
                                    ? BadelineSound + e.Path[MadelineSound.Length..] : e.Path;
                                sound.Play(path, e.Parameter, e.Value, chaserIndex: index);
                            }
                        }
                    }
                }
            }

            visual.Sprite.Update(dt);
            visual.Pos = new PointF(Position.X + wobbleX, Position.Y + wobbleY);
            HairMeta.TryGet(visual.CurrentFrameId, out HairMeta.Meta meta);
            visual.Hair.Update(dt);
            visual.Hair.AfterUpdate(dt, new PointF(visual.Pos.X + meta.Offset.X * visual.Facing,
                visual.Pos.Y - 9f * visual.SpriteScaleY + meta.Offset.Y), visual.Facing, 4);
            body.Draw(visual, bangs, hair, time);
            if (leaveTrail && opacity > 0f)
            {
                Stamp trail = trails[nextTrail] ??= new Stamp();
                trail.CopyTrail(body, opacity);
                nextTrail = (nextTrail + 1) % TrailSlots;
            }
        }

        static PointF Approach(PointF current, PointF destination, float amount)
        {
            float x = destination.X - current.X, y = destination.Y - current.Y;
            float distance = MathF.Sqrt(x * x + y * y);
            return distance <= amount ? destination :
                new PointF(current.X + x * amount / distance, current.Y + y * amount / distance);
        }

        public void Render(List<Native.ItemLayer> layers, int scale, float time)
        {
            if (!visible) return;
            for (int i = 0; i < TrailSlots; i++)
            {
                Stamp trail = trails[(nextTrail + i) % TrailSlots];
                if (trail == null || time - trail.Time >= TrailDuration) continue;
                float remaining = 1f - (time - trail.Time) / TrailDuration;
                trail.Render(layers, scale, laughing ? ActorDepth + 2 : TrailDepth,
                    .75f * remaining * remaining * remaining);
            }
            body.Render(layers, scale, laughing ? ActorDepth + 3 : ActorDepth, opacity);
        }

        public void Dispose()
        {
            body.Dispose();
            foreach (Stamp trail in trails) trail?.Dispose();
        }

        sealed class Stamp : IDisposable
        {
            readonly Bitmap bitmap = new(Size, Size);
            readonly Graphics graphics;
            Rectangle area;
            PointF point;
            public float Time = float.NegativeInfinity;

            public Stamp() => graphics = new Graphics(bitmap);

            public void Draw(Player avatar, IReadOnlyList<string> bangs, Color hair, float time)
            {
                Time = time;
                point = new PointF(MathF.Floor(avatar.Pos.X), MathF.Floor(avatar.Pos.Y));
                graphics.BeginFrame(area);
                PetEffects.DrawHair(graphics, avatar, bangs, point.X - Anchor, point.Y - Anchor, hair);
                PetEffects.DrawBody(graphics, avatar, Anchor, Anchor, Color.White);
                area = graphics.Dirty;
            }

            public void CopyTrail(Stamp source, float opacity)
            {
                Time = source.Time;
                point = source.point;
                graphics.BeginFrame(area);
                Rectangle r = source.area;
                // TrailManager whitens the snapshot RGB while keeping its alpha, then
                // tints the mask. Multiplying the sprite RGB darkens the outline and clothes.
                graphics.DrawRegion(source.bitmap, r, r.X, r.Y, opacity, Player.NormalHairColor, silhouette: true);
                area = graphics.Dirty;
            }

            public void Render(List<Native.ItemLayer> layers, int scale, int depth, float opacity) =>
                DesktopItem.AddLayer(layers, bitmap, area, point, Anchor, Anchor, scale, depth, opacity: opacity);

            public void Dispose()
            {
                graphics.Dispose();
                bitmap.Dispose();
            }
        }
    }
}
