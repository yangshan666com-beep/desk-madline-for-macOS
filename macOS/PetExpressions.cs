using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Xml;
using System.Xml.Linq;
using SkiaSharp;

namespace DeskMadeline.Mac;

/// <summary>Brief desktop reactions using Madeline's original dialogue portrait animations.</summary>
internal sealed class PetExpressions : IDisposable
{
    internal enum Mood { Happy, Upset, Surprised, Angry }

    const string Portrait = "portrait_madeline";
    const float Lifetime = .85f, FadeIn = .07f, FadeOut = .17f, MinimumInterval = 4f;
    const double ClickWindow = 10, AngryWindow = 10;
    const int CardSize = 38, BorderWidth = 2, TailWidth = 5, TailHeight = 9, Gap = 7;
    static readonly SKColor Outline = new SKColor(23, 37, 64);
    static readonly SKColor Frame = new SKColor(212, 235, 255);
    // Squash beside Madeline, stretch off the ground, then rebound after impact.
    static readonly (float Time, float Width, float Height, float Y)[] BounceKeyframes =
    {
        (0f,    .62f, .52f,   7f),
        (.06f,  .85f, 1.14f, -6f),
        (.13f,  .99f, 1.02f, -12f),
        (.22f, 1.14f, .80f,   0f),
        (.29f,  .96f, 1.07f, -4f),
        (.38f,   1f,    1f,   0f),
    };

    readonly GameSprite sprite;
    readonly PortraitAtlas atlas;
    readonly Dictionary<string, Bitmap> portraits = new();
    readonly Bitmap border, rightTail, leftTail;
    float age, sincePopup = MinimumInterval;
    bool enabled = true;
    double olderClick = double.NegativeInfinity, latestClick = double.NegativeInfinity;
    double lastAngryClick = double.NegativeInfinity, angryUntil = double.NegativeInfinity;

    private PetExpressions(GameSprite sprite, PortraitAtlas atlas)
    {
        this.sprite = sprite;
        this.atlas = atlas;
        border = CreateBorder();
        rightTail = new Bitmap(TailWidth, TailHeight);
        for (int x = 0; x < TailWidth; x++)
            for (int y = x; y < TailHeight - x; y++)
                rightTail.Native.SetPixel(x, y, y == x || y == TailHeight - x - 1 ? Outline : Frame);
        leftTail = rightTail.Mirror();
    }

    internal static PetExpressions Load()
    {
        string directory = CelesteInstall.AtlasesDirectory;
        string xml = CelesteInstall.GraphicsFile("Portraits.xml");
        if (directory == null || xml == null || !File.Exists(Path.Combine(directory, "Portraits.meta")))
            return null;

        try
        {
            XElement madeline = XDocument.Load(xml).Root?.Element(Portrait);
            if (madeline == null) return null;
            var entries = CelesteAtlas.ReadMeta(Path.Combine(directory, "Portraits.meta"), out var pages);
            var atlas = new PortraitAtlas(directory, entries, pages);
            var bank = SpriteBank.Load(atlas, new XDocument(new XElement("Sprites", madeline)), Array.Empty<XDocument>());
            if (bank.Has(Portrait))
            {
                var sprite = new GameSprite();
                bank.CreateOn(sprite, Portrait);
                return new PetExpressions(sprite, atlas);
            }
            foreach (string failure in bank.Failures) PetWindow.Log("Portrait sprite: " + failure);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or XmlException)
        {
            PetWindow.Log("Madeline portraits: " + error.Message);
        }
        return null;
    }

    internal readonly record struct PlayerFrame(int State, bool Tired, int HyperJumps)
    {
        public PlayerFrame(Player player) : this(player.State, player.IsLowStamina, player.HyperJumpCount) { }
    }

    internal Mood? Current { get; private set; }

    internal readonly record struct PortraitFrame(Bitmap Border, Rectangle Bounds,
        Bitmap Artwork, RectangleF ArtworkBounds, Bitmap Tail, Rectangle TailBounds, float Opacity);

    public void Reset()
    {
        ReleaseControl();
        angryUntil = double.NegativeInfinity;
    }

