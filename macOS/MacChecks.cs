using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using SkiaSharp;
using static DeskMadeline.Mac.Native;

namespace DeskMadeline.Mac;

internal static class MacChecks
{
    const float Dt = 1f / 60f;
    const int Scale = 3;
    static int passed, failed;
    static readonly Native.DisplayInfo screen = new Native.DisplayInfo(1,
        new Native.DesktopRect(0, 0, 1280, 800), new Native.DesktopRect(0, 24, 1280, 730));
    static readonly Native.WindowInfo first = Window(21, 60, 360, 240, 380);
    static readonly Native.WindowInfo second = Window(22, 360, 330, 300, 410);

    static Native.WindowInfo Window(uint id, float x, float y, float w, float h) =>
        new Native.WindowInfo(id, 100, new Native.DesktopRect(x, y, w, h));

    public static int Run(string[] args)
    {
        int upstream = GridChecks.Run() + DreamChecks.Run() + DreamHyperChecks.Run() + LiftChecks.Run() +
            PushChecks.Run() + MoveChecks.Run() + IdleChecks.Run() + SoundChecks.Run() +
            SuperDashChecks.Run() + WaterChecks.Run() + SpriteBankChecks.Run() + HairChecks.Run() +
            AnimationChecks.Run() + BumperChecks.Run();
        Console.WriteLine("\nMAC DESKTOP");
        var bank = PetWindow.LoadOriginalSprites();
        string dump = args.FirstOrDefault(arg => !arg.StartsWith("--", StringComparison.Ordinal));
        TestTerrain();
        TestWindowTransitions();
        TestMovement(bank, dump);
        TestCrouchingHair(bank, dump);
        TestRefills(bank);
        TestDesktopItems(bank);
        TestBoostersAndMoveBlocks(bank);
        TestCourseProps();
        TestGeneratedCourses();
        TestResizableProps(dump);
        TestDreamBlockOutline(dump);
        TestPickupAndBumper(dump);
        TestMenuIcon(bank, dump);
        TestExpressions(bank, dump);
        TestBadeline(bank);
        TestAssistModes();
        TestClickEscape();
        TestController();
        TestRumble();
        TestDreamRumble();
        TestDrag(bank);
        if (args.Contains("--sound")) TestSound();
        Console.WriteLine($"Mac checks: {passed} passed, {failed} failed. Upstream failures: {upstream}.");
        return failed + upstream == 0 ? 0 : 1;
    }

    static void TestBadeline(SpriteBank bank)
    {
        using var chase = new DesktopBadeline(bank, null);
        var layers = new List<Native.ItemLayer>();
        var monitor = new RectangleF(0, 0, 1500, 750);
        var player = new Player
        {
            Solids = new List<Solid> { new Solid { L = 0, R = 1500, T = 100, B = 112 } },
        };
        player.ResetTo(new PointF(160, 100));
        Check("the Badeline bank is available and the chase is off by default", chase.Available && chase.Count == 0);
        chase.SetCount(DesktopBadeline.MaxCount);
        for (int i = 0; i < 230; i++)
        {
            player.Pos = new PointF(160 + i * 2f, 100f);
            chase.CheckCollision(player);
            chase.Advance(player, monitor, Dt);
        }
        chase.Render(layers, Scale);
        var avatars = layers.Where(x => x.Depth == 2).ToArray();
        Check("six chasers follow one route with successively delayed poses",
            !player.IsDead && avatars.Length == 6 && avatars.Zip(avatars.Skip(1), (a, b) => a.Image.Bounds.X > b.Image.Bounds.X).All(x => x));
        Check("the original chaser snapshots fade and pass mouse input through",
            layers.All(l => l.ItemId == 0) &&
            layers.Any(l => l.Depth == 1 && l.Opacity is > 0 and < .75f));
        Check("Old Site chasers use the original purple hair and white interpolation",
            avatars.Length >= 2 && ContainsOpaqueColor(avatars[0].Image, Color.FromArgb(0x9b, 0x3f, 0xb5)) &&
            ContainsOpaqueColor(avatars[1].Image, Color.FromArgb(0xab, 0x5f, 0xc1)));
        Check("TrailManager silhouettes retain the solid Celeste trail color",
            layers.Where(l => l.Depth == 1).Any(l => ContainsOpaqueColor(l.Image, Player.NormalHairColor)));

        chase.SetCount(1);
        player.ResetTo(new PointF(160, 100));
        for (int i = 0; i < 80; i++)
        {
            chase.Advance(player, monitor, Dt);
            chase.CheckCollision(player);
        }
        Check("Badeline cannot kill Madeline while easing in", !player.IsDead);
        for (int i = 0; i < 14; i++) chase.Advance(player, monitor, Dt);
        player.Pos = new PointF(162, 100);
        chase.CheckCollision(player);
        chase.CheckCollision(player);
        Check("a chaser hit uses the directional death exactly once",
            player.IsDead && player.IsPreDeath && player.DeathSequenceCount == 1);
        chase.Advance(player, monitor, Dt);
        layers.Clear();
        chase.Render(layers, Scale);
        Check("Badeline stays on screen and laughs during the death animation", layers.Any(l => l.Depth == 5));
        for (int i = 0; i < 110; i++)
        {
            player.Update(Dt, default);
            chase.Advance(player, monitor, Dt);
            chase.CheckCollision(player);
        }
        layers.Clear();
        chase.Render(layers, Scale);
        Check("an idle Madeline can respawn without another chase",
            !player.IsDead && !player.IsRespawning && player.DeathSequenceCount == 1 && layers.Count == 0);
        player.Pos = new PointF(164, 100);
        chase.Advance(player, monitor, Dt);
        chase.Render(layers, Scale);
        Check("movement rearms the chase on the next route", layers.Any(l => l.Depth == 2));
        chase.SetCount(0);
        layers.Clear();
        chase.Render(layers, Scale);
        Check("disabling Badeline clears all actors and trails", chase.Count == 0 && layers.Count == 0);
    }

    static unsafe bool ContainsOpaqueColor(Native.ImageLayer layer, Color color)
    {
        var pixels = (byte*)layer.Pixels;
        for (int y = 0; y < layer.Height; y++)
        for (int x = 0; x < layer.Width; x++)
        {
            byte* pixel = pixels + y * layer.Stride + 4 * x;
            if (pixel[3] == 255 && pixel[2] == color.R && pixel[1] == color.G && pixel[0] == color.B)
                return true;
        }
        return false;
    }

    static void TestAssistModes()
    {
        var player = new Player { FreezeFramesEnabled = false };
        player.ResetTo(new PointF(150, 100));
        player.Stamina = 15f;
        player.InfiniteStamina = true;
        player.Update(Dt, default);
        Check("infinite stamina immediately restores the climbing meter", player.Stamina == 110f && !player.IsLowStamina);
        player.InfiniteStamina = false;
        player.Stamina = 15f;
        player.Update(Dt, default);
        Check("disabling the stamina assist restores the fatigue threshold", player.IsLowStamina);

        player.SetDashMode(2);
        Check("two-dash assist refills to two", player.Dashes == 2 && !player.InfiniteDash);
        player.SetDashMode(1);
        Check("switching back to one caps and refills at one", player.Dashes == 1 && player.DashCapacity == 1);
        player.SetDashMode(-1);
        for (int dash = 0; dash < 3; dash++)
        {
            player.BufferDash();
            player.Update(Dt, new PetInput { AimY = -1, DashPressed = player.HasDashBuffer });
            for (int i = 0; i < 20; i++) player.Update(Dt, new PetInput { AimY = -1 });
        }
        Check("infinite dash assist allows three consecutive airborne dashes and refills on cooldown",
            player.InfiniteDash && player.DashSequenceCount == 3 && !player.onGround && player.Dashes == 2);
    }

    static void TestTerrain()
    {
        var world = new DesktopTerrain();
        var p = new Player();
        var screens = new[] { screen };
        var overlap = Window(23, 150, 270, 180, 160);
        world.Apply(p, screens, new[] { overlap, first }, 100, Scale, WindowMaterial.Solid, Dt);
        Check("front windows subtract their full rectangle from rear top ledges",
            p.Solids.Any(s => s.Id == (IntPtr)21 && s.T == 120 && s.L == 20 && s.R == 50) &&
            !p.Solids.Any(s => s.Id == (IntPtr)21 && s.T == 120 && s.L <= 60 && s.R > 60));
        Check("hollow borders leave the interior free", !p.CollidesAt(30, 160));
        Check("the work area bottom supports the player above the Dock",
            p.Solids.Any(s => s.Id == DesktopTerrain.Floor && s.T == 251));

        world.Apply(p, screens, new[] { first, second }, 100, Scale, WindowMaterial.Solid, Dt);
        p.ResetTo(new PointF(60, 120));
        p.Update(Dt, default);
        var shifted = Window(21, 69, 354, 240, 380);
        world.Apply(p, screens, new[] { shifted, second }, 100, Scale, WindowMaterial.Solid, Dt);
        Check("dragging a window carries its rider and supplies the new lift speed",
            p.Pos == new PointF(63, 118) && Near(p.LiftSpeed.X, 180) && Near(p.LiftSpeed.Y, -120));
        world.Apply(p, screens, new[] { shifted, second }, 100, Scale, WindowMaterial.Solid, Dt);
        Check("a stationary window stops reporting fresh lift speed",
            p.Solids.Where(s => s.Id == (IntPtr)21).All(s => s.LiftX == 0 && s.LiftY == 0));

        var raised = Window(21, 69, 352, 240, 380);
        world.Apply(p, screens, new[] { raised, second }, 100, Scale, WindowMaterial.Solid, Dt);
        Check("slow drags carry the player by the snapped border change without sinking into it",
            p.Pos == new PointF(63, 117) && !p.CollidesHere);
        p.ResetTo(new PointF(116, 155));
        p.State = Player.StClimb;
        p.Facing = 1;
        var wallShift = Window(22, 369, 330, 300, 410);
        world.Apply(p, screens, new[] { raised, wallShift }, 100, Scale, WindowMaterial.Solid, Dt);
        Check("a wall pulled away carries the player who is climbing it",
            p.Pos == new PointF(119, 155) && p.RidingId == (IntPtr)22 && Near(p.LiftSpeed.X, 180));

        world.Apply(p, screens, new[] { first, second }, 100, Scale, WindowMaterial.Dream, Dt);
        Check("Dream uses a filled dream collider", p.Solids.Any(s => s.Id == (IntPtr)21 && s.Dream && s.B > 240));
        world.Apply(p, screens, new[] { first, second }, 100, Scale, WindowMaterial.Water, Dt);
        Check("Water contributes pools and the desktop perimeter stays solid",
            p.Waters.Count == 2 && p.Solids.All(s => s.OffScreen));

        var leftScreen = new Native.DisplayInfo(2, new Native.DesktopRect(-900, 80, 900, 600),
            new Native.DesktopRect(-900, 104, 900, 576));
        world.Apply(p, new[] { screen, leftScreen }, Array.Empty<Native.WindowInfo>(), 100, Scale, WindowMaterial.Solid, Dt);
        Check("a heart on the second monitor uses the whole display including its reserved system areas",
            world.NearestDisplayFrame(new PointF(-90, 100)) ==
                new RectangleF(-900f / Scale, 80f / Scale, 900f / Scale, 600f / Scale));
        Check("the shared seam between offset monitors stays open",
            !p.CollidesAt(0, 100) && p.MinX == -300);
        Check("the exposed segment above the smaller display remains a wall", p.CollidesAt(0, 30));
        world.Apply(p, screens, new[] { Window(90, 0, 0, 1280, 800) }, 100, Scale, WindowMaterial.Solid, Dt);
        Check("a foreground fullscreen window hides the overlay on its display",
            world.FullscreenDisplay == 1 && p.Solids.All(s => s.OffScreen));
    }

