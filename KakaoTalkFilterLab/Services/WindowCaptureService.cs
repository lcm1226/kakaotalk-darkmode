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
        var candidateMask = new bool[converted.PixelWidth * converted.PixelHeight];

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
                candidateMask[y * converted.PixelWidth + x] = IsColorCandidate(originalPixels, converted.PixelWidth, converted.PixelHeight, x, y, stride);
            }
        }

        var preserveMask = BuildPreserveMask(candidateMask, originalPixels, converted.PixelWidth, converted.PixelHeight, stride);

        for (var y = 0; y < converted.PixelHeight; y++)
        {
            for (var x = 0; x < converted.PixelWidth; x++)
            {
                if (!preserveMask[y * converted.PixelWidth + x])
                {
                    continue;
                }

                var index = y * stride + x * 4;
                var preserveStrength = GetPreserveStrength(preserveMask, converted.PixelWidth, converted.PixelHeight, x, y);
                if (preserveStrength <= 0)
                {
                    continue;
                }

                ApplyPreservedColor(
                    ref outputPixels[index],
                    ref outputPixels[index + 1],
                    ref outputPixels[index + 2],
                    originalPixels[index + 2],
                    originalPixels[index + 1],
                    originalPixels[index],
                    preserveStrength);
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

    private static bool IsColorCandidate(byte[] pixels, int width, int height, int x, int y, int stride)
    {
        var index = y * stride + x * 4;
        var blue = pixels[index];
        var green = pixels[index + 1];
        var red = pixels[index + 2];

        var chroma = GetChroma(red, green, blue);
        if (chroma < 40)
        {
            return false;
        }

        var max = Math.Max(red, Math.Max(green, blue));
        var saturation = max == 0 ? 0 : chroma / (double)max;
        if (saturation < 0.28)
        {
            return false;
        }

        var luminance = GetLuminance(red, green, blue);
        if (luminance < 24 || luminance > 236)
        {
            return false;
        }

        var colorfulNeighbors = 0;

        foreach (var (nx, ny) in EnumerateNeighborCoordinates(x, y, width, height, 1))
        {
            var neighborIndex = ny * stride + nx * 4;
            var neighborBlue = pixels[neighborIndex];
            var neighborGreen = pixels[neighborIndex + 1];
            var neighborRed = pixels[neighborIndex + 2];
            var neighborChroma = GetChroma(neighborRed, neighborGreen, neighborBlue);

            if (neighborChroma >= 28)
            {
                colorfulNeighbors++;
            }
        }

        return colorfulNeighbors >= 3;
    }

    private static void ApplyPreservedColor(
        ref byte blueOut,
        ref byte greenOut,
        ref byte redOut,
        byte red,
        byte green,
        byte blue,
        double preserveStrength)
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

        redOut = BlendChannel(redOut, ClampToByte(scaledRed), preserveStrength);
        greenOut = BlendChannel(greenOut, ClampToByte(scaledGreen), preserveStrength);
        blueOut = BlendChannel(blueOut, ClampToByte(scaledBlue), preserveStrength);
    }

    private static bool[] BuildPreserveMask(bool[] candidateMask, byte[] pixels, int width, int height, int stride)
    {
        var preserveMask = new bool[candidateMask.Length];
        var visited = new bool[candidateMask.Length];
        var queue = new Queue<int>();
        var component = new List<int>();

        for (var start = 0; start < candidateMask.Length; start++)
        {
            if (!candidateMask[start] || visited[start])
            {
                continue;
            }

            queue.Clear();
            component.Clear();

            visited[start] = true;
            queue.Enqueue(start);

            var minX = start % width;
            var maxX = minX;
            var minY = start / width;
            var maxY = minY;
            var chromaSum = 0.0;
            var saturationSum = 0.0;

            while (queue.Count > 0)
            {
                var current = queue.Dequeue();
                component.Add(current);

                var x = current % width;
                var y = current / width;
                if (x < minX) minX = x;
                if (x > maxX) maxX = x;
                if (y < minY) minY = y;
                if (y > maxY) maxY = y;

                var pixelIndex = y * stride + x * 4;
                var blue = pixels[pixelIndex];
                var green = pixels[pixelIndex + 1];
                var red = pixels[pixelIndex + 2];
                var chroma = GetChroma(red, green, blue);
                chromaSum += chroma;

                var max = Math.Max(red, Math.Max(green, blue));
                saturationSum += max == 0 ? 0 : chroma / (double)max;

                foreach (var (nx, ny) in EnumerateNeighborCoordinates(x, y, width, height, 1))
                {
                    var neighbor = ny * width + nx;
                    if (!candidateMask[neighbor] || visited[neighbor])
                    {
                        continue;
                    }

                    visited[neighbor] = true;
                    queue.Enqueue(neighbor);
                }
            }

            var componentWidth = maxX - minX + 1;
            var componentHeight = maxY - minY + 1;
            var boundingArea = componentWidth * componentHeight;
            var fillRatio = boundingArea == 0 ? 0 : component.Count / (double)boundingArea;

            if (!ShouldKeepComponent(
                    component.Count,
                    componentWidth,
                    componentHeight,
                    fillRatio,
                    chromaSum / component.Count,
                    saturationSum / component.Count))
            {
                continue;
            }

            foreach (var index in component)
            {
                preserveMask[index] = true;
            }
        }

        return preserveMask;
    }

    private static bool ShouldKeepComponent(
        int area,
        int width,
        int height,
        double fillRatio,
        double averageChroma,
        double averageSaturation)
    {
        if (averageChroma < 46 || averageSaturation < 0.34)
        {
            return false;
        }

        if (fillRatio < 0.42)
        {
            return false;
        }

        if (area >= 90 && width >= 8 && height >= 8)
        {
            return true;
        }

        if (area >= 48 && width >= 6 && height >= 6 && fillRatio >= 0.55)
        {
            return true;
        }

        return area >= 24 && width >= 5 && height >= 5 && fillRatio >= 0.70;
    }

    private static double GetPreserveStrength(bool[] mask, int width, int height, int x, int y)
    {
        var preservedNeighbors = 0;
        foreach (var (nx, ny) in EnumerateNeighborCoordinates(x, y, width, height, 1))
        {
            if (mask[ny * width + nx])
            {
                preservedNeighbors++;
            }
        }

        return preservedNeighbors switch
        {
            >= 7 => 1.0,
            6 => 0.88,
            5 => 0.72,
            4 => 0.58,
            _ => 0.0
        };
    }

    private static IEnumerable<(int X, int Y)> EnumerateNeighborCoordinates(int x, int y, int width, int height, int radius)
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

                yield return (nx, ny);
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

    private static byte BlendChannel(byte baseValue, byte preservedValue, double preserveStrength)
    {
        return ClampToByte(baseValue + ((preservedValue - baseValue) * preserveStrength));
    }

    private delegate void PixelTransform(ref byte b, ref byte g, ref byte r, byte a);
}
