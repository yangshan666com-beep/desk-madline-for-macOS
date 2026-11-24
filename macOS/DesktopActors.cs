using System;
using System.Collections.Generic;
using System.Drawing;

namespace DeskMadeline.Mac;

internal static class DesktopActorSounds
{
    internal static void Drain(Queue<PlayerSoundEvent> events, DesktopItemFrame frame, SoundEffects audio)
    {
        while (events.TryDequeue(out PlayerSoundEvent sound))
        {
            if (audio == null) frame.Play(sound.Path);
            else audio.Play(sound.Path, sound.Parameter, sound.Value);
        }
    }
}

internal sealed class DesktopBumper(int id, DesktopItemPlacement placement, SpriteBloom bloom, SoundEffects audio)
    : DesktopItemWithEffects(id, placement, 80)
{
    // Preserve the original sine offset across replays of the same placement.
    readonly Bumper actor = new Bumper(PointF.Empty, (placement.X * .11f + placement.Y * .07f) % (2f * MathF.Tau));
    static readonly PType Ambient = new PType
    {
        Tex = new[] { "rect" }, Color = Color.FromArgb(0x47, 0xb5, 0xcc),
        Color2 = Color.FromArgb(0xc4, 0xf4, 0xff), BlinkColor = true, FadeInAndOut = true,
        Size = .5f, SizeRange = .1f, TextureScale = true, RotateAlongDirection = true,
        LifeMin = .2f, LifeMax = .4f, SpeedMin = 10f, SpeedMax = 20f,
    };
    static readonly PType Launch = new PType
    {
        Tex = new[] { "rect" }, Color = Color.FromArgb(0x47, 0xb5, 0xcc),
        Color2 = Color.FromArgb(0xc4, 0xf4, 0xff), BlinkColor = true, LateFade = true,
        Size = .5f, SizeRange = .1f, TextureScale = true, RandomRotation = true,
        LifeMin = .6f, LifeMax = 1.2f, SpeedMin = 40f, SpeedMax = 140f,
        SpeedMultiplier = .1f, GravY = 10f,
    };
    public override bool Available => Art("bumper/idle00") != null && Art("rect") != null;
    internal int ParticleCount => Particles.Count;
    protected override PointF Position => Held ? Anchor : actor.Pos;

    public override void Prepare(DesktopTerrain terrain, int scale) => actor.Anchor = Anchor;

    public override bool Advance(DesktopItemFrame frame)
    {
        int before = actor.Hits;
        PointF previous = actor.Pos;
        actor.Update(frame.Dt, frame.Player);
        DesktopActorSounds.Drain(actor.SoundEvents, frame, audio);
        float x = previous.X - Anchor.X, y = previous.Y - Anchor.Y;
        for (int i = 0; i < actor.AmbientPuffs; i++)
        {
            float angle = Random.Shared.NextSingle() * MathF.Tau;
            Particles.Emit(Ambient, x + MathF.Cos(angle) * 8f, y + MathF.Sin(angle) * 8f, angle, MathF.PI / 12f, 1);
        }
        if (actor.Hits != before)
        {
            var dir = actor.LaunchDirection;
            Particles.Emit(Launch, actor.HitPosition.X - Anchor.X + dir.X * 12f, actor.HitPosition.Y - Anchor.Y + dir.Y * 12f,
                MathF.Atan2(dir.Y, dir.X), .6981317f / 2f, 12, 3f, 3f);
        }
        return false;
    }

    protected override Rectangle Paint()
    {
        Rectangle area;
        using (var g = new Graphics(Body))
        {
            g.Clear();
            DrawCentered(g, Art(actor.FrameId), Center, Center);
            area = g.Dirty;
        }
        // The point light fades out during the original .6-second recovery.
        return bloom.Apply(Body, area, Center, Center, actor.Ready ? 1f : 0f, radius: 16, opacity: .5f, ambient: .2f);
    }
}

