using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Numerics;

namespace DeskMadeline.Mac;

internal sealed class DesktopMoveBlock(int id, DesktopItemPlacement placement, SpriteBloom bloom, SoundEffects audio)
    : DesktopSolidItem(id, placement, Dimensions(placement.Kind))
{
    internal const string SoundPrefix = "event:/game/04_cliffside/arrowblock_";
    static readonly Color IdleFill = Color.FromArgb(0x47, 0x40, 0x70);
    static readonly Color MovingFill = Color.FromArgb(0x30, 0xb3, 0x35);
    static readonly Color BreakingFill = Color.FromArgb(0xcc, 0x25, 0x41);
    static readonly PType Dust = new PType
    {
        Tex = new[] { "refillParticle" }, Color = Color.Black, Size = 1f, TextureScale = true,
        LifeMin = .4f, LifeMax = .6f, SpeedMin = 20f, SpeedMax = 40f, SpeedMultiplier = .25f, LateFade = true,
    };

    readonly Bitmap tiles = Art(placement.Kind switch
    {
        DesktopItemKind.MoveBlockLeft or DesktopItemKind.MoveBlockRight => "moveBlock/base_h",
        DesktopItemKind.MoveBlockUp => "moveBlock/base_v",
        _ => "moveBlock/base",
    });
    readonly Bitmap button = Art("moveBlock/button"), broken = Art("moveBlock/x");
    readonly Bitmap[] arrows = Frames("moveBlock/arrow", 8), fragments = Frames("moveBlock/debris", 3);
    readonly List<Debris> debris = new List<Debris>();
    readonly ParticleSystem particles = new ParticleSystem();
    readonly Random random = new Random();
    Bitmap effects;
    Vector2 offset, counter, shake, impact;
    float speed, angle = HomeAngle(placement.Kind), timer, shakeTimer, crashTimer, crashReset, noSteer, flash;
    float particleRemainder;
    Color fill = IdleFill;
    bool topPressed, leftPressed, rightPressed, loopPlaying;
    Phase phase;
    enum Phase { Idle, Starting, Moving, Breaking, Hidden, ReformShake, ReformTravel }

    bool Horizontal => Placement.Kind is DesktopItemKind.MoveBlockLeft or DesktopItemKind.MoveBlockRight;
    bool Steerable => Placement.Kind != DesktopItemKind.MoveBlockDown;
    float BaseAngle => HomeAngle(Placement.Kind);
    int SteerSign => Placement.Kind == DesktopItemKind.MoveBlockLeft ? -1 : 1;
    string MoveKey => FormattableString.Invariant($"desktop.moveBlock.{Id}");
    protected override int Depth => 1;
    protected override PointF Position => new PointF(Anchor.X + offset.X, Anchor.Y + offset.Y);
    public override bool Collidable => phase != Phase.Hidden;
    bool Visible => phase < Phase.Hidden;
    public override bool Available => tiles != null && broken != null && button != null &&
        Art("refillParticle") != null && arrows.All(x => x != null) && fragments.All(x => x != null);

    // Match the A-side blocks in Golden Ridge: b-01, b-07 and c-06.
    static Size Dimensions(DesktopItemKind kind) => kind switch
    {
        DesktopItemKind.MoveBlockUp => new Size(16, 32),
        DesktopItemKind.MoveBlockDown => new Size(32, 32),
        _ => new Size(32, 16),
    };

    static float HomeAngle(DesktopItemKind kind) => kind switch
    {
        DesktopItemKind.MoveBlockLeft => MathF.PI,
        DesktopItemKind.MoveBlockUp => -MathF.PI / 2f,
        DesktopItemKind.MoveBlockDown => MathF.PI / 2f,
        _ => 0f,
    };

    public override void OnGrabbed() => StopMovingSound();

    public override bool Advance(DesktopItemFrame frame)
    {
        float dt = frame.Dt;
        Player player = frame.Player;
        if (shakeTimer > 0f)
        {
            shakeTimer -= dt;
            if (shakeTimer <= 0f) shake = Vector2.Zero;
            else if (frame.OnInterval(.04f)) shake = Shake();
        }
        flash = Math.Max(0f, flash - 5f * dt);

        switch (phase)
        {
            case Phase.Idle:
                if (player.BeingDragged || !player.IsRiding(Collider)) break;
                frame.Play(SoundPrefix + "activate");
                ActivateParticles(player);
                shakeTimer = timer = .2f;
                phase = Phase.Starting;
                break;
            case Phase.Starting:
                if (!OverInterval(ref timer, dt)) break;
                phase = Phase.Moving;
                crashTimer = .15f;
                crashReset = .1f;
                noSteer = .2f;
                goto case Phase.Moving;
            case Phase.Moving:
                StartMovingSound();
                AdvanceMotion(frame);
                break;
            case Phase.Breaking:
                if (!OverInterval(ref timer, dt)) break;
                BreakApart();
                timer = 2.2f;
                phase = Phase.Hidden;
                break;
            case Phase.Hidden:
                if (!OverInterval(ref timer, dt)) break;
                foreach (Debris piece in debris) piece.Moving = false;
                if (BlockedAt(player, Position) || (!player.IsDead && !player.IsRespawning && player.OverlapsHitbox(Collider))) break;
                frame.Play(SoundPrefix + "reform_begin");
                timer = .2f;
                phase = Phase.ReformShake;
                break;
            case Phase.ReformShake:
                if (!OverInterval(ref timer, dt)) break;
                foreach (Debris piece in debris) piece.StartReturn(random);
                timer = .6f;
                phase = Phase.ReformTravel;
                break;
            case Phase.ReformTravel:
                if (!OverInterval(ref timer, dt)) break;
                ClearDebris();
                phase = Phase.Idle;
                fill = IdleFill;
                flash = 1f;
                frame.Play(SoundPrefix + "reappear");
                break;
        }

        UpdateButtons(frame);
        Color target = phase is Phase.Starting or Phase.Moving ? MovingFill :
            phase == Phase.Idle ? IdleFill : BreakingFill;
        fill = Lerp(fill, target, Math.Min(1f, 10f * dt));
        foreach (Debris piece in debris)
            piece.Advance(frame, Anchor, SolidId, phase == Phase.ReformShake, random, audio);
        return false;
    }

    void AdvanceMotion(DesktopItemFrame frame)
    {
        float dt = frame.Dt;
        Player player = frame.Player;
        float target = BaseAngle;
        bool steer = Steerable && (Horizontal ? OnTop(player) : player.State == Player.StClimb && player.IsRiding(Collider));
        if (steer)
        {
            noSteer -= dt;
            if (noSteer <= 0f)
            {
                int input = Horizontal ? frame.Input.MoveY : frame.Input.MoveX;
                target += MathF.PI / 4f * SteerSign * input;
            }
        }
        else noSteer = .2f;

        if (frame.OnInterval(.02f)) MoveParticles();
        speed = Approach(speed, 60f, 300f * dt);
        angle = Approach(angle, target, MathF.PI * 16f * dt);
        Vector2 velocity = new Vector2(MathF.Cos(angle), MathF.Sin(angle)) * speed;
        PointF lift = new PointF(velocity.X, velocity.Y);
        bool hit = MoveAxis(player, Horizontal ? velocity.X * dt : velocity.Y * dt, Horizontal, lift, primary: true);
        MoveAxis(player, Horizontal ? velocity.Y * dt : velocity.X * dt, !Horizontal, lift, primary: false);

        audio?.SetLoopParameter(MoveKey, "arrow_stop", hit ? 1f : 0f);
        float reflected = MathF.Atan2(velocity.Y, -velocity.X);
        audio?.SetLoopParameter(MoveKey, "arrow_influence", (int)MathF.Floor((MathF.Tau - reflected) % MathF.Tau / MathF.Tau * 8f + .5f) + 1);
        if (hit)
        {
            crashReset = .1f;
            if (crashTimer <= 0f)
            {
                frame.Play(SoundPrefix + "break");
                StopMovingSound();
                speed = 0f;
                angle = BaseAngle;
                timer = shakeTimer = .2f;
                phase = Phase.Breaking;
            }
            else crashTimer -= dt;
        }
        else if (crashReset > 0f) crashReset -= dt;
        else crashTimer = .15f;
    }

    bool MoveAxis(Player player, float movement, bool horizontal, PointF lift, bool primary)
    {
        float accumulated = movement + (horizontal ? counter.X : counter.Y);
        int amount = (int)MathF.Round(accumulated);
        if (horizontal) counter.X = accumulated - amount;
        else counter.Y = accumulated - amount;
        int direction = Math.Sign(amount);
        for (int i = 0; i < Math.Abs(amount); i++)
        {
            PointF previous = Position;
            PointF next = horizontal ? new PointF(previous.X + direction, previous.Y)
                : new PointF(previous.X, previous.Y + direction);
            if (!BlockedAt(player, next) && (primary || CanPushSideways(player, next, horizontal, direction)))
            {
                SetPosition(player, next, lift);
                continue;
            }
            if (horizontal) counter.X = 0f;
            else counter.Y = 0f;
            if (!primary) return true;
            for (int offset = 1; offset <= 3; offset++)
            for (int side = 1; side >= -1; side -= 2)
            {
                PointF shifted = horizontal ? new PointF(next.X, next.Y + offset * side)
                    : new PointF(next.X + offset * side, next.Y);
                if (BlockedAt(player, shifted)) continue;
                PointF corrected = horizontal ? new PointF(previous.X, shifted.Y) : new PointF(shifted.X, previous.Y);
                SetPosition(player, corrected, lift);
                SetPosition(player, shifted, lift);
                return false;
            }
            return true;
        }
        return false;
    }

    bool CanPushSideways(Player player, PointF next, bool horizontal, int direction)
    {
        if (player.IsDead || player.IsRespawning || player.BeingDragged || !player.OverlapsHitbox(BlockAt(next))) return true;
        PointF pos = Position;
        if (horizontal && Math.Sign(player.Pos.X - pos.X) != direction) return true;
        if (!horizontal && (direction >= 0 || player.Pos.Y > pos.Y)) return true;
        player.GetHitbox(out float l, out float t, out float r, out float b);
        float dx = horizontal ? direction : 0, dy = horizontal ? 0 : direction;
        foreach (Solid solid in player.Solids)
            if (solid.Id != SolidId && l + dx < solid.R && r + dx > solid.L && t + dy < solid.B && b + dy > solid.T)
                return false;
        return true;
    }

    void SetPosition(Player player, PointF next, PointF lift)
    {
        PointF old = Position;
        offset = new Vector2(next.X - Anchor.X, next.Y - Anchor.Y);
        MoveSolid(player, old, next, lift);
    }

    bool OnTop(Player player)
    {
        Solid solid = Collider;
        return !player.IsDead && !player.IsRespawning && !player.BeingDragged &&
            player.CollisionBottom <= solid.T && player.CollisionBottom + 1f > solid.T &&
            player.Pos.X - 4f < solid.R && player.Pos.X + 4f > solid.L;
    }

    void UpdateButtons(DesktopItemFrame frame)
    {
        Player p = frame.Player;
        PointF pos = Position;
        bool touch = Steerable && Collidable && !p.IsDead && !p.IsRespawning && !p.BeingDragged;
        bool top = touch && Horizontal && p.OverlapsHitbox(BlockAt(new PointF(pos.X, pos.Y - 1)));
        bool left = touch && !Horizontal && p.OverlapsHitbox(BlockAt(new PointF(pos.X - 1, pos.Y)));
        bool right = touch && !Horizontal && p.OverlapsHitbox(BlockAt(new PointF(pos.X + 1, pos.Y)));
        if ((top && !topPressed) || (left && !leftPressed) || (right && !rightPressed))
            frame.Play(SoundPrefix + "side_depress");
        if ((!top && topPressed) || (!left && leftPressed) || (!right && rightPressed))
            frame.Play(SoundPrefix + "side_release");
        (topPressed, leftPressed, rightPressed) = (top, left, right);
    }

    void ActivateParticles(Player player)
    {
        PointF pos = Position;
        float halfW = Width / 2f, halfH = Height / 2f;
        if ((!Steerable || Horizontal) && !player.OverlapsHitbox(BlockAt(new PointF(pos.X - 1, pos.Y))))
            Emit(offset.X - halfW, offset.Y, MathF.PI, Height / 2, 0f, halfH - 2f);
        if ((!Steerable || Horizontal) && !player.OverlapsHitbox(BlockAt(new PointF(pos.X + 1, pos.Y))))
            Emit(offset.X + halfW, offset.Y, 0f, Height / 2, 0f, halfH - 2f);
        if ((!Steerable || !Horizontal) && !player.OverlapsHitbox(BlockAt(new PointF(pos.X, pos.Y - 1))))
            Emit(offset.X, offset.Y - halfH, -MathF.PI / 2f, Width / 2, halfW - 2f, 0f);
        Emit(offset.X, offset.Y + halfH, MathF.PI / 2f, Width / 2, halfW - 2f, 0f);
    }

    void MoveParticles()
    {
        particleRemainder += (Horizontal ? Height : Width) / 32f;
        int count = (int)particleRemainder;
        particleRemainder -= count;
        if (count == 0) return;
        switch (Placement.Kind)
        {
            case DesktopItemKind.MoveBlockRight:
                Emit(offset.X - Width / 2f + 1f, offset.Y, MathF.PI, count, 0f, Height / 2f - 2f);
                break;
            case DesktopItemKind.MoveBlockLeft:
                Emit(offset.X + Width / 2f, offset.Y, 0f, count, 0f, Height / 2f - 2f);
                break;
            case DesktopItemKind.MoveBlockUp:
                Emit(offset.X, offset.Y + Height / 2f, MathF.PI / 2f, count, Width / 2f - 2f, 0f);
                break;
            default:
                Emit(offset.X, offset.Y - Height / 2f + 1f, -MathF.PI / 2f, count, Width / 2f - 2f, 0f);
                break;
        }
    }

    void Emit(float x, float y, float angle, int count, float spreadX, float spreadY)
        => particles.Emit(Dust, x, y, angle, .34906584f / 2f, count, spreadX, spreadY);

    void BreakApart()
    {
        impact = offset;
        for (int x = 2; x < Width; x += 4)
        for (int y = 2; y < Height; y += 4)
        {
            Vector2 direction = new Vector2(x - Width / 2f, y - Height / 2f);
            Emit(offset.X + direction.X, offset.Y + direction.Y, MathF.Atan2(direction.Y, direction.X), 1, 2f, 2f);
        }
        for (int x = 4; x < Width; x += 8)
        for (int y = 4; y < Height; y += 8)
        {
            Vector2 home = new Vector2(x - Width / 2f, y - Height / 2f);
            Vector2 speed = Vector2.Normalize(home) * (60f + 60f * random.NextSingle());
            debris.Add(new Debris(fragments[random.Next(fragments.Length)], offset + home, home, speed, random));
        }
        offset = counter = Vector2.Zero;
    }

    public override void AfterFrame(float dt) => particles.Update(dt);

    protected override Rectangle Paint()
    {
        if (!Visible) return Rectangle.Empty;
        float left = Center - Width / 2f + shake.X, top = Center - Height / 2f + shake.Y;
        Rectangle area;
        using (var g = new Graphics(Body))
        {
            g.Clear();
            g.FillRectangle(Color.Black, left - 1, top - 1, Width + 2, Height + 2);
            if (Steerable) DrawButtons(g, left, top);
            g.FillRectangle(fill, left + 3, top + 3, Width - 6, Height - 6);
            DrawFrameTiles(g, tiles, left, top, interior: true);
            float cx = left + Width / 2f, cy = top + Height / 2f;
            g.FillRectangle(fill, cx - 4, cy - 4, 8, 8);
            int arrow = Math.Clamp((int)MathF.Floor((MathF.Tau - angle) % MathF.Tau / MathF.Tau * 8f + .5f), 0, 7);
            DrawCentered(g, phase == Phase.Breaking ? broken : arrows[arrow], cx, cy);
            if (flash > 0f)
            {
                float grow = 4f * flash;
                g.FillRectangle(Color.FromArgb((int)(255 * flash), Color.White),
                    left - grow, top - grow, Width + 2f * grow, Height + 2f * grow);
            }
            area = g.Dirty;
        }
        return bloom.Apply(Body, area, Center, Center, 0f, ambient: .25f);
    }

    void DrawButtons(Graphics g, float left, float top)
    {
        int count = (Horizontal ? Width : Height) / 8;
        for (int i = 0; i < count; i++)
        {
            int tile = i == 0 ? 0 : i == count - 1 ? 2 : 1;
            var src = new Rectangle(tile * 8, 0, 8, 8);
            if (Horizontal)
                g.DrawRegion(button, src, left + i * 8, top - (topPressed ? 2f : 4f), tint: fill);
            else
            {
                DrawSide(left + (leftPressed ? 2 : 0), mirror: true);
                DrawSide(left + Width - (rightPressed ? 2 : 0), mirror: false);
            }

            void DrawSide(float x, bool mirror)
            {
                var saved = g.Save();
                g.TranslateTransform(x, top + i * 8 + 4);
                g.RotateTransform(90);
                if (mirror) g.ScaleTransform(1, -1);
                g.DrawRegion(button, src, -4, -4, tint: fill);
                g.Restore(saved);
            }
        }
    }

    public override void Render(List<Native.ItemLayer> layers, int scale)
    {
        base.Render(layers, scale);
        foreach (Debris piece in debris)
        {
            Rectangle area = piece.Paint();
            var world = new PointF(Anchor.X + piece.Position.X, Anchor.Y + piece.Position.Y);
            AddLayer(layers, piece.Image, area, world, 8, 8, scale, Depth, Id);
        }
        if (particles.Count == 0) return;
        effects ??= new Bitmap(144, 144);
        Vector2 origin = Visible ? offset : impact;
        using var g = new Graphics(effects);
        g.Clear();
        particles.Draw(g, origin.X - 72, origin.Y - 72, Anchor.X, Anchor.Y);
        AddLayer(layers, effects, g.Dirty, new PointF(Anchor.X + origin.X, Anchor.Y + origin.Y), 72, 72, scale, -2);
    }

    void StartMovingSound()
    {
        if (loopPlaying) return;
        loopPlaying = true;
        audio?.PlayKept(MoveKey, SoundPrefix + "move");
        audio?.SetLoopParameter(MoveKey, "arrow_stop", 0f);
    }

    void StopMovingSound()
    {
        if (!loopPlaying) return;
        loopPlaying = false;
        audio?.StopLoop(MoveKey);
    }

    Vector2 Shake() => new Vector2(random.Next(-1, 2), random.Next(-1, 2));
    static Color Lerp(Color from, Color to, float t) => Color.FromArgb(
        (int)(from.R + (to.R - from.R) * t), (int)(from.G + (to.G - from.G) * t), (int)(from.B + (to.B - from.B) * t));

    void ClearDebris()
    {
        foreach (Debris piece in debris) piece.Image.Dispose();
        debris.Clear();
    }

    public override void Dispose()
    {
        StopMovingSound();
        ClearDebris();
        effects?.Dispose();
        base.Dispose();
    }

    sealed class Debris
    {
        readonly Bitmap sprite;
        readonly Vector2 home;
        readonly float spin;
        Vector2 speed, shake, from, control, counter;
        float angle, returning = -1;
        bool firstHit;
        public Vector2 Position;
        public bool Moving = true;
        public readonly Bitmap Image = new Bitmap(16, 16);

        public Debris(Bitmap sprite, Vector2 position, Vector2 home, Vector2 speed, Random random)
        {
            this.sprite = sprite;
            this.home = home;
            this.speed = speed;
            Position = position;
            angle = random.NextSingle() * MathF.Tau;
            spin = (3.4906585f + random.NextSingle() * (10.471975f - 3.4906585f)) * (random.Next(2) == 0 ? -1f : 1f);
        }

        public void StartReturn(Random random)
        {
            returning = 0f;
            from = Position;
            Vector2 toward = home - from;
            var direction = toward == Vector2.Zero ? Vector2.Zero : Vector2.Normalize(toward);
            control = (home + from) / 2f + new Vector2(direction.Y, -direction.X) *
                ((16f + random.NextSingle() * 16f) * (random.Next(2) == 0 ? -1f : 1f));
        }

        public void Advance(DesktopItemFrame frame, PointF origin, IntPtr block, bool shaking, Random rng, SoundEffects audio)
        {
            float dt = frame.Dt;
            if (returning >= 0f)
            {
                float ease = 1f - MathF.Pow(1f - returning, 3f);
                Position = Vector2.Lerp(Vector2.Lerp(from, control, ease), Vector2.Lerp(control, home, ease), ease);
                returning = Math.Min(1f, returning + dt / .65f);
            }
            else if (Moving)
            {
                speed.X = Approach(speed.X, 0f, dt * 100f);
                if (!Blocked(Position + Vector2.UnitY)) speed.Y += 400f * dt;
                counter += speed * dt;
                int dx = (int)MathF.Round(counter.X), dy = (int)MathF.Round(counter.Y);
                counter -= new Vector2(dx, dy);
                if (Step(dx, Vector2.UnitX)) { speed.X *= -.5f; counter.X = 0f; }
                if (Step(dy, Vector2.UnitY))
                {
                    if (firstHit || speed.Y > 50f)
                        audio?.Play("event:/game/general/debris_stone", "debris_velocity", Math.Clamp(speed.Y / 600f, 0f, 1f));
                    speed.Y = speed.Y is > 0f and < 40f ? 0f : speed.Y * -.25f;
                    counter.Y = 0f;
                    firstHit = false;
                }
            }
            if (shaking && frame.OnInterval(.05f)) shake = new Vector2(rng.Next(-1, 2), rng.Next(-1, 2));
            angle += spin * Math.Clamp((MathF.Abs(speed.Y) - 50f) / 100f, 0f, 1f) * dt;

            bool Step(int count, Vector2 axis)
            {
                Vector2 shift = axis * Math.Sign(count);
                for (int i = 0; i < Math.Abs(count); i++)
                {
                    if (Blocked(Position + shift)) return true;
                    Position += shift;
                }
                return false;
            }

            bool Blocked(Vector2 candidate)
            {
                float x = origin.X + candidate.X, y = origin.Y + candidate.Y;
                foreach (Solid solid in frame.Player.Solids)
                    if (solid.Id != block && x - 2f < solid.R && x + 2f > solid.L &&
                        y - 2f < solid.B && y + 2f > solid.T) return true;
                return false;
            }
        }

        public Rectangle Paint()
        {
            using var g = new Graphics(Image);
            g.Clear();
            g.TranslateTransform(8f + shake.X, 8f + shake.Y);
            g.RotateTransform(angle * 180f / MathF.PI);
            float scale = returning >= 0f ? 1f + returning * .5f : 1f;
            DrawCentered(g, sprite, 0f, 0f, scale, scale);
            return g.Dirty;
        }
    }
}
