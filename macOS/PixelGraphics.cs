global using Bitmap = DeskMadeline.Mac.PixelBitmap;
global using Graphics = DeskMadeline.Mac.PixelCanvas;

using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using SkiaSharp;

namespace DeskMadeline.Mac;

public sealed class PixelBitmap : IDisposable
{
    internal SKBitmap Native { get; }
    public int Width { get; }
    public int Height { get; }
    public int RowBytes { get; }
    public IntPtr Address { get; }
    bool immutable;
    SKImage drawingImage;
    internal SKImage DrawingImage => immutable ? drawingImage ??= SKImage.FromBitmap(Native) : null;
    internal unsafe Span<byte> Pixels => new Span<byte>((void*)Address, RowBytes * Height);

    public PixelBitmap(int width, int height)
    {
        Native = new SKBitmap(width, height, SKColorType.Bgra8888, SKAlphaType.Premul);
        Native.Erase(SKColors.Transparent);
        Width = width;
        Height = height;
        RowBytes = Native.RowBytes;
        Address = Native.GetPixels();
    }

    // Skia can share the storage of a finished sprite with one reusable image.
    internal void Freeze()
    {
        Native.SetImmutable();
        immutable = true;
    }

    internal static PixelBitmap FromBgra(int width, int height, byte[] data)
    {
        var result = new PixelBitmap(width, height);
        data.AsSpan().CopyTo(result.Pixels);
        return result;
    }

    internal static PixelBitmap Decode(Stream stream)
    {
        using var codec = SKCodec.Create(stream) ?? throw new InvalidDataException("Invalid image.");
        var result = new PixelBitmap(codec.Info.Width, codec.Info.Height);
        var read = codec.GetPixels(result.Native.Info, result.Address);
        if (read == SKCodecResult.Success) return result;
        result.Dispose();
        throw new InvalidDataException("Image decode failed: " + read);
    }

    internal PixelBitmap Extract(Rectangle region, Size size, Point offset)
    {
        var result = new PixelBitmap(size.Width, size.Height);
        var target = Rectangle.Intersect(new Rectangle(offset, region.Size), new Rectangle(Point.Empty, size));
        if (target.IsEmpty) return result;
        int sourceX = region.X + target.X - offset.X;
        int sourceY = region.Y + target.Y - offset.Y;
        int sourceStride = RowBytes, targetStride = result.RowBytes;
        int rowWidth = target.Width * 4;
        ReadOnlySpan<byte> source = Pixels;
        Span<byte> output = result.Pixels;
        for (int y = 0; y < target.Height; y++)
            source.Slice((sourceY + y) * sourceStride + sourceX * 4, rowWidth)
                .CopyTo(output.Slice((target.Y + y) * targetStride + target.X * 4, rowWidth));
        return result;
    }

    internal Rectangle OpaqueBounds(Rectangle area)
    {
        int left = area.Right, top = area.Bottom, right = area.Left, bottom = area.Top;
        ReadOnlySpan<byte> pixels = Pixels;
        int stride = RowBytes;
        for (int y = area.Top; y < area.Bottom; y++)
        {
            int row = y * stride;
            for (int x = area.Left; x < area.Right; x++)
            {
                if (pixels[row + x * 4 + 3] == 0) continue;
                left = Math.Min(left, x);
                top = Math.Min(top, y);
                right = Math.Max(right, x + 1);
                bottom = Math.Max(bottom, y + 1);
            }
        }
        return right > left && bottom > top ? Rectangle.FromLTRB(left, top, right, bottom) : Rectangle.Empty;
    }

    internal PixelBitmap Mirror()
    {
        int width = Width, height = Height;
        var result = new PixelBitmap(width, height);
        int sourceStride = RowBytes / 4, targetStride = result.RowBytes / 4;
        ReadOnlySpan<uint> source = MemoryMarshal.Cast<byte, uint>(Pixels);
        Span<uint> target = MemoryMarshal.Cast<byte, uint>(result.Pixels);
        for (int y = 0; y < height; y++)
        {
            ReadOnlySpan<uint> row = source.Slice(y * sourceStride, width);
            Span<uint> mirrored = target.Slice(y * targetStride, width);
            for (int x = 0; x < width; x++) mirrored[width - 1 - x] = row[x];
        }
        return result;
    }

    public Color GetPixel(int x, int y) => Color.FromArgb(unchecked((int)(uint)Native.GetPixel(x, y)));
    public void SetPixel(int x, int y, Color color) => Native.SetPixel(x, y, ToSkia(color));
    internal static SKColor ToSkia(Color color) => new SKColor(color.R, color.G, color.B, color.A);

    internal void SavePng(string path)
    {
        using var data = Native.Encode(SKEncodedImageFormat.Png, 100);
        using var stream = File.Create(path);
        data.SaveTo(stream);
    }

    public void Dispose()
    {
        drawingImage?.Dispose();
        Native.Dispose();
    }
}

/// <summary>Draw at the game's 1× resolution. AppKit does the integer upscale once.</summary>
public sealed class PixelCanvas : IDisposable
{
    readonly SKCanvas canvas;
    readonly SKPaint paint = new SKPaint();
    readonly Rectangle bounds;
    readonly Action<Rectangle> recordDraw;
    readonly Dictionary<(int Color, bool Silhouette), SKColorFilter> filters = new();

    public Rectangle Dirty { get; private set; }

    public PixelCanvas(PixelBitmap target, Action<Rectangle> recordDraw = null)
    {
        canvas = new SKCanvas(target.Native);
        bounds = new Rectangle(0, 0, target.Width, target.Height);
        this.recordDraw = recordDraw;
    }