    public void ReleaseControl()
    {
        Current = null;
        CancelClicks();
    }

    public void SetEnabled(bool value)
    {
        enabled = value;
        if (!enabled) Reset();
    }

    public void CancelClicks() => olderClick = latestClick = double.NegativeInfinity;

    public void PickedUp()
    {
        CancelClicks();
        Show(Mood.Surprised);
    }

    public bool Clicked(double timestamp)
    {
        if (!enabled) return false;
        if (timestamp <= angryUntil) return true;
        // Keep the previous two releases so an expired click doesn't discard
        // a later one that is still inside the rolling ten-second window.
        bool threeClicks = timestamp - olderClick <= ClickWindow;
        olderClick = latestClick;
        latestClick = timestamp;
        if (!threeClicks) return false;
        CancelClicks();
        angryUntil = timestamp + AngryWindow;
        // A deliberate click sequence can replace the current reaction. Give
        // anger its own interval so continuing to click cannot keep it onscreen.
        if (timestamp - lastAngryClick >= MinimumInterval && Show(Mood.Angry))
            lastAngryClick = timestamp;
        return true;
    }

    public void Update(float dt, Player player, PlayerFrame before, bool controlled)
    {
        sincePopup = Math.Min(MinimumInterval, sincePopup + dt);
        if (!enabled) return;
        if (Current.HasValue)
        {
            age += dt;
            if (age >= Lifetime) Current = null;
            else sprite.Update(dt);
        }
        if (player.BeingDragged) return;
        if (player.IsDead || player.IsPreDeath || player.IsRespawning)
        {
            Reset();
            return;
        }
        if (!controlled)
        {
            ReleaseControl();
            return;
        }

        // Player counts the crouched SuperJump at takeoff, after the dash aim and jump buffer are resolved.
        if (player.HyperJumpCount != before.HyperJumps)
            Show(Mood.Happy);
        else if (!before.Tired && player.IsLowStamina && before.State == Player.StClimb)
            Show(Mood.Upset);
    }

    bool Show(Mood mood)
    {
        if (!enabled || (mood != Mood.Angry && sincePopup < MinimumInterval)) return false;
        string animation = mood == Mood.Happy ? "idle_normal" : "idle_" + mood.ToString().ToLowerInvariant();
        if (!sprite.Has(animation)) return false;
        age = sincePopup = 0f;
        Current = mood;
        sprite.Play(animation, restart: true);
        return true;
    }

    public PortraitFrame Render(Rectangle actor, RectangleF monitor, bool facingRight)
    {
        if (!Current.HasValue) return default;
        float enter = Math.Clamp(age / FadeIn, 0f, 1f);
        Rectangle place = Place(actor, monitor, facingRight, age, out bool onLeft);
        if (place.IsEmpty || GetPortrait(sprite.Texture) is not Bitmap artwork) return default;

        float alpha = Math.Min(.5f + .5f * enter, (Lifetime - age) / FadeOut);
        int tailWidth = (int)MathF.Round(TailWidth * place.Width / (float)CardSize);
        int tailHeight = (int)MathF.Round(TailHeight * place.Height / (float)CardSize);
        int tailX = onLeft ? place.Right - 1 : place.Left - tailWidth + 1;
        int tailInset = tailHeight / 2 + 2;
        int tailY = place.Top + Math.Clamp(actor.Top + 3 - place.Top, tailInset, place.Height - tailInset) - tailHeight / 2;
        var artworkBounds = (RectangleF)place;
        artworkBounds.Inflate(-place.Width * (BorderWidth / (float)CardSize),
            -place.Height * (BorderWidth / (float)CardSize));
        return new PortraitFrame(border, place, artwork, artworkBounds, onLeft ? rightTail : leftTail,
            new Rectangle(tailX, tailY, tailWidth, tailHeight), alpha);
    }