    static void TestWindowTransitions()
    {
        var terrain = new DesktopTerrain();
        var player = new Player();
        var shifted = Window(21, 69, 354, 240, 380);
        var covered = Window(99, 120, 300, 150, 170);

        void Windows(params Native.WindowInfo[] windows) =>
            terrain.Apply(player, new[] { screen }, windows, 100, Scale, WindowMaterial.Solid, Dt);

        Windows(first);
        player.ResetTo(new PointF(60, 116));
        player.MoveV(8);
        Windows(shifted);
        Check("a landing at the end of a frame catches the next window movement",
            player.Pos == new PointF(63, 118));

        Windows(first);
        player.ResetTo(new PointF(60, 120));
        player.Update(Dt, default);
        player.MoveH(50);
        PointF afterWalk = player.Pos;
        Windows(shifted);
        Check("stepping off a ledge leaves the player behind when the window moves",
            player.Pos == afterWalk && player.LiftSpeed.IsEmpty);

        Windows(first);
        player.ResetTo(new PointF(60, 120));
        player.Update(Dt, default);
        Windows(shifted);
        player.Update(Dt, default);
        PointF beforeSwitch = player.Pos;
        Windows(covered, shifted);
        player.Update(Dt, default);
        Check("covering a moving ledge releases its rider without an old lift boost",
            player.Pos == beforeSwitch && player.Speed.Y >= 0 && player.LiftSpeed.IsEmpty);

        Windows(first);
        player.ResetTo(new PointF(60, 120));
        player.Update(Dt, default);
        Windows(Window(21, 186, 390, 240, 380));
        Check("a window snapped to a distant position does not teleport its rider",
            player.Pos == new PointF(60, 120) && player.LiftSpeed.IsEmpty);

        Windows();
        player.ResetTo(new PointF(60, 122));
        Windows(first);
        Check("a newly revealed border separates the player by the shortest free offset",
            player.Pos == new PointF(60, 120) && !player.CollidesHere && player.LiftSpeed.IsEmpty);

        player.LiftSpeed = new PointF(90, -120);
        player.ResetTo(new PointF(70, 115));
        Check("reset and respawn clear all cached platform momentum", player.LiftSpeed.IsEmpty);

        terrain.Apply(player, new[] { screen }, new[] { first }, 100, Scale, WindowMaterial.Solid, Dt,
            new DesktopTerrain.PointerSample(new PointF(100, 365), true));
        player.ResetTo(new PointF(60, 120));
        var dragged = Window(21, 420, 360, 240, 380);
        terrain.Apply(player, new[] { screen }, new[] { dragged }, 101, Scale, WindowMaterial.Solid, Dt,
            new DesktopTerrain.PointerSample(new PointF(460, 365), true));
        Check("a fast pointer-tracked drag carries Madeline even across app activation",
            player.Pos == new PointF(180, 120) && !player.CollidesHere);

        Windows(Window(21, 60, 360, 241, 380));
        player.ResetTo(new PointF(104, 155));
        player.Facing = -1;
        player.State = Player.StClimb;
        Windows(Window(21, 61, 360, 241, 380));
        Check("the trailing wall edge follows its own rounded pixel when climbing",
            player.Pos == new PointF(105, 155) && player.RidingId == (IntPtr)21 && !player.CollidesHere);

        Windows(first);
        player.ResetTo(new PointF(60, 120));
        terrain.Apply(player, new[] { screen }, new[] { Window(21, 78, 360, 240, 380) }, 100, Scale,
            WindowMaterial.Solid, Dt, prepareItems: () => player.Solids = new List<Solid>(terrain.BaseSolids)
            {
                new Solid { Id = (IntPtr)(-512), L = 67, T = 100, R = 76, B = 120 },
            });
        Check("window carry uses ordinary collision against a placed solid",
            player.Pos == new PointF(63, 120) && !player.CollidesHere);

        Windows();
        RectangleF remaining = terrain.Monitors[0];
        Check("an off-display recovery uses the nearest free standing position",
            DesktopTerrain.TryStandIn(player, remaining, new PointF(remaining.Right + 45, remaining.Bottom + 40),
                out PointF recovered) && recovered == new PointF(remaining.Right - Player.HitHalfWidth, remaining.Bottom));
    }

    static void TestMovement(SpriteBank bank, string dump)
    {
        var p = new Player();
        var pulses = new List<(RumbleStrength, RumbleLength)>();
        p.OnRumble = (strength, length) => pulses.Add((strength, length));
        p.ResetSprite(bank, "player", Player.ModeMadeline, null);
        using var fx = new PetEffects(p, bank);
        var keys = new DesktopInput();
        var world = new DesktopTerrain();
        world.Apply(p, new[] { screen }, new[] { first, second }, 100, Scale, WindowMaterial.Solid, Dt);
        keys.Receive(DesktopInput.Capture, 1);
        float sceneTime = 0f;

        void Hold(int key) => keys.Receive(key, 1);
        void Release(int key) => keys.Receive(key, 0);
        void Tick(int frames = 1)
        {
            for (int i = 0; i < frames; i++)
            {
                PetInput input = keys.Sample(p);
                bool frozen = p.IsHitStopped;
                int oldState = p.State;
                if (!frozen)
                {
                    sceneTime += Dt;
                    fx.UpdateComponents(Dt);
                }
                p.Update(Dt, input, sceneTime);
                fx.AfterPlayer(Dt, oldState, frozen);
            }
        }
        void Snap(string name)
        {
            var frame = fx.Render(false);
            Check(name + " draws a nonempty original frame", HasPixels(frame.Image, frame.Area));
            if (dump == null || frame.Area.IsEmpty) return;
            Directory.CreateDirectory(dump);
            using var picture = frame.Image.Extract(frame.Area, frame.Area.Size, Point.Empty);
            using var scaled = new Bitmap(picture.Width * 5, picture.Height * 5);
            using (var g = new Graphics(scaled)) g.DrawImage(picture, 0, 0, scaled.Width, scaled.Height);
            scaled.SavePng(Path.Combine(dump, name + ".png"));
        }

        p.ResetTo(new PointF(66, 120));
        Tick(2);
        Check("Madeline is grounded on the first window", p.onGround && p.GroundId == (IntPtr)21);
        Snap("idle");
        Hold(DesktopInput.D);
        Tick(10);
        Check("original run cap and run cycle", Near(p.Speed.X, 90) && p.AnimId.StartsWith("run", StringComparison.Ordinal));
        Snap("run");
        Release(DesktopInput.D);
        Hold(DesktopInput.S);
        Tick(10);
        Check("original crouched hitbox and pose", p.Ducking && p.CurrentHitHeight == 6 &&
            (p.AnimId == "duck" || p.AnimId == "duckSlow"));
        Snap("duck");
        Release(DesktopInput.S);
        Tick(2);

        Hold(DesktopInput.D);
        Hold(DesktopInput.Jump);
        pulses.Clear();
        Tick();
        Check("jump starts at -105 game pixels/sec", Near(p.Speed.Y, -105) && p.JumpEffectCount > 0);
        Tick(9);
        Check("a regular jump has no rumble before landing", pulses.Count == 0);
        Check("holding jump follows the original rising arc", p.Pos.Y <= 108 && p.Speed.Y < 0);
        Snap("jump");
        Release(DesktopInput.Jump);
        Hold(DesktopInput.Dash);
        Tick();
        Check("dash starts with hitstop and spends the dash", p.IsHitStopped && p.Dashes == 0);
        Check("DashBegin requests strong rumble for a quarter second",
            pulses.SequenceEqual(new[] { (RumbleStrength.Strong, RumbleLength.Medium) }));
        int hitstopFrames = 0;
        while (p.IsFrozen && hitstopFrames < 8) { Tick(); hitstopFrames++; }
        Check("the horizontal dash starts at 240 after the aim frame",
            Near(p.Speed.X, 240) && p.DashDir == new PointF(1, 0) && hitstopFrames == 4);
        Release(DesktopInput.Dash);
        Tick(4);
        Check("spending the dash fades the hair from red toward blue",
            p.Dashes == 0 && p.HairColor.B > p.HairColor.R);
        Snap("dash");
        Check("dash hitstop and resume do not request another pulse",
            pulses.SequenceEqual(new[] { (RumbleStrength.Strong, RumbleLength.Medium) }));
        pulses.Clear();
        Release(DesktopInput.D);
        for (int i = 0; i < 90 && p.GroundId != (IntPtr)22; i++) Tick();
        Check("the jump and air dash land on the second window's top border",
            p.GroundId == (IntPtr)22 && p.Pos.Y == 110 && p.onGround);
        Check("the landing requests one short light pulse",
            pulses.SequenceEqual(new[] { (RumbleStrength.Light, RumbleLength.Short) }));
        Check("the refill flashes the hair white", p.Dashes == 1 && p.HairColor.ToArgb() == Color.White.ToArgb());
        Tick(10);
        Check("the refill restores the original red after the flash",
            p.HairColor.ToArgb() == Player.NormalHairColor.ToArgb());
        Snap("landed");

        p.ResetTo(new PointF(116, 155));
        fx.Reset();
        Tick();
        Hold(DesktopInput.D);
        Hold(DesktopInput.GrabZ);
        Hold(DesktopInput.W);
        pulses.Clear();
        Tick();
        Check("grabbing the wall requests one short medium pulse",
            pulses.SequenceEqual(new[] { (RumbleStrength.Medium, RumbleLength.Short) }));
        pulses.Clear();
        Tick(6);
        Check("the climb no-move delay suppresses periodic pulses", pulses.Count == 0);
        int expectedClimbPulses = 0;
        for (int i = 0; i < 17; i++)
        {
            int interval = (int)(sceneTime / .2f);
            Tick();
            if ((int)(sceneTime / .2f) > interval) expectedClimbPulses++;
        }
        Check("climbing requests a short climb pulse at the scene's .2-second marks",
            expectedClimbPulses > 0 && pulses.Count == expectedClimbPulses &&
            pulses.All(pulse => pulse == (RumbleStrength.Climb, RumbleLength.Short)));
        Check("grab and up enter the original climbing state and spend stamina",
            p.State == Player.StClimb && Near(p.Speed.Y, -45) && p.Stamina < 110);
        Snap("climb");
        Release(DesktopInput.D);
        Release(DesktopInput.W);
        Release(DesktopInput.GrabZ);
        Hold(DesktopInput.A);
        Hold(DesktopInput.Jump);
        Tick();
        Check("jumping away from the wall uses the original wall-jump impulse",
            p.State == Player.StNormal && Near(p.Speed.X, -130) && Near(p.Speed.Y, -105));
        Snap("wall-jump");
        keys.Receive(DesktopInput.Capture, 0);
        Check("releasing keyboard clears the held directions and buttons",
            keys.Sample(p).MoveX == 0 && !keys.Sample(p).JumpHeld);
    }

    static void TestCrouchingHair(SpriteBank bank, string dump)
    {
        var player = new Player();
        player.ResetSprite(bank, "player", Player.ModeMadeline, null);
        using var effects = new PetEffects(player, bank);
        var terrain = new DesktopTerrain();
        terrain.Apply(player, new[] { screen }, new[] { first }, 100, Scale, WindowMaterial.Solid, Dt);
        var window = terrain.Windows.Single().Value;
        player.ResetTo(new PointF(window.Left + window.Width / 2, window.Top));
        var keys = new DesktopInput();
        keys.Receive(DesktopInput.Capture, 1);

        void Tick(int frames)
        {
            for (int i = 0; i < frames; i++)
            {
                int previousState = player.State;
                effects.UpdateComponents(Dt);
                player.Update(Dt, keys.Sample(player));
                effects.AfterPlayer(Dt, previousState, false);
            }
        }

        Tick(20);
        keys.Receive(DesktopInput.S, 1);
        Tick(24);
        var unclipped = effects.Render(false).Actor.Pixels.ToArray();
        var frame = effects.Render(false, windows: terrain.Windows);
        var pixels = frame.Actor.Pixels;
        int overlapBefore = 0, overlapAfter = 0;
        bool outsideMatches = true;
        for (int y = 0; y < frame.Actor.Height; y++)
            for (int x = 0; x < frame.Actor.Width; x++)
            {
                int at = y * frame.Actor.RowBytes + x * 4;
                if (window.Contains(frame.ActorOrigin.X + x + .5f, frame.ActorOrigin.Y + y + .5f))
                {
                    if (unclipped[at + 3] > 0) overlapBefore++;
                    if (pixels[at + 3] > 0) overlapAfter++;
                }
                else if (!pixels.Slice(at, 4).SequenceEqual(unclipped.AsSpan(at, 4)))
                    outsideMatches = false;
            }
        Check("crouching above the window leaves its surface clear of the avatar's hair",
            player.Ducking && player.GroundId == (IntPtr)21 && overlapBefore > 0 && overlapAfter == 0);
        Check("the window clip preserves all avatar pixels outside the window", outsideMatches);

        if (dump != null)
        {
            Directory.CreateDirectory(dump);
            var view = new Rectangle((int)player.Pos.X - 16, (int)window.Top - 17, 32, 24);
            var actorRegion = view;
            actorRegion.Offset(-frame.ActorOrigin.X, -frame.ActorOrigin.Y);
            using var cropped = frame.Actor.Extract(actorRegion, view.Size, Point.Empty);
            using var preview = new Bitmap(view.Width * 6, view.Height * 6);
            using (var canvas = new SKCanvas(preview.Native))
            using (var paint = new SKPaint { Color = new SKColor(131, 122, 181) })
            {
                canvas.DrawRect(0, 0, preview.Width, preview.Height, paint);
                paint.Color = new SKColor(29, 30, 35);
                canvas.DrawRect(0, ((int)window.Top - view.Y) * 6, preview.Width, preview.Height, paint);
            }
            using (var g = new Graphics(preview))
                g.DrawImage(cropped, 0, 0, preview.Width, preview.Height);
            preview.SavePng(Path.Combine(dump, "crouch-window-edge.png"));
        }

        var foreground = Window(23, 120, 300, 180, 190);
        terrain.Apply(player, new[] { screen }, new[] { foreground, first }, 100, Scale, WindowMaterial.Solid, Dt);
        var inside = effects.Render(false, windows: terrain.Windows);
        Check("when crouching inside a foreground window, a window behind it cannot clip the hair",
            inside.Actor.Pixels.SequenceEqual(unclipped));
    }

    static void TestMenuIcon(SpriteBank bank, string dump)
    {
        using Bitmap icon = PetEffects.CreateMenuIcon(bank);
        bool redHair = false;
        bool blueJacket = false;
        if (icon != null)
        {
            for (int y = 0; y < icon.Height; y++)
                for (int x = 0; x < icon.Width; x++)
                {
                    var pixel = icon.GetPixel(x, y);
                    redHair |= y < icon.Height / 2 && pixel.ToArgb() == Player.NormalHairColor.ToArgb();
                    blueJacket |= y > icon.Height / 2 && pixel.A != 0 && pixel.B > pixel.R * 1.5f;
                }
            if (dump != null)
            {
                Directory.CreateDirectory(dump);
                icon.SavePng(Path.Combine(dump, "menu-icon.png"));
                using var standing = PetEffects.CreateStandingPose(bank);
                standing?.SavePng(Path.Combine(dump, "standing-madeline.png"));
            }
        }
        Check("the menu icon composites original red hair over the original body", redHair && blueJacket);
    }

