using System;
using System.Collections.Generic;
using System.Drawing;
using DeskMadeline.Mac;
using SkiaSharp;

namespace DeskMadeline;

/// <summary>Player presentation from the desktop port, drawn in game pixels before AppKit scales it.</summary>
internal sealed class PetEffects : IDisposable
{
    const int CanvasSize = 1024, Anchor = CanvasSize / 2;
    const int ActorSize = 64, ActorAnchor = ActorSize / 2;
    const int TrailSize = 64, SlashSize = 32;
    readonly Bitmap canvas = new Bitmap(CanvasSize, CanvasSize);
    readonly Bitmap actor = new Bitmap(ActorSize, ActorSize);
    readonly Graphics sceneGraphics, actorGraphics;
    Rectangle lastDraw, lastActorDraw;
    readonly Player player;
    readonly ParticleSystem particles = new ParticleSystem();
    readonly GameSprite sweat = new GameSprite();
    readonly Random random = new Random();
    readonly List<Trail> trails = new List<Trail>();
    readonly List<Slash> slashes = new List<Slash>();
    readonly List<WaveRing> waveRings = new List<WaveRing>();
    readonly Dictionary<(int Frame, float Angle), Bitmap> slashFrames = new Dictionary<(int, float), Bitmap>();
    readonly List<string> bangs;
    int dashSequence, jumpCount, wallJumpCount, landingCount, pushDustCount, sweatSequence;
    int launchCount, ringDashSequence, dashTrailStage;
    float flashClock, dashParticleClock, skidClock, dashTrailClock, dreamTrailClock;
    float launchClock, nextRing;
    bool tiredFlash, dashPending, launchActive;

    sealed record Trail(Bitmap Image, PointF At)
    {
        public float Age;
    }

    struct Slash { public PointF Position; public float Angle, Age; }
    struct WaveRing { public float X, Y, Angle, Progress; }

    readonly PType dust = new PType
    {
        Tex = new[] { "smoke0", "smoke1", "smoke2", "smoke3" },
        Color = Color.White, GravY = 4f, LifeMin = .3f, LifeMax = .5f,
        Size = 5.6f, SizeRange = 1.6f, SpeedMin = 5f, SpeedMax = 15f, ScaleOut = true,
    };

    static readonly PType dashBlue = DashParticle(0x44b7ff, 0x75c9ff);
    static readonly PType dashRed = DashParticle(0xac3232, 0xe05959);

    static PType DashParticle(int start, int end) => new PType
    {
        Tex = new[] { "dashParticle" },
        Color = Color.FromArgb(start | unchecked((int)0xff000000)),
        Color2 = Color.FromArgb(end | unchecked((int)0xff000000)),
        BlinkColor = true, GravY = 8f, LifeMin = 1f, LifeMax = 1.8f, Size = 1f,
        SpeedMin = 10f, SpeedMax = 20f, LateFade = true,
    };

    public PetEffects(Player player, SpriteBank bank)
    {
        this.player = player;
        sceneGraphics = new Graphics(canvas);
        actorGraphics = new Graphics(actor);
        player.OnStarFlyParticles = EmitStarFlyParticles;
        bank.CreateOn(sweat, "player_sweat");
        bangs = AtlasLookup.Subtextures(Sprites.Atlas, "characters/player/bangs");
        Reset();
    }

    void EmitStarFlyParticles(StarFlyParticle kind, PointF origin, float direction)
    {
        if (kind == StarFlyParticle.Boost)
            particles.Emit(DesktopFeather.Boost, origin.X, origin.Y, direction, 1.3962634f / 2f, 12, 4f, 4f);
        else
            particles.Emit(DesktopFeather.Flying, origin.X, origin.Y, direction, MathF.PI / 6f, 1, 2f, 2f);
    }