    internal static Rectangle Place(Rectangle actor, RectangleF monitor, bool preferLeft, float time, out bool onLeft)
    {
        int leftEdge = (int)MathF.Ceiling(monitor.Left) + 2, rightEdge = (int)MathF.Floor(monitor.Right) - 2;
        int topEdge = (int)MathF.Ceiling(monitor.Top) + 2, bottomEdge = (int)MathF.Floor(monitor.Bottom) - 3;
        int left = actor.Left - Gap, right = actor.Right + Gap;
        bool fitsLeft = left - leftEdge >= CardSize && left <= rightEdge;
        bool fitsRight = rightEdge - right >= CardSize && right >= leftEdge;
        onLeft = fitsLeft && (preferLeft || !fitsRight);
        if ((!fitsLeft && !fitsRight) || bottomEdge - topEdge < CardSize) return Rectangle.Empty;

        var bounce = SampleBounce(time);
        int width = Math.Min((int)MathF.Round(CardSize * bounce.Width), onLeft ? left - leftEdge : rightEdge - right);
        int height = Math.Min((int)MathF.Round(CardSize * bounce.Height), bottomEdge - topEdge);
        int bottom = actor.Top - 24 + CardSize + (int)MathF.Round(bounce.Y);
        int top = Math.Clamp(bottom - height, topEdge, bottomEdge - height);
        return new Rectangle(onLeft ? left - width : right, top, width, height);
    }

    static (float Width, float Height, float Y) SampleBounce(float time)
    {
        for (int i = 1; i < BounceKeyframes.Length; i++)
        {
            var end = BounceKeyframes[i];
            if (time >= end.Time) continue;
            var start = BounceKeyframes[i - 1];
            float t = (time - start.Time) / (end.Time - start.Time);
            float ease = t * t * (3f - 2f * t);
            return (Blend(start.Width, end.Width), Blend(start.Height, end.Height), Blend(start.Y, end.Y));

            float Blend(float from, float to) => from + (to - from) * ease;
        }
        return (1f, 1f, 0f);
    }

    static Bitmap CreateBorder()
    {
        // Rasterize the original outline at game resolution and punch out the
        // old portrait clip. AppKit draws the source artwork behind this border.
        var bitmap = new Bitmap(CardSize, CardSize);
        using var canvas = new SKCanvas(bitmap.Native);
        using var paint = new SKPaint { Color = Outline };
        canvas.DrawRoundRect(new SKRect(0, 0, CardSize, CardSize), 4, 4, paint);
        paint.Color = Frame;
        canvas.DrawRoundRect(new SKRect(1, 1, CardSize - 1, CardSize - 1), 3, 3, paint);
        paint.BlendMode = SKBlendMode.Clear;
        canvas.DrawRoundRect(new SKRect(BorderWidth, BorderWidth,
            CardSize - BorderWidth, CardSize - BorderWidth), 2, 2, paint);
        return bitmap;
    }

    Bitmap GetPortrait(string frame)
    {
        if (frame == null) return null;
        if (portraits.TryGetValue(frame, out Bitmap cached)) return cached;
        try
        {
            cached = atlas.Decode(frame);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            PetWindow.Log("Madeline portrait " + frame + ": " + error.Message);
        }
        portraits[frame] = cached;
        return cached;
    }

    public void Dispose()
    {
        foreach (Bitmap portrait in portraits.Values) portrait?.Dispose();
        portraits.Clear();
        border.Dispose();
        rightTail.Dispose();
        leftTail.Dispose();
    }

    sealed class PortraitAtlas(string directory, Dictionary<string, CelesteAtlas.Entry> entries, List<string> pages) : IFrameAtlas
    {
        public bool Has(string path) => entries.ContainsKey(path);

        public Bitmap Decode(string path)
        {
            var entry = entries[path];
            // Portraits use Monocle's PackerNoAtlas layout, one .data file per frame.
            using Bitmap texture = CelesteAtlas.DecodePage(Path.Combine(directory, pages[entry.Page], path + ".data"));
            return texture.Extract(new Rectangle(0, 0, texture.Width, texture.Height),
                new Size(entry.FrameWidth, entry.FrameHeight), new Point(entry.OffsetX, entry.OffsetY));
        }
    }
}