    static void TestExpressions(SpriteBank bank, string dump)
    {
        using var expressions = PetExpressions.Load();
        Check("Madeline's dialogue animations load from the installed portrait atlas", expressions != null);
        if (expressions == null) return;
        var p = new Player
        {
            MinX = 0, MaxX = 800,
            Solids = new List<Solid>
            {
                new Solid { Id = (IntPtr)1, L = 0, T = 170, R = 800, B = 176 },
                new Solid { Id = (IntPtr)2, L = 340, T = 40, R = 344, B = 170 },
            },
        };
        p.ResetSprite(bank, "player", Player.ModeMadeline, null);
        p.ResetTo(new PointF(100, 120));
        using var fx = new PetEffects(p, bank);
        var keys = new DesktopInput();
        keys.Receive(DesktopInput.Capture, 1);
        var monitor = new RectangleF(0, 8, 500, 300);

        void Tick(int frames = 1, bool controlled = true)
        {
            for (int i = 0; i < frames; i++)
            {
                var input = keys.Sample(p);
                var before = new PetExpressions.PlayerFrame(p);
                bool frozen = p.IsHitStopped;
                if (!frozen) fx.UpdateComponents(Dt);
                p.Update(Dt, input);
                fx.AfterPlayer(Dt, before.State, frozen);
                expressions.Update(Dt, p, before, controlled);
            }
        }

        void SavePreview(PetEffects.RenderedFrame frame, Rectangle view, int previewScale, string path)
        {
            var spriteRegion = view;
            spriteRegion.Offset(-frame.Origin.X, -frame.Origin.Y);
            using var cropped = frame.Image.Extract(spriteRegion, view.Size, Point.Empty);
            using var preview = new Bitmap(view.Width * previewScale, view.Height * previewScale);
            var portrait = frame.Portrait;
            using (var g = new Graphics(preview)) g.DrawImage(cropped, 0, 0, preview.Width, preview.Height);
            if (portrait.Border != null)
            {
                using var canvas = new SKCanvas(preview.Native);
                using var shadow = SKImageFilter.CreateDropShadow(0, previewScale, 0, 0, SKColors.Black.WithAlpha(77));
                using var paint = new SKPaint
                {
                    Color = SKColors.White.WithAlpha((byte)MathF.Round(portrait.Opacity * 255)),
                    ImageFilter = shadow,
                };
                canvas.SaveLayer(paint);
                Draw(portrait.Tail, portrait.TailBounds, SKFilterMode.Nearest);
                Draw(portrait.Artwork, portrait.ArtworkBounds, SKFilterMode.Linear);
                Draw(portrait.Border, portrait.Bounds, SKFilterMode.Nearest);
                canvas.Restore();

                void Draw(Bitmap bitmap, RectangleF bounds, SKFilterMode filter)
                {
                    var box = SKRect.Create((bounds.X - view.X) * previewScale, (bounds.Y - view.Y) * previewScale,
                        bounds.Width * previewScale, bounds.Height * previewScale);
                    using var image = SKImage.FromBitmap(bitmap.Native);
                    canvas.DrawImage(image, box, new SKSamplingOptions(filter, SKMipmapMode.None));
                }
            }
            preview.SavePng(path);
        }

        void Snap(string name)
        {
            if (dump == null) return;
            var frame = fx.Render(false, expressions, monitor);
            var view = frame.Area;
            view.Offset(frame.Origin);
            if (frame.Portrait.Border != null)
            {
                var bounds = Rectangle.Union(frame.Portrait.Bounds, frame.Portrait.TailBounds);
                bounds.Height++;
                view = Rectangle.Union(view, bounds);
            }
            SavePreview(frame, view, 4, Path.Combine(dump, "expression-" + name + ".png"));
        }

        Tick(60);
        Check("an untouched fall does not show a player reaction", p.onGround && expressions.Current == null);
        keys.Receive(DesktopInput.D, 1);
        Tick(6);
        Check("starting a run leaves the portrait hidden", p.Speed.X > 0 && expressions.Current == null);
        keys.Receive(DesktopInput.D, 0);
        Tick(14);

        keys.Receive(DesktopInput.S, 1);
        Tick(8);
        Check("crouching leaves the portrait hidden", p.Ducking && expressions.Current == null);
        keys.Receive(DesktopInput.S, 0);
        Tick();

        keys.Receive(DesktopInput.Jump, 1);
        Tick(7);
        Check("an ordinary jump does not count as a Hyper",
            p.Speed.Y < 0 && p.HyperJumpCount == 0 && expressions.Current == null);
        keys.Receive(DesktopInput.Jump, 0);
        Tick(60);
        Check("landing after an ordinary jump leaves the portrait hidden", p.onGround && expressions.Current == null);

        bool DashJump(bool crouched)
        {
            keys.Receive(DesktopInput.Capture, 1);
            p.ResetTo(new PointF(100, 170));
            p.Facing = 1;
            fx.Reset();
            Tick(3);
            keys.Receive(DesktopInput.D, 1);
            if (crouched) keys.Receive(DesktopInput.S, 1);
            keys.Receive(DesktopInput.Dash, 1);
            Tick(6);
            bool dashQuiet = p.State == Player.StDash && expressions.Current == null;
            keys.Receive(DesktopInput.Dash, 0);
            int jumps = p.JumpEffectCount;
            keys.Receive(DesktopInput.Jump, 1);
            for (int i = 0; i < 8 && p.JumpEffectCount == jumps; i++) Tick();
            keys.Receive(DesktopInput.D, 0);
            keys.Receive(DesktopInput.S, 0);
            keys.Receive(DesktopInput.Jump, 0);
            return dashQuiet;
        }

        Check("the dash stays quiet and a standing Super (260 / -105) does not show happiness",
            DashJump(crouched: false) && Near(p.Speed.X, 260f) && Near(p.Speed.Y, -105f) &&
            p.HyperJumpCount == 0 && expressions.Current == null);
        Check("the down-diagonal dash stays quiet and the actual Hyper (325 / -52.5) shows happiness",
            DashJump(crouched: true) && Near(p.Speed.X, 325f) && Near(p.Speed.Y, -52.5f) &&
            p.HyperJumpCount == 1 && expressions.Current == PetExpressions.Mood.Happy);
        Tick(24);
        var naked = fx.Render(false);
        var hitPixels = naked.Actor.Pixels.ToArray();
        var framed = fx.Render(false, expressions, monitor);
        Check("the portrait layer retains the original Celeste artwork at source resolution",
            PortraitMatchesSource(framed.Portrait.Artwork, "madeline/normal00"));
        Check("the portrait border has hard pixel edges and the original stepped opening",
            IsPixelPortraitBorder(framed.Portrait.Border));
        Check("the separate portrait layer preserves the sprite canvas and avatar's mouse target",
            framed.Portrait.Border != null && framed.Area == naked.Area && framed.ActorArea == naked.ActorArea &&
            framed.ActorOrigin == naked.ActorOrigin && framed.Actor.Pixels.SequenceEqual(hitPixels));
        Snap("hyper");
        expressions.SetEnabled(false);
        Check("switching expressions off removes the portrait immediately", expressions.Current == null);
        Tick(4 * 60 + 1);
        DashJump(crouched: true);
        Check("a successful Hyper while expressions are disabled stays quiet",
            p.HyperJumpCount == 2 && expressions.Current == null);
        expressions.SetEnabled(true);
        Check("switching expressions back on does not replay the suppressed Hyper", expressions.Current == null);
        DashJump(crouched: true);
        Check("a new Hyper can show happiness after expressions are re-enabled",
            p.HyperJumpCount == 3 && expressions.Current == PetExpressions.Mood.Happy);
        Tick(60);
        DashJump(crouched: true);
        Check("successive Hypers respect the four-second interval",
            p.HyperJumpCount == 4 && expressions.Current == null);
        Tick(4 * 60 + 1);

        void GrabWall()
        {
            p.ResetTo(new PointF(336, 128));
            p.Facing = 1;
            fx.Reset();
            expressions.Reset();
            keys.Receive(DesktopInput.GrabZ, 1);
            keys.Receive(DesktopInput.W, 1);
            Tick(10);
        }

        GrabWall();
        Check("grabbing a wall stays quiet until stamina is low", p.State == Player.StClimb && expressions.Current == null);
        p.Stamina = 20.2f;
        Tick(5);
        Check("crossing the climbing stamina threshold shows distress",
            p.IsLowStamina && expressions.Current == PetExpressions.Mood.Upset);
        Snap("tired");
        Tick(controlled: false);
        Check("automatic activity after control is released clears the expression", expressions.Current == null);
        keys.Receive(DesktopInput.Capture, 0);

        var drag = new DesktopDrag();
        PickUp(1);
        Check("releasing control and picking up Madeline does not bypass the interval",
            p.BeingDragged && expressions.Current == null);
        drag.Finish(p, monitor, 1.1, Scale, cancel: true);
        Tick(4 * 60 + 1, controlled: false);
        p.ResetTo(new PointF(160, 135));
        fx.Reset();
        expressions.SetEnabled(false);
        PickUp(2);
        Check("mouse pickup also stays quiet when expressions are disabled", p.BeingDragged && expressions.Current == null);
        drag.Finish(p, monitor, 2.1, Scale, cancel: true);
        expressions.SetEnabled(true);
        PickUp(3);
        Check("mouse pickup shows surprise while Madeline is being held",
            p.BeingDragged && expressions.Current == PetExpressions.Mood.Surprised);

        string previewFolder = dump == null ? null : Path.Combine(dump, "portrait-bounce");
        if (previewFolder != null) Directory.CreateDirectory(previewFolder);
        var bounce = new Rectangle[53];
        for (int frameNumber = 0; frameNumber < bounce.Length; frameNumber++)
        {
            var frame = fx.Render(false, expressions, monitor);
            bounce[frameNumber] = frame.Portrait.Bounds;
            if (previewFolder != null && frameNumber % 2 == 0)
            {
                var view = new Rectangle((int)p.Pos.X - 63, (int)p.Pos.Y - 62, 84, 70);
                SavePreview(frame, view, 3, Path.Combine(previewFolder, $"{frameNumber / 2:00}.png"));
            }
            Tick(controlled: false);
        }
        Check("the visible portrait stretches upward, squashes on impact and rebounds once",
            !bounce[0].IsEmpty && bounce[4].Height > bounce[0].Height &&
            bounce[8].Bottom < bounce[0].Bottom &&
            bounce[13].Width > bounce[8].Width && bounce[13].Height < bounce[8].Height &&
            bounce[17].Bottom < bounce[13].Bottom &&
            bounce[25].Size == bounce[30].Size && bounce[^1].IsEmpty);

        var offsetMonitor = new RectangleF(-280, 4, 280, 180);
        bool fitsCorners = true;
        for (int i = 0; i <= 26; i++)
        {
            var left = PetExpressions.Place(new Rectangle(-278, 5, 12, 17), offsetMonitor, true, i * Dt, out bool leftOfActor);
            var right = PetExpressions.Place(new Rectangle(-16, 175, 12, 17), offsetMonitor, false, i * Dt, out bool rightOfActor);
            fitsCorners &= !left.IsEmpty && !leftOfActor && left.Left > -266 && left.Top > 4 &&
                !right.IsEmpty && rightOfActor && right.Right < -16 && right.Bottom < 184;
        }
        Check("portraits switch sides and stay on an offset monitor throughout the bounce", fitsCorners);

        drag.Finish(p, monitor, 4, Scale, cancel: true);
        expressions.Reset();
        Tap(10);
        Tap(14);
        Check("two clicks leave Madeline's expression alone", expressions.Current == null);
        bool firstEscape = Tap(19.9);
        var angry = fx.Render(false, expressions, monitor).Portrait;
        Check("three clicks within ten seconds show the original angry portrait in the pixel frame",
            firstEscape && expressions.Current == PetExpressions.Mood.Angry && IsPixelPortraitBorder(angry.Border) &&
            PortraitMatchesSource(angry.Artwork, "madeline/angry00"));
        if (dump != null && angry.Artwork != null)
            SavePreview(fx.Render(false, expressions, monitor),
                new Rectangle((int)p.Pos.X - 63, (int)p.Pos.Y - 62, 84, 70), Scale, Path.Combine(dump, "portrait-angry.png"));

        Check("each follow-up click during the angry reaction requests an escape", Tap(20) && Tap(20.3));
        Tick(60);
        Check("follow-up clicks still request escapes after the portrait fades", Tap(24) && expressions.Current == null);
        expressions.ReleaseControl();
        Check("releasing keyboard control preserves the remaining angry window", Tap(29.89));
        Check("follow-up clicks leave the ten-second deadline at the original trigger", !Tap(30));
        Tap(33);
        Check("widely spaced clicks do not accumulate toward anger", !Tap(41) && expressions.Current == null);
        bool nextEscape = Tap(41.2);
        Check("an expired click preserves the two recent clicks in the rolling window",
            nextEscape && expressions.Current == PetExpressions.Mood.Angry);
        expressions.SetEnabled(false);
        Check("turning off expressions clears the angry portrait and follow-up escapes",
            !Tap(42) && !Tap(42.2) && !Tap(42.4) && expressions.Current == null);

        bool Tap(double at)
        {
            PointF pointer = new PointF(p.Pos.X * Scale, p.Pos.Y * Scale - 12);
            drag.Begin(p, pointer, at, Scale);
            return drag.Finish(p, monitor, at + .06, Scale, cancel: false) && expressions.Clicked(at + .06);
        }

        void PickUp(double at)
        {
            PointF pointer = new PointF(p.Pos.X * Scale, p.Pos.Y * Scale - 12);
            drag.Begin(p, pointer, at, Scale);
            if (drag.Move(p, new PointF(pointer.X + 6, pointer.Y), at + .03, Scale))
                expressions.PickedUp();
        }
    }