    public void Reset()
    {
        foreach (Trail trail in trails) trail.Image.Dispose();
        trails.Clear();
        slashes.Clear();
        waveRings.Clear();
        particles.Clear();
        dashSequence = ringDashSequence = player.DashSequenceCount;
        jumpCount = player.JumpEffectCount;
        wallJumpCount = player.WallJumpEffectCount;
        landingCount = player.LandingEffectCount;
        pushDustCount = player.PushDustCount;
        sweatSequence = player.SweatAnimSequenceCount;
        launchCount = player.LaunchCount;
        dashPending = launchActive = false;
        dashTrailStage = 0;
        dashTrailClock = dashParticleClock = skidClock = dreamTrailClock = 0f;
    }

    // Exercise the first jump, dash and trail draw on the loading screen so
    // their first-use runtime and graphics work cannot stall the live player.
    internal static void WarmUp(SpriteBank bank)
    {
        const float step = 1f / 60f;
        var preview = new Player
        {
            Solids = new List<Solid> { new Solid { L = -128, T = 0, R = 128, B = 8 } },
        };
        preview.ResetSprite(bank, "player", Player.ModeMadeline, null);
        preview.ResetTo(PointF.Empty);
        using var effects = new PetEffects(preview, bank);
        for (int frame = 0; frame < 10; frame++)
        {
            if (frame == 1) preview.BufferJump();
            if (frame == 4) preview.BufferDash();
            bool frozen = preview.IsHitStopped;
            if (!frozen) effects.UpdateComponents(step);
            int previous = preview.State;
            preview.Update(step, new PetInput
            {
                MoveX = 1, AimX = 1, JumpHeld = frame is >= 1 and < 4,
                JumpPressed = preview.HasJumpBuffer, DashPressed = preview.HasDashBuffer,
            });
            effects.AfterPlayer(step, previous, frozen);
            effects.Render(false);
        }
    }

    public void UpdateComponents(float dt)
    {
        player.Sprite.Update(dt);
        sweat.Update(dt);
        EmitAnimationParticles();
        flashClock += dt;
        while (flashClock >= .05f)
        {
            flashClock -= .05f;
            tiredFlash = !tiredFlash;
        }
    }

    public void AfterPlayer(float dt, int previousState, bool frozenAtStart)
    {
        if (!frozenAtStart)
        {
            bool restart = sweatSequence != player.SweatAnimSequenceCount;
            sweatSequence = player.SweatAnimSequenceCount;
            if (sweat.Has(player.SweatAnimId)) sweat.Play(player.SweatAnimId, restart);
            player.UpdateCarryPosition(HairMeta.CarryYOffset(player.CurrentFrameId) * player.SpriteScaleY);
            EmitAnimationParticles();
            UpdateHair(dt);
        }

        if (frozenAtStart || (player.FreezeFramesEnabled && player.IsHitStopped))
        {
            UpdateDash(0f);
            return;
        }

        UpdateWavedashWaves(dt);
        EmitMovementParticles(dt, previousState);
        particles.Update(dt);
        UpdateDash(dt);
    }

    public void UpdateHair(float dt)
    {
        HairMeta.TryGet(player.CurrentFrameId, out HairMeta.Meta meta);
        player.UpdateHairOnly(dt, meta.Offset.X, meta.Offset.Y);
    }

