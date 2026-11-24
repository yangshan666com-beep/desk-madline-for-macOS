using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Drawing;
#if !DESK_PIXEL
using System.Drawing.Imaging;
#endif
using System.IO;
using System.Runtime.InteropServices;

namespace DeskMadeline
{
    /// <summary>
    /// Reads sprites out of an installed Celeste's atlases, the way SoundEffects reads its
    /// banks, so a build need not carry any of the game's artwork.
    /// </summary>
    /// <remarks>
    /// Both formats are Monocle's and are ported from it. Atlas.ReadAtlasData's Packer case
    /// gives the .meta layout, and VirtualTexture's ".data" case the image: a width, a height,
    /// a flag for whether alpha is stored, then runs of pixels. Each run is a length byte, an
    /// alpha byte when alpha is stored, and the colour as B, G, R -- which is already the byte
    /// order GDI+ wants, so the colour copies straight across.
    /// </remarks>
    internal static class CelesteAtlas
    {
        /// <summary>One sprite's place on a page, and where it sits in its untrimmed frame.</summary>
        /// <remarks>
        /// OffsetX and OffsetY are where the trimmed region belongs within the frame, which is
        /// the negative of what the file stores -- Monocle negates them the same way.
        /// </remarks>
        internal readonly struct Entry
        {
            public readonly int Page, X, Y, Width, Height, OffsetX, OffsetY, FrameWidth, FrameHeight;

            public Entry(int page, int x, int y, int width, int height,
                int offsetX, int offsetY, int frameWidth, int frameHeight)
            {
                Page = page; X = x; Y = y; Width = width; Height = height;
                OffsetX = offsetX; OffsetY = offsetY; FrameWidth = frameWidth; FrameHeight = frameHeight;
            }
        }

        /// <summary>Every sprite in an atlas, by its path, plus the pages they live on.</summary>
        internal static Dictionary<string, Entry> ReadMeta(string metaPath, out List<string> pages)
        {
            var entries = new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);
            pages = new List<string>();

            using var stream = File.OpenRead(metaPath);
            using var reader = new BinaryReader(stream);
            reader.ReadInt32();     // version
            reader.ReadString();    // packer
            reader.ReadInt32();     // packer version
            short pageCount = reader.ReadInt16();
            for (int page = 0; page < pageCount; page++)
            {
                pages.Add(reader.ReadString());
                short spriteCount = reader.ReadInt16();
                for (int i = 0; i < spriteCount; i++)
                {
                    string path = reader.ReadString().Replace('\\', '/');
                    short x = reader.ReadInt16(), y = reader.ReadInt16();
                    short width = reader.ReadInt16(), height = reader.ReadInt16();
                    short offsetX = reader.ReadInt16(), offsetY = reader.ReadInt16();
                    short frameWidth = reader.ReadInt16(), frameHeight = reader.ReadInt16();
                    entries[path] = new Entry(page, x, y, width, height,
                        -offsetX, -offsetY, frameWidth, frameHeight);
                }
            }
            return entries;
        }

        /// <summary>Decode one page. These are large, so read what is needed and let it go.</summary>
        internal static Bitmap DecodePage(string dataPath)
        {
            ReadOnlySpan<byte> data = File.ReadAllBytes(dataPath);
            if (data.Length < 9) throw new InvalidDataException("Incomplete atlas page header.");
            int width = BinaryPrimitives.ReadInt32LittleEndian(data);
            int height = BinaryPrimitives.ReadInt32LittleEndian(data.Slice(4));
            if (width <= 0 || height <= 0 || (long)width * height > int.MaxValue / 4)
                throw new InvalidDataException("Invalid atlas page dimensions.");

#if DESK_PIXEL
            var bitmap = new Bitmap(width, height);
            try
            {
                DecodeRuns(data.Slice(9), data[8] == 1, MemoryMarshal.Cast<byte, uint>(bitmap.Pixels));
                return bitmap;
            }
            catch
            {
                bitmap.Dispose();
                throw;
            }
#else
            byte[] pixels = new byte[width * height * 4];
            DecodeRuns(data.Slice(9), data[8] == 1, MemoryMarshal.Cast<byte, uint>(pixels.AsSpan()));
            var bitmap = new Bitmap(width, height, PixelFormat.Format32bppPArgb);
            BitmapData locked = bitmap.LockBits(new Rectangle(0, 0, width, height),
                ImageLockMode.WriteOnly, PixelFormat.Format32bppPArgb);
            try
            {
                for (int row = 0; row < height; row++)
                    Marshal.Copy(pixels, row * width * 4,
                        locked.Scan0 + row * locked.Stride, width * 4);
            }
            finally { bitmap.UnlockBits(locked); }
            return bitmap;
#endif
        }

        static void DecodeRuns(ReadOnlySpan<byte> data, bool hasAlpha, Span<uint> pixels)
        {
            int source = 0, target = 0;
            while (target < pixels.Length)
            {
                if (data.Length - source < (hasAlpha ? 2 : 1))
                    throw new InvalidDataException("Incomplete atlas pixel run.");
                int length = data[source++];
                if (length == 0) throw new InvalidDataException("Empty atlas pixel run.");
                uint alpha = hasAlpha ? data[source++] : 255u;
                uint bgra = 0;
                if (alpha != 0)
                {
                    if (data.Length - source < 3) throw new InvalidDataException("Incomplete atlas pixel colour.");
                    // The file stores premultiplied BGR. Fully transparent runs omit it.
                    bgra = data[source] | (uint)data[source + 1] << 8 | (uint)data[source + 2] << 16 | alpha << 24;
                    source += 3;
                }
                int count = Math.Min(length, pixels.Length - target);
                pixels.Slice(target, count).Fill(bgra);
                target += count;
            }
        }

        /// <summary>Cut a sprite out of its page and give it its untrimmed frame back.</summary>
        internal static Bitmap Extract(Bitmap page, in Entry entry)
        {
            int width = Math.Max(1, entry.FrameWidth), height = Math.Max(1, entry.FrameHeight);
#if DESK_PIXEL
            return page.Extract(new Rectangle(entry.X, entry.Y, entry.Width, entry.Height),
                new Size(width, height), new Point(entry.OffsetX, entry.OffsetY));
#else
            var sprite = new Bitmap(width, height, PixelFormat.Format32bppPArgb);
            using var g = Graphics.FromImage(sprite);
            g.CompositingMode = System.Drawing.Drawing2D.CompositingMode.SourceCopy;
            // The packer trims transparent edges away and records where what is left belongs.
            g.DrawImage(page,
                new Rectangle(entry.OffsetX, entry.OffsetY, entry.Width, entry.Height),
                new Rectangle(entry.X, entry.Y, entry.Width, entry.Height),
                GraphicsUnit.Pixel);
            return sprite;
#endif
        }
    }
}