internal sealed class DesktopPuffer(int id, DesktopItemPlacement placement, SpriteBloom bloom, SoundEffects audio)
    : DesktopItemWithEffects(id, placement, 128)
{
    Puffer actor = new Puffer(PointF.Empty);
    PointF home;
    bool initialized;
    float worldBottom;
    readonly PointF[] arcAt = new PointF[28], arcIn = new PointF[28];
    readonly float[] arcAlpha = new float[28];
    static readonly PType Blast = new PType
    {
        Tex = new[] { "refillParticle" }, Color = Color.FromArgb(0xff, 0x72, 0x66),
        Color2 = Color.FromArgb(0xff, 0xcd, 0x82), BlinkColor = true, LateFade = true,
        LifeMin = .3f, LifeMax = .55f, SpeedMin = 20f, SpeedMax = 40f, SpeedMultiplier = .2f, TextureScale = true,
    };
    public override bool Available => Art("puffer/idle00") != null;
    protected override PointF Position => Held ? Anchor : actor.Pos;
    protected override PointF EffectView => actor.Pos;

    public override void Prepare(DesktopTerrain terrain, int scale)
    {
        if (!initialized || (home != Anchor && !Held && !actor.BeingDragged))
        {
            actor = new Puffer(Anchor);
            home = Anchor;
            initialized = true;
        }
        if (Held)
        {
            actor.DragTo(Anchor);
            home = Anchor;
        }
        else if (actor.BeingDragged) actor.EndDrag();
        var monitor = terrain.NearestMonitor(Anchor);
        worldBottom = monitor.IsEmpty ? float.PositiveInfinity : monitor.Bottom;
    }

    public override void OnGrabbed() => actor.BeginDrag();
    public override DesktopItemPlacement PlacementWhileHeld(int scale) =>
        Placement with { X = actor.Pos.X * scale, Y = actor.Pos.Y * scale };

    public override bool Advance(DesktopItemFrame frame)
    {
        actor.Update(frame.Dt, frame.Player, frame.Player.Solids, Array.Empty<TheoCrystal>(), worldBottom);
        DesktopActorSounds.Drain(actor.SoundEvents, frame, audio);
        if (actor.Explosions > 0)
        {
            for (float angle = 0f; angle < MathF.Tau; angle += .17453292f)
            {
                float away = 12f + Random.Shared.NextSingle() * 6f;
                Particles.Emit(Blast, actor.Pos.X - Anchor.X + MathF.Cos(angle) * away,
                    actor.Pos.Y - Anchor.Y + MathF.Sin(angle) * away, angle, .03490659f, 1);
            }
        }
        // In Celeste, leaving the bottom removes the actor until room reload.
        if (actor.Removed) actor = new Puffer(Anchor);
        return false;
    }

    protected override Rectangle Paint()
    {
        Rectangle area;
        using (var g = new Graphics(Body))
        {
            g.Clear();
            Bitmap image = Art(actor.FrameId);
            if (image != null)
            {
                var state = g.Save();
                g.TranslateTransform(Center, Center);
                g.RotateTransform(actor.Rotation * 180f / MathF.PI);
                g.ScaleTransform(actor.Facing, 1f);
                if (actor.Outlined) DrawOutline(g, image, 0, 0, actor.Scale.X, actor.Scale.Y);
                DrawCentered(g, image, 0, 0, actor.Scale.X, actor.Scale.Y);
                g.Restore(state);
            }
            if (!Held)
            {
                int marks = actor.AggroArc(arcAt, arcIn, arcAlpha);
                int x = (int)MathF.Round(actor.Pos.X), y = (int)MathF.Round(actor.Pos.Y);
                for (int i = 0; i < marks; i++)
                {
                    int alpha = (int)MathF.Round(Math.Clamp(arcAlpha[i], 0f, 1f) * 255f);
                    if (alpha <= 0) continue;
                    Color color = Color.FromArgb(alpha, Color.White);
                    float ax = Center + MathF.Floor(arcAt[i].X) - x;
                    float ay = Center + MathF.Floor(arcAt[i].Y) - y;
                    g.FillRectangle(color, ax, ay, 1f, 1f);
                    if (!arcIn[i].IsEmpty)
                        g.FillRectangle(color, MathF.Round(ax - arcIn[i].X), MathF.Round(ay - arcIn[i].Y), 1f, 1f);
                }
                if (actor.HasEye)
                    g.FillRectangle(Color.Black, Center + MathF.Floor(actor.Eye.X) - x, Center + MathF.Floor(actor.Eye.Y) - y, 1f, 1f);
            }
            area = g.Dirty;
        }
        return bloom.Apply(Body, area, Center, Center, 0f, ambient: .5f);
    }
}

internal sealed class DesktopJellyfish(int id, DesktopItemPlacement placement, SpriteBloom bloom, SoundEffects audio)
    : DesktopItem(id, placement, 72)
{
    public Glider Actor { get; } = new Glider(PointF.Empty);
    PointF home;
    bool initialized;
    RectangleF monitor;
    public override bool Available => Art("glider/idle0") != null;
    protected override PointF Position => Held ? Anchor : Actor.Pos;

    public override void Prepare(DesktopTerrain terrain, int scale)
    {
        monitor = terrain.NearestMonitor(Anchor);
        if (!initialized || home != Anchor)
        {
            if (!Actor.IsHeld)
            {
                if (Held) Actor.DragTo(Anchor);
                else Actor.SnapIntoView(Anchor);
            }
            home = Anchor;
            initialized = true;
        }
        if (!Held && Actor.BeingDragged) Actor.EndDrag(PointF.Empty);
    }

    public override void OnGrabbed()
    {
        Actor.BeginDrag(Actor.Holder);
        audio?.StopLoop(Actor);
    }

    public override DesktopItemPlacement PlacementWhileHeld(int scale) =>
        Placement with { X = Actor.Pos.X * scale, Y = Actor.Pos.Y * scale };

    public override bool Advance(DesktopItemFrame frame)
    {
        var player = frame.Player;
        Actor.Update(frame.Dt, frame.Input, player.Solids, player.MinX, player.MaxX);
        DesktopActorSounds.Drain(Actor.SoundEvents, frame, audio);
        if (Actor.MovementSoundActive)
        {
            audio?.StartLoop(Actor, "event:/new_content/game/10_farewell/glider_movement");
            audio?.SetLoopParameter(Actor, "glider_speed", Actor.MovementSoundSpeed);
        }
        else audio?.StopLoop(Actor);
        if (!Actor.IsHeld && !monitor.IsEmpty && Actor.Pos.Y > monitor.Bottom + 16f)
            Actor.SnapIntoView(Anchor);
        return false;
    }

    protected override Rectangle Paint()
    {
        Rectangle area;
        using (var g = new Graphics(Body))
        {
            g.Clear();
            Bitmap image = Art(Actor.FrameId);
            var state = g.Save();
            g.TranslateTransform(Center, Center);
            g.RotateTransform(Actor.Rotation * 180f / MathF.PI);
            DrawOutline(g, image, 0f, -3.84f * Actor.ScaleY, Actor.ScaleX, Actor.ScaleY);
            DrawCentered(g, image, 0f, -3.84f * Actor.ScaleY, Actor.ScaleX, Actor.ScaleY);
            g.Restore(state);
            area = g.Dirty;
        }
        return bloom.Apply(Body, area, Center, Center, 0f, ambient: .5f);
    }

    public override void Dispose()
    {
        Actor.Holder?.ReleaseHoldableForDrag(Actor);
        audio?.StopLoop(Actor);
        base.Dispose();
    }
}
