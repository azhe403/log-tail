#:package SkiaSharp@3.119.4
#:package SkiaSharp.NativeAssets.Win32@3.119.4

// Generates every raster and container format of the LogTail icon from one set of
// geometry constants, so the SVG master, the PNG previews, the Windows .ico and the
// macOS .icns can never drift apart.
//
// Run from the repository root:
//   dotnet run tools/GenerateIcons/GenerateIcons.cs
//
// The "Stream Bars" mark: three greys that taper upward (history getting shorter) and
// one full-width accent bar at the bottom (the live line being appended right now).

using System.Buffers.Binary;
using System.Text;
using SkiaSharp;

const string OutputDir = "assets/icon";

// ── Design tokens ────────────────────────────────────────────────────────────
SKColor tileBackground = Parse("#1E1E22");   // matches RegionColor in App.axaml dark palette
SKColor[] barColors = [Parse("#5E5E68"), Parse("#82828C"), Parse("#A6A6B0")];
SKColor accentColor = Parse("#22D3EE");

const float DesignSize = 256f;          // all geometry is authored in this space
const float TileRadius = 57f;
const float BarLeft = 52f;
const float BarTop = 44f;
const float BarHeight = 30f;
const float BarPitch = 46f;
const float AccentWidth = 152f;         // full width: the live line
float[] barWidths = [152f, 124f, 96f];  // tapering history

// Pixels an .ico should carry. A width byte of 0 encodes 256 in the ICO directory.
int[] icoSizes = [16, 24, 32, 48, 64, 128, 256];
int[] pngSizes = [16, 32, 48, 64, 128, 256, 512, 1024];

// (icns chunk type, pixel size)
(string Type, int Size)[] icnsChunks =
[
    ("icp4", 16), ("icp5", 32), ("icp6", 64),
    ("ic07", 128), ("ic08", 256), ("ic09", 512), ("ic10", 1024),
];

// ── Drawing ──────────────────────────────────────────────────────────────────
void DrawIcon(SKCanvas canvas)
{
    canvas.Clear(SKColors.Transparent);

    using SKPaint paint = new() { IsAntialias = true };

    paint.Color = tileBackground;
    canvas.DrawRoundRect(new SKRect(0, 0, DesignSize, DesignSize), TileRadius, TileRadius, paint);

    for (int i = 0; i < barWidths.Length; i++)
    {
        paint.Color = barColors[i];
        float y = BarTop + (i * BarPitch);
        canvas.DrawRoundRect(new SKRect(BarLeft, y, BarLeft + barWidths[i], y + BarHeight), BarHeight / 2, BarHeight / 2, paint);
    }

    paint.Color = accentColor;
    float accentY = BarTop + (barWidths.Length * BarPitch);
    canvas.DrawRoundRect(new SKRect(BarLeft, accentY, BarLeft + AccentWidth, accentY + BarHeight), BarHeight / 2, BarHeight / 2, paint);
}

byte[] RenderPng(int size)
{
    using SKBitmap bitmap = new(size, size, SKColorType.Rgba8888, SKAlphaType.Premul);
    using SKCanvas canvas = new(bitmap);
    canvas.Scale(size / DesignSize);
    DrawIcon(canvas);
    canvas.Flush();

    using SKImage image = SKImage.FromBitmap(bitmap);
    using SKData data = image.Encode(SKEncodedImageFormat.Png, 100);
    return data.ToArray();
}

// ── Container formats ────────────────────────────────────────────────────────

// ICO: 6-byte ICONDIR, one 16-byte ICONDIRENTRY per image, then the image data.
// Every entry is a PNG - accepted by Windows since Vista, and it keeps the 256px
// entry from overflowing the 16-bit offsets that the classic BMP form requires.
byte[] BuildIco(int[] sizes)
{
    byte[][] images = [.. sizes.Select(RenderPng)];
    using MemoryStream stream = new();
    using BinaryWriter writer = new(stream);

    writer.Write((ushort)0);                       // reserved
    writer.Write((ushort)1);                       // type: 1 = icon
    writer.Write((ushort)sizes.Length);

    int offset = 6 + (16 * sizes.Length);
    for (int i = 0; i < sizes.Length; i++)
    {
        byte encoded = (byte)(sizes[i] >= 256 ? 0 : sizes[i]);   // 0 encodes 256
        writer.Write(encoded);                    // width
        writer.Write(encoded);                    // height
        writer.Write((byte)0);                    // palette size
        writer.Write((byte)0);                    // reserved
        writer.Write((ushort)1);                  // colour planes
        writer.Write((ushort)32);                 // bits per pixel
        writer.Write(images[i].Length);
        writer.Write(offset);
        offset += images[i].Length;
    }

    foreach (byte[] image in images) writer.Write(image);
    writer.Flush();
    return stream.ToArray();
}