    sealed class EscapeRoll(int choice) : Random
    {
        public override int Next(int maxValue) => Math.Min(choice, maxValue - 1);
    }

    static void TestClickEscape()
    {
        var player = new Player
        {
            Solids = new List<Solid> { new Solid { L = 0, T = 170, R = 480, B = 180 } },
        };
        var dash = Run(choice: 0);
        Check("the straight escape performs exactly one original dash without a jump",
            dash.Move == ClickEscape.Technique.Dash && dash.Dash && dash.Jumps == 0 && dash.Released);
        var super = Run(choice: 1);
        Check("the Super escape jumps on the first frame after the horizontal dash aim",
            super.Move == ClickEscape.Technique.Super && super.Super && super.Jumps == 1 && super.Released);
        var wave = Run(choice: 2);
        Check("the Wavedash escape jumps, dashes diagonally down and takes off on the next landing frame",
            wave.Move == ClickEscape.Technique.Wavedash && wave.Diagonal && wave.Wave && wave.Jumps == 2 && wave.Released);
        Check("the Wavedash uses the original ground refill before its second takeoff", wave.Refilled);

        player.Solids.Add(new Solid { L = 104, T = 130, R = 112, B = 170 });
        var blocked = Run(choice: 0, direction: -1);
        Check("the escape chooses the clear side when the direction away from the click is blocked", blocked.Dash);
        player.Solids.RemoveAt(1);

        player.Solids.Add(new Solid { L = 0, T = 145, R = 480, B = 159 });
        var ceiling = Run(choice: 2);
        Check("a low ceiling leaves the ordinary dash available without an attempted jump",
            ceiling.Move == ClickEscape.Technique.Dash && ceiling.Dash && ceiling.Jumps == 0);
        player.Solids.RemoveAt(1);

        var emptyDash = Run(choice: 0, emptyDash: true);
        Check("the escape waits in the air for the game's normal dash refill", emptyDash.WaitedForRefill && emptyDash.Dash);

        player.ResetTo(new PointF(100, 170));
        var repeated = new ClickEscape(new EscapeRoll(0));
        int initialDashes = player.DashSequenceCount;
        repeated.Request(98);
        for (int i = 0; i < 80 && repeated.Active; i++)
        {
            if (i is 2 or 4) repeated.Request(98);
            player.Update(Dt, repeated.Sample(player, Dt));
        }
        Check("taps during a dash each queue an escape without interrupting the current move",
            player.DashSequenceCount == initialDashes + 3 && !repeated.Active);

        player.ResetTo(new PointF(100, 0));
        player.Dashes = 0;
        repeated.Request(98);
        initialDashes = player.DashSequenceCount;
        for (int i = 0; i < 52; i++) repeated.Sample(player, Dt);
        Check("an escape attempt expires when the player cannot refill in time",
            player.DashSequenceCount == initialDashes && !repeated.Active);

        (ClickEscape.Technique Move, bool Dash, bool Super, bool Diagonal, bool Wave, bool Refilled,
            int Jumps, bool Released, bool WaitedForRefill) Run(int choice, int direction = 1, bool emptyDash = false)
        {
            player.ResetTo(new PointF(100, emptyDash ? 142 : 170));
            if (emptyDash) player.Dashes = 0;
            var escape = new ClickEscape(new EscapeRoll(choice));
            escape.Request(98);
            int firstDash = player.DashSequenceCount, firstJump = player.JumpEffectCount;
            int firstLanding = player.WavedashCount, aimedAt = -1, landedAt = -1;
            bool dashSpeed = false, superSpeed = false, diagonal = false, waveSpeed = false, refilled = false;
            bool waitedForRefill = false;
            for (int i = 0; i < 70 && escape.Active; i++)
            {
                PetInput input = escape.Sample(player, Dt);
                if (i == 0) waitedForRefill = !input.DashPressed && player.Dashes == 0;
                player.Update(Dt, input);
                if (player.State == Player.StDash && player.DashDir.X != 0f && aimedAt < 0) aimedAt = i;
                if (player.State == Player.StDash && player.DashDir.X == direction && Near(player.Speed.X, direction * 240f))
                    dashSpeed = true;
                if (player.DashDir.X * direction > 0f && player.DashDir.Y > 0f && player.Pos.Y < 170f)
                    diagonal = true;
                if (player.WavedashCount > firstLanding && landedAt < 0) landedAt = i;
                if (i == aimedAt + 1 && aimedAt >= 0 && Near(player.Speed.X, direction * 260f) && Near(player.Speed.Y, -105f))
                    superSpeed = true;
                if (i == landedAt + 1 && landedAt >= 0 && Near(player.Speed.X, direction * 325f) && Near(player.Speed.Y, -52.5f))
                {
                    waveSpeed = true;
                    refilled = player.Dashes == player.DashCapacity;
                }
            }
            bool oneDash = player.DashSequenceCount == firstDash + 1;
            return (escape.Selected, dashSpeed && oneDash, superSpeed && oneDash, diagonal,
                waveSpeed && oneDash, refilled, player.JumpEffectCount - firstJump, !escape.Active, waitedForRefill);
        }
    }

    static bool PortraitMatchesSource(Bitmap artwork, string path)
    {
        if (artwork == null) return false;
        string directory = CelesteInstall.AtlasesDirectory;
        var entries = CelesteAtlas.ReadMeta(Path.Combine(directory, "Portraits.meta"), out var pages);
        var entry = entries[path];
        using Bitmap source = CelesteAtlas.DecodePage(Path.Combine(directory, pages[entry.Page], path + ".data"));
        return artwork.Width == source.Width && artwork.Height == source.Height &&
            artwork.Pixels.SequenceEqual(source.Pixels);
    }

    static bool IsPixelPortraitBorder(Bitmap border)
    {
        if (border == null || border.Width != 38 || border.Height != 38) return false;
        var pixels = border.Pixels;
        for (int i = 3; i < pixels.Length; i += 4)
            if (pixels[i] != 0 && pixels[i] != 255) return false;
        return border.GetPixel(19, 0) == Color.FromArgb(23, 37, 64) &&
            border.GetPixel(19, 1) == Color.FromArgb(212, 235, 255) &&
            border.GetPixel(19, 2).A == 0 && border.GetPixel(0, 0).A == 0 &&
            border.GetPixel(2, 2).A == 255 && border.GetPixel(3, 2).A == 0;
    }

    static void TestController()
    {
        var p = new Player();
        var keys = new DesktopInput();
        keys.Receive(DesktopInput.Capture, 1);

        PetInput Sample(GamepadButtons buttons = 0, float x = 0, float y = 0, float l2 = 0, float r2 = 0)
            => keys.Sample(p, new GamepadState(1, buttons, x, y, l2, r2));

        var dpad = Sample(GamepadButtons.Left | GamepadButtons.Down);
        Check("DualSense D-pad uses the original digital move and dash aim",
            dpad.MoveX == -1 && dpad.MoveY == 1 && dpad.AimX == -1 && dpad.AimY == 1);
        var diagonal = Sample(x: .70710677f, y: .70710677f);
        Check("full analog diagonal aims up-right without triggering the .7 vertical move threshold",
            diagonal.MoveX == 1 && diagonal.MoveY == 0 && diagonal.GliderMoveY == -1 &&
            diagonal.AimX == 1 && diagonal.AimY == -1 &&
            diagonal.FeatherX > .6f && diagonal.FeatherY < -.6f);
        var below = Sample(x: .24f, y: -.24f);
        Check("stick drift is removed by FNA's axis deadzone and Monocle's radial deadzone",
            below.MoveX == 0 && below.MoveY == 0 && below.AimX == 0 && below.AimY == 0);
        var radial = Sample(x: .38f, y: .38f);
        Check("the radial deadzone accepts two sub-threshold components together",
            radial.MoveX == 0 && radial.MoveY == 0 && radial.AimX == 1 && radial.AimY == -1);
        var upward = Sample(x: .8f, y: .4435f);
        var downward = Sample(x: .8f, y: -.4435f);
        Check("dash aim applies the original narrower upper cardinal cone",
            upward.AimY == -1 && downward.AimX == 1 && downward.AimY == 0);
        Check("D-pad still drives movement when the corresponding stick is below its threshold",
            Sample(GamepadButtons.Up, y: .35f).MoveY == -1);
        Sample(x: .65f);
        keys.Receive(DesktopInput.A, 1);
        var mixed = Sample(x: .65f);
        Check("digital movement uses TakeNewer while analog aim prefers the stronger side",
            mixed.MoveX == -1 && mixed.AimX == -1 && mixed.FeatherX == -1f);
        keys.Receive(DesktopInput.A, 0);

        Check("Cross buffers a jump", Sample(GamepadButtons.Cross).JumpPressed && p.HasJumpBuffer);
        p.ResetTo(PointF.Empty);
        Check("holding Cross does not re-buffer jump", !Sample(GamepadButtons.Cross).JumpPressed);
        Check("Triangle can buffer jump while Cross is held",
            Sample(GamepadButtons.Cross | GamepadButtons.Triangle).JumpPressed);
        p.ResetTo(PointF.Empty);
        Check("Square buffers a dash", Sample(GamepadButtons.Square).DashPressed);
        p.ResetTo(PointF.Empty);
        Check("Circle can buffer dash while Square is held",
            Sample(GamepadButtons.Square | GamepadButtons.Circle).DashPressed);
        Check("L1 and R1 both grab", Sample(GamepadButtons.L1).GrabHeld && Sample(GamepadButtons.R1).GrabHeld);
        Check("L2 and R2 apply FNA's deadzone before Celeste's .2 threshold",
            !Sample(l2: .2f, r2: .2f).GrabHeld && !Sample(l2: .294f, r2: .294f).GrabHeld &&
            Sample(l2: .295f).GrabHeld && Sample(r2: .295f).GrabHeld);
        p.ResetTo(PointF.Empty);
        keys.Receive(DesktopInput.Capture, 0);
        var released = Sample(GamepadButtons.Cross, x: 1f);
        Check("releasing control gates both the keyboard and DualSense",
            released.MoveX == 0 && !released.JumpPressed && !released.JumpHeld);
    }

    static void TestRumble()
    {
        var motors = new List<float>();
        var rumble = new ControllerRumble(motors.Add);
        rumble.Advance(0f, 1, true);
        rumble.Request(RumbleStrength.Strong, RumbleLength.Medium);
        rumble.Advance(.125f, 1, true);
        rumble.Request(RumbleStrength.Light, RumbleLength.Long);
        rumble.Request(RumbleStrength.Strong, RumbleLength.Short);
        Check("weaker pulses and shorter equal pulses cannot interrupt a dash",
            motors.SequenceEqual(new[] { 1f }));
        rumble.Request(RumbleStrength.Strong, RumbleLength.Medium);
        rumble.Advance(.125f, 1, true);
        Check("a longer equal pulse extends the countdown", motors.SequenceEqual(new[] { 1f, 1f }));
        rumble.Advance(.125f, 1, true);
        Check("expiry stops both motors", motors.SequenceEqual(new[] { 1f, 1f, 0f }));

        motors.Clear();
        rumble.SetAmount(RumbleAmount.Half);
        rumble.Request(RumbleStrength.Climb, RumbleLength.Short);
        rumble.Request(RumbleStrength.Light, RumbleLength.Medium);
        rumble.Request(RumbleStrength.Medium, RumbleLength.Short);
        rumble.Request(RumbleStrength.Strong, RumbleLength.Short);
        Check("Half uses the original four amplitudes at 50 percent",
            motors.SequenceEqual(new[] { .025f, .075f, .2f, .5f }));
        rumble.SetAmount(RumbleAmount.Off);
        rumble.Advance(Dt, 1, true);
        rumble.Request(RumbleStrength.Strong, RumbleLength.Medium);
        Check("Off stops an active pulse and suppresses the next request",
            motors.SequenceEqual(new[] { .025f, .075f, .2f, .5f, 0f }));

        motors.Clear();
        rumble.SetAmount(RumbleAmount.On);
        rumble.Request(RumbleStrength.Medium, RumbleLength.Short);
        rumble.Advance(Dt, 1, false);
        rumble.Request(RumbleStrength.Strong, RumbleLength.Medium);
        Check("losing control stops rumble immediately", motors.SequenceEqual(new[] { .4f, 0f }));
        rumble.Advance(Dt, 1, true);
        rumble.Request(RumbleStrength.Strong, RumbleLength.Medium);
        rumble.Advance(Dt, 0, true);
        rumble.Request(RumbleStrength.Strong, RumbleLength.Medium);
        rumble.Advance(Dt, 2, true);
        rumble.Request(RumbleStrength.Light, RumbleLength.Short);
        Check("a replacement controller starts with a fresh countdown",
            motors.SequenceEqual(new[] { .4f, 0f, 1f, .15f }));
        Check("FNA truncates the original amplitudes to unsigned 16-bit values",
            SdlController.MotorValue(0f) == 0 && SdlController.MotorValue(.05f) == 3276 &&
            SdlController.MotorValue(.15f) == 9830 && SdlController.MotorValue(.4f) == 26214 &&
            SdlController.MotorValue(.5f) == 32767 && SdlController.MotorValue(1f) == 65535);
    }

