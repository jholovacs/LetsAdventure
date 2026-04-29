using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace LetsAdventure.WorldEditor.Services;

internal static class Map2dPyramidCodec
{
    /// <summary>Pad BGRA raster to <paramref name="tw"/>×<paramref name="th"/> by replicating edge pixels; returns new buffer.</summary>
    public static byte[] PadBgraToSize(ReadOnlySpan<byte> src, int srcW, int srcH, int tw, int th)
    {
        if (srcW < 1 || srcH < 1 || tw < 1 || th < 1)
            throw new ArgumentOutOfRangeException();
        var dstStride = tw * 4;
        var dst = new byte[dstStride * th];
        for (var y = 0; y < th; y++)
        {
            var sy = Math.Min(y, srcH - 1);
            for (var x = 0; x < tw; x++)
            {
                var sx = Math.Min(x, srcW - 1);
                var si = (sy * srcW + sx) * 4;
                var di = y * dstStride + x * 4;
                dst[di] = src[si];
                dst[di + 1] = src[si + 1];
                dst[di + 2] = src[si + 2];
                dst[di + 3] = src[si + 3];
            }
        }

        return dst;
    }

    /// <summary>4 children arranged [00][01] / [10][11]; each child max <paramref name="expectedW"/>×<paramref name="expectedH"/> BGRA, padded internally to that size.</summary>
    public static byte[] QuarterFromFourChildren(
        byte[]? c00, int w00, int h00,
        byte[]? c01, int w01, int h01,
        byte[]? c10, int w10, int h10,
        byte[]? c11, int w11, int h11,
        int expectedW,
        int expectedH,
        int outW,
        int outH)
    {
        expectedW = Math.Max(1, expectedW);
        expectedH = Math.Max(1, expectedH);
        outW = Math.Max(1, outW);
        outH = Math.Max(1, outH);
        var cw = expectedW * 2;
        var ch = expectedH * 2;
        var merged = new byte[cw * ch * 4];
        void Blit(byte[]? src, int sw, int sh, int ox, int oy)
        {
            if (src is null || sw < 1 || sh < 1)
                return;
            var pad = PadBgraToSize(src, sw, sh, expectedW, expectedH);
            for (var y = 0; y < expectedH; y++)
            {
                for (var x = 0; x < expectedW; x++)
                {
                    var si = (y * expectedW + x) * 4;
                    var mx = ox + x;
                    var my = oy + y;
                    var di = (my * cw + mx) * 4;
                    merged[di] = pad[si];
                    merged[di + 1] = pad[si + 1];
                    merged[di + 2] = pad[si + 2];
                    merged[di + 3] = pad[si + 3];
                }
            }
        }

        Blit(c00, w00, h00, 0, 0);
        Blit(c01, w01, h01, expectedW, 0);
        Blit(c10, w10, h10, 0, expectedH);
        Blit(c11, w11, h11, expectedW, expectedH);

        return BoxDownsample2X2(merged, cw, ch, outW, outH);
    }

    public static byte[] BoxDownsample2X2(byte[] bgra, int w, int h, int outW, int outH)
    {
        if (w < 2 || h < 2)
            throw new ArgumentException("Need at least 2×2 for 2×2 box filter.");
        var dstStride = outW * 4;
        var dst = new byte[dstStride * outH];
        for (var oy = 0; oy < outH; oy++)
        {
            var y0 = Math.Min(oy * 2, h - 2);
            for (var ox = 0; ox < outW; ox++)
            {
                var x0 = Math.Min(ox * 2, w - 2);
                var sumB = 0;
                var sumG = 0;
                var sumR = 0;
                var sumA = 0;
                for (var dy = 0; dy < 2; dy++)
                {
                    for (var dx = 0; dx < 2; dx++)
                    {
                        var i = ((y0 + dy) * w + (x0 + dx)) * 4;
                        sumB += bgra[i];
                        sumG += bgra[i + 1];
                        sumR += bgra[i + 2];
                        sumA += bgra[i + 3];
                    }
                }

                var o = oy * dstStride + ox * 4;
                dst[o] = (byte)(sumB / 4);
                dst[o + 1] = (byte)(sumG / 4);
                dst[o + 2] = (byte)(sumR / 4);
                dst[o + 3] = (byte)(sumA / 4);
            }
        }

        return dst;
    }

