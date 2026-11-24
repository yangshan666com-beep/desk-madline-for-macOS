using System;
using System.Drawing;

namespace DeskMadeline.Mac;

/// <summary>Celeste's point and ambient bloom, applied to the item's own 1× render target.</summary>
internal sealed class SpriteBloom(Bitmap gradient)
{
    const int Reach = 4;
    // Nine taps from the installed GaussianBlur.xnb, in each direction.
    static ReadOnlySpan<int> Weights => [5, 9, 12, 15, 18, 15, 12, 9, 5];
    byte[] horizontal = Array.Empty<byte>();
    public bool Available => gradient != null;

    public Rectangle Apply(Bitmap image, Rectangle sprite, float centerX, float centerY, float lightAlpha,
        int radius = 16, float opacity = .8f, float ambient = 0f)
    {
        int strength = Available ? (int)(255f * opacity * lightAlpha) : 0;
        int baseAlpha = (int)(255f * ambient);
        if ((strength == 0 && baseAlpha == 0) || sprite.IsEmpty) return sprite;

        // The desktop has no Level render target. Use the original point mask and
        // additive pass on the prop's own pixels, including its chapter's ambient
        // bloom. The desktop and other applications do not enter the blur buffer.
        Rectangle spread = Rectangle.Intersect(Rectangle.Inflate(sprite, Reach, Reach),
            new Rectangle(0, 0, image.Width, image.Height));
        Span<byte> pixels = image.Pixels;
        int stride = image.RowBytes;
        if (horizontal.Length < pixels.Length) Array.Resize(ref horizontal, pixels.Length);
        ReadOnlySpan<int> weights = Weights;
        for (int y = sprite.Top; y < sprite.Bottom; y++)
        {
            int row = y * stride;
            for (int x = spread.Left; x < spread.Right; x++)
            {
                int b = 0, g = 0, r = 0;
                for (int sx = Math.Max(x - Reach, sprite.Left); sx <= Math.Min(x + Reach, sprite.Right - 1); sx++)
                {
                    int i = row + sx * 4, w = weights[sx - x + Reach];
                    b += pixels[i] * w;
                    g += pixels[i + 1] * w;
                    r += pixels[i + 2] * w;
                }
                int at = row + x * 4;
                horizontal[at] = RoundBlur(b);
                horizontal[at + 1] = RoundBlur(g);
                horizontal[at + 2] = RoundBlur(r);
            }
        }

        Span<byte> mask = Available ? gradient.Pixels : Span<byte>.Empty;
        int maskWidth = gradient?.Width ?? 0, maskHeight = gradient?.Height ?? 0;
        int maskStride = gradient?.RowBytes ?? 0;
        float scaleX = maskWidth / (2f * radius), scaleY = maskHeight / (2f * radius);
        for (int y = spread.Top; y < spread.Bottom; y++)
        {
            int my = (int)MathF.Floor((y + .5f - centerY + radius) * scaleY);
            bool maskY = strength != 0 && (uint)my < (uint)maskHeight;
            int maskRow = my * maskStride;
            for (int x = spread.Left; x < spread.Right; x++)
            {
                int mx = (int)MathF.Floor((x + .5f - centerX + radius) * scaleX);
                int alpha = baseAlpha;
                if (maskY && (uint)mx < (uint)maskWidth)
                {
                    int point = (mask[maskRow + mx * 4 + 3] * strength + 127) / 255;
                    alpha += (point * (255 - baseAlpha) + 127) / 255;
                }
                if (alpha == 0) continue;
                int b = 0, g = 0, r = 0;
                for (int sy = Math.Max(y - Reach, sprite.Top); sy <= Math.Min(y + Reach, sprite.Bottom - 1); sy++)
                {
                    int i = sy * stride + x * 4, w = weights[sy - y + Reach];
                    b += horizontal[i] * w;
                    g += horizontal[i + 1] * w;
                    r += horizontal[i + 2] * w;
                }
                int at = y * stride + x * 4;
                pixels[at] = Add(pixels[at], b, alpha);
                pixels[at + 1] = Add(pixels[at + 1], g, alpha);
                pixels[at + 2] = Add(pixels[at + 2], r, alpha);
                // AppKit composites over the desktop with source-over. Give the
                // halo only enough alpha to carry its premultiplied colour.
                pixels[at + 3] = Math.Max(pixels[at + 3], Math.Max(pixels[at], Math.Max(pixels[at + 1], pixels[at + 2])));
            }
        }
        return spread;
    }

    static byte RoundBlur(int value) => (byte)((value + 50) / 100);
    static byte Add(byte pixel, int blur, int alpha) => (byte)Math.Min(255, pixel + (RoundBlur(blur) * alpha + 127) / 255);
}