    static void TestDreamRumble()
    {
        var player = new Player
        {
            Pos = new PointF(90, 150),
            Solids = new List<Solid> { new Solid { Id = (IntPtr)1, L = 100, T = 0, R = 200, B = 200, Dream = true } },
            FreezeFramesEnabled = false,
        };
        var pulses = new List<(RumbleStrength, RumbleLength)>();
        player.OnRumble = (strength, length) => pulses.Add((strength, length));
        var input = new PetInput { MoveX = 1, AimX = 1 };
        player.BufferDash();
        for (int i = 0; i < 20 && player.State != Player.StDreamDash; i++)
        {
            input.DashPressed = player.HasDashBuffer;
            player.Update(Dt, input);
        }
        Check("entering the dream block preserves the DashBegin pulse",
            player.State == Player.StDreamDash &&
            pulses.SequenceEqual(new[] { (RumbleStrength.Strong, RumbleLength.Medium) }));
        pulses.Clear();
        for (int i = 0; i < 60 && player.State == Player.StDreamDash; i++) player.Update(Dt, default);
        Check("dream traversal refreshes light rumble before the medium exit pulse",
            player.State == Player.StNormal && pulses.Count > 2 &&
            pulses[^1] == (RumbleStrength.Medium, RumbleLength.Short) &&
            pulses.Take(pulses.Count - 1).All(pulse => pulse == (RumbleStrength.Light, RumbleLength.Medium)));
    }

    static void TestDrag(SpriteBank bank)
    {
        var p = new Player();
        p.ResetSprite(bank, "player", Player.ModeMadeline, null);
        var world = new DesktopTerrain();
        world.Apply(p, new[] { screen }, new[] { first, second }, 100, Scale, WindowMaterial.Solid, Dt);
        var drag = new DesktopDrag();

        p.ResetTo(new PointF(60, 120));
        p.State = Player.StDash;
        p.Dashes = 0;
        p.Stamina = 45f;
        drag.Begin(p, new PointF(180, 336), 10, Scale);
        Check("pressing a moving player leaves the dash running until the pointer actually drags",
            !p.BeingDragged && p.State == Player.StDash && p.Dashes == 0 && p.Stamina == 45f);
        drag.Move(p, new PointF(450, 260), 10.08, Scale);
        Check("picking up an active dash starts the dangling pose and preserves resources",
            p.BeingDragged && p.State == Player.StNormal && p.AnimId == "dangling" &&
            p.Dashes == 0 && p.Stamina == 45f);
        p.Update(Dt, default);
        Check("drag uses the grabbed offset and snaps to the game grid",
            p.Pos == new PointF(150, 95) && p.Speed.IsEmpty);
        drag.Finish(p, world.NearestMonitor(p.Pos), 10.2, Scale, cancel: false);
        Check("release after a stationary hold drops from the selected position",
            !p.BeingDragged && p.Pos == new PointF(150, 95) && p.Speed.IsEmpty);
        for (int i = 0; i < 60 && p.GroundId != (IntPtr)22; i++) p.Update(Dt, default);
        Check("a dropped Madeline lands on the window underneath", p.GroundId == (IntPtr)22 && p.Pos.Y == 110);

        drag.Begin(p, new PointF(450, 306), 11, Scale);
        drag.Move(p, new PointF(720, 306), 11.01, Scale);
        drag.Finish(p, world.NearestMonitor(p.Pos), 11.02, Scale, cancel: false);
        Check("a fast drag releases with the upstream 400 game pixels/sec throw cap",
            !p.BeingDragged && Near(p.Speed.X, 400f) && Near(p.Speed.Y, 0f));

        drag.Begin(p, new PointF(720, 306), 12, Scale);
        drag.Move(p, new PointF(730, 320), 12.02, Scale);
        bool cancelled = drag.Finish(p, world.NearestMonitor(p.Pos), 12.03, Scale, cancel: true);
        Check("cancelling a drag clears the capture without throwing or counting a click",
            !cancelled && !p.BeingDragged && p.Speed.IsEmpty);

        PointF pos = p.Pos;
        drag.Begin(p, new PointF(730, 320), 13, Scale);
        bool moved = drag.Move(p, new PointF(731, 321), 13.02, Scale);
        bool click = drag.Finish(p, world.NearestMonitor(p.Pos), 13.04, Scale, cancel: false);
        Check("a short click with small pointer jitter leaves Madeline in place",
            click && !moved && p.Pos == pos && p.Speed.IsEmpty);

        drag.Begin(p, new PointF(730, 320), 14, Scale);
        Check("holding the mouse down does not count as a click",
            !drag.Finish(p, world.NearestMonitor(p.Pos), 14.8, Scale, cancel: false));

        p.State = Player.StDash;
        p.Speed = new PointF(240, 0);
        drag.Begin(p, new PointF(730, 320), 15, Scale, allowDrag: false);
        bool escapeTap = drag.Finish(p, world.NearestMonitor(p.Pos), 15.08, Scale, cancel: false);
        Check("a tap on an escaping Madeline registers while preserving her dash",
            escapeTap && !p.BeingDragged && p.State == Player.StDash && p.Speed.X == 240);
        drag.Begin(p, new PointF(730, 320), 16, Scale, allowDrag: false);
        drag.Move(p, new PointF(780, 330), 16.05, Scale);
        Check("a held or moved pointer cannot drag an escaping Madeline",
            !drag.Finish(p, world.NearestMonitor(p.Pos), 16.1, Scale, cancel: false) &&
            !p.BeingDragged && p.Pos == pos && p.Speed.X == 240);
    }

    static void TestRefills(SpriteBank bank)
    {
        var sounds = new List<string>();
        var pulses = new List<(RumbleStrength, RumbleLength)>();
        using var refills = new DesktopItems(sounds.Add, (strength, length) => pulses.Add((strength, length)));
        var anchor = new DesktopItemPlacement(DesktopItemKind.Refill, 810, 294);
        using var icon = refills.CreateIcon(DesktopItemKind.Refill);
        bool loaded = icon != null && refills.Add(anchor);
        Check("the desktop palette uses the installed Celeste refill frames", loaded);
        if (!loaded) return;

        Bitmap original = Sprites.Get("refill/idle00", false);
        var sourceColors = Pixels(original).ToArray();
        var visibleColors = Pixels(icon).ToArray();
        Check("the original green facets get brighter without losing their hue",
            visibleColors.Count(c => c.A == 255 && c.G >= 219 && c.G > c.R && c.G > c.B) > 10 &&
            !sourceColors.Any(c => c.A == 255 && c.G >= 219 && c.G > c.R && c.G > c.B));
        Check("bloom preserves the sprite's white highlights and the icon's transparent corners",
            sourceColors.Count(c => c.ToArgb() == -1) == visibleColors.Count(c => c.ToArgb() == -1) &&
            icon.GetPixel(0, 0).A == 0);

        var p = new Player();
        p.ResetSprite(bank, "player", Player.ModeMadeline, null);
        var terrain = new DesktopTerrain();
        refills.Prepare(p, terrain, Scale);
        var preview = refills.Render(Scale);
        var image = preview[0].Image;
        Check("the crystal gets an independent desktop layer at the drop location",
            preview.Length == 1 && preview[0].Depth == 1 && preview[0].ItemId > 0 && image.Pixels != IntPtr.Zero &&
            image.Bounds.X + image.Bounds.Width / 2f == anchor.X);
        Check("the ready crystal has a translucent glow on the desktop", HasHalo(image));
        refills.Prepare(p, terrain, 6);
        var resized = refills.Render(6)[0].Image.Bounds;
        Check("resizing Madeline leaves the crystal anchored in screen points", resized.X + resized.Width / 2f == anchor.X);
        refills.Prepare(p, terrain, Scale);

        static IEnumerable<Color> Pixels(Bitmap bitmap)
        {
            for (int y = 0; y < bitmap.Height; y++)
                for (int x = 0; x < bitmap.Width; x++) yield return bitmap.GetPixel(x, y);
        }

        PointF center = anchor.ToGame(Scale);
        p.ResetTo(new PointF(center.X, center.Y - 6f));
        p.Dashes = 0;
        refills.CheckCollisions(p);
        Check("refill contact uses the hurtbox and excludes touching edges", p.Dashes == 0 && sounds.Count == 0);
        p.Pos = center;
        p.Dashes = p.DashCapacity;
        p.Stamina = 20f;
        refills.CheckCollisions(p);
        Check("full dash and exactly 20 stamina leave the crystal available", p.Stamina == 20f && sounds.Count == 0);
        p.Stamina = 19f;
        Check("less than 20 stamina can use a refill even with full dash", p.UseRefill() && p.Stamina == 110f);

        p.Dashes = 0;
        p.Stamina = 50f;
        float time = 0f;
        int activeFrames = 0;
        void Tick(PetInput input = default)
        {
            refills.Prepare(p, terrain, Scale);
            bool active = !p.IsHitStopped;
            if (active)
            {
                activeFrames++;
                refills.BeforePlayer(p, Dt, time += Dt);
            }
            p.Update(Dt, input, time, refills.CheckCollisions);
            if (active && refills.AfterPlayer(p, Dt, time)) p.ApplyFreeze(DesktopRefill.PickupFreeze);
        }

        Tick();
        Check("pickup restores dash and stamina with the original sound and rumble",
            p.Dashes == p.DashCapacity && p.Stamina == 110f &&
            sounds.SequenceEqual(new[] { DesktopRefill.TouchSound }) &&
            pulses.SequenceEqual(new[] { (RumbleStrength.Medium, RumbleLength.Medium) }));
        int freezeFrames = 0;
        while (p.IsHitStopped && freezeFrames < 8) { Tick(); freezeFrames++; }
        Check("pickup leaves the sprite visible during its three hitstop frames",
            freezeFrames == 3 && refills.Render(Scale)[0].Depth == 1 && activeFrames == 1);
        Tick();
        Check("the hollow outline moves behind Madeline after hitstop", refills.Render(Scale)[0].Depth == -1);
        Check("the hollow crystal keeps the last of its light just after pickup", HasHalo(refills.Render(Scale)[0].Image));
        p.Dashes = 0;
        p.Pos = center;
        refills.CheckCollisions(p);
        Check("the hollow crystal cannot be collected again", p.Dashes == 0 && sounds.Count == 1);
        p.Pos = new PointF(center.X - 100f, center.Y);
        Tick(); Tick(); Tick(); Tick();
        Check("the shatter coroutine produces a foreground effect after its wait", refills.Render(Scale).Length == 2);

        while (activeFrames < 19) Tick();
        Check("the hollow crystal's light fades out while it is unavailable", !HasHalo(refills.Render(Scale)[0].Image));

        while (sounds.Count < 2 && activeFrames < 154) Tick();
        Check("the refill returns in about 2.5 active seconds with regen particles and sound",
            activeFrames >= 149 && activeFrames <= 151 && sounds.SequenceEqual(new[] { DesktopRefill.TouchSound, DesktopRefill.ReturnSound }) &&
            refills.Render(Scale) is { Length: 2 } && refills.Render(Scale)[0].Depth == 1);
        p.Pos = center;
        refills.CheckCollisions(p);
        Check("the regenerated crystal can be collected", p.Dashes == p.DashCapacity && sounds.Last() == DesktopRefill.TouchSound);
        refills.Clear();
        Check("clearing desktop items removes their render layers", refills.Render(Scale).IsEmpty);

        sounds.Clear();
        refills.Add(anchor);
        p.ResetTo(new PointF(center.X - 33f, center.Y + 5f));
        var keys = new DesktopInput();
        keys.Receive(DesktopInput.Capture, 1);
        keys.Receive(DesktopInput.D, 1);
        keys.Receive(DesktopInput.Dash, 1);
        for (int i = 0; i < 18 && sounds.Count == 0; i++) Tick(keys.Sample(p));
        Check("a real dash through the crystal recharges Madeline before she lands",
            p.State == Player.StDash && !p.onGround && p.Dashes == p.DashCapacity &&
            sounds.SequenceEqual(new[] { DesktopRefill.TouchSound }));
    }