    void UpdateDash(float dt)
    {
        for (int i = trails.Count - 1; i >= 0; i--)
        {
            trails[i].Age += dt;
            if (trails[i].Age < 1f) continue;
            trails[i].Image.Dispose();
            trails.RemoveAt(i);
        }
        for (int i = slashes.Count - 1; i >= 0; i--)
        {
            Slash slash = slashes[i];
            slash.Age += dt;
            slash.Position.X += MathF.Cos(slash.Angle) * 8f * dt;
            slash.Position.Y += MathF.Sin(slash.Angle) * 8f * dt;
            if (slash.Age >= .4f) slashes.RemoveAt(i);
            else slashes[i] = slash;
        }

        if (dashSequence != player.DashSequenceCount)
        {
            dashSequence = player.DashSequenceCount;
            dashPending = true;
            dashTrailStage = 0;
        }

        // DashCoroutine yields a frame after DashBegin and reads aim after hitstop.
        // A super taken before that point never creates a slash or an initial snapshot.
        if (dashPending && !player.IsFrozen)
        {
            dashPending = false;
            if (!player.DashDir.IsEmpty)
            {
                slashes.Add(new Slash { Position = player.Center,
                    Angle = MathF.Atan2(player.DashDir.Y, player.DashDir.X) });
                CaptureTrail();
                dashTrailStage = 1;
                dashTrailClock = 0f;
                return;
            }
        }

        if (dashTrailStage != 0)
        {
            dashTrailClock += dt;
            bool super = player.SuperDashing;
            if (player.State != Player.StDash)
            {
                // The wait has to expire for the coroutine's final snapshot. An early
                // jump or collision replaced the coroutine.
                if (dashTrailClock >= (super ? .3f : .15f) && !player.IsDead)
                    CaptureTrail();
                dashTrailStage = 0;
            }
            else if (dashTrailStage == 1 && dashTrailClock >= (super ? .1f : .08f))
            {
                CaptureTrail();
                dashTrailStage = super ? 2 : 3;
            }
            else if (dashTrailStage == 2 && dashTrailClock >= .2f)
            {
                CaptureTrail();
                dashTrailStage = 3;
            }
        }

        if (player.State == Player.StDreamDash && dt > 0)
        {
            dreamTrailClock -= dt;
            if (dreamTrailClock <= 0f)
            {
                CaptureTrail();
                dreamTrailClock += .1f;
            }
        }
        else if (player.State != Player.StDreamDash)
            dreamTrailClock = 0f;
    }

    void CaptureTrail()
    {
        const float center = TrailSize / 2f;
        using var snapshot = new Bitmap(TrailSize, TrailSize);
        using (var graphics = new Graphics(snapshot))
        {
            DrawHair(graphics, player, bangs, player.Pos.X - center, player.Pos.Y - center);
            DrawBody(graphics, player, center, center, Color.White);
        }
        var mask = new Bitmap(TrailSize, TrailSize);
        using (var graphics = new Graphics(mask))
            Sprites.DrawSilhouette(graphics, snapshot, player.TrailColor(player.LastDashWasTwo), 0, 0, TrailSize, TrailSize);
        mask.Freeze();
        trails.Add(new Trail(mask, player.Pos));
    }

    void EmitMovementParticles(float dt, int previousState)
    {
        const float up = -MathF.PI / 2;
        if (wallJumpCount != player.WallJumpEffectCount)
        {
            wallJumpCount = player.WallJumpEffectCount;
            int direction = player.LastWallJumpDirection;
            float angle = direction < 0 ? -3 * MathF.PI / 4 : -MathF.PI / 4;
            EmitDust(player.Pos.X - direction * 2f, player.Pos.Y - 5.5f, angle, 4);
        }
        if (jumpCount != player.JumpEffectCount)
        {
            jumpCount = player.JumpEffectCount;
            EmitDust(player.Pos.X, player.Pos.Y, up, 4);
        }
        if (landingCount != player.LandingEffectCount)
        {
            landingCount = player.LandingEffectCount;
            EmitDust(player.Pos.X, player.Pos.Y, up, 8);
        }
        if (player.WallSlideDustActive)
            EmitDust(player.Pos.X + player.WallSlideDirection * 5f, player.Pos.Y - 1.5f, up, 1);
        if (player.IsDashAttacking && player.AnimId == "skid")
        {
            skidClock += dt;
            while (skidClock >= .02f)
            {
                skidClock -= .02f;
                EmitDust(player.Pos.X, player.Pos.Y, up, 1);
            }
        }
        else skidClock = 0f;

        if (player.State != Player.StDash)
        {
            dashParticleClock = 0f;
            return;
        }
        if (previousState != Player.StDash) dashParticleClock = 0f;
        dashParticleClock += dt;
        while (dashParticleClock >= .02f && !player.Speed.IsEmpty)
        {
            dashParticleClock -= .02f;
            float x = player.Center.X + (float)(random.NextDouble() * 4 - 2);
            float y = player.Center.Y + (float)(random.NextDouble() * 4 - 2);
            particles.Emit(player.LastDashWasTwo ? dashRed : dashBlue, x, y,
                MathF.Atan2(player.DashDir.Y, player.DashDir.X), MathF.PI / 3, 1);
        }
    }