    /// <summary>
    /// Box-filter resample: each destination pixel averages the source pixels it covers
    /// (covers upscale, downscale, and identity).
    /// </summary>
    public static byte[] ResizeBgraAreaAverage(byte[] src, int sw, int sh, int dw, int dh)
    {
        if (sw < 1 || sh < 1 || dw < 1 || dh < 1)
            throw new ArgumentOutOfRangeException();
        if (src.Length < (long)sw * sh * 4)
            throw new ArgumentException("BGRA buffer too small.");

        var dst = new byte[dw * dh * 4];
        var dstStride = dw * 4;
        for (var dy = 0; dy < dh; dy++)
        {
            var fy0 = dy * (double)sh / dh;
            var fy1 = (dy + 1) * (double)sh / dh;
            var y0 = (int)Math.Floor(fy0);
            var y1 = (int)Math.Ceiling(fy1);
            if (y1 <= y0)
                y1 = Math.Min(sh, y0 + 1);
            y0 = Math.Clamp(y0, 0, Math.Max(0, sh - 1));
            y1 = Math.Clamp(y1, y0 + 1, sh);

            for (var dx = 0; dx < dw; dx++)
            {
                var fx0 = dx * (double)sw / dw;
                var fx1 = (dx + 1) * (double)sw / dw;
                var x0 = (int)Math.Floor(fx0);
                var x1 = (int)Math.Ceiling(fx1);
                if (x1 <= x0)
                    x1 = Math.Min(sw, x0 + 1);
                x0 = Math.Clamp(x0, 0, Math.Max(0, sw - 1));
                x1 = Math.Clamp(x1, x0 + 1, sw);

                long sumB = 0, sumG = 0, sumR = 0, sumA = 0;
                var cnt = 0;
                for (var y = y0; y < y1; y++)
                {
                    for (var x = x0; x < x1; x++)
                    {
                        var i = (y * sw + x) * 4;
                        sumB += src[i];
                        sumG += src[i + 1];
                        sumR += src[i + 2];
                        sumA += src[i + 3];
                        cnt++;
                    }
                }

                if (cnt == 0)
                {
                    var i = (y0 * sw + x0) * 4;
                    sumB = src[i];
                    sumG = src[i + 1];
                    sumR = src[i + 2];
                    sumA = src[i + 3];
                    cnt = 1;
                }

                var o = dy * dstStride + dx * 4;
                dst[o] = (byte)(sumB / cnt);
                dst[o + 1] = (byte)(sumG / cnt);
                dst[o + 2] = (byte)(sumR / cnt);
                dst[o + 3] = (byte)(sumA / cnt);
            }
        }

        return dst;
    }

    public static void SaveBgraPng(string path, int w, int h, byte[] bgra)
    {
        var stride = w * 4;
        var bmp = new WriteableBitmap(w, h, 96, 96, PixelFormats.Bgra32, null);
        bmp.WritePixels(new Int32Rect(0, 0, w, h), bgra, stride, 0);
        var dir = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(bmp));
        using var fs = File.Create(path);
        enc.Save(fs);
    }

    public static byte[]? TryLoadBgraPng(string path, out int w, out int h)
    {
        w = 0;
        h = 0;
        if (!File.Exists(path))
            return null;
        try
        {
            using var fs = File.OpenRead(path);
            var dec = new PngBitmapDecoder(fs, BitmapCreateOptions.PreservePixelFormat,
                BitmapCacheOption.OnLoad);
            var frame = dec.Frames[0];
            w = frame.PixelWidth;
            h = frame.PixelHeight;
            if (w < 1 || h < 1)
                return null;
            var stride = w * 4;
            var buf = new byte[stride * h];
            frame.CopyPixels(buf, stride, 0);
            return buf;
        }
        catch
        {
            return null;
        }
    }
}