    public void Clear() => canvas.Clear(SKColors.Transparent);
    internal void BeginFrame(Rectangle area)
    {
        Dirty = Rectangle.Empty;
        Clear(area);
    }

    internal void Clear(Rectangle area)
    {
        if (area.IsEmpty) return;
        int state = canvas.Save();
        canvas.ClipRect(SKRect.Create(area.X, area.Y, area.Width, area.Height));
        canvas.Clear(SKColors.Transparent);
        canvas.RestoreToCount(state);
    }
    public int Save() => canvas.Save();
    public void Restore(int count) => canvas.RestoreToCount(count);
    internal void Exclude(SKRegion region) => canvas.ClipRegion(region, SKClipOperation.Difference);
    public void TranslateTransform(float x, float y) => canvas.Translate(x, y);
    public void RotateTransform(float degrees) => canvas.RotateDegrees(degrees);
    public void ScaleTransform(float x, float y) => canvas.Scale(x, y);

    bool Mark(SKRect rect)
    {
        var mapped = canvas.TotalMatrix.MapRect(rect);
        var rounded = Rectangle.FromLTRB((int)Math.Floor(mapped.Left), (int)Math.Floor(mapped.Top),
            (int)Math.Ceiling(mapped.Right), (int)Math.Ceiling(mapped.Bottom));
        rounded = Rectangle.Intersect(rounded, bounds);
        if (rounded.Width <= 0 || rounded.Height <= 0) return false;
        Dirty = Dirty.IsEmpty ? rounded : Rectangle.Union(Dirty, rounded);
        recordDraw?.Invoke(rounded);
        return true;
    }

    public void DrawImage(PixelBitmap bitmap, float x, float y, float width, float height,
        float alpha = 1f, Color? tint = null, bool silhouette = false)
    {
        if (bitmap == null || width <= 0 || height <= 0 || alpha <= 0) return;
        var dest = SKRect.Create(x, y, width, height);
        if (!Mark(dest)) return;
        paint.Color = SKColors.White.WithAlpha((byte)Math.Round(Math.Clamp(alpha, 0f, 1f) * 255f));
        paint.ColorFilter = tint is Color c ? GetTintFilter(c, silhouette) : null;
        if (bitmap.DrawingImage is SKImage image) canvas.DrawImage(image, dest, paint);
        else canvas.DrawBitmap(bitmap.Native, dest, paint); // Default sampling is nearest-neighbor.
        paint.ColorFilter = null;
    }

    public void DrawRegion(PixelBitmap bitmap, Rectangle source, float x, float y, float alpha = 1f,
        Color? tint = null, bool silhouette = false)
    {
        var dest = SKRect.Create(x, y, source.Width, source.Height);
        if (!Mark(dest)) return;
        paint.Color = SKColors.White.WithAlpha((byte)Math.Round(alpha * 255f));
        paint.ColorFilter = tint is Color color ? GetTintFilter(color, silhouette) : null;
        var crop = SKRect.Create(source.X, source.Y, source.Width, source.Height);
        if (bitmap.DrawingImage is SKImage image) canvas.DrawImage(image, crop, dest, paint);
        else canvas.DrawBitmap(bitmap.Native, crop, dest, paint);
        paint.ColorFilter = null;
    }

    public void FillRectangle(Color color, float x, float y, float width, float height)
    {
        var rect = SKRect.Create(x, y, width, height);
        if (!Mark(rect)) return;
        paint.Color = PixelBitmap.ToSkia(color);
        canvas.DrawRect(rect, paint);
    }

    SKColorFilter GetTintFilter(Color tint, bool silhouette)
    {
        if (!silhouette && tint.R == 255 && tint.G == 255 && tint.B == 255) return null;
        var key = (tint.ToArgb(), silhouette);
        if (filters.TryGetValue(key, out SKColorFilter filter)) return filter;
        if (filters.Count >= 32) ClearFilters();
        filter = TintFilter(tint, silhouette);
        filters[key] = filter;
        return filter;
    }

    static SKColorFilter TintFilter(Color tint, bool silhouette)
    {
        if (silhouette) return SKColorFilter.CreateBlendMode(PixelBitmap.ToSkia(tint), SKBlendMode.SrcIn);
        return SKColorFilter.CreateColorMatrix(new float[]
        {
            tint.R / 255f, 0, 0, 0, 0,
            0, tint.G / 255f, 0, 0, 0,
            0, 0, tint.B / 255f, 0, 0,
            0, 0, 0, 1, 0,
        });
    }

    public void DrawPolygon(Color color, PointF[] points)
    {
        using var path = new SKPath();
        path.MoveTo(points[0].X + 0.5f, points[0].Y + 0.5f);
        for (int i = 1; i < points.Length; i++) path.LineTo(points[i].X + 0.5f, points[i].Y + 0.5f);
        path.Close();
        var coverage = path.Bounds;
        coverage.Inflate(1f, 1f);
        if (!Mark(coverage)) return;
        using var stroke = new SKPaint { Color = PixelBitmap.ToSkia(color), Style = SKPaintStyle.Stroke, StrokeWidth = 1f };
        canvas.DrawPath(path, stroke);
    }

    public void Dispose()
    {
        paint.Dispose();
        ClearFilters();
        canvas.Dispose();
    }

    void ClearFilters()
    {
        foreach (SKColorFilter filter in filters.Values) filter.Dispose();
        filters.Clear();
    }
}