// ICNS: 'icns' + total file length, then [4-byte type][4-byte length][PNG] chunks.
// Lengths are big-endian and count the 8-byte chunk header.
byte[] BuildIcns((string Type, int Size)[] chunks)
{
    (string Type, byte[] Data)[] payloads = [.. chunks.Select(c => (c.Type, RenderPng(c.Size)))];

    int total = 8 + payloads.Sum(p => 8 + p.Data.Length);
    using MemoryStream stream = new();
    using BinaryWriter writer = new(stream);

    writer.Write(Encoding.ASCII.GetBytes("icns"));
    WriteBigEndian(writer, total);
    foreach ((string type, byte[] data) in payloads)
    {
        writer.Write(Encoding.ASCII.GetBytes(type));
        WriteBigEndian(writer, 8 + data.Length);
        writer.Write(data);
    }

    writer.Flush();
    return stream.ToArray();
}

void WriteBigEndian(BinaryWriter writer, int value)
{
    Span<byte> buffer = stackalloc byte[4];
    BinaryPrimitives.WriteInt32BigEndian(buffer, value);
    writer.Write(buffer);
}

// The SVG master, emitted from the same constants so the vector and the rasters
// cannot disagree. Every shape here is a rect with rounded corners - no <path> needed.
byte[] BuildSvg()
{
    StringBuilder svg = new();
    svg.AppendLine("<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 256 256\" width=\"256\" height=\"256\" role=\"img\" aria-label=\"LogTail\">");
    svg.AppendLine("  <title>LogTail</title>");
    svg.AppendLine($"  <rect width=\"256\" height=\"256\" rx=\"{TileRadius}\" fill=\"{Hex(tileBackground)}\"/>");

    for (int i = 0; i < barWidths.Length; i++)
    {
        float y = BarTop + (i * BarPitch);
        svg.AppendLine($"  <rect x=\"{BarLeft}\" y=\"{y}\" width=\"{barWidths[i]}\" height=\"{BarHeight}\" rx=\"{BarHeight / 2}\" fill=\"{Hex(barColors[i])}\"/>");
    }

    float accentY = BarTop + (barWidths.Length * BarPitch);
    svg.AppendLine($"  <rect x=\"{BarLeft}\" y=\"{accentY}\" width=\"{AccentWidth}\" height=\"{BarHeight}\" rx=\"{BarHeight / 2}\" fill=\"{Hex(accentColor)}\"/>");
    svg.AppendLine("</svg>");

    return Encoding.UTF8.GetBytes(svg.ToString());
}

SKColor Parse(string hex) => SKColor.Parse(hex);

string Hex(SKColor color) => $"#{color.Red:X2}{color.Green:X2}{color.Blue:X2}";

// ── Run ──────────────────────────────────────────────────────────────────────
Directory.CreateDirectory(OutputDir);
Console.WriteLine($"Writing icon assets to {Path.GetFullPath(OutputDir)}\n");

void Emit(string name, byte[] bytes)
{
    File.WriteAllBytes(Path.Combine(OutputDir, name), bytes);
    Console.WriteLine($"  {name,-22} {bytes.Length,9:N0} bytes");
}

Emit("logtail.svg", BuildSvg());
Emit("logtail.ico", BuildIco(icoSizes));
Emit("logtail.icns", BuildIcns(icnsChunks));
foreach (int size in pngSizes)
{
    Emit($"logtail-{size}.png", RenderPng(size));
}

Console.WriteLine("\nDone.");
