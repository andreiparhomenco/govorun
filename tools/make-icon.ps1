# Builds the app icon and tray logo from assets\logo-source.png:
#   - removes the white background (flood fill from the edges, so the parrot's
#     white chest and the bubble interior are preserved)
#   - crops to content, pads to square
#   - writes src\Govorun.App\govorun.ico (16/32/48/256, PNG-compressed)
#   - writes src\Govorun.App\Assets\logo.png (256 px, transparent) for the tray
$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.Drawing

$repo = Split-Path $PSScriptRoot -Parent
$srcPng = Join-Path $repo "assets\logo-source.png"
$outIco = Join-Path $repo "src\Govorun.App\govorun.ico"
$outPng = Join-Path $repo "src\Govorun.App\Assets\logo.png"

Add-Type -ReferencedAssemblies System.Drawing @"
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

public static class IconMaker
{
    // Flood-fills background pixels reachable from the image border with transparency.
    // "Background" = low-saturation pixel (r ~= g ~= b) of any brightness, so both a
    // plain white sheet and a gray gradient are removed while the blue glow survives.
    public static Bitmap RemoveOuterBackground(Bitmap src, int maxSaturation)
    {
        int w = src.Width, h = src.Height;
        Bitmap bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
        using (Graphics g = Graphics.FromImage(bmp)) g.DrawImage(src, 0, 0, w, h);

        BitmapData data = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadWrite, PixelFormat.Format32bppArgb);
        int[] px = new int[w * h];
        Marshal.Copy(data.Scan0, px, 0, px.Length);

        bool[] visited = new bool[w * h];
        Queue<int> queue = new Queue<int>();
        for (int x = 0; x < w; x++) { queue.Enqueue(x); queue.Enqueue((h - 1) * w + x); }
        for (int y = 0; y < h; y++) { queue.Enqueue(y * w); queue.Enqueue(y * w + w - 1); }

        while (queue.Count > 0)
        {
            int i = queue.Dequeue();
            if (visited[i]) continue;
            visited[i] = true;
            int c = px[i];
            int r = (c >> 16) & 0xFF, gr = (c >> 8) & 0xFF, b = c & 0xFF;
            int max = Math.Max(r, Math.Max(gr, b)), min = Math.Min(r, Math.Min(gr, b));
            int sat = max - min;
            bool nearWhite = min > 225;              // parrot chest — keep
            bool dark = max < 95;                    // beak / eye — keep
            bool grayBg = sat <= maxSaturation;      // flat gray background
            bool blueHaze = b >= r && sat < 75 && min > 95; // outer glow
            if (nearWhite || dark || !(grayBg || blueHaze)) continue;
            px[i] = 0; // transparent
            int x = i % w, y = i / w;
            if (x > 0) queue.Enqueue(i - 1);
            if (x < w - 1) queue.Enqueue(i + 1);
            if (y > 0) queue.Enqueue(i - w);
            if (y < h - 1) queue.Enqueue(i + w);
        }