    void EmitAnimationParticles()
    {
        while (pushDustCount < player.PushDustCount)
        {
            pushDustCount++;
            EmitDust(player.Pos.X - player.Facing * 5f, player.Pos.Y - 1f,
                MathF.Atan2(-.5f, -player.Facing), 1, 0f);
        }
    }

    void EmitDust(float x, float y, float direction, int count, float range = 4f)
    {
        float perpendicular = direction - MathF.PI / 2;
        particles.Emit(dust, x, y, direction, .5f, count,
            MathF.Abs(MathF.Cos(perpendicular) * range), MathF.Abs(MathF.Sin(perpendicular) * range));
    }

    internal readonly record struct RenderedFrame(Bitmap Image, Rectangle Area, Point Origin,
        Bitmap Actor, Rectangle ActorArea, Point ActorOrigin, PetExpressions.PortraitFrame Portrait);

    public RenderedFrame Render(bool waking, PetExpressions expressions = null, RectangleF monitor = default,
        IReadOnlyList<KeyValuePair<IntPtr, RectangleF>> windows = null)
    {
        int camX = Snap(player.Pos.X) - Anchor, camY = Snap(player.Pos.Y) - Anchor;
        Graphics g = sceneGraphics;
        g.BeginFrame(lastDraw);

        foreach (Trail trail in trails)
        {
            float remain = 1f - trail.Age;
            g.DrawImage(trail.Image, Snap(trail.At.X) - camX - TrailSize / 2,
                Snap(trail.At.Y) - camY - TrailSize / 2, TrailSize, TrailSize, .75f * remain * remain * remain);
        }
        DrawWavedashWaves(g, camX, camY);

        var actorOrigin = new Point(Snap(player.Pos.X) - ActorAnchor, Snap(player.Pos.Y) - ActorAnchor);
        Rectangle actorArea = Rectangle.Empty;
        if (player.IsPreDeath)
            DrawPreDeath(g, camX, camY);
        else if (player.IsDead)
            DrawDeathEffect(g, camX, camY, player.DeathPosition, player.DeathColor, player.DeathPercent);
        else if (player.IsRespawning)
            DrawDeathEffect(g, camX, camY, player.RespawnEffectPosition, player.RespawnColor, player.RespawnPercent);
        else
        {
            // The separate bitmap lets the mouse target share the exact avatar silhouette
            // while trails and particles stay click-through.
            Graphics body = actorGraphics;
            body.BeginFrame(lastActorDraw);
            if (player.StarFlyOutline)
            {
                DrawBody(body, player, ActorAnchor - 1, ActorAnchor, Color.Black);
                DrawBody(body, player, ActorAnchor + 1, ActorAnchor, Color.Black);
                DrawBody(body, player, ActorAnchor, ActorAnchor - 1, Color.Black);
                DrawBody(body, player, ActorAnchor, ActorAnchor + 1, Color.Black);
            }
            int hairClip = body.Save();
            if (player.Ducking && windows is { Count: > 0 })
                ClipHairBehindWindows(body, actorOrigin, windows);
            DrawHair(body, player, bangs, actorOrigin.X, actorOrigin.Y);
            body.Restore(hairClip);
            DrawBody(body, player, ActorAnchor, ActorAnchor,
                player.IsLowStamina && tiredFlash ? Color.Red : Color.White);
            lastActorDraw = body.Dirty;
            actorArea = actor.OpaqueBounds(lastActorDraw);
            if (!actorArea.IsEmpty)
                g.DrawImage(actor, Anchor - ActorAnchor, Anchor - ActorAnchor, ActorSize, ActorSize);
            if (!waking) DrawSweat(g, Anchor, Anchor);
        }
        particles.Draw(g, camX, camY);
        foreach (Slash slash in slashes)
        {
            Bitmap image = GetSlashStamp(Math.Min(3, (int)(slash.Age / .1f)), slash.Angle);
            g.DrawImage(image, Snap(slash.Position.X) - camX - SlashSize / 2,
                Snap(slash.Position.Y) - camY - SlashSize / 2, SlashSize, SlashSize);
        }
        PetExpressions.PortraitFrame portrait = default;
        if (!actorArea.IsEmpty && !waking && expressions?.Current != null)
        {
            var worldActor = actorArea;
            worldActor.Offset(actorOrigin);
            portrait = expressions.Render(worldActor, monitor, player.Facing > 0);
        }
        lastDraw = g.Dirty;
        return new RenderedFrame(canvas, lastDraw, new Point(camX, camY), actor, actorArea, actorOrigin, portrait);
    }

