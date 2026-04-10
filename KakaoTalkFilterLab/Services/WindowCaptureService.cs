using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.IO;
using KakaoTalkFilterLab.Models;
using KakaoTalkFilterLab.Native;

namespace KakaoTalkFilterLab.Services;

internal sealed class WindowCaptureService
{
    public BitmapSource? Capture(WindowInfo window)
    {
        var hBitmap = Win32.TryCaptureWindowBitmap(window.Handle, window.Width, window.Height);
        if (hBitmap == nint.Zero)
        {
            return null;
        }

        try
        {
            var source = Imaging.CreateBitmapSourceFromHBitmap(
                hBitmap,
                nint.Zero,
                Int32Rect.Empty,
                BitmapSizeOptions.FromEmptyOptions());

            source.Freeze();
            return source;
        }
        finally
        {
            Win32.DeleteGdiObject(hBitmap);
        }
    }

    public BitmapSource Invert(BitmapSource source)
    {
        return Transform(source, static (ref byte b, ref byte g, ref byte r, byte a) =>
        {
            r = (byte)(255 - r);
            g = (byte)(255 - g);
            b = (byte)(255 - b);
        });
    }

    public BitmapSource SmartInvert(BitmapSource source)
    {
        var converted = new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
        converted.Freeze();

        var stride = converted.PixelWidth * 4;
        var originalPixels = new byte[stride * converted.PixelHeight];
        converted.CopyPixels(originalPixels, stride, 0);

        var outputPixels = new byte[originalPixels.Length];
        Buffer.BlockCopy(originalPixels, 0, outputPixels, 0, originalPixels.Length);
        var preserveMask = new bool[converted.PixelWidth * converted.PixelHeight];

        for (var y = 0; y < converted.PixelHeight; y++)
        {
            for (var x = 0; x < converted.PixelWidth; x++)
            {
                var index = y * stride + x * 4;
                var blue = originalPixels[index];
                var green = originalPixels[index + 1];
                var red = originalPixels[index + 2];
                var alpha = originalPixels[index + 3];

                if (alpha == 0)
                {
                    continue;
                }

                outputPixels[index] = (byte)(255 - blue);
                outputPixels[index + 1] = (byte)(255 - green);
                outputPixels[index + 2] = (byte)(255 - red);
                preserveMask[y * converted.PixelWidth + x] = ShouldPreserveColorRegion(originalPixels, converted.PixelWidth, converted.PixelHeight, x, y, stride);
            }
        }

        preserveMask = OpenMask(preserveMask, converted.PixelWidth, converted.PixelHeight);

        for (var y = 0; y < converted.PixelHeight; y++)
        {
            for (var x = 0; x < converted.PixelWidth; x++)
            {
                if (!preserveMask[y * converted.PixelWidth + x])
                {
                    continue;
                }

                var index = y * stride + x * 4;
                ApplyPreservedColor(
                    ref outputPixels[index],
                    ref outputPixels[index + 1],
                    ref outputPixels[index + 2],
                    originalPixels[index + 2],
                    originalPixels[index + 1],
                    originalPixels[index]);
            }
        }

        var result = BitmapSource.Create(
            converted.PixelWidth,
            converted.PixelHeight,
            converted.DpiX,
            converted.DpiY,
            PixelFormats.Bgra32,
            null,
            outputPixels,
            stride);

        result.Freeze();
        return result;
    }