    static void TestDesktopItems(SpriteBank bank)
    {
        var p = new Player();
        p.ResetSprite(bank, "player", Player.ModeMadeline, null);
        var terrain = new DesktopTerrain();
        var sounds = new List<string>();
        using var items = new DesktopItems(sounds.Add, (_, _) => { });
        float time = 0f;
        void Tick(PetInput input = default)
        {
            items.Prepare(p, terrain, Scale);
            bool frozen = p.IsHitStopped;
            if (!frozen)
            {
                time += Dt;
                items.BeforePlayer(p, Dt, time);
                p.Sprite.Update(Dt);
            }
            p.Update(Dt, input, time, items.CheckCollisions);
            if (!frozen && items.AfterPlayer(p, Dt, time)) p.ApplyFreeze(DesktopRefill.PickupFreeze);
        }

        bool artAvailable = true;
        foreach (DesktopItemKind kind in Enum.GetValues<DesktopItemKind>())
        {
            using Bitmap icon = items.CreateIcon(kind);
            bool available = icon != null && HasPixels(icon, new Rectangle(0, 0, icon.Width, icon.Height));
            Check($"the palette renders original art for {kind}", available);
            artAvailable &= available;
        }
        if (!artAvailable) return;

        var zip = new DesktopItemPlacement(DesktopItemKind.ZipMover, 420f, 240f, RouteX: 288f);
        items.Add(zip);
        p.ResetTo(new PointF(140f, 72f));
        Tick();
        Check("a rider starts the red-light platform's arming delay", p.Pos.X == 140f && sounds.SingleOrDefault() == DesktopZipMover.MoveSound);
        for (int i = 0; i < 46 && p.Solids[0].L < 220f; i++) Tick();
        Check("the platform reaches its endpoint and carries Madeline along the cable",
            p.Solids[0].L == 220f && p.Pos == new PointF(236f, 72f) && p.GroundSurfaceSoundIndex == 7);
        p.Pos = new PointF(90f, 75f);
        for (int i = 0; i < 158; i++) Tick();
        Check("the zip mover pauses and returns to the original end", p.Solids[0].L == 124f);
        var zipLayers = items.Render(Scale);
        int zipId = zipLayers[1].ItemId;
        items.BeginDrag(zipId, new PointF(420f, 240f), Scale);
        items.Drag(zipId, new PointF(456f, 255f));
        items.Prepare(p, terrain, Scale);
        Check("dragging moves the cable and releases its solid while held", p.Solids.Count == 0 &&
            items.SavePlacements().Single() == (zip with { X = 456f, Y = 255f }));
        items.EndDrag(zipId, new PointF(456f, 255f), cancel: false);
        items.Prepare(p, terrain, Scale);
        Check("the moved platform collides at its new location and can be removed", p.Solids[0].L == 136f && items.Delete(zipId));

        items.Add(new DesktopItemPlacement(DesktopItemKind.Cloud, 360, 420));
        p.ResetTo(new PointF(120f, 148f));
        items.Prepare(p, terrain, Scale);
        p.MoveV(-18f);
        bool fromBelow = p.Pos.Y == 130f;
        p.MoveV(24f);
        Check("clouds can be crossed from below and landed on from above", fromBelow && p.Pos.Y == 140f && p.GroundSurfaceSoundIndex == 4);
        for (int i = 0; i < 60 && p.Speed.Y > -200f; i++) Tick();
        Check("the cloud dips, rises and boosts its rider at -200 px/s", p.Speed.Y == -200f && p.Pos.Y < 140f &&
            sounds.Contains("event:/game/04_cliffside/cloud_blue_boost"));

        items.Clear();
        terrain.Apply(p, new[] { screen }, Array.Empty<Native.WindowInfo>(), 0, Scale, WindowMaterial.Solid, Dt);
        float floor = terrain.Monitors[0].Bottom;
        items.Add(new DesktopItemPlacement(DesktopItemKind.Spring, 540, (floor - 20f) * Scale));
        p.ResetTo(new PointF(180f, floor - 1f));
        p.Speed = new PointF(30f, 80f);
        p.Dashes = 0;
        Tick();
        Check("the floor spring attaches below the cursor and triggers SuperBounce", p.Speed == new PointF(0f, -185f) &&
            p.Pos.Y == floor - 6f && p.Dashes == p.DashCapacity && sounds.Contains("event:/game/general/spring"));

        items.Clear();
        terrain = new DesktopTerrain();
        items.Add(new DesktopItemPlacement(DesktopItemKind.DustBunny, 600, 300));
        p.ResetTo(new PointF(200, 104));
        p.Invincible = true;
        Tick();
        bool protectedPlayer = !p.IsDead;
        p.Invincible = false;
        Tick();
        Check("the Chapter 3 dust bunny uses its lethal contact collider and honors invincibility", protectedPlayer && p.IsDead);

        items.Clear();
        items.Add(new DesktopItemPlacement(DesktopItemKind.Feather, 600, 300));
        p.ResetTo(new PointF(200, 104));
        p.Dashes = 0;
        Tick();
        Check("touching the feather enters the original transformation", p.State == Player.StStarFly && p.AnimId == "startStarFly" &&
            sounds.Contains(DesktopFeather.GetSound));
        var flyRight = new PetInput { FeatherX = 1f, MoveX = 1, AimX = 1 };
        for (int i = 0; i < 50 && !p.StarFlyOutline; i++) Tick(flyRight);
        Check("the feather launches at 250 px/s and restores dashes", p.StarFlyOutline && p.Speed.X == 250f &&
            p.Dashes == p.DashCapacity && p.HairCount == 7);
        Tick(new PetInput { FeatherY = -1f, MoveY = -1, AimY = -1 });
        Check("the feather turns toward held input at the original angular rate",
            Near(MathF.Atan2(p.Speed.Y, p.Speed.X), -5.5850534f * Dt));
        for (int i = 0; i < 124 && p.State == Player.StStarFly; i++) Tick(flyRight);
        Check("feather flight expires after two seconds of steering", p.State == Player.StNormal && p.Speed.X <= 140f);
        for (int i = 0; i < 65 && !sounds.Contains(DesktopFeather.ReturnSound); i++) Tick();
        Check("the feather returns with its original respawn event", sounds.Contains(DesktopFeather.ReturnSound));
    }

    static void TestBoostersAndMoveBlocks(SpriteBank bank)
    {
        var player = new Player();
        player.ResetSprite(bank, "player", Player.ModeMadeline, null);
        var terrain = new DesktopTerrain();
        var sounds = new List<string>();
        using var items = new DesktopItems(sounds.Add, (_, _) => { });
        float time = 0f;
        void Tick(PetInput input = default)
        {
            items.Prepare(player, terrain, Scale);
            bool frozen = player.IsHitStopped;
            if (!frozen)
            {
                time += Dt;
                items.BeforePlayer(player, Dt, time);
                player.Sprite.Update(Dt);
            }
            player.Update(Dt, input, time, items.CheckCollisions);
            if (!frozen && items.AfterPlayer(player, Dt, time, input)) player.ApplyFreeze(DesktopRefill.PickupFreeze);
        }

        terrain.Apply(player, new[] { screen }, Array.Empty<Native.WindowInfo>(), 0, Scale, WindowMaterial.Solid, Dt);
        bool newZipShapes = items.Place(DesktopItemKind.ZipSquare, new PointF(480f, 330f), terrain, Scale) &&
            items.Place(DesktopItemKind.ZipTall, new PointF(750f, 390f), terrain, Scale);
        items.Prepare(player, terrain, Scale);
        var placements = items.SavePlacements();
        Check("Chapter 1 zip movers use the square and tall colliders with the correct cable axes",
            newZipShapes && placements[0].RouteX != 0 && placements[0].RouteY == 0 &&
            placements[1].RouteX == 0 && placements[1].RouteY != 0 &&
            player.Solids[^2].R - player.Solids[^2].L == 24f && player.Solids[^2].B - player.Solids[^2].T == 24f &&
            player.Solids[^1].R - player.Solids[^1].L == 16f && player.Solids[^1].B - player.Solids[^1].T == 32f);

        items.Clear();
        terrain = new DesktopTerrain();
        var aimRight = new PetInput { MoveX = 1, AimX = 1, FeatherX = 1f };
        var green = new DesktopItemPlacement(DesktopItemKind.BoosterGreen, 600, 300);
        items.Add(green);
        player.ResetTo(new PointF(200, 108));
        player.Dashes = 0;
        Tick();
        Check("the green bubble captures Madeline and refills her dash", player.State == Player.StBoost &&
            player.Dashes == player.DashCapacity && sounds.Contains(DesktopBooster.Sound(false, "enter")));
        player.BufferDash();
        Tick(aimRight);
        for (int i = 0; i < 8 && player.Speed.X == 0f; i++) Tick(aimRight);
        Check("a green booster launches a normal dash on input without spending the refill",
            player.State == Player.StDash && player.Speed.X == 240f && player.Dashes == player.DashCapacity &&
            sounds.Contains(DesktopBooster.Sound(false, "dash")));
        for (int i = 0; i < 100 && !sounds.Contains(DesktopBooster.Sound(false, "reappear")); i++) Tick();
        Check("the green booster pops when the dash ends and reappears", sounds.Contains(DesktopBooster.Sound(false, "end")) &&
            sounds.Contains(DesktopBooster.Sound(false, "reappear")));

        items.Clear();
        items.Add(green with { Kind = DesktopItemKind.BoosterRed });
        player.ResetTo(new PointF(200, 108));
        Tick();
        int windup = 0;
        while (player.State == Player.StBoost && windup < 26) { Tick(aimRight); windup++; }
        for (int i = 0; i < 40; i++) Tick(aimRight);
        Check("the red bubble releases automatically and sustains a 240 px/s red dash",
            windup >= 15 && windup <= 18 && player.State == Player.StRedDash && player.Speed.X == 240f &&
            player.IsDashAttacking && sounds.Contains(DesktopBooster.Sound(true, "dash")));
        terrain.BaseSolids.Add(new Solid { Id = (IntPtr)99, L = player.Pos.X + 12, R = player.Pos.X + 20, T = -300, B = 500 });
        for (int i = 0; i < 8 && player.State == Player.StRedDash; i++) Tick(aimRight);
        Check("a wall ends red booster travel, pops the bubble and enters hit squash",
            player.State == Player.StHitSquash && player.Speed.X == 0f && sounds.Contains(DesktopBooster.Sound(true, "end")));

        terrain = new DesktopTerrain();
        foreach (var (kind, dx, dy) in new[]
        {
            (DesktopItemKind.MoveBlockLeft, -1, 0), (DesktopItemKind.MoveBlockRight, 1, 0),
            (DesktopItemKind.MoveBlockUp, 0, -1), (DesktopItemKind.MoveBlockDown, 0, 1),
        })
        {
            items.Clear();
            if (!items.Add(new DesktopItemPlacement(kind, 600, 450))) { Check($"{kind} has original game art", false); continue; }
            items.Prepare(player, terrain, Scale);
            Solid start = player.Solids[0];
            player.ResetTo(new PointF(200, start.T));
            for (int i = 0; i < 32; i++) Tick();
            Solid now = player.Solids[0];
            Check($"{kind} follows its arrow and carries the rider",
                Math.Sign(now.L - start.L) == dx && Math.Sign(now.T - start.T) == dy &&
                player.Pos.X == (now.L + now.R) / 2f && player.CollisionBottom == now.T);
        }

        items.Clear();
        items.Add(new DesktopItemPlacement(DesktopItemKind.MoveBlockRight, 600, 450));
        player.ResetTo(new PointF(200, 142));
        for (int i = 0; i < 45; i++) Tick(new PetInput { MoveY = 1 });
        Check("riding a sideways arrow block allows downward steering after the delay",
            player.Solids[0].L > 184 && player.Solids[0].T > 142);

        items.Clear();
        sounds.Clear();
        terrain.BaseSolids.Add(new Solid { Id = (IntPtr)98, L = 228, R = 240, T = -100, B = 500 });
        items.Add(new DesktopItemPlacement(DesktopItemKind.MoveBlockRight, 600, 450));
        player.ResetTo(new PointF(200, 142));
        for (int i = 0; i < 90 && !sounds.Contains(DesktopMoveBlock.SoundPrefix + "break"); i++) Tick();
        bool broke = sounds.Contains(DesktopMoveBlock.SoundPrefix + "break");
        player.ResetTo(new PointF(100, 110));
        for (int i = 0; i < 228 && !sounds.Contains(DesktopMoveBlock.SoundPrefix + "reappear"); i++) Tick();
        Check("an obstructed arrow block breaks and reforms at its placed origin", broke &&
            sounds.Contains(DesktopMoveBlock.SoundPrefix + "reappear") && player.Solids[^1].L == 184f);
    }

    static unsafe bool HasHalo(Native.ImageLayer layer)
    {
        if (layer.Pixels == IntPtr.Zero) return false;
        byte* pixels = (byte*)layer.Pixels;
        for (int y = 0; y < layer.Height; y++)
            for (int x = 0; x < layer.Width; x++)
                if (pixels[y * layer.Stride + x * 4 + 3] is > 0 and < 255) return true;
        return false;
    }

    static void TestSound()
    {
        using var sound = new SoundEffects(() => true, () => false, 2, 0);
        Check("the installed FMOD runtime initializes with the game's four sound banks", sound.Available);
        if (!sound.Available) return;
        foreach (string path in new[] { "jump", "duck", "stand", "dash_red_right", "dash_red_left",
            "jump_super", "jump_superslide", "wallslide", "water_move_shallow", "dreamblock_enter" })
        {
            try
            {
                Check("FMOD resolves " + path, sound.HasEventForCheck("event:/char/madeline/" + path));
            }
            catch (InvalidOperationException e) { Check(e.Message, false); }
        }
        Check("FMOD resolves both original crystal sound events", sound.HasEventForCheck(DesktopRefill.TouchSound) &&
            sound.HasEventForCheck(DesktopRefill.ReturnSound));
        foreach (string path in new[] { DesktopZipMover.MoveSound, "event:/game/04_cliffside/cloud_blue_boost",
            "event:/game/general/spring", DesktopFeather.GetSound, DesktopFeather.RenewSound, DesktopFeather.ReturnSound,
            "event:/game/06_reflection/feather_state_loop", "event:/game/06_reflection/feather_state_warning",
            "event:/game/06_reflection/feather_state_end", "event:/game/06_reflection/feather_state_bump" })
            Check("FMOD resolves " + path, sound.HasEventForCheck(path));

        sound.SetChaseMusic(false);
        Check("launch with chase off leaves the music bank unloaded and the desktop silent",
            !sound.MusicBankLoadedForCheck && sound.ChaseMusicForCheck == IntPtr.Zero);
        sound.SetChaseMusic(true);
        for (int i = 0; i < 30 && sound.ChasePlaybackForCheck() != 0; i++)
        {
            sound.Update();
            System.Threading.Thread.Sleep(20);
        }
        IntPtr track = sound.ChaseMusicForCheck;
        Check("the original Old Site chase event enters FMOD playback", track != IntPtr.Zero &&
            sound.HasEventForCheck(SoundEffects.ChaseMusicEvent) && sound.ChasePlaybackForCheck() == 0);
        sound.SetChaseMusic(true);
        Check("changing the active chaser count keeps one music instance", track != IntPtr.Zero &&
            sound.ChaseMusicForCheck == track);
        sound.SetChaseMusic(false);
        Check("disabling the last chaser immediately stops and releases the music",
            sound.ChaseMusicForCheck == IntPtr.Zero && sound.ChasePlaybackForCheck() == 2);
        sound.SetChaseMusic(true);
        Check("re-enabling opens a fresh instance without reloading music.bank", sound.MusicBankLoadedForCheck &&
            sound.ChaseMusicForCheck != IntPtr.Zero);
        sound.SetChaseMusic(false);
        foreach (bool red in new[] { false, true })
        foreach (string action in red ? new[] { "enter", "dash", "end", "reappear", "move" } : new[] { "enter", "dash", "end", "reappear" })
            Check("FMOD resolves " + DesktopBooster.Sound(red, action), sound.HasEventForCheck(DesktopBooster.Sound(red, action)));
        foreach (string action in new[] { "activate", "move", "break", "reform_begin", "reappear", "side_depress", "side_release" })
            Check("FMOD resolves " + DesktopMoveBlock.SoundPrefix + action, sound.HasEventForCheck(DesktopMoveBlock.SoundPrefix + action));
        foreach (string path in new[] {
            "event:/game/general/crystalheart_blue_get", "event:/game/general/crystalheart_bounce",
            "event:/game/general/crystalheart_pulse", DesktopRefill.DoubleTouchSound, DesktopRefill.DoubleReturnSound,
            "event:/game/general/platform_disintegrate", "event:/game/general/platform_return",
            "event:/game/general/touchswitch_any", "event:/game/general/touchswitch_last_oneshot",
            "event:/game/general/touchswitch_gate_open", "event:/game/general/touchswitch_gate_finish",
            "event:/game/general/cassette_block_switch_1", "event:/game/general/cassette_block_switch_2",
            "event:/game/general/strawberry_touch", "event:/game/general/strawberry_get",
            "event:/game/04_cliffside/cloud_pink_boost", "event:/game/06_reflection/pinballbumper_hit",
            "event:/game/06_reflection/crushblock_move_loop", "event:/game/05_mirror_temple/swapblock_move",
            "event:/game/09_core/switch_to_cold", "event:/game/09_core/iceblock_touch",
            "event:/game/09_core/bounceblock_break", "event:/new_content/game/10_farewell/glider_movement" })
            Check("FMOD resolves " + path, sound.HasEventForCheck(path));
    }