        Marshal.Copy(px, 0, data.Scan0, px.Length);
        bmp.UnlockBits(data);
        return bmp;
    }

    public static Bitmap CropAndSquare(Bitmap src, double paddingRatio)
    {
        int w = src.Width, h = src.Height;
        BitmapData data = src.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        int[] px = new int[w * h];
        Marshal.Copy(data.Scan0, px, 0, px.Length);
        src.UnlockBits(data);

        int minX = w, minY = h, maxX = -1, maxY = -1;
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
                if (((px[y * w + x] >> 24) & 0xFF) > 8)
                {
                    if (x < minX) minX = x;
                    if (x > maxX) maxX = x;
                    if (y < minY) minY = y;
                    if (y > maxY) maxY = y;
                }
        if (maxX < 0) throw new Exception("image is fully transparent");

        int cw = maxX - minX + 1, ch = maxY - minY + 1;
        int side = (int)(Math.Max(cw, ch) * (1 + 2 * paddingRatio));
        Bitmap outBmp = new Bitmap(side, side, PixelFormat.Format32bppArgb);
        using (Graphics g = Graphics.FromImage(outBmp))
        {
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.DrawImage(src, (side - cw) / 2, (side - ch) / 2,
                new Rectangle(minX, minY, cw, ch), GraphicsUnit.Pixel);
        }
        return outBmp;
    }

    // Classic BMP-encoded icon entry (BITMAPINFOHEADER + BGRA bottom-up + AND mask).
    // Older Windows shell components render these more reliably than PNG entries.
    public static byte[] ResizeToBmpEntry(Bitmap src, int size)
    {
        using (Bitmap bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb))
        {
            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.DrawImage(src, 0, 0, size, size);
            }

            int maskStride = ((size + 31) / 32) * 4;
            byte[] header = new byte[40];
            BitConverter.GetBytes(40).CopyTo(header, 0);                 // biSize
            BitConverter.GetBytes(size).CopyTo(header, 4);               // biWidth
            BitConverter.GetBytes(size * 2).CopyTo(header, 8);           // biHeight (XOR+AND)
            BitConverter.GetBytes((short)1).CopyTo(header, 12);          // biPlanes
            BitConverter.GetBytes((short)32).CopyTo(header, 14);         // biBitCount
            BitConverter.GetBytes(size * size * 4 + maskStride * size).CopyTo(header, 20); // biSizeImage

            byte[] result = new byte[40 + size * size * 4 + maskStride * size];
            header.CopyTo(result, 0);

            BitmapData data = bmp.LockBits(new Rectangle(0, 0, size, size), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            byte[] pixels = new byte[size * size * 4];
            Marshal.Copy(data.Scan0, pixels, 0, pixels.Length);
            bmp.UnlockBits(data);

            // XOR data: bottom-up rows; AND mask left zeroed (alpha channel rules).
            for (int y = 0; y < size; y++)
                Array.Copy(pixels, y * size * 4, result, 40 + (size - 1 - y) * size * 4, size * 4);

            return result;
        }
    }

    public static byte[] ResizeToPng(Bitmap src, int size)
    {
        using (Bitmap bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb))
        {
            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.DrawImage(src, 0, 0, size, size);
            }
            using (var ms = new System.IO.MemoryStream())
            {
                bmp.Save(ms, ImageFormat.Png);
                return ms.ToArray();
            }
        }
    }
}
"@

$src = New-Object System.Drawing.Bitmap($srcPng)
$transparent = [IconMaker]::RemoveOuterBackground($src, 25)
$square = [IconMaker]::CropAndSquare($transparent, 0.02)
Write-Host "Content cropped to $($square.Width)x$($square.Height)"

# Tray logo
New-Item -ItemType Directory -Force (Split-Path $outPng) | Out-Null
[System.IO.File]::WriteAllBytes($outPng, [IconMaker]::ResizeToPng($square, 256))

# Multi-size .ico: classic BMP entries for small sizes (reliable in the Win10 shell),
# PNG entry only for 256 px.
$sizes = @(16, 24, 32, 48, 256)
$pngs = @{}
foreach ($s in $sizes) {
    if ($s -ge 256) { $pngs[$s] = [IconMaker]::ResizeToPng($square, $s) }
    else { $pngs[$s] = [IconMaker]::ResizeToBmpEntry($square, $s) }
}

$fs = [System.IO.File]::Create($outIco)
$bw = New-Object System.IO.BinaryWriter($fs)
$bw.Write([uint16]0); $bw.Write([uint16]1); $bw.Write([uint16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
foreach ($s in $sizes) {
    $data = $pngs[$s]
    $dim = if ($s -ge 256) { 0 } else { $s }
    $bw.Write([byte]$dim); $bw.Write([byte]$dim)
    $bw.Write([byte]0); $bw.Write([byte]0)
    $bw.Write([uint16]1); $bw.Write([uint16]32)
    $bw.Write([uint32]$data.Length); $bw.Write([uint32]$offset)
    $offset += $data.Length
}
foreach ($s in $sizes) { $bw.Write($pngs[$s]) }
$bw.Close()

$square.Dispose(); $transparent.Dispose(); $src.Dispose()
Write-Host "Icon: $outIco ($((Get-Item $outIco).Length) bytes)"
Write-Host "Tray logo: $outPng ($((Get-Item $outPng).Length) bytes)"