    public void SavePng(BitmapSource source, string path)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(source));

        using var stream = File.Create(path);
        encoder.Save(stream);
    }

    private static BitmapSource Transform(
        BitmapSource source,
        PixelTransform transform,
        Action<byte[], int, int>? postProcess = null)
    {
        var converted = new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
        converted.Freeze();

        var stride = converted.PixelWidth * 4;
        var pixelBytes = new byte[stride * converted.PixelHeight];
        converted.CopyPixels(pixelBytes, stride, 0);

        for (var index = 0; index < pixelBytes.Length; index += 4)
        {
            transform(ref pixelBytes[index], ref pixelBytes[index + 1], ref pixelBytes[index + 2], pixelBytes[index + 3]);
        }

        postProcess?.Invoke(pixelBytes, converted.PixelWidth, converted.PixelHeight);

        var result = BitmapSource.Create(
            converted.PixelWidth,
            converted.PixelHeight,
            converted.DpiX,
            converted.DpiY,
            PixelFormats.Bgra32,
            null,
            pixelBytes,
            stride);

        result.Freeze();
        return result;
    }

    private static byte ClampToByte(double value)
    {
        return (byte)Math.Clamp((int)Math.Round(value), 0, 255);
    }

    private static bool ShouldPreserveColorRegion(byte[] pixels, int width, int height, int x, int y, int stride)
    {
        var index = y * stride + x * 4;
        var blue = pixels[index];
        var green = pixels[index + 1];
        var red = pixels[index + 2];

        var chroma = GetChroma(red, green, blue);
        if (chroma < 28)
        {
            return false;
        }

        var max = Math.Max(red, Math.Max(green, blue));
        var saturation = max == 0 ? 0 : chroma / (double)max;
        if (saturation < 0.20)
        {
            return false;
        }

        var luminance = GetLuminance(red, green, blue);
        var colorfulNeighbors = 0;
        var similarNeighbors = 0;
        var totalNeighbors = 0;
        var distantColorHits = 0;
        var chromaTotal = chroma;
        var maxNeighborChroma = chroma;

        foreach (var (nx, ny, distance) in EnumerateNeighborCoordinates(x, y, width, height, 2))
        {
            totalNeighbors++;
            var neighborIndex = ny * stride + nx * 4;
            var neighborBlue = pixels[neighborIndex];
            var neighborGreen = pixels[neighborIndex + 1];
            var neighborRed = pixels[neighborIndex + 2];
            var neighborChroma = GetChroma(neighborRed, neighborGreen, neighborBlue);

            if (neighborChroma >= 18)
            {
                colorfulNeighbors++;
            }

            chromaTotal += neighborChroma;
            if (neighborChroma > maxNeighborChroma)
            {
                maxNeighborChroma = neighborChroma;
            }

            var neighborLuminance = GetLuminance(neighborRed, neighborGreen, neighborBlue);
            var colorDistance = Math.Abs(red - neighborRed) + Math.Abs(green - neighborGreen) + Math.Abs(blue - neighborBlue);
            if (Math.Abs(luminance - neighborLuminance) < 70 && colorDistance < 190)
            {
                similarNeighbors++;

                if (distance >= 2)
                {
                    distantColorHits++;
                }
            }
        }

        if (totalNeighbors < 8)
        {
            return false;
        }

        var averageChroma = chromaTotal / (double)(totalNeighbors + 1);
        return colorfulNeighbors >= 8
            && similarNeighbors >= 6
            && distantColorHits >= 3
            && averageChroma >= 32
            && maxNeighborChroma >= 52;
    }

    private static void ApplyPreservedColor(ref byte blueOut, ref byte greenOut, ref byte redOut, byte red, byte green, byte blue)
    {
        var luminance = GetLuminance(red, green, blue);
        var brightnessScale = luminance switch
        {
            > 220 => 0.66,
            > 180 => 0.74,
            > 130 => 0.86,
            > 80 => 0.94,
            _ => 1.0
        };

        var scaledRed = red * brightnessScale;
        var scaledGreen = green * brightnessScale;
        var scaledBlue = blue * brightnessScale;

        var average = (scaledRed + scaledGreen + scaledBlue) / 3.0;
        const double saturationBoost = 1.18;

        scaledRed = average + (scaledRed - average) * saturationBoost;
        scaledGreen = average + (scaledGreen - average) * saturationBoost;
        scaledBlue = average + (scaledBlue - average) * saturationBoost;

        redOut = ClampToByte(scaledRed);
        greenOut = ClampToByte(scaledGreen);
        blueOut = ClampToByte(scaledBlue);
    }

    private static bool[] OpenMask(bool[] mask, int width, int height)
    {
        return DilateMask(ErodeMask(mask, width, height), width, height);
    }

    private static bool[] ErodeMask(bool[] mask, int width, int height)
    {
        var result = new bool[mask.Length];
        for (var y = 1; y < height - 1; y++)
        {
            for (var x = 1; x < width - 1; x++)
            {
                var keep = true;
                for (var dy = -1; dy <= 1 && keep; dy++)
                {
                    for (var dx = -1; dx <= 1; dx++)
                    {
                        if (!mask[(y + dy) * width + (x + dx)])
                        {
                            keep = false;
                            break;
                        }
                    }
                }

                result[y * width + x] = keep;
            }
        }

        return result;
    }

    private static bool[] DilateMask(bool[] mask, int width, int height)
    {
        var result = new bool[mask.Length];
        for (var y = 1; y < height - 1; y++)
        {
            for (var x = 1; x < width - 1; x++)
            {
                var on = false;
                for (var dy = -1; dy <= 1 && !on; dy++)
                {
                    for (var dx = -1; dx <= 1; dx++)
                    {
                        if (mask[(y + dy) * width + (x + dx)])
                        {
                            on = true;
                            break;
                        }
                    }
                }

                result[y * width + x] = on;
            }
        }

        return result;
    }

    private static IEnumerable<(int X, int Y, int Distance)> EnumerateNeighborCoordinates(int x, int y, int width, int height, int radius)
    {
        for (var dy = -radius; dy <= radius; dy++)
        {
            for (var dx = -radius; dx <= radius; dx++)
            {
                if (dx == 0 && dy == 0)
                {
                    continue;
                }

                var nx = x + dx;
                var ny = y + dy;
                if (nx < 0 || ny < 0 || nx >= width || ny >= height)
                {
                    continue;
                }

                yield return (nx, ny, Math.Max(Math.Abs(dx), Math.Abs(dy)));
            }
        }
    }

    private static int GetChroma(byte red, byte green, byte blue)
    {
        var max = Math.Max(red, Math.Max(green, blue));
        var min = Math.Min(red, Math.Min(green, blue));
        return max - min;
    }

    private static double GetLuminance(byte red, byte green, byte blue)
    {
        return 0.2126 * red + 0.7152 * green + 0.0722 * blue;
    }

    private delegate void PixelTransform(ref byte b, ref byte g, ref byte r, byte a);
}
