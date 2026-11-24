using System;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
#if DESK_MACOS
using System.Threading.Tasks;
#endif
using System.Xml.Linq;
using DeskMadeline.Mac;

namespace DeskMadeline;

internal sealed class PetWindow : IDisposable
{
    const double FixedStep = 1.0 / 60.0;
#if DESK_MACOS
    // A display tick can arrive just before a 60 Hz step is due. Carry the
    // small phase difference forward so it cannot change the simulation rate.
    const double FrameTolerance = .00075;
#else
    const double FrameTolerance = 0;
#endif
    const float Step = (float)FixedStep;
    const int Reset = 2, ToggleAutonomy = 3, ToggleSound = 4, Scale = 5, Terrain = 6, ChooseGame = 7, Rumble = 10;
    const int ToggleExpressions = 11, ClearDesktopItems = 13;
    const int GenerateCourse = 14, ReplayCourse = 15, RemoveCourse = 16;
#if DESK_MACOS
    const int BadelineCount = 18, ToggleInfiniteStamina = 19, DashAssist = 20;
#endif
    static bool Chinese => CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "zh";
    internal static PetWindow Instance;
    readonly Settings settings = Settings.Load();
    readonly Player player = new Player();
    readonly DesktopTerrain desktop = new DesktopTerrain();
    readonly DesktopInput keys = new DesktopInput();
    readonly DesktopDrag drag = new DesktopDrag();
    readonly ClickEscape escape = new ClickEscape(Random.Shared);
    readonly System.Collections.Generic.HashSet<DesktopItemKind> itemCatalog = new();
    readonly IdleDirector idle = new IdleDirector(new Random()) { DebugEnabled = false };
    PetEffects effects;
    PetExpressions expressions;
    DesktopItems items;
    DesktopBadeline chase;
    readonly Action<Player> checkItems;
    readonly Action prepareItems;
    SoundEffects sound;
    SdlController controller;
    readonly ControllerRumble rumble;
    float sceneTime;
    bool waking, ducking, ready;
    bool announcedCourse;
    double accumulator, sincePoll;

    PetWindow()
    {
        rumble = new ControllerRumble(strength => controller?.Vibrate(strength));
        rumble.SetAmount(settings.Rumble);
        player.OnRumble = rumble.Request;
        checkItems = p =>
        {
            items.CheckCollisions(p);
            if (!items.HeartPickupActive) chase?.CheckCollision(p);
        };
        prepareItems = () => items.Prepare(player, desktop, settings.Scale);
    }

    [STAThread]
    public static int Main(string[] args)
    {
#if MAC_CHECKS
        if (args.Length != 0 && args[0] == "--check")
            return MacChecks.Run(args.Skip(1).ToArray());
#endif

        using var pet = new PetWindow();
        Instance = pet;
        try { return Native.Run(pet.Start, pet.Frame, pet.ReceiveKey, pet.Mouse, pet.Command, pet.Dispose); }
        catch (Exception e) { Log(e.ToString()); return 1; }
        finally { Instance = null; }
    }

    public static void Log(string message)
    {
#if DESK_WINDOWS
        Native.Log(message);
#else
        Console.Error.WriteLine(message);
#endif
    }
    internal Color ResolveHairColor(int dashes, Color fallback) => fallback;

    internal static SpriteBank LoadOriginalSprites()
    {
        string assets = Path.Combine(AppContext.BaseDirectory, "..", "Resources", "assets", "player");
        if (!Directory.Exists(assets)) assets = Path.Combine(AppContext.BaseDirectory, "assets", "player");
        Sprites.LoadAll(assets);
        string xml = CelesteInstall.GraphicsFile("Sprites.xml");
        if (xml == null || Sprites.LoadedFromCeleste == 0)
            throw new InvalidDataException("Celeste's Gameplay atlas or Sprites.xml could not be loaded.");

        var bank = SpriteBank.Load(Sprites.Atlas, XDocument.Load(xml), Array.Empty<XDocument>());
        foreach (string failure in bank.Failures.Where(x => x.StartsWith("player:", StringComparison.OrdinalIgnoreCase)
            || x.StartsWith("player_sweat:", StringComparison.OrdinalIgnoreCase)))
            Log("Sprite bank: " + failure);
        if (!bank.Has("player") || !bank.Has("player_sweat") || Sprites.Get("idle00", false) == null)
            throw new InvalidDataException("The atlas does not contain the original player sprites.");
        HairMeta.LoadPlayerSprites(bank, Sprites.Atlas);
        return bank;
    }