    static void TestCourseProps()
    {
        var player = new Player();
        var terrain = new DesktopTerrain();
        terrain.Monitors.Add(new RectangleF(0, 0, 420, 265));
        var sounds = new List<string>();
        using var items = new DesktopItems(sounds.Add, (_, _) => { });
        float time = 0f;
        void Add(DesktopItemKind kind, float x, float y, float dx = 0, float dy = 0)
            => items.Add(new DesktopItemPlacement(kind, x * Scale, y * Scale, dx * Scale, dy * Scale));
        void Reset(float x, float y)
        {
            items.Clear();
            sounds.Clear();
            player.ResetTo(new PointF(x, y));
            time = 0f;
        }
        void Tick(int ticks = 1, int direction = 0, bool grab = false)
        {
            for (int i = 0; i < ticks; i++)
            {
                bool frozen = player.IsHitStopped;
                if (!frozen) time += Dt;
                var input = new PetInput { MoveX = direction, AimX = direction, GrabHeld = grab, DashPressed = player.HasDashBuffer };
                items.Prepare(player, terrain, Scale);
                if (!frozen) items.BeforePlayer(player, Dt, time, input);
                player.Update(Dt, input, time, items.CheckCollisions);
                if (!frozen && items.AfterPlayer(player, Dt, time, input)) player.ApplyFreeze(DesktopRefill.PickupFreeze);
            }
        }

        Reset(174f, 105f);
        Add(DesktopItemKind.Heart, 180f, 100f);
        Tick();
        Check("body contact bounces off the original crystal heart without collecting it",
            sounds.Contains("event:/game/general/crystalheart_bounce") &&
            !sounds.Contains("event:/game/general/crystalheart_blue_get") && player.Speed.X < 0f);
        player.ResetTo(new PointF(149f, 105f));
        player.BufferDash();
        Tick(18, 1);
        Check("a dash collects the crystal heart with its original blue pickup event",
            sounds.Count(s => s == "event:/game/general/crystalheart_blue_get") == 1);

        Reset(180f, 105f);
        Add(DesktopItemKind.DoubleRefill, 180f, 100f);
        Tick();
        Check("the pink crystal grants two temporary dashes", player.Dashes == 2 && player.DashCapacity == 1 &&
            sounds.Contains(DesktopRefill.DoubleTouchSound));

        Reset(171f, 105f);
        Add(DesktopItemKind.Bumper, 180f, 100f);
        Tick(3);
        Check("the desktop bumper uses the shared vanilla radial launch", player.State == Player.StLaunch &&
            player.ExplodeLaunchSequenceCount > 0 && sounds.Contains("event:/game/06_reflection/pinballbumper_hit"));

        Reset(171f, 108f);
        Add(DesktopItemKind.Puffer, 180f, 100f);
        Tick(5);
        Check("side contact with the placed puffer produces an explosion launch", player.State == Player.StLaunch &&
            player.ExplodeLaunchSequenceCount > 0);

        Reset(165f, 105f);
        Add(DesktopItemKind.Jellyfish, 174f, 104f);
        Tick(10, 1, grab: true);
        Check("the ordinary grab binding picks up a placed jellyfish", player.Holding is Glider);
        Tick(20);
        Check("releasing grab throws the held jellyfish and clears the player hold", player.Holding == null);

        Reset(180f, 100f);
        Add(DesktopItemKind.PinkCloud, 180f, 100f);
        Tick(28);
        Check("the fragile cloud launches and temporarily loses its collision",
            player.JumpThrus.Count == 0 && sounds.Contains("event:/game/04_cliffside/cloud_pink_boost"));
        player.ResetTo(new PointF(50f, 70f));
        Tick(170);
        Check("the fragile cloud's original respawn restores its jump-through surface", player.JumpThrus.Count == 1);

        Reset(180f, 100f);
        Add(DesktopItemKind.CrumblePlatform, 180f, 104f);
        Tick(48);
        Check("a ridden crumble platform disintegrates", player.Solids.Count == 0 &&
            sounds.Contains("event:/game/general/platform_disintegrate"));
        Tick(145);
        Check("a clear crumble platform reforms at its original location", player.Solids.Any(solid => solid.T == 100f));

        Reset(125f, 112f);
        Add(DesktopItemKind.DreamBlock, 180f, 106f);
        player.BufferDash();
        Tick(15, 1);
        Check("a standalone dream block enters the existing dream-dash state", player.State == Player.StDreamDash &&
            player.Solids.Single().Dream);

        Reset(115f, 100f);
        Add(DesktopItemKind.SwapBlock, 180f, 200f, dx: 64f);
        player.BufferDash();
        Tick(19, 1);
        Check("a swap block hears a regular dash from elsewhere in the room", player.Solids[0].L >= 220f);
        Tick(150);
        Check("the swap block returns to its home end", player.Solids[0].L == 164f);

        Reset(139f, 110f);
        Add(DesktopItemKind.KevinBlock, 180f, 106f);
        player.BufferDash();
        Tick(45, 1);
        Check("a dash hit rebounds and activates the Kevin block in the original collision direction",
            sounds.Contains("event:/game/06_reflection/crushblock_activate") && player.Solids[0].L < 164f);

        Reset(180f, 105f);
        Add(DesktopItemKind.TouchSwitch, 180f, 100f);
        Add(DesktopItemKind.TouchSwitch, 205f, 100f);
        Add(DesktopItemKind.SwitchGate, 250f, 170f, dy: -40f);
        Tick(50);
        bool waits = player.Solids[0].T == 154f;
        player.ResetTo(new PointF(205f, 105f));
        Tick(180);
        Check("the linked gate waits for all switches and then moves to its endpoint",
            waits && player.Solids[0].T == 114f && sounds.Count(s => s == "event:/game/general/touchswitch_last_oneshot") == 1);

        Reset(65f, 90f);
        Add(DesktopItemKind.CassetteBlue, 180f, 160f);
        Add(DesktopItemKind.CassettePink, 220f, 160f);
        Tick(4);
        bool blue = player.Solids.Single().L == 164f;
        Tick(81);
        Check("cassette blocks share the original eight-beat switching cycle",
            blue && player.Solids.Single().L == 204f);

        Reset(65f, 90f);
        Add(DesktopItemKind.CoreToggle, 65f, 87f);
        Add(DesktopItemKind.CoreBlock, 180f, 160f);
        Tick(2);
        player.ResetTo(new PointF(180f, 152f));
        Tick(105);
        Check("touching the core switch makes the ice block crumble without a hot launch",
            sounds.Contains("event:/game/09_core/switch_to_cold") && sounds.Contains("event:/game/09_core/iceblock_touch") &&
            !sounds.Contains("event:/game/09_core/bounceblock_touch") && player.Solids.Count == 0);

        Reset(206f, 140f);
        terrain.Apply(player, new[] { screen }, new[] { Window(50, 300, 300, 300, 240) },
            100, Scale, WindowMaterial.Solid, Dt);
        Add(DesktopItemKind.WallSpringLeft, 200f, 135f);
        Tick();
        Check("a wall spring snaps to the exposed window edge and uses the original side bounce",
            player.Speed.X == 240f && player.Speed.Y == -140f && sounds.Contains("event:/game/general/spring"));

        Reset(180f, 105f);
        Add(DesktopItemKind.Strawberry, 180f, 100f);
        Add(DesktopItemKind.DreamBlock, 180f, 113f);
        Tick(35);
        Check("a strawberry follows first and collects only after safe ground",
            sounds.IndexOf("event:/game/general/strawberry_touch") == 0 &&
            sounds.Contains("event:/game/general/strawberry_get"));

        Reset(115f, 100f);
        Add(DesktopItemKind.SwapBlock, 170f, 90f, dx: 48f);
        Tick();
        int handle = items.Render(Scale).ToArray().Single(layer => (layer.ItemId & DesktopItem.RouteEndpoint) != 0).ItemId;
        var endpoint = new PointF(218f * Scale, 90f * Scale);
        items.BeginDrag(handle, endpoint, Scale);
        var drop = new PointF(240f * Scale, 90f * Scale);
        bool moved = items.EndDrag(handle, drop, cancel: false);
        Check("dragging the swap endpoint preserves the block origin and persists its new route",
            moved && items.SavePlacements().Single().X == 170f * Scale && items.SavePlacements().Single().RouteX == 70f * Scale);
    }

    static void TestGeneratedCourses()
    {
        var displays = new[] {
            screen,
            new DisplayInfo(2, new DesktopRect(-980, 0, 980, 650), new DesktopRect(-980, 24, 980, 615)),
        };
        foreach (int scale in new[] { 2, 3, 6 })
        {
            var player = new Player();
            var terrain = new DesktopTerrain();
            terrain.Apply(player, displays, new[] { Window(37, 15, 50, 1250, 680) }, 100, scale, WindowMaterial.Solid, Dt);
            using var items = new DesktopItems(_ => { }, (_, _) => { });
            var manual = new DesktopItemPlacement(DesktopItemKind.Refill, 1240, 710);
            items.Add(manual);
            bool allPlayable = true;
            var variants = new HashSet<string>();
            DesktopCourse last = null;
            for (int seed = 0; seed < 12; seed++)
            {
                var course = DesktopCourse.Generate(terrain, scale, new PointF(90, 95), new Random(seed), items.SavePlacements(), _ => true);
                if (course == null)
                {
                    Console.WriteLine($"    no course found at {scale}x for seed {seed}");
                    allPlayable = false;
                    continue;
                }
                allPlayable &= course.CanFinish(scale);
                variants.Add(string.Join("/", course.Placements.Select(p => $"{p.Kind}:{p.X}:{p.Y}")));
                last = course;
            }
            Check($"12 distinct {scale}x layouts reach the heart with the live player physics",
                allPlayable && variants.Count == 12);
            Check($"the {scale}x route can be replaced and replayed without serializing generated props",
                last != null && items.ReplaceCourse(last) && items.ReplaceCourse(last) &&
                items.SavePlacements().SequenceEqual(new[] { manual }));
            items.ClearCourse();
            Check($"removing the {scale}x route preserves the manually placed item",
                items.Course == null && items.SavePlacements().SequenceEqual(new[] { manual }));
        }

        var waterPlayer = new Player();
        var water = new DesktopTerrain { CourseArena = new RectangleF(60f, 60f, 160f, 80f) };
        water.Apply(waterPlayer, new[] { screen }, new[] { Window(38, 60, 75, 920, 660) }, 100, Scale, WindowMaterial.Water, Dt);
        Check("the route reserves only its footprint inside a water window",
            waterPlayer.Waters.Count == 4 && waterPlayer.Waters.All(s =>
                !RectangleF.FromLTRB(s.L, s.T, s.R, s.B).IntersectsWith(water.CourseArena)));
    }