    void ClipHairBehindWindows(Graphics graphics, Point origin, IReadOnlyList<KeyValuePair<IntPtr, RectangleF>> windows)
    {
        var avatarBounds = new RectangleF(origin.X, origin.Y, ActorSize, ActorSize);
        var center = player.Center;
        using var insideWindows = new SKRegion();
        using var hiddenHair = new SKRegion();
        using var exposedWindow = new SKRegion();
        // The first window at a pixel determines whether the hair is behind it.
        // When the avatar is inside a hollow window, its surface stays behind her.
        foreach (var entry in windows)
        {
            var window = entry.Value;
            if (!window.IntersectsWith(avatarBounds)) continue;
            var bounds = new SKRectI(Grid(window.Left) - origin.X, Grid(window.Top) - origin.Y,
                Grid(window.Right) - origin.X, Grid(window.Bottom) - origin.Y);
            if (window.Contains(center))
            {
                insideWindows.Op(bounds, SKRegionOperation.Union);
                continue;
            }
            exposedWindow.SetRect(bounds);
            exposedWindow.Op(insideWindows, SKRegionOperation.Difference);
            hiddenHair.Op(exposedWindow, SKRegionOperation.Union);
        }
        graphics.Exclude(hiddenHair);

        static int Grid(float value) => (int)DesktopTerrain.ToGame(value, 1);
    }

    Bitmap GetSlashStamp(int frame, float angle)
    {
        if (slashFrames.TryGetValue((frame, angle), out Bitmap cached)) return cached;
        var texture = Sprites.Get("slash0" + frame, false);
        if (texture == null) return null;
        if (slashFrames.Count >= 64)
        {
            foreach (Bitmap old in slashFrames.Values) old.Dispose();
            slashFrames.Clear();
        }
        var result = new Bitmap(SlashSize, SlashSize);
        using (var g = new Graphics(result))
        {
            g.TranslateTransform(SlashSize / 2f, SlashSize / 2f);
            if (MathF.Abs(angle - MathF.PI) > .01f) g.RotateTransform(angle * 180 / MathF.PI);
            g.DrawImage(texture, -texture.Width / 2f, -texture.Height / 2f, texture.Width, texture.Height);
        }
        result.Freeze();
        slashFrames[(frame, angle)] = result;
        return result;
    }

    internal static void DrawBody(Graphics g, Player player, float anchorX, float anchorY, Color color)
    {
        bool flip = player.Facing < 0;
        Bitmap frame = Sprites.Get(player.CurrentFrameId, flip);
        if (frame == null) return;
        var origin = player.Sprite.OriginFor(frame.Width, frame.Height);
        float sx = player.SpriteScaleX, sy = player.SpriteScaleY;
        int x = Snap(anchorX - (flip ? frame.Width - origin.X : origin.X) * sx);
        int y = Snap(anchorY - origin.Y * sy);
        int w = Snap(frame.Width * sx), h = Snap(frame.Height * sy);
        Color tint = color == Color.White && player.State == Player.StStarFly ? player.StarFlySpriteColor : color;
        Sprites.DrawTinted(g, frame, tint, x, y, w, h);
        if (color == Color.White && player.SpriteMode != Player.ModeBadeline &&
            Sprites.BakedHairMask(player.CurrentFrameId, flip) is Bitmap hair)
            Sprites.DrawTinted(g, hair, player.HairColor, x, y, w, h);
        if (color == Color.White && player.AnimId == "startStarFly" &&
            Sprites.Get(FormattableString.Invariant($"startStarFlyWhite{player.Sprite.CurrentAnimationFrame:00}"), flip) is Bitmap white)
        {
            float alpha = player.Sprite.CurrentAnimationFrame / (float)player.Sprite.CurrentAnimationTotalFrames;
            Sprites.DrawTinted(g, white, Player.StarFlyGold, x, y, w, h, alpha);
        }
    }