    int Start()
    {
        try
        {
            CelesteInstall.Chosen = settings.Celeste;
            bool pickedInstall = !CelesteInstall.HasBundledContent && CelesteInstall.Directory == null;
            if (pickedInstall)
            {
                Native.Alert("Desk Madeline", Chinese
#if DESK_WINDOWS
                    ? "请选择《蔚蓝》的安装目录（Celeste.exe 所在文件夹）。桌宠会从中读取原版动画和音效。"
                    : "Select the installed Celeste folder containing Celeste.exe to use the game's original animations and sound.");
#else
                    ? "请选取本机安装的 Celeste.app。桌宠会直接读取游戏中的动画和音效。"
                    : "Choose your local Celeste.app to use the game's original animations and sound.");
#endif
                string picked = Native.PickGame();
                if (picked == null) return 0;
                SetInstall(picked);
            }
            ReloadGame(startup: true);
            if (pickedInstall)
            {
                settings.Celeste = CelesteInstall.Chosen;
                settings.Save();
            }
            desktop.Poll(player, settings.Scale, settings.Terrain, Step);
            items.Prepare(player, desktop, settings.Scale);
            Spawn(startup: true);
            Render();
            return 1;
        }
        catch (Exception e)
        {
            Report(e);
            return 0;
        }
    }

    void SetInstall(string picked)
    {
#if DESK_WINDOWS
        string install = CelesteInstall.IsInstall(picked) ? Path.GetFullPath(picked) : null;
#else
        string install = MacCelesteInstall.ResourcesAt(picked);
#endif
        if (install == null || !File.Exists(Path.Combine(install, "Content", "Graphics", "Sprites.xml")) ||
            !File.Exists(Path.Combine(install, "Content", "Graphics", "Atlases", "Gameplay.meta")))
            throw new InvalidDataException(Chinese ? "所选位置缺少 Celeste.exe、Sprites.xml 或 Gameplay 图集。"
                : "The selected location is missing Celeste.exe, Sprites.xml, or the Gameplay atlas.");
        CelesteInstall.Chosen = install;
    }

    void ReloadGame(bool startup = false)
    {
        ready = false;
        items?.Dispose();
        chase?.Dispose();
        desktop.CourseArena = RectangleF.Empty;
        Native.CourseStatus(0, null, 0);
#if DESK_MACOS
        // Resolve the install once before graphics and FMOD read from it.
        _ = CelesteInstall.Directory;
        var artwork = Task.Run(LoadOriginalSprites);
        SpriteBank bank;
        try { ReloadSound(); }
        finally { bank = artwork.GetAwaiter().GetResult(); }
#else
        SpriteBank bank = LoadOriginalSprites();
        ReloadSound();
#endif
        effects?.Dispose();
        player.ResetSprite(bank, "player", Player.ModeMadeline, null);
        player.InfiniteStamina = settings.InfiniteStamina;
        player.SetDashMode(settings.DashAssist);
#if DESK_MACOS
        chase = new DesktopBadeline(bank, sound);
        chase.SetCount(settings.BadelineCount);
        sound.SetChaseMusic(chase.Count > 0);
#endif
        effects = new PetEffects(player, bank);
        expressions?.Dispose();
        expressions = PetExpressions.Load();
        expressions?.SetEnabled(settings.ShowExpressions);
        if (expressions != null) Log("Madeline portrait reactions loaded from Celeste.");
        items = new DesktopItems(path => sound.Play(path), rumble.Request, sound);
        items.Restore(settings.DesktopItems);
        itemCatalog.Clear();
        foreach (DesktopItemKind kind in Enum.GetValues<DesktopItemKind>())
        {
            using Bitmap icon = items.CreateIcon(kind);
            Native.ItemIcon((int)kind, icon?.Address ?? IntPtr.Zero, icon?.RowBytes ?? 0, icon?.Width ?? 0, icon?.Height ?? 0);
            if (icon == null) Log($"Celeste sprites unavailable for {kind}; disabled placement.");
            else itemCatalog.Add(kind);
        }
        rumble.Stop();
        controller?.Dispose();
        controller = SdlController.OpenCeleste(CelesteInstall.Directory);
#if DESK_WINDOWS
        using Bitmap portrait = PetEffects.CreateMenuIcon(bank);
        if (portrait != null)
            Native.Icon(portrait.Address, portrait.RowBytes, portrait.Width, portrait.Height);
#endif
        Log($"Celeste: {Sprites.LoadedFromCeleste} sprite entries, " +
            (CelesteInstall.HasBundledContent ? "bundled graphics" : CelesteInstall.Directory));
#if DESK_MACOS
        if (startup) PetEffects.WarmUp(bank);
#endif
        RefreshMenu();
        ready = true;
    }

    void ReloadSound()
    {
        sound?.Dispose();
        sound = new SoundEffects(() => keys.Capturing, () => desktop.FullscreenDisplay != 0, settings.Sound ? 2 : 0, 100);
    }

    void RefreshMenu()
    {
        Native.Status(settings.Scale, settings.Explore ? 1 : 0, settings.Sound ? 1 : 0,
            sound?.Available == true ? 1 : 0, (int)settings.Terrain, (int)settings.Rumble, settings.ShowExpressions ? 1 : 0);
#if DESK_MACOS
        Native.AssistStatus(chase.Count, chase.Available ? DesktopBadeline.MaxCount : 0,
            settings.InfiniteStamina ? 1 : 0, settings.DashAssist);
#endif
    }

    void Frame(double elapsed)
    {
        if (!ready) return;
        try
        {
            accumulator = Math.Min(.1, accumulator + elapsed);
            sincePoll += elapsed;
            if (accumulator + FrameTolerance < FixedStep) return;
            // Reuse one window snapshot for catch-up steps, so skipped time
            // cannot be mistaken for a change in window velocity.
            desktop.Poll(player, settings.Scale, settings.Terrain,
                (float)Math.Clamp(sincePoll, FixedStep / 2, FixedStep * 2), prepareItems);
            sincePoll = 0;
            while (accumulator + FrameTolerance >= FixedStep)
            {
                accumulator -= FixedStep;
                Tick();
            }
            Render();
        }
        catch (Exception e)
        {
            ready = false;
            Report(e);
            Native.Quit();
        }
    }

    void Tick()
    {
        sound.Update();
        var gamepad = controller?.Poll() ?? default;
        if (gamepad.Connected == 0) Native.Gamepad(out gamepad);
        rumble.Advance(Step, controller?.Connection ?? 0,
            keys.Capturing && !player.BeingDragged && desktop.FullscreenDisplay == 0);
        float step = Step * items.AdvanceHeartPickup(player, desktop, Step);
        if (!settings.ShowExpressions || items.HeartPickupActive) escape.Cancel();
        if (items.HeartPickupActive) chase?.Reset();
        PetInput input = keys.Sample(player, gamepad,
            acceptInput: !escape.Active && !player.BeingDragged && step > 0f);
        items.Prepare(player, desktop, settings.Scale);
        if (step == 0f)
        {
            UpdateCourse();
            return;
        }
        if (escape.Active) input = escape.Sample(player, Step);
        bool frozen = player.FreezeFramesEnabled && player.IsHitStopped;
        if (!frozen) sceneTime += step;
        if (!frozen) items.BeforePlayer(player, step, sceneTime, input);
        if (!UpdatePlayer(input, frozen, step)) AdvanceItemsAfterPlayer(frozen, input, step);
        if (!frozen && !waking && !items.HeartPickupActive && chase is { Count: > 0 })
            chase.Advance(player, desktop.NearestMonitor(player.Pos), step);
        UpdateCourse();
    }

    void AdvanceItemsAfterPlayer(bool frozen, PetInput input, float step)
    {
        if (!frozen && items.AfterPlayer(player, step, sceneTime, input)) player.ApplyFreeze(DesktopRefill.PickupFreeze);
    }

    bool UpdatePlayer(PetInput input, bool frozen, float step)
    {
        if (waking)
        {
            if (input.JumpPressed || input.DashPressed || input.MoveX != 0 || input.MoveY != 0 || input.GrabHeld)
                FinishWake();
            else
            {
                effects.UpdateHair(step);
                player.Sprite.Update(step);
                DrainSounds();
                if (player.Sprite.CurrentAnimationID == "" && player.Sprite.LastAnimationID == "wakeUp")
                    FinishWake();
                return false;
            }
        }

        if (keys.Capturing || escape.Active || player.BeingDragged || items.Course != null || items.HeartPickupActive || !settings.Explore)
        {
            idle.NoteRealInput();
            idle.ConsumeWakeRequest();
        }
        else
        {
            Native.Cursor(out float x, out float y);
            var context = new IdleContext
            {
                Player = player, Solids = player.Solids, Monitors = desktop.Monitors, Windows = desktop.Windows,
                Cursor = new PointF(x / settings.Scale, y / settings.Scale),
                ForegroundFullscreen = desktop.FullscreenDisplay != 0,
                WindowsAreKevin = false, SeekersDormant = false,
                EdgesClimbable = true, EdgeLeft = player.MinX, EdgeRight = player.MaxX,
                Gliders = Array.Empty<Glider>(), Seekers = Array.Empty<Seeker>(), Puffers = Array.Empty<Puffer>(),
            };
            PetInput automatic = idle.Drive(step, context);
            if (idle.Engaged) input = automatic;
            if (idle.ConsumeWakeRequest())
            {
                BeginWake();
                return false;
            }
        }

        if (!frozen) effects.UpdateComponents(step);
        int previousState = player.State;
        var before = new PetExpressions.PlayerFrame(player);
        player.Napping = idle.Napping;
        player.Update(step, input, sceneTime, checkItems);
        AdvanceItemsAfterPlayer(frozen, input, step);
        UpdateSounds(previousState);
        effects.AfterPlayer(step, previousState, frozen);
        expressions?.Update(step, player, before, keys.Capturing || escape.Active);
        KeepOnDesktop();
        return true;
    }

    void KeepOnDesktop()
    {
        if (player.IsDead || player.IsPreDeath || player.IsRespawning || player.BeingDragged) return;
        RectangleF monitor = desktop.NearestMonitor(player.Pos);
        if (monitor.IsEmpty) return;
        // Display disconnects can remove the entire world beneath her. Seams and
        // ordinary desktop edges are already handled by the perimeter Solids.
        if ((player.Pos.Y > monitor.Bottom + 16 || player.Pos.Y < monitor.Top - 16 ||
             player.Pos.X < monitor.Left - 16 || player.Pos.X > monitor.Right + 16) &&
            DesktopTerrain.TryStandIn(player, monitor, player.Pos, out PointF recovered))
            ResetPlayer(recovered, startup: false);
    }

    void UpdateSounds(int previousState)
    {
        if (previousState == Player.StDreamDash && player.State != Player.StDreamDash) sound.StopLoop();
        DrainSounds();
        if (player.State == Player.StStarFly)
            sound.SetLoopParameter(Player.StarFlyLoop, "feather_speed", player.StarFlyHasInput ? 1f : 0f);
        if (player.Ducking != ducking)
        {
            if (!player.IsDead && !player.IsRespawning && (player.Ducking || player.onGround))
                sound.Play(player.Ducking ? "event:/char/madeline/duck" : "event:/char/madeline/stand");
            ducking = player.Ducking;
        }

        if (player.SwimSurfaceMoving) sound.StartLoop("swim", "event:/char/madeline/water_move_shallow");
        else sound.StopLoop("swim");
        if (player.AnimId == "wallslide" && player.Speed.Y > 0)
        {
            sound.StartLoop("wallslide", "event:/char/madeline/wallslide");
            sound.SetLoopParameter("wallslide", "surface_index", player.WallSurfaceSoundIndex(player.Facing));
        }
        else sound.StopLoop("wallslide");
    }

    void DrainSounds()
    {
        while (player.SoundEvents.TryDequeue(out PlayerSoundEvent e))
        {
            chase?.RecordSound(e);
            if (e.IsStop) sound.StopLoop(e.Key);
            else if (e.Key != null) sound.PlayKept(e.Key, e.Path);
            else
            {
                sound.Play(e.Path, e.Parameter, e.Value);
                if (e.Path == "event:/char/madeline/dreamblock_enter")
                    sound.StartLoop("event:/char/madeline/dreamblock_travel");
            }
        }
    }

    void BeginWake()
    {
        if (!player.Sprite.Has("wakeUp")) return;
        waking = true;
        player.PlaySprite("wakeUp", true);
    }

    void FinishWake()
    {
        waking = false;
        player.PlaySprite("idle", true);
    }

    void Spawn(bool startup)
    {
        var area = startup ? desktop.Monitors.FirstOrDefault() : desktop.NearestMonitor(player.Pos);
        if (area.IsEmpty) return;
        float center = MathF.Round(area.Left + area.Width / 2);
        PointF spawn = new PointF(center, startup ? area.Bottom : area.Top + 18);
        if (startup && settings.Terrain == WindowMaterial.Solid)
        {
            // Start on an exposed ledge when the desktop has one with room overhead.
            foreach (var solid in player.Solids)
            {
                if (solid.OffScreen || solid.R - solid.L < 18 || solid.T < area.Top + 16 ||
                    solid.T >= area.Bottom || solid.L >= area.Right - 8 || solid.R <= area.Left + 8) continue;
                var x = MathF.Round(Math.Clamp((solid.L + solid.R) / 2, area.Left + 8, area.Right - 8));
                if (x < solid.L + 5 || x > solid.R - 5 || player.CollidesAt(x, solid.T)) continue;
                spawn = new PointF(x, solid.T);
                break;
            }
        }
        if (DesktopTerrain.TryStandIn(player, area, spawn, out PointF safe))
            ResetPlayer(safe, startup);
    }

    void ResetPlayer(PointF spawn, bool startup)
    {
        escape.Cancel();
        chase?.Reset();
        items.CancelHeartPickup();
        player.ResetTo(spawn);
        player.SoundEvents.Clear();
        rumble.Stop();
        effects.Reset();
        expressions?.Reset();
        sound.StopLoop();
        sound.StopLoop("wallslide");
        sound.StopLoop("swim");
        sound.StopLoop(Player.StarFlyLoop);
        sound.StopLoop(Player.StarFlyWarning);
        ducking = false;
        idle.NoteRealInput();
        idle.ConsumeWakeRequest();
        if (startup) BeginWake();
        else FinishWake();
    }

    void UpdateCourse()
    {
        DesktopCourse course = items.Course;
        if (course == null) return;
        if (items.CourseComplete)
        {
            if (!announcedCourse && !items.HeartPickupActive)
            {
                announcedCourse = true;
                Native.CourseStatus(2, Chinese ? "收集水晶之心 · 关卡完成" : "Crystal Heart collected · Course complete", 0);
            }
            return;
        }
        if (player.BeingDragged) return;
        if (player.IsRespawning || !player.IsDead && player.Pos.X >= course.Arena.Left &&
            player.Pos.X <= course.Arena.Right && player.Pos.Y > course.Arena.Bottom)
            StartCourse(course, focus: false, announce: false);
    }

    void GenerateNewCourse()
    {
        desktop.Poll(player, settings.Scale, settings.Terrain, Step);
        DesktopCourse next = DesktopCourse.Generate(desktop, settings.Scale, player.Pos, Random.Shared,
            items.SavePlacements(), itemCatalog.Contains);
        if (next != null && StartCourse(next, focus: true, announce: true)) return;
        Native.CourseStatus(items.Course == null ? 0 : items.CourseComplete ? 2 : 1,
            Chinese ? "没有找到可用的关卡空间。可缩小角色或移开一些道具。"
                : "No playable course fits. Try a smaller character size or move some props.", 0);
    }

    bool StartCourse(DesktopCourse course, bool focus, bool announce)
    {
        if (!items.ReplaceCourse(course)) return false;
        desktop.CourseArena = course.Arena;
        desktop.Poll(player, settings.Scale, settings.Terrain, Step);
        items.Prepare(player, desktop, settings.Scale);
        ResetPlayer(course.Start, startup: false);
        player.Facing = course.Direction;
        announcedCourse = false;
        Native.CourseStatus(1, announce ? Chinese ? "关卡已就绪 · 冲刺撞向水晶之心"
            : "Course ready · Dash into the Crystal Heart" : null, focus ? 1 : 0);
        return true;
    }

    void ClearCurrentCourse()
    {
        if (items.Course == null) return;
        items.ClearCourse();
        desktop.CourseArena = RectangleF.Empty;
        desktop.Poll(player, settings.Scale, settings.Terrain, Step);
        items.Prepare(player, desktop, settings.Scale);
        player.DisplaceOutOfSolids();
        Native.CourseStatus(0, null, 0);
    }

    unsafe void Render()
    {
        var monitor = expressions?.Current != null ? desktop.NearestMonitor(player.Pos) : RectangleF.Empty;
        var frame = effects.Render(waking, expressions, monitor, desktop.Windows);
        int scale = settings.Scale;
        var r = frame.Area;
        var portrait = frame.Portrait;
        var portraitLayer = new Native.PortraitLayer(Layer(portrait.Border, portrait.Bounds, scale),
            Layer(portrait.Artwork, portrait.ArtworkBounds, scale), Layer(portrait.Tail, portrait.TailBounds, scale),
            portrait.Opacity);
        Native.Present(At(frame.Image, r), frame.Image.RowBytes, r.Width, r.Height,
            (frame.Origin.X + r.X) * scale, (frame.Origin.Y + r.Y) * scale, scale, desktop.FullscreenDisplay,
            portraitLayer, items.HeartPickupForeground ? 1 : 0);
        var placed = items.Render(scale, chase);
        fixed (Native.ItemLayer* layers = placed)
            Native.PresentItems(layers, placed.Length, desktop.FullscreenDisplay);
        var hit = frame.ActorArea;
        Native.HitTarget(At(frame.Actor, hit), frame.Actor.RowBytes, hit.Width, hit.Height,
            (frame.ActorOrigin.X + hit.X) * scale, (frame.ActorOrigin.Y + hit.Y) * scale, scale);

        static IntPtr At(Bitmap bitmap, Rectangle area) =>
            area.IsEmpty ? IntPtr.Zero : bitmap.Address + area.Y * bitmap.RowBytes + area.X * 4;

        static Native.ImageLayer Layer(Bitmap bitmap, RectangleF bounds, int scale) =>
            bitmap == null ? default : new Native.ImageLayer(bitmap.Address, bitmap.RowBytes, bitmap.Width, bitmap.Height,
                new Native.DesktopRect(bounds.X * scale, bounds.Y * scale, bounds.Width * scale, bounds.Height * scale));
    }

    void ReceiveKey(int key, int down)
    {
        keys.Receive(key, down);
        if (key == DesktopInput.Capture && down == 0)
        {
            rumble.Stop();
            if (!escape.Active) expressions?.ReleaseControl();
        }
    }

    void Mouse(Native.MouseAction action, float x, float y, double timestamp, int subject, Native.ResizeEdge resize)
    {
        if (!ready) return;
        var cursor = new PointF(x, y);
        int scale = settings.Scale;
        if (action == Native.MouseAction.PlaceItem)
        {
            if (items.Place((DesktopItemKind)subject, cursor, desktop, scale)) SaveItems();
            return;
        }
        if (subject != 0)
        {
            switch (action)
            {
                case Native.MouseAction.Down: items.BeginDrag(subject, cursor, scale, resize); break;
                case Native.MouseAction.Drag: items.Drag(subject, cursor); break;
                case Native.MouseAction.Up:
                case Native.MouseAction.Cancel:
                    if (items.EndDrag(subject, cursor, action == Native.MouseAction.Cancel))
                    {
                        if (resize != Native.ResizeEdge.None)
                        {
                            items.Prepare(player, desktop, scale);
                            if (!player.IsDead && !player.IsRespawning && !player.BeingDragged && player.CollidesHere)
                                player.DisplaceOutOfSolids();
                        }
                        SaveItems();
                    }
                    break;
                case Native.MouseAction.DeleteItem:
                    if (items.Delete(subject)) SaveItems();
                    break;
            }
            return;
        }
        switch (action)
        {
            case Native.MouseAction.Down:
                if (waking) FinishWake();
                drag.Begin(player, cursor, timestamp, scale, allowDrag: !escape.Active);
                break;
            case Native.MouseAction.Drag:
                MoveDrag();
                break;
            case Native.MouseAction.Up:
                MoveDrag();
                if (drag.Finish(player, desktop.NearestMonitor(player.Pos), timestamp, scale, cancel: false))
                {
                    if (expressions?.Clicked(timestamp) == true) escape.Request(cursor.X / scale);
                }
                else expressions?.CancelClicks();
                break;
            case Native.MouseAction.Cancel:
                drag.Finish(player, desktop.NearestMonitor(player.Pos), timestamp, scale, cancel: true);
                expressions?.CancelClicks();
                break;
        }

        void MoveDrag()
        {
            if (!drag.Move(player, cursor, timestamp, scale)) return;
            rumble.Stop();
            expressions?.PickedUp();
        }
    }

    void SaveItems()
    {
        settings.DesktopItems = items.SavePlacements();
        settings.Save();
    }

    void Command(int command, int argument)
    {
        if (!ready) return;
        try
        {
            switch (command)
            {
#if DESK_WINDOWS
                case GenerateCourse:
                    GenerateNewCourse();
                    return;
                case ReplayCourse:
                    if (items.Course != null) StartCourse(items.Course, focus: true, announce: true);
                    return;
                case RemoveCourse:
                    ClearCurrentCourse();
                    return;
#endif
                case ClearDesktopItems:
                    ClearCurrentCourse();
                    items.Clear();
                    SaveItems();
                    return;
                case Reset:
                    Spawn(startup: false);
                    return;
                case ToggleAutonomy:
                    settings.Explore = !settings.Explore;
                    break;
                case ToggleSound:
                    settings.Sound = !settings.Sound;
                    sound.Mode = settings.Sound ? 2 : 0;
                    break;
                case ToggleExpressions:
                    settings.ShowExpressions = !settings.ShowExpressions;
                    expressions?.SetEnabled(settings.ShowExpressions);
                    break;
                case Rumble:
                    settings.Rumble = (RumbleAmount)argument;
                    rumble.SetAmount(settings.Rumble);
                    break;
#if DESK_MACOS
                case BadelineCount:
                    chase.SetCount(argument);
                    settings.BadelineCount = chase.Count;
                    sound.SetChaseMusic(chase.Count > 0);
                    break;
                case ToggleInfiniteStamina:
                    player.InfiniteStamina = settings.InfiniteStamina = !settings.InfiniteStamina;
                    break;
                case DashAssist:
                    if (argument is not (-1 or 1 or 2) || settings.DashAssist == argument) return;
                    player.SetDashMode(settings.DashAssist = argument);
                    break;
#endif
                case Scale:
                    bool hadCourse = items.Course != null;
                    ClearCurrentCourse();
                    float ratio = settings.Scale / (float)argument;
                    settings.Scale = argument;
                    escape.Cancel();
                    chase?.Reset();
                    player.ResetTo(new PointF(MathF.Round(player.Pos.X * ratio), MathF.Round(player.Pos.Y * ratio)));
                    effects.Reset();
                    expressions?.Reset();
                    FinishWake();
                    desktop.Poll(player, settings.Scale, settings.Terrain, Step);
                    items.Prepare(player, desktop, settings.Scale);
                    player.DisplaceOutOfSolids();
                    sound.StopLoop(Player.StarFlyLoop);
                    sound.StopLoop(Player.StarFlyWarning);
                    if (hadCourse) GenerateNewCourse();
                    break;
                case Terrain:
                    settings.Terrain = (WindowMaterial)argument;
                    desktop.Poll(player, settings.Scale, settings.Terrain, Step);
                    items.Prepare(player, desktop, settings.Scale);
                    if (settings.Terrain != WindowMaterial.Water) player.DisplaceOutOfSolids();
                    break;
                case ChooseGame:
                    string picked = Native.PickGame();
                    if (picked == null) return;
                    string previous = CelesteInstall.Chosen;
                    SetInstall(picked);
                    try { ReloadGame(); }
                    catch
                    {
                        CelesteInstall.Chosen = previous;
                        ReloadGame();
                        throw;
                    }
                    settings.Celeste = CelesteInstall.Chosen;
                    items.Prepare(player, desktop, settings.Scale);
                    Spawn(startup: true);
                    break;
            }
            settings.Save();
            RefreshMenu();
        }
        catch (Exception e) { Report(e); }
    }

    static void Report(Exception e)
    {
        Log(e.ToString());
        Native.Alert("Desk Madeline", e.Message);
    }

    public void Dispose()
    {
        ready = false;
        rumble.Stop();
        controller?.Dispose();
        controller = null;
        chase?.Dispose();
        chase = null;
        sound?.Dispose();
        sound = null;
        expressions?.Dispose();
        expressions = null;
        items?.Dispose();
        items = null;
        effects?.Dispose();
        effects = null;
    }
}