    static void TestResizableProps(string dump)
    {
        var player = new Player();
        var terrain = new DesktopTerrain();
        using var items = new DesktopItems(_ => { }, (_, _) => { });
        var kinds = new[] { DesktopItemKind.DreamBlock, DesktopItemKind.CassetteBlue, DesktopItemKind.CassettePink,
            DesktopItemKind.CassetteYellow, DesktopItemKind.CassetteGreen };
        foreach (DesktopItemKind kind in kinds)
        {
            items.Clear();
            var start = new DesktopItemPlacement(kind, 360, 300);
            items.Add(start);
            items.Prepare(player, terrain, Scale);
            var first = items.Render(Scale)[0];
            var corner = new PointF(first.HitBounds.X + first.HitBounds.Width, first.HitBounds.Y + first.HitBounds.Height);
            var larger = new PointF(corner.X + 47, corner.Y + 73);
            items.BeginDrag(first.ItemId, corner, Scale, ResizeEdge.Bottom | ResizeEdge.Right);
            items.Drag(first.ItemId, larger);
            items.Prepare(player, terrain, Scale);
            var preview = items.Render(Scale)[0];
            Check(kind + " enlarges from its corner in whole tiles while fixing the opposite edges",
                first.HitMode == ItemHitMode.Resize && preview.HitBounds.X == first.HitBounds.X &&
                preview.HitBounds.Y == first.HitBounds.Y && preview.HitBounds.Width == first.HitBounds.Width + 48 &&
                preview.HitBounds.Height == first.HitBounds.Height + 72);
            items.EndDrag(first.ItemId, larger, cancel: false);
            var saved = items.SavePlacements().Single();
            string json = System.Text.Json.JsonSerializer.Serialize(saved);
            items.Clear();
            items.Restore(new[] { System.Text.Json.JsonSerializer.Deserialize<DesktopItemPlacement>(json) });
            items.Prepare(player, terrain, Scale);
            var restored = items.Render(Scale)[0];
            Check(kind + " restores the adjusted geometry from the saved placement", restored.HitBounds == preview.HitBounds);
            if (kind is DesktopItemKind.DreamBlock or DesktopItemKind.CassetteBlue)
                Check(kind + " collision follows the new dimensions", player.Solids.Any(s =>
                    s.R - s.L == saved.Width && s.B - s.T == saved.Height));

            items.BeginDrag(restored.ItemId, corner, Scale, ResizeEdge.Top);
            items.Drag(restored.ItemId, larger);
            items.EndDrag(restored.ItemId, larger, cancel: true);
            Check(kind + " cancels a resize without saving a change", items.SavePlacements().Single() == saved);
        }

        var size = new Size(40, 32);
        var initial = new DesktopItemPlacement(DesktopItemKind.DreamBlock, 200, 150, Width: 40, Height: 32);
        foreach (ResizeEdge edges in new[] { ResizeEdge.Top, ResizeEdge.Right, ResizeEdge.Bottom, ResizeEdge.Left,
            ResizeEdge.Top | ResizeEdge.Right, ResizeEdge.Top | ResizeEdge.Left, ResizeEdge.Bottom | ResizeEdge.Left,
            ResizeEdge.Bottom | ResizeEdge.Right })
        {
            var result = DesktopResizableBlock.Resize(initial, size, edges, Scale,
                (edges & ResizeEdge.Left) != 0 ? -24 : 24, (edges & ResizeEdge.Top) != 0 ? -48 : 48);
            bool horizontal = (edges & (ResizeEdge.Left | ResizeEdge.Right)) != 0;
            bool vertical = (edges & (ResizeEdge.Top | ResizeEdge.Bottom)) != 0;
            int h = (edges & ResizeEdge.Left) != 0 ? 1 : -1, v = (edges & ResizeEdge.Top) != 0 ? 1 : -1;
            Check("the " + edges + " resize handle fixes the opposite sides",
                result.Width == (horizontal ? 48 : 40) && result.Height == (vertical ? 48 : 32) &&
                result.X + h * result.Width * Scale / 2 == initial.X + h * size.Width * Scale / 2 &&
                result.Y + v * result.Height * Scale / 2 == initial.Y + v * size.Height * Scale / 2);
        }

        foreach (bool vertical in new[] { false, true })
        {
            items.Clear();
            items.Add(new DesktopItemPlacement(DesktopItemKind.SwapBlock, 540, 300, vertical ? 0 : 70 * Scale,
                vertical ? -53 * Scale : 0));
            items.Prepare(player, terrain, Scale);
            var layers = items.Render(Scale).ToArray();
            var path = layers.Single(layer => layer.HitMode == ItemHitMode.Region);
            Check("the swap track covers the complete " + (vertical ? "vertical" : "horizontal") + " travel rectangle",
                path.Image.Width == (vertical ? 32 : 102) && path.Image.Height == (vertical ? 69 : 16) &&
                path.HitBounds.Width == 32 * Scale && path.HitBounds.Height == 16 * Scale &&
                layers.Length == 2);
            DumpLayers(dump, vertical ? "swap-vertical.png" : "swap-horizontal.png", layers);
        }
    }

    static void TestDreamBlockOutline(string dump)
    {
        var placement = new DesktopItemPlacement(DesktopItemKind.DreamBlock, 360, 300, Width: 72, Height: 96);
        using var block = new DesktopDreamBlock(1, placement, new SpriteBloom(Sprites.Get("refillBloom", false)))
        {
            Anchor = placement.ToGame(Scale),
        };
        var player = new Player();
        var layers = new List<Native.ItemLayer>();
        bool corners = true, noExposedFill = true, capturedLeak = false;
        for (int i = 0; i < 80; i++)
        {
            block.Advance(new DesktopItemFrame(player, Dt, i * Dt, _ => { }, (_, _) => { }));
            layers.Clear();
            block.Render(layers, Scale);
            var layer = layers[0];
            var image = layer.Image;
            int left = (int)((layer.HitBounds.X - image.Bounds.X) / Scale);
            int top = (int)((layer.HitBounds.Y - image.Bounds.Y) / Scale);
            int right = left + placement.Width - 1, bottom = top + placement.Height - 1;
            corners &= White(left + 1, top + 1) && White(right - 1, top + 1) &&
                White(left + 1, bottom - 1) && White(right - 1, bottom - 1);
            bool frameSealed = true;
            for (int x = left + 2; x <= right - 2; x++)
                frameSealed &= !DarkBeforeOutline(x, 0, 0, 1, top + 2) &&
                    !DarkBeforeOutline(x, image.Height - 1, 0, -1, image.Height - bottom + 1);
            for (int y = top + 2; y <= bottom - 2; y++)
                frameSealed &= !DarkBeforeOutline(0, y, 1, 0, left + 2) &&
                    !DarkBeforeOutline(image.Width - 1, y, -1, 0, image.Width - right + 1);
            noExposedFill &= frameSealed;
            if (!frameSealed && !capturedLeak)
            {
                DumpLayers(dump, "dream-border-leak.png", layers);
                capturedLeak = true;
            }
            if (i == 30) DumpLayers(dump, "dream-border.png", layers);

            bool White(int x, int y) => Pixel(x, y, 0) == 255 && Pixel(x, y, 1) == 255 &&
                Pixel(x, y, 2) == 255 && Pixel(x, y, 3) == 255;

            bool DarkBeforeOutline(int x, int y, int dx, int dy, int pixels)
            {
                for (int j = 0; j < pixels; j++, x += dx, y += dy)
                {
                    if (Pixel(x, y, 3) != 255) continue;
                    return Pixel(x, y, 0) < 64 && Pixel(x, y, 1) < 64 && Pixel(x, y, 2) < 64;
                }
                return false;
            }

            byte Pixel(int x, int y, int channel) =>
                System.Runtime.InteropServices.Marshal.ReadByte(image.Pixels, y * image.Stride + x * 4 + channel);
        }
        Check("dream blocks join their animated edges with the original two-pixel corner caps", corners);
        Check("the dream block's opaque black fill stays behind its white outline throughout the wobble", noExposedFill);
    }

    static void TestPickupAndBumper(string dump)
    {
        using var pickup = new DesktopHeartPickup();
        var player = new Player { Solids = new List<Solid> { new Solid { L = 0, T = 140, R = 480, B = 160 } } };
        player.ResetTo(new PointF(180, 140));
        pickup.Start(new PointF(174, 134));
        Check("a collected heart begins with the original white overlay", pickup.Available && pickup.WhiteHeart);
        int flashes = 0, finishes = 0, framesToOrbs = 0;
        bool tenOrbs = false, absorbed = false, card = false, whiteViewport = false, darkViewport = false;
        bool sparsePoem = false;
        var viewport = new RectangleF(0, 0, 480, 280);
        var expectedBounds = new Native.DesktopRect(0, 0, viewport.Width * Scale, viewport.Height * Scale);
        for (int frame = 0; frame < 300 && pickup.Active; frame++)
        {
            float rate = pickup.Advance(Dt, player, viewport, () => flashes++, () => finishes++);
            if (rate > 0f) player.Update(Dt * rate, default);
            if (flashes == 0) framesToOrbs++;
            if (pickup.OrbCount == 10 && !tenOrbs)
            {
                tenOrbs = true;
                var flash = new List<Native.ItemLayer>();
                pickup.Render(flash, Scale);
                whiteViewport = flash.Any(layer => IsSolidFill(layer, 7, 255, 255));
                DumpLayers(dump, "heart-flash.png", flash);
            }
            if (tenOrbs && !pickup.ShowingHeart && pickup.PlayerInFront && pickup.OrbCount == 0) absorbed = true;
            if (pickup.ShowingHeart && rate == 0f) card = true;
            if (frame is 64 or 86 or 177 or 215)
            {
                var layers = new List<Native.ItemLayer>();
                pickup.Render(layers, Scale);
                if (frame == 86)
                    darkViewport = layers.Any(layer => IsSolidFill(layer, 3, 0, 217)) &&
                        !layers.Any(layer => layer.Depth == 7);
                if (frame == 177)
                {
                    var foreground = layers.Where(layer => layer.Depth == 6).ToArray();
                    var bounds = foreground.Select(layer => layer.Image.Bounds.Bounds).ToArray();
                    sparsePoem = foreground.Length > 1 &&
                        !bounds.Where((rectangle, i) => bounds.Skip(i + 1).Any(rectangle.IntersectsWith)).Any() &&
                        foreground.Sum(layer => layer.Image.Width * layer.Image.Height) < viewport.Width * viewport.Height * 4;
                }
                DumpLayers(dump, "heart-" + frame + ".png", layers);
            }
        }
        Check("the heart freezes briefly then starts one flash and ten absorb orbs",
            flashes == 1 && framesToOrbs is >= 11 and <= 14 && tenOrbs);
        Check("all heart orbs finish before the blue reveal", absorbed && card);
        Check("the flash fills the viewport over Madeline and the particles", whiteViewport);
        Check("the formation backdrop reaches a uniform 85% black below Madeline", darkViewport);
        Check("the heart reveal submits separate occupied regions instead of a full-display bitmap", sparsePoem);
        Check("the heart reveal releases its sound and resumes gameplay", finishes == 1 && !pickup.Active);

        bool IsSolidFill(Native.ItemLayer layer, int depth, byte channel, byte alpha) =>
            layer.Depth == depth && layer.Image.Width == 1 && layer.Image.Height == 1 &&
            layer.Image.Bounds == expectedBounds &&
            System.Runtime.InteropServices.Marshal.ReadByte(layer.Image.Pixels, 0) == channel &&
            System.Runtime.InteropServices.Marshal.ReadByte(layer.Image.Pixels, 3) == alpha;

        var sounds = new List<string>();
        using var bumper = new DesktopBumper(1, new DesktopItemPlacement(DesktopItemKind.Bumper, 525, 402),
            new SpriteBloom(Sprites.Get("refillBloom", false)), null) { Anchor = new PointF(175, 134) };
        bumper.Prepare(new DesktopTerrain(), Scale);
        var frameState = new DesktopItemFrame(player, Dt, 0f, sounds.Add, (_, _) => { });
        player.ResetTo(new PointF(169, 140));
        bumper.Advance(frameState);
        Check("one pinball hit emits exactly twelve launch particles",
            bumper.ParticleCount == 12 && player.State == Player.StLaunch);
        player.ResetTo(new PointF(100, 140));
        for (int i = 0; i < 24; i++)
        {
            bumper.Advance(frameState);
            bumper.AfterFrame(Dt);
        }
        Check("pinball cooldown does not repeat the launch particle burst",
            bumper.ParticleCount == 12 && sounds.Count(path => path.EndsWith("pinballbumper_hit", StringComparison.Ordinal)) == 1);
    }

    static void DumpLayers(string directory, string name, IReadOnlyList<Native.ItemLayer> layers)
    {
        if (directory == null || layers.Count == 0) return;
        Rectangle area = Rectangle.Ceiling(layers.Select(layer => layer.Image.Bounds.Bounds).Aggregate(RectangleF.Union));
        using var frame = new Bitmap(area.Width, area.Height);
        using (var g = new Graphics(frame))
        {
            g.Clear();
            g.FillRectangle(Color.FromArgb(0x39, 0x45, 0x57), 0, 0, area.Width, area.Height);
            foreach (var layer in layers.OrderBy(layer => layer.Depth))
            {
                var image = layer.Image;
                var pixels = new byte[image.Width * image.Height * 4];
                for (int y = 0; y < image.Height; y++)
                    System.Runtime.InteropServices.Marshal.Copy(image.Pixels + y * image.Stride, pixels,
                        y * image.Width * 4, image.Width * 4);
                using Bitmap sprite = Bitmap.FromBgra(image.Width, image.Height, pixels);
                g.DrawImage(sprite, image.Bounds.X - area.X, image.Bounds.Y - area.Y, image.Bounds.Width, image.Bounds.Height);
            }
        }
        Directory.CreateDirectory(directory);
        frame.SavePng(Path.Combine(directory, name));
    }

    static bool HasPixels(Bitmap bitmap, Rectangle area)
    {
        var pixels = bitmap.Pixels;
        for (int y = area.Top; y < area.Bottom; y++)
            for (int x = area.Left; x < area.Right; x++)
                if (pixels[y * bitmap.RowBytes + x * 4 + 3] != 0) return true;
        return false;
    }

    static bool Near(float value, float target) => MathF.Abs(value - target) < .001f;

    static void Check(string label, bool ok)
    {
        Console.WriteLine(ok ? "  ok   " + label : "  FAIL " + label);
        if (ok) passed++;
        else failed++;
    }
}