    void DrawSweat(Graphics g, float anchorX, float anchorY)
    {
        Bitmap texture = Sprites.Get(sweat.Texture, player.Facing < 0);
        if (texture == null) return;
        float sx = player.SpriteScaleX, sy = player.SpriteScaleY;
        PointF origin = sweat.OriginFor(texture.Width, texture.Height);
        g.DrawImage(texture, Snap(anchorX - (player.Facing < 0 ? texture.Width - origin.X : origin.X) * sx),
            Snap(anchorY - origin.Y * sy), Snap(texture.Width * sx), Snap(texture.Height * sy));
    }

    // Celeste.PlayerHair.Render / GetHairScale. Draw the four outline offsets first,
    // then go from the ends to the bangs. The root is floored in world coordinates.
    internal static void DrawHair(Graphics g, Player player, IReadOnlyList<string> bangs,
        float camX, float camY, Color? colorOverride = null)
    {
        if (!HairMeta.TryGet(player.CurrentFrameId, out var meta) || !meta.HasHair) return;
        var hair = player.Hair;
        int count = Math.Min(hair.ActiveCount, hair.Nodes.Count);
        if (count == 0 || bangs.Count == 0) return;
        string root = bangs[Math.Clamp(meta.Bangs, 0, bangs.Count - 1)];
        bool flip = player.Facing < 0;
        Bitmap rootTexture = Sprites.Get(root, flip);
        Bitmap segment = Sprites.Get("characters/player/hair00", false);
        for (int i = 0; i < count; i++)
        {
            DrawPiece(i, -1, 0, Color.Black);
            DrawPiece(i, 1, 0, Color.Black);
            DrawPiece(i, 0, -1, Color.Black);
            DrawPiece(i, 0, 1, Color.Black);
        }
        for (int i = count - 1; i >= 0; i--)
            DrawPiece(i, 0, 0, colorOverride ?? player.HairColor);

        void DrawPiece(int i, int dx, int dy, Color color)
        {
            PointF node = hair.Nodes[i];
            float yScale = .25f + (1f - (float)i / count) * .75f;
            float xScale = yScale * MathF.Abs(player.SpriteScaleX);
            Bitmap image = i == 0 ? rootTexture : segment;
            if (image == null) return;
            if (i == 0) node = new PointF(MathF.Floor(node.X), MathF.Floor(node.Y));
            float originX = i == 0 && flip ? image.Width - 5 : 5;
            Sprites.DrawTinted(g, image, color,
                node.X - camX + dx - originX * xScale, node.Y - camY + dy - 5 * yScale,
                image.Width * xScale, image.Height * yScale);
        }
    }

    internal static Bitmap CreateStandingPose(SpriteBank bank)
    {
        // Player frames omit the dynamic hair. Compose it over the original idle pose.
        var avatar = new Player();
        avatar.ResetSprite(bank, "player", Player.ModeMadeline, null);
        avatar.ResetTo(new PointF(16, 24));
        avatar.PlaySprite("idle", true);
        HairMeta.TryGet(avatar.CurrentFrameId, out HairMeta.Meta hair);
        for (int i = 0; i < 30; i++) avatar.UpdateHairOnly(1f / 60f, hair.Offset.X, hair.Offset.Y);
        var bangs = AtlasLookup.Subtextures(Sprites.Atlas, "characters/player/bangs");

        using var pose = new Bitmap(32, 32);
        Rectangle area;
        using (var g = new Graphics(pose))
        {
            DrawHair(g, avatar, bangs, 0, 0);
            DrawBody(g, avatar, avatar.Pos.X, avatar.Pos.Y, Color.White);
            area = pose.OpaqueBounds(g.Dirty);
        }
        return area.IsEmpty ? null : pose.Extract(area, area.Size, Point.Empty);
    }

    internal static Bitmap CreateMenuIcon(SpriteBank bank)
    {
        using var cropped = CreateStandingPose(bank);
        if (cropped == null) return null;
        const int size = 18;
        var icon = new Bitmap(size * 2, size * 2);
        using (var g = new Graphics(icon))
        {
            float scale = Math.Min(2f, Math.Min(icon.Width / (float)cropped.Width, icon.Height / (float)cropped.Height));
            int width = Snap(cropped.Width * scale), height = Snap(cropped.Height * scale);
            g.DrawImage(cropped, (icon.Width - width) / 2, (icon.Height - height) / 2, width, height);
        }
        return icon;
    }

    static int Snap(float value) => (int)MathF.Round(value);
    static float SnapPx(float value) => MathF.Round(value);
    static float SnapEven(float value) => MathF.Round(value / 2f) * 2f;

    public void Dispose()
    {
        player.OnStarFlyParticles = null;
        Reset();
        foreach (Bitmap bitmap in slashFrames.Values) bitmap.Dispose();
        slashFrames.Clear();
        sceneGraphics.Dispose();
        actorGraphics.Dispose();
        canvas.Dispose();
        actor.Dispose();
    }

    void DrawPreDeath(Graphics g, float camX, float camY)
    {
        float dx = player.DeathBodyPosition.X - player.Pos.X;
        float dy = player.DeathBodyPosition.Y - player.Pos.Y;
        DrawHair(g, player, bangs, camX - dx, camY - dy,
            player.DeathBodyFrameId != null && player.DeathBodyFrameId.EndsWith("00")
                ? Color.White : player.DeathColor);
        Bitmap frame = Sprites.Get(player.DeathBodyFrameId, player.Facing < 0);
        if (frame == null) return;
        var state = g.Save();
        g.TranslateTransform(SnapPx(player.DeathBodyPosition.X - camX),
            SnapPx(player.DeathBodyPosition.Y - camY));
        g.RotateTransform(player.DeathBodyRotation * 180f / (float)Math.PI);
        g.ScaleTransform(player.DeathBodyScale, player.DeathBodyScale);
        g.DrawImage(frame, -16f, -32f, 32f, 32f);
        g.Restore(state);
    }

    void DrawDeathEffect(Graphics g, float camX, float camY,
        PointF effectPosition, Color effectColor, float effectPercent, string texturePath = "characters/player/hair00")
    {
        Bitmap texture = Sprites.Get(texturePath, false) ?? Sprites.Get("hair00", false);
        if (texture == null) return;
        float ease = effectPercent;
        float cubeOut = 1f - (float)Math.Pow(1f - ease, 3f);
        float radius = cubeOut * 24f;
        float scale = ease < 0.5f
            ? 0.5f + ease
            : 1f - (float)Math.Pow((ease - 0.5f) * 2f, 3f);
        Color color = ((int)Math.Floor(ease * 10f) & 1) == 0
            ? effectColor : Color.White;
        float centerX = effectPosition.X - camX;
        float centerY = effectPosition.Y - camY;
        for (int i = 0; i < 8; i++)
        {
            float angle = ((float)i / 8f + ease * 0.25f) * (float)Math.PI * 2f;
            float x = SnapPx(centerX + (float)Math.Cos(angle) * radius);
            float y = SnapPx(centerY + (float)Math.Sin(angle) * radius);
            float w = SnapEven(texture.Width * scale), h = SnapEven(texture.Height * scale);
            Sprites.DrawTinted(g, texture, Color.Black, x - w / 2f - 1f, y - h / 2f, w, h);
            Sprites.DrawTinted(g, texture, Color.Black, x - w / 2f + 1f, y - h / 2f, w, h);
            Sprites.DrawTinted(g, texture, Color.Black, x - w / 2f, y - h / 2f - 1f, w, h);
            Sprites.DrawTinted(g, texture, Color.Black, x - w / 2f, y - h / 2f + 1f, w, h);
        }
        for (int i = 0; i < 8; i++)
        {
            float angle = ((float)i / 8f + ease * 0.25f) * (float)Math.PI * 2f;
            float x = SnapPx(centerX + (float)Math.Cos(angle) * radius);
            float y = SnapPx(centerY + (float)Math.Sin(angle) * radius);
            float w = SnapEven(texture.Width * scale), h = SnapEven(texture.Height * scale);
            Sprites.DrawTinted(g, texture, color, x - w / 2f, y - h / 2f, w, h);
        }
    }

    void DrawWavedashWaves(Graphics g, float camX, float camY)
    {
        foreach (var ring in waveRings)
        {
            float alpha = 0.6f * (1f - ring.Progress);
            int a = Math.Max(0, Math.Min(255, (int)Math.Round(alpha * 255f)));
            var points = new PointF[16];
            float maxRadius = 4f + (14f - 4f) * ring.Progress;
            float nx = (float)Math.Cos(ring.Angle), ny = (float)Math.Sin(ring.Angle);
            for (int i = 0; i < points.Length; i++)
            {
                float radians = i * (float)Math.PI * 2f / points.Length;
                float vx = (float)Math.Cos(radians), vy = (float)Math.Sin(radians);
                float along = Math.Abs(vx * nx + vy * ny);
                float radius = maxRadius * (1f - 0.5f * along);
                points[i] = new PointF(
                    SnapPx(ring.X - camX + vx * radius),
                    SnapPx(ring.Y - camY + vy * radius));
            }
            g.DrawPolygon(Color.FromArgb(a, Color.White), points);
        }
    }

    void UpdateWavedashWaves(float dt)
    {
        // Existing SpeedRing entities update before Player can add another one;
        // newly emitted rings therefore render once at lerp=0 and origin position.
        for (int i = waveRings.Count - 1; i >= 0; i--)
        {
            var ring = waveRings[i];
            ring.Progress += 3f * dt;
            ring.X += (float)Math.Cos(ring.Angle) * 10f * dt;
            ring.Y += (float)Math.Sin(ring.Angle) * 10f * dt;
            if (ring.Progress >= 1f) waveRings.RemoveAt(i);
            else waveRings[i] = ring;
        }

        // DashBegin explicitly sets vanilla's `launched` flag false. This is
        // particularly important for multi-ultras: each new diagonal dash ends
        // the preceding jump's speed-ring sequence.
        if (player.DashSequenceCount != ringDashSequence)
        {
            ringDashSequence = player.DashSequenceCount;
            launchActive = false;
            launchClock = 0f;
            nextRing = 0.15f;
        }
        bool launchStartedThisFrame = false;
        if (player.LaunchCount != launchCount)
        {
            launchCount = player.LaunchCount;
            // SuperJump only sets vanilla's `launched` flag. If a launch is
            // already active it does not reset launchedTimer, which prevents
            // chained hypers/ultras from restarting and overproducing rings.
            if (!launchActive)
            {
                launchActive = true;
                launchClock = 0f;
                nextRing = 0.15f;
                launchStartedThisFrame = true;
            }
        }

        // Player checks `launched` before its StateMachine update. A SuperJump
        // created during that update therefore starts aging on the next frame.
        if (launchActive && !launchStartedThisFrame)
        {
            float speedSq = player.Speed.X * player.Speed.X + player.Speed.Y * player.Speed.Y;
            if (speedSq < 19600f)
            {
                launchActive = false;
                launchClock = 0f;
            }
            else
            {
                launchClock += dt;
                if (launchClock >= 0.5f)
                {
                    launchActive = false;
                    launchClock = 0f;
                }
                else
                {
                    while (launchClock >= nextRing)
                    {
                        waveRings.Add(new WaveRing
                        {
                            X = player.Pos.X,
                            Y = player.Pos.Y - 5.5f,
                            Angle = (float)Math.Atan2(player.Speed.Y, player.Speed.X)
                        });
                        nextRing += 0.15f;
                    }
                }
            }
        }

    }
}
