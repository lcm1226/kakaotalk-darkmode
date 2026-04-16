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
    private static readonly Geometry KakaoProfileGeometry40 = CreateKakaoProfileGeometry40();

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

    public BitmapSource Invert(
        BitmapSource source,
        double strength = 1.0,
        double brightness = 0,
        double contrast = 1.0,
        double gamma = 1.0)
    {
        strength = Math.Clamp(strength, 0, 1);
        return Transform(source, (ref byte b, ref byte g, ref byte r, byte a) =>
        {
            var invertedRed = (byte)(255 - r);
            var invertedGreen = (byte)(255 - g);
            var invertedBlue = (byte)(255 - b);

            r = BlendChannel(r, invertedRed, strength);
            g = BlendChannel(g, invertedGreen, strength);
            b = BlendChannel(b, invertedBlue, strength);
        },
        (pixels, width, height) =>
        {
            ApplyToneAdjustments(pixels, brightness, contrast, gamma);
            NormalizeOuterEdge(pixels, width, height);
        });
    }

    public BitmapSource SmartInvert(
        BitmapSource source,
        double strength = 1.0,
        double brightness = 0,
        double contrast = 1.0,
        double gamma = 1.0)
    {
        strength = Math.Clamp(strength, 0, 1);
        var converted = new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
        converted.Freeze();

        var stride = converted.PixelWidth * 4;
        var originalPixels = new byte[stride * converted.PixelHeight];
        converted.CopyPixels(originalPixels, stride, 0);

        var outputPixels = new byte[originalPixels.Length];
        Buffer.BlockCopy(originalPixels, 0, outputPixels, 0, originalPixels.Length);
        var candidateMask = new bool[converted.PixelWidth * converted.PixelHeight];
        var photoCandidateMask = new bool[converted.PixelWidth * converted.PixelHeight];

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
                photoCandidateMask[y * converted.PixelWidth + x] = IsPhotoCandidate(originalPixels, converted.PixelWidth, converted.PixelHeight, x, y, stride);
            }
        }

        var preserveStrengthMap = BuildPreserveStrengthMap(candidateMask, originalPixels, converted.PixelWidth, converted.PixelHeight, stride);
        var (photoPreserveStrengthMap, defaultAvatarMask, normalizedAvatarMask) = BuildPhotoPreserveMaps(photoCandidateMask, originalPixels, converted.PixelWidth, converted.PixelHeight, stride);

        for (var y = 0; y < converted.PixelHeight; y++)
        {
            for (var x = 0; x < converted.PixelWidth; x++)
            {
                var componentStrength = preserveStrengthMap[y * converted.PixelWidth + x];
                var photoComponentStrength = photoPreserveStrengthMap[y * converted.PixelWidth + x];
                if (componentStrength <= 0 && photoComponentStrength <= 0)
                {
                    continue;
                }

                var index = y * stride + x * 4;
                var preserveStrength = GetPreserveStrength(preserveStrengthMap, converted.PixelWidth, converted.PixelHeight, x, y) * componentStrength;
                var photoPreserveStrength = GetPreserveStrength(photoPreserveStrengthMap, converted.PixelWidth, converted.PixelHeight, x, y) * photoComponentStrength;

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

        if (strength < 1.0)
        {
            for (var index = 0; index < outputPixels.Length; index += 4)
            {
                outputPixels[index] = BlendChannel(originalPixels[index], outputPixels[index], strength);
                outputPixels[index + 1] = BlendChannel(originalPixels[index + 1], outputPixels[index + 1], strength);
                outputPixels[index + 2] = BlendChannel(originalPixels[index + 2], outputPixels[index + 2], strength);
            }
        }

        ApplyToneAdjustments(outputPixels, brightness, contrast, gamma);

        for (var y = 0; y < converted.PixelHeight; y++)
        {
            for (var x = 0; x < converted.PixelWidth; x++)
            {
                var photoComponentStrength = photoPreserveStrengthMap[y * converted.PixelWidth + x];
                if (photoComponentStrength <= 0)
                {
                    continue;
                }

                var index = y * stride + x * 4;
                var photoPreserveStrength = GetPreserveStrength(photoPreserveStrengthMap, converted.PixelWidth, converted.PixelHeight, x, y) * photoComponentStrength;
                if (photoPreserveStrength <= 0)
                {
                    continue;
                }

                if (defaultAvatarMask[y * converted.PixelWidth + x]
                    ? ShouldSkipSmallAvatarEdgePixel(originalPixels, converted.PixelWidth, converted.PixelHeight, x, y, stride, photoPreserveStrength)
                    : normalizedAvatarMask[y * converted.PixelWidth + x]
                        ? ShouldSkipNormalizedAvatarFringePixel(originalPixels, converted.PixelWidth, converted.PixelHeight, x, y, stride, photoPreserveStrength)
                        : ShouldSkipPhotoPreservePixel(originalPixels, converted.PixelWidth, converted.PixelHeight, x, y, stride, photoPreserveStrength))
                {
                    continue;
                }

                ApplyOriginalPreservedColor(
                    ref outputPixels[index],
                    ref outputPixels[index + 1],
                    ref outputPixels[index + 2],
                    originalPixels[index + 2],
                    originalPixels[index + 1],
                    originalPixels[index],
                    photoPreserveStrength);
            }
        }

        NormalizeOuterEdge(outputPixels, converted.PixelWidth, converted.PixelHeight);

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

    public void SaveSmartDebugOutputs(BitmapSource source, string outputDirectory)
    {
        Directory.CreateDirectory(outputDirectory);

        var converted = new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
        converted.Freeze();

        var stride = converted.PixelWidth * 4;
        var pixels = new byte[stride * converted.PixelHeight];
        converted.CopyPixels(pixels, stride, 0);

        var photoCandidateMask = new bool[converted.PixelWidth * converted.PixelHeight];
        for (var y = 0; y < converted.PixelHeight; y++)
        {
            for (var x = 0; x < converted.PixelWidth; x++)
            {
                photoCandidateMask[y * converted.PixelWidth + x] = IsPhotoCandidate(
                    pixels,
                    converted.PixelWidth,
                    converted.PixelHeight,
                    x,
                    y,
                    stride);
            }
        }

        var (_, defaultAvatarMask, _) = BuildPhotoPreserveMaps(
            photoCandidateMask,
            pixels,
            converted.PixelWidth,
            converted.PixelHeight,
            stride);

        SavePng(
            CreateMaskBitmap(photoCandidateMask, converted.PixelWidth, converted.PixelHeight, converted.DpiX, converted.DpiY),
            Path.Combine(outputDirectory, "debug-photo-candidate.png"));
        SavePng(
            CreateMaskBitmap(defaultAvatarMask, converted.PixelWidth, converted.PixelHeight, converted.DpiX, converted.DpiY),
            Path.Combine(outputDirectory, "debug-default-avatar-mask.png"));
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

    private static BitmapSource CreateMaskBitmap(bool[] mask, int width, int height, double dpiX, double dpiY)
    {
        var stride = width * 4;
        var pixels = new byte[stride * height];
        for (var i = 0; i < mask.Length; i++)
        {
            var index = i * 4;
            var value = mask[i] ? (byte)255 : (byte)0;
            pixels[index] = value;
            pixels[index + 1] = value;
            pixels[index + 2] = value;
            pixels[index + 3] = 255;
        }

        var result = BitmapSource.Create(
            width,
            height,
            dpiX,
            dpiY,
            PixelFormats.Bgra32,
            null,
            pixels,
            stride);
        result.Freeze();
        return result;
    }

    private static byte ClampToByte(double value)
    {
        return (byte)Math.Clamp((int)Math.Round(value), 0, 255);
    }

    private static void ApplyToneAdjustments(byte[] pixels, double brightness, double contrast, double gamma)
    {
        var brightnessOffset = Math.Clamp(brightness, -1.0, 1.0) * 0.28;
        var contrastScale = Math.Clamp(contrast, 0.5, 1.7);
        var gammaScale = Math.Clamp(gamma, 0.6, 1.8);

        if (Math.Abs(brightnessOffset) < 0.0001 &&
            Math.Abs(contrastScale - 1.0) < 0.0001 &&
            Math.Abs(gammaScale - 1.0) < 0.0001)
        {
            return;
        }

        for (var index = 0; index < pixels.Length; index += 4)
        {
            pixels[index] = AdjustToneChannel(pixels[index], brightnessOffset, contrastScale, gammaScale);
            pixels[index + 1] = AdjustToneChannel(pixels[index + 1], brightnessOffset, contrastScale, gammaScale);
            pixels[index + 2] = AdjustToneChannel(pixels[index + 2], brightnessOffset, contrastScale, gammaScale);
        }
    }

    private static void NormalizeOuterEdge(byte[] pixels, int width, int height)
    {
        if (width < 3 || height < 3)
        {
            return;
        }

        var stride = width * 4;

        for (var x = 0; x < width; x++)
        {
            CopyPixel(pixels, stride, x, 1, x, 0);
            CopyPixel(pixels, stride, x, height - 2, x, height - 1);
        }

        for (var y = 0; y < height; y++)
        {
            CopyPixel(pixels, stride, 1, y, 0, y);
            CopyPixel(pixels, stride, width - 2, y, width - 1, y);
        }

        NormalizeRoundedWindowCorners(pixels, width, height, stride);
    }

    private static void NormalizeRoundedWindowCorners(byte[] pixels, int width, int height, int stride)
    {
        var radius = Math.Max(4, Math.Min(8, Math.Min(width, height) / 28));
        NormalizeCorner(pixels, width, height, stride, 0, 0, radius);
        NormalizeCorner(pixels, width, height, stride, width - 1, 0, radius);
        NormalizeCorner(pixels, width, height, stride, 0, height - 1, radius);
        NormalizeCorner(pixels, width, height, stride, width - 1, height - 1, radius);
    }

    private static void NormalizeCorner(byte[] pixels, int width, int height, int stride, int cornerX, int cornerY, int radius)
    {
        var sampleAx = cornerX == 0 ? Math.Min(width - 1, 2) : Math.Max(0, width - 3);
        var sampleAy = cornerY == 0
            ? Math.Min(height - 1, radius + 3)
            : Math.Max(0, height - radius - 4);
        var sampleBx = cornerX == 0
            ? Math.Min(width - 1, radius + 3)
            : Math.Max(0, width - radius - 4);
        var sampleBy = cornerY == 0 ? Math.Min(height - 1, 2) : Math.Max(0, height - 3);

        var sampleAIndex = sampleAy * stride + sampleAx * 4;
        var sampleBIndex = sampleBy * stride + sampleBx * 4;
        var blue = (byte)((pixels[sampleAIndex] + pixels[sampleBIndex]) / 2);
        var green = (byte)((pixels[sampleAIndex + 1] + pixels[sampleBIndex + 1]) / 2);
        var red = (byte)((pixels[sampleAIndex + 2] + pixels[sampleBIndex + 2]) / 2);
        var alpha = (byte)((pixels[sampleAIndex + 3] + pixels[sampleBIndex + 3]) / 2);

        var startX = Math.Max(0, cornerX == 0 ? 0 : width - radius - 1);
        var endX = Math.Min(width - 1, cornerX == 0 ? radius : width - 1);
        var startY = Math.Max(0, cornerY == 0 ? 0 : height - radius - 1);
        var endY = Math.Min(height - 1, cornerY == 0 ? radius : height - 1);

        var centerX = cornerX == 0 ? radius : width - radius - 1;
        var centerY = cornerY == 0 ? radius : height - radius - 1;
        var maxDistance = radius * radius;

        for (var y = startY; y <= endY; y++)
        {
            for (var x = startX; x <= endX; x++)
            {
                var dx = x - centerX;
                var dy = y - centerY;
                if ((dx * dx) + (dy * dy) <= maxDistance)
                {
                    continue;
                }

                var index = y * stride + x * 4;
                pixels[index] = blue;
                pixels[index + 1] = green;
                pixels[index + 2] = red;
                pixels[index + 3] = alpha;
            }
        }
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

    private static bool IsPhotoCandidate(byte[] pixels, int width, int height, int x, int y, int stride)
    {
        var index = y * stride + x * 4;
        var blue = pixels[index];
        var green = pixels[index + 1];
        var red = pixels[index + 2];
        var alpha = pixels[index + 3];
        if (alpha == 0)
        {
            return false;
        }

        var luminance = GetLuminance(red, green, blue);
        if (luminance < 16 || luminance > 245)
        {
            return false;
        }

        var chroma = GetChroma(red, green, blue);
        var localTexture = GetLocalTexture(pixels, width, height, x, y, stride);
        if (localTexture < 7 && chroma < 22)
        {
            return false;
        }

        var avatarLikeNeighbors = 0;
        foreach (var (nx, ny) in EnumerateNeighborCoordinates(x, y, width, height, 1))
        {
            var neighborIndex = ny * stride + nx * 4;
            var neighborChroma = GetChroma(
                pixels[neighborIndex + 2],
                pixels[neighborIndex + 1],
                pixels[neighborIndex]);
            if (GetLocalTexture(pixels, width, height, nx, ny, stride) >= 7 || neighborChroma >= 22)
            {
                avatarLikeNeighbors++;
            }
        }

        return avatarLikeNeighbors >= 3;
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
        const double saturationBoost = 1.10;

        scaledRed = average + (scaledRed - average) * saturationBoost;
        scaledGreen = average + (scaledGreen - average) * saturationBoost;
        scaledBlue = average + (scaledBlue - average) * saturationBoost;

        redOut = BlendChannel(redOut, ClampToByte(scaledRed), preserveStrength);
        greenOut = BlendChannel(greenOut, ClampToByte(scaledGreen), preserveStrength);
        blueOut = BlendChannel(blueOut, ClampToByte(scaledBlue), preserveStrength);
    }

    private static void ApplyOriginalPreservedColor(
        ref byte blueOut,
        ref byte greenOut,
        ref byte redOut,
        byte red,
        byte green,
        byte blue,
        double preserveStrength)
    {
        redOut = BlendChannel(redOut, red, preserveStrength);
        greenOut = BlendChannel(greenOut, green, preserveStrength);
        blueOut = BlendChannel(blueOut, blue, preserveStrength);
    }

    private static double[] BuildPreserveStrengthMap(bool[] candidateMask, byte[] pixels, int width, int height, int stride)
    {
        var preserveStrengthMap = new double[candidateMask.Length];
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

            var componentStrength = GetComponentPreserveStrength(component.Count, componentWidth, componentHeight, fillRatio);
            foreach (var index in component)
            {
                preserveStrengthMap[index] = componentStrength;
            }
        }

        return preserveStrengthMap;
    }

    private static (double[] preserveStrengthMap, bool[] defaultAvatarMask, bool[] normalizedAvatarMask) BuildPhotoPreserveMaps(bool[] candidateMask, byte[] pixels, int width, int height, int stride)
    {
        var preserveStrengthMap = new double[candidateMask.Length];
        var defaultAvatarMask = new bool[candidateMask.Length];
        var normalizedAvatarMask = new bool[candidateMask.Length];
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
            var textureSum = 0.0;
            var chromaSum = 0.0;
            var redSum = 0.0;
            var greenSum = 0.0;
            var blueSum = 0.0;

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
                textureSum += GetLocalTexture(pixels, width, height, x, y, stride);
                chromaSum += GetChroma(red, green, blue);
                redSum += red;
                greenSum += green;
                blueSum += blue;

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

            if (!ShouldKeepPhotoComponent(
                    component.Count,
                    minX,
                    maxX,
                    componentWidth,
                    componentHeight,
                    width,
                    fillRatio,
                    textureSum / component.Count,
                    chromaSum / component.Count))
            {
                continue;
            }

            var averageTexture = textureSum / component.Count;
            var averageChroma = chromaSum / component.Count;
            var isFlatDefaultAvatar = IsFlatDefaultAvatarComponent(averageTexture, averageChroma);
            var componentStrength = GetPhotoComponentPreserveStrength(component.Count, componentWidth, componentHeight, fillRatio);
            var edgePadding = GetAvatarEdgePadding(componentWidth, componentHeight, fillRatio);
            var (maskMinX, maskMaxX, maskMinY, maskMaxY) = GetAvatarMaskBounds(
                minX,
                maxX,
                minY,
                maxY,
                componentWidth,
                componentHeight,
                width,
                height,
                fillRatio,
                edgePadding,
                isFlatDefaultAvatar);
            var maskWidth = maskMaxX - maskMinX + 1;
            var maskHeight = maskMaxY - maskMinY + 1;
            var isNormalizedAvatarSlot = IsNormalizedAvatarSlot(maskWidth, maskHeight, componentWidth, componentHeight);
            if (isFlatDefaultAvatar)
            {
                componentStrength = 1.0;
            }
            var shape = GetAvatarShape(
                maskWidth,
                maskHeight,
                fillRatio,
                isNormalizedAvatarSlot,
                isFlatDefaultAvatar,
                averageTexture,
                averageChroma);
            var inset = GetAvatarInset(maskWidth, maskHeight, fillRatio, shape);
            if (isFlatDefaultAvatar)
            {
                inset = 0;
            }
            else if (isNormalizedAvatarSlot)
            {
                inset += 1;
            }
            var innerLeft = maskMinX + inset;
            var innerTop = maskMinY + inset;
            var innerWidth = Math.Max(1, maskWidth - inset * 2);
            var innerHeight = Math.Max(1, maskHeight - inset * 2);
            if (isFlatDefaultAvatar)
            {
                innerLeft = maskMinX;
                innerTop = maskMinY;
                innerWidth = maskWidth;
                innerHeight = maskHeight;
            }
            else if (isNormalizedAvatarSlot)
            {
                innerLeft = maskMinX + 1;
                innerTop = maskMinY + 1;
                innerWidth = Math.Max(1, maskWidth - 2);
                innerHeight = Math.Max(1, maskHeight - 2);
            }
            var cornerRadius = GetPhotoCornerRadius(innerWidth, innerHeight, fillRatio);
            if (isFlatDefaultAvatar)
            {
                cornerRadius = Math.Max(2, cornerRadius - 3);
            }
            var componentMask = new bool[maskWidth * maskHeight];
            foreach (var componentIndex in component)
            {
                var componentX = componentIndex % width;
                var componentY = componentIndex / width;
                if (componentX < maskMinX || componentX > maskMaxX || componentY < maskMinY || componentY > maskMaxY)
                {
                    continue;
                }

                componentMask[(componentY - maskMinY) * maskWidth + (componentX - maskMinX)] = true;
            }

            if (isFlatDefaultAvatar)
            {
                ExpandFlatAvatarComponentMask(
                    componentMask,
                    maskMinX,
                    maskMinY,
                    maskWidth,
                    maskHeight,
                    pixels,
                    width,
                    height,
                    stride,
                    redSum / component.Count,
                    greenSum / component.Count,
                    blueSum / component.Count);
            }
            else if (isNormalizedAvatarSlot)
            {
                ExpandSmallAvatarComponentMask(
                    componentMask,
                    maskMinX,
                    maskMinY,
                    maskWidth,
                    maskHeight,
                    pixels,
                    width,
                    height,
                    stride,
                    redSum / component.Count,
                    greenSum / component.Count,
                    blueSum / component.Count);

                if (averageTexture < 15.5)
                {
                    ErodeSmallLogoAvatarMask(componentMask, maskWidth, maskHeight);
                }
            }

            var componentSearchRadius = GetAvatarComponentSearchRadius(
                componentWidth,
                componentHeight,
                fillRatio,
                isNormalizedAvatarSlot,
                isFlatDefaultAvatar,
                shape);
            var requiresComponentProximity = !isFlatDefaultAvatar;

            for (var y = maskMinY; y <= maskMaxY; y++)
            {
                for (var x = maskMinX; x <= maskMaxX; x++)
                {
                    var shapeCoverage = isFlatDefaultAvatar
                        ? GetComponentMaskCoverage(componentMask, maskMinX, maskMinY, maskWidth, maskHeight, x, y)
                        : GetAvatarShapeCoverage(x, y, innerLeft, innerTop, innerWidth, innerHeight, cornerRadius, shape);
                    if (shapeCoverage <= 0)
                    {
                        continue;
                    }

                    if (requiresComponentProximity &&
                        !HasNearbyComponentPixel(componentMask, maskMinX, maskMinY, maskWidth, maskHeight, x, y, componentSearchRadius))
                    {
                        continue;
                    }

                    var preserveIndex = y * width + x;
                    preserveStrengthMap[preserveIndex] = Math.Max(
                        preserveStrengthMap[preserveIndex],
                        componentStrength * shapeCoverage);
                    if (isFlatDefaultAvatar)
                    {
                        defaultAvatarMask[preserveIndex] = true;
                    }
                    else if (isNormalizedAvatarSlot)
                    {
                        normalizedAvatarMask[preserveIndex] = true;
                    }
                }
            }
        }

        return (preserveStrengthMap, defaultAvatarMask, normalizedAvatarMask);
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

    private static double GetComponentPreserveStrength(int area, int width, int height, double fillRatio)
    {
        if (area >= 120 && width >= 10 && height >= 10)
        {
            return 1.0;
        }

        if (area >= 64 && width >= 7 && height >= 7)
        {
            return fillRatio >= 0.72 ? 0.90 : 0.82;
        }

        return fillRatio >= 0.78 ? 0.74 : 0.66;
    }

    private static bool ShouldKeepPhotoComponent(
        int area,
        int minX,
        int maxX,
        int width,
        int height,
        int frameWidth,
        double fillRatio,
        double averageTexture,
        double averageChroma)
    {
        if (area < 180 || width < 16 || height < 16)
        {
            return false;
        }

        var aspectRatio = width / (double)height;
        if (aspectRatio < 0.72 || aspectRatio > 1.38)
        {
            return false;
        }

        var isSmallAvatarLike =
            width >= 20 &&
            width <= 56 &&
            height >= 20 &&
            height <= 56 &&
            aspectRatio >= 0.78 &&
            aspectRatio <= 1.28 &&
            fillRatio >= 0.42;

        var centerX = (minX + maxX) / 2.0;
        if (isSmallAvatarLike)
        {
            if (centerX < frameWidth * 0.14 || centerX > frameWidth * 0.82)
            {
                return false;
            }
        }
        else if (centerX < frameWidth * 0.10 || centerX > frameWidth * 0.33)
        {
            return false;
        }

        if (width > 96 || height > 96)
        {
            return false;
        }

        if (fillRatio < 0.34)
        {
            return false;
        }

        if (averageTexture < 9.5 && !(averageChroma >= 18 && width <= 84 && height <= 84))
        {
            return false;
        }

        return averageChroma >= 7 || averageTexture >= 11.5;
    }

    private static double GetPhotoComponentPreserveStrength(int area, int width, int height, double fillRatio)
    {
        if (area >= 1000 && width >= 28 && height >= 28)
        {
            return 1.0;
        }

        if (area >= 360 && width >= 18 && height >= 18)
        {
            return fillRatio >= 0.58 ? 0.97 : 0.90;
        }

        return fillRatio >= 0.48 ? 0.86 : 0.78;
    }

    private static int GetPhotoCornerRadius(int width, int height, double fillRatio)
    {
        var size = Math.Min(width, height);
        var radiusRatio = fillRatio < 0.74 ? 0.50 : 0.22;
        return Math.Max(2, (int)Math.Round(size * radiusRatio));
    }

    private static (int minX, int maxX, int minY, int maxY) GetAvatarMaskBounds(
        int minX,
        int maxX,
        int minY,
        int maxY,
        int componentWidth,
        int componentHeight,
        int frameWidth,
        int frameHeight,
        double fillRatio,
        int edgePadding,
        bool isFlatDefaultAvatar)
    {
        var aspectRatio = componentWidth / (double)componentHeight;
        var shouldNormalizeToSlot =
            componentWidth >= 30 &&
            componentWidth <= 52 &&
            componentHeight >= 30 &&
            componentHeight <= 52 &&
            aspectRatio >= 0.82 &&
            aspectRatio <= 1.22;

        if (shouldNormalizeToSlot && isFlatDefaultAvatar)
        {
            var avatarSize = Math.Max(componentWidth, componentHeight) + 2;
            var centerX = (minX + maxX) / 2.0;
            var centerY = (minY + maxY) / 2.0;
            var fittedMinX = Math.Max(0, (int)Math.Round(centerX - ((avatarSize - 1) / 2.0)));
            var fittedMinY = Math.Max(0, (int)Math.Round(centerY - ((avatarSize - 1) / 2.0)));
            var fittedMaxX = Math.Min(frameWidth - 1, fittedMinX + avatarSize - 1);
            var fittedMaxY = Math.Min(frameHeight - 1, fittedMinY + avatarSize - 1);
            return (fittedMinX, fittedMaxX, fittedMinY, fittedMaxY);
        }

        if (shouldNormalizeToSlot)
        {
            const int avatarSlotSize = 40;
            var centerX = (minX + maxX) / 2.0;
            var centerY = (minY + maxY) / 2.0;
            var normalizedMinX = Math.Max(0, (int)Math.Round(centerX - ((avatarSlotSize - 1) / 2.0)));
            var normalizedMinY = Math.Max(0, (int)Math.Round(centerY - ((avatarSlotSize - 1) / 2.0)));
            var normalizedMaxX = Math.Min(frameWidth - 1, normalizedMinX + avatarSlotSize - 1);
            var normalizedMaxY = Math.Min(frameHeight - 1, normalizedMinY + avatarSlotSize - 1);
            return (normalizedMinX, normalizedMaxX, normalizedMinY, normalizedMaxY);
        }

        return (
            Math.Max(0, minX - edgePadding),
            Math.Min(frameWidth - 1, maxX + edgePadding),
            Math.Max(0, minY - edgePadding),
            Math.Min(frameHeight - 1, maxY + edgePadding));
    }

    private static bool IsNormalizedAvatarSlot(int maskWidth, int maskHeight, int componentWidth, int componentHeight)
    {
        return maskWidth == 40 &&
               maskHeight == 40 &&
               componentWidth <= 52 &&
               componentHeight <= 52;
    }

    private static bool IsFlatDefaultAvatarComponent(double averageTexture, double averageChroma)
    {
        return averageTexture < 32 && averageChroma < 96;
    }

    private static int GetAvatarEdgePadding(int width, int height, double fillRatio)
    {
        var size = Math.Min(width, height);
        if (size <= 54)
        {
            return 0;
        }

        return fillRatio < 0.70 ? 1 : 0;
    }

    private static int GetAvatarInset(int width, int height, double fillRatio, AvatarShape shape)
    {
        var size = Math.Min(width, height);
        var inset = shape switch
        {
            AvatarShape.Circle => size >= 44 ? 4 : 3,
            AvatarShape.KakaoProfile => size >= 44 ? 2 : 1,
            AvatarShape.SquircleSoft => size >= 44 ? 2 : 1,
            AvatarShape.SquircleStrong => size >= 44 ? 2 : 1,
            _ => fillRatio < 0.70 ? 3 : 2
        };

        return Math.Max(0, inset);
    }

    private static int GetAvatarComponentSearchRadius(
        int width,
        int height,
        double fillRatio,
        bool isNormalizedAvatarSlot,
        bool isFlatDefaultAvatar,
        AvatarShape shape)
    {
        var size = Math.Min(width, height);
        if (isNormalizedAvatarSlot && isFlatDefaultAvatar)
        {
            return shape == AvatarShape.SquircleStrong ? 3 : 2;
        }

        if (isNormalizedAvatarSlot && shape == AvatarShape.SquircleSoft)
        {
            return 2;
        }

        if (size <= 54)
        {
            return fillRatio >= 0.58 ? 1 : 0;
        }

        return fillRatio >= 0.60 ? 2 : 1;
    }

    private static double GetPreserveStrength(double[] strengthMap, int width, int height, int x, int y)
    {
        var preservedNeighbors = 0;
        foreach (var (nx, ny) in EnumerateNeighborCoordinates(x, y, width, height, 1))
        {
            if (strengthMap[ny * width + nx] > 0)
            {
                preservedNeighbors++;
            }
        }

        return preservedNeighbors switch
        {
            8 => 1.0,
            7 => 0.90,
            6 => 0.68,
            5 => 0.42,
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

    private static double GetLocalTexture(byte[] pixels, int width, int height, int x, int y, int stride)
    {
        var index = y * stride + x * 4;
        var baseLuminance = GetLuminance(pixels[index + 2], pixels[index + 1], pixels[index]);
        var differenceSum = 0.0;
        var samples = 0;

        foreach (var (nx, ny) in EnumerateNeighborCoordinates(x, y, width, height, 1))
        {
            var neighborIndex = ny * stride + nx * 4;
            var neighborLuminance = GetLuminance(
                pixels[neighborIndex + 2],
                pixels[neighborIndex + 1],
                pixels[neighborIndex]);
            differenceSum += Math.Abs(baseLuminance - neighborLuminance);
            samples++;
        }

        return samples == 0 ? 0 : differenceSum / samples;
    }

    private static bool ShouldSkipPhotoPreservePixel(
        byte[] pixels,
        int width,
        int height,
        int x,
        int y,
        int stride,
        double preserveStrength)
    {
        if (preserveStrength >= 0.98)
        {
            return false;
        }

        var index = y * stride + x * 4;
        var blue = pixels[index];
        var green = pixels[index + 1];
        var red = pixels[index + 2];
        var luminance = GetLuminance(red, green, blue);
        var texture = GetLocalTexture(pixels, width, height, x, y, stride);
        var chroma = GetChroma(red, green, blue);

        return luminance >= 198 && texture < 10.5 && chroma < 34;
    }

    private static bool ShouldSkipSmallAvatarEdgePixel(
        byte[] pixels,
        int width,
        int height,
        int x,
        int y,
        int stride,
        double preserveStrength)
    {
        if (preserveStrength >= 0.68)
        {
            return false;
        }

        var index = y * stride + x * 4;
        var blue = pixels[index];
        var green = pixels[index + 1];
        var red = pixels[index + 2];
        var luminance = GetLuminance(red, green, blue);
        var texture = GetLocalTexture(pixels, width, height, x, y, stride);
        var chroma = GetChroma(red, green, blue);

        return luminance >= 172 && texture < 20 && chroma < 72;
    }

    private static bool ShouldSkipNormalizedAvatarFringePixel(
        byte[] pixels,
        int width,
        int height,
        int x,
        int y,
        int stride,
        double preserveStrength)
    {
        var index = y * stride + x * 4;
        var blue = pixels[index];
        var green = pixels[index + 1];
        var red = pixels[index + 2];
        var luminance = GetLuminance(red, green, blue);
        var texture = GetLocalTexture(pixels, width, height, x, y, stride);
        var chroma = GetChroma(red, green, blue);

        if (luminance >= 214 && texture < 18 && chroma < 66)
        {
            return true;
        }

        if (preserveStrength >= 0.92)
        {
            return false;
        }

        return luminance >= 188 && texture < 12.5 && chroma < 44;
    }

    private static bool HasNearbyComponentPixel(
        bool[] componentMask,
        int left,
        int top,
        int width,
        int height,
        int x,
        int y,
        int radius)
    {
        var localX = x - left;
        var localY = y - top;
        if (localX < 0 || localY < 0 || localX >= width || localY >= height)
        {
            return false;
        }

        if (componentMask[localY * width + localX])
        {
            return true;
        }

        foreach (var (nx, ny) in EnumerateNeighborCoordinates(localX, localY, width, height, radius))
        {
            if (componentMask[ny * width + nx])
            {
                return true;
            }
        }

        return false;
    }

    private static double GetComponentMaskCoverage(
        bool[] componentMask,
        int left,
        int top,
        int width,
        int height,
        int x,
        int y)
    {
        var localX = x - left;
        var localY = y - top;
        if (localX < 0 || localY < 0 || localX >= width || localY >= height)
        {
            return 0.0;
        }

        if (componentMask[localY * width + localX])
        {
            return 1.0;
        }

        var directNeighbors = 0;
        foreach (var (nx, ny) in EnumerateNeighborCoordinates(localX, localY, width, height, 1))
        {
            if (componentMask[ny * width + nx])
            {
                directNeighbors++;
            }
        }

        return directNeighbors switch
        {
            >= 5 => 0.60,
            >= 3 => 0.42,
            >= 1 => 0.24,
            _ => 0.0
        };
    }

    private static void ExpandFlatAvatarComponentMask(
        bool[] componentMask,
        int left,
        int top,
        int width,
        int height,
        byte[] pixels,
        int frameWidth,
        int frameHeight,
        int stride,
        double averageRed,
        double averageGreen,
        double averageBlue)
    {
        var expanded = new bool[componentMask.Length];
        Array.Copy(componentMask, expanded, componentMask.Length);

        for (var localY = 0; localY < height; localY++)
        {
            for (var localX = 0; localX < width; localX++)
            {
                var localIndex = localY * width + localX;
                if (componentMask[localIndex])
                {
                    continue;
                }

                if (!HasNearbyComponentPixel(componentMask, 0, 0, width, height, localX, localY, 1))
                {
                    continue;
                }

                var x = left + localX;
                var y = top + localY;
                if (x < 0 || y < 0 || x >= frameWidth || y >= frameHeight)
                {
                    continue;
                }

                var pixelIndex = y * stride + x * 4;
                var blue = pixels[pixelIndex];
                var green = pixels[pixelIndex + 1];
                var red = pixels[pixelIndex + 2];
                var alpha = pixels[pixelIndex + 3];
                if (alpha == 0)
                {
                    continue;
                }

                var luminance = GetLuminance(red, green, blue);
                if (luminance < 20 || luminance > 245)
                {
                    continue;
                }

                var redDiff = red - averageRed;
                var greenDiff = green - averageGreen;
                var blueDiff = blue - averageBlue;
                var colorDistance = Math.Sqrt((redDiff * redDiff) + (greenDiff * greenDiff) + (blueDiff * blueDiff));

                if (colorDistance <= 72)
                {
                    expanded[localIndex] = true;
                }
            }
        }

        Array.Copy(expanded, componentMask, componentMask.Length);
    }

    private static void ExpandSmallAvatarComponentMask(
        bool[] componentMask,
        int left,
        int top,
        int width,
        int height,
        byte[] pixels,
        int frameWidth,
        int frameHeight,
        int stride,
        double averageRed,
        double averageGreen,
        double averageBlue)
    {
        var expanded = new bool[componentMask.Length];
        Array.Copy(componentMask, expanded, componentMask.Length);

        for (var localY = 0; localY < height; localY++)
        {
            for (var localX = 0; localX < width; localX++)
            {
                var localIndex = localY * width + localX;
                if (componentMask[localIndex])
                {
                    continue;
                }

                if (!HasNearbyComponentPixel(componentMask, 0, 0, width, height, localX, localY, 1))
                {
                    continue;
                }

                var x = left + localX;
                var y = top + localY;
                if (x < 0 || y < 0 || x >= frameWidth || y >= frameHeight)
                {
                    continue;
                }

                var pixelIndex = y * stride + x * 4;
                var blue = pixels[pixelIndex];
                var green = pixels[pixelIndex + 1];
                var red = pixels[pixelIndex + 2];
                var alpha = pixels[pixelIndex + 3];
                if (alpha == 0)
                {
                    continue;
                }

                var luminance = GetLuminance(red, green, blue);
                if (luminance < 18 || luminance > 245)
                {
                    continue;
                }

                var redDiff = red - averageRed;
                var greenDiff = green - averageGreen;
                var blueDiff = blue - averageBlue;
                var colorDistance = Math.Sqrt((redDiff * redDiff) + (greenDiff * greenDiff) + (blueDiff * blueDiff));

                if (colorDistance <= 98)
                {
                    expanded[localIndex] = true;
                }
            }
        }

        Array.Copy(expanded, componentMask, componentMask.Length);
    }

    private static void ErodeSmallLogoAvatarMask(bool[] componentMask, int width, int height)
    {
        var eroded = new bool[componentMask.Length];

        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var index = y * width + x;
                if (!componentMask[index])
                {
                    continue;
                }

                var keep = true;
                foreach (var (nx, ny) in EnumerateNeighborCoordinates(x, y, width, height, 1))
                {
                    if (!componentMask[ny * width + nx])
                    {
                        keep = false;
                        break;
                    }
                }

                if (keep)
                {
                    eroded[index] = true;
                }
            }
        }

        Array.Copy(eroded, componentMask, componentMask.Length);
    }

    private static bool IsInsideAvatarShape(
        int x,
        int y,
        int left,
        int top,
        int width,
        int height,
        int radius,
        AvatarShape shape)
    {
        return shape switch
        {
            AvatarShape.Circle => IsInsideEllipse(x, y, left, top, width, height),
            AvatarShape.KakaoProfile => IsInsideKakaoProfilePoint(x + 0.5, y + 0.5, left, top, width, height),
            AvatarShape.SquircleSoft => IsInsideSquircle(x, y, left, top, width, height, 2.0),
            AvatarShape.SquircleStrong => IsInsideSquircle(x, y, left, top, width, height, 2.03),
            _ => IsInsideRoundedRect(x, y, left, top, width, height, radius)
        };
    }

    private static double GetAvatarShapeCoverage(
        int x,
        int y,
        int left,
        int top,
        int width,
        int height,
        int radius,
        AvatarShape shape)
    {
        var insideSamples = 0;
        var sampleOffsets = new[] { 0.125, 0.375, 0.625, 0.875 };
        foreach (var offsetY in sampleOffsets)
        {
            foreach (var offsetX in sampleOffsets)
            {
                if (IsInsideAvatarShapePoint(x + offsetX, y + offsetY, left, top, width, height, radius, shape))
                {
                    insideSamples++;
                }
            }
        }

        return insideSamples / 16.0;
    }

    private static bool IsInsideAvatarShapePoint(
        double x,
        double y,
        int left,
        int top,
        int width,
        int height,
        int radius,
        AvatarShape shape)
    {
        return shape switch
        {
            AvatarShape.Circle => IsInsideEllipsePoint(x, y, left, top, width, height),
            AvatarShape.KakaoProfile => IsInsideKakaoProfilePoint(x, y, left, top, width, height),
            AvatarShape.SquircleSoft => IsInsideSquirclePoint(x, y, left, top, width, height, 2.0),
            AvatarShape.SquircleStrong => IsInsideSquirclePoint(x, y, left, top, width, height, 2.03),
            _ => IsInsideRoundedRectPoint(x, y, left, top, width, height, radius)
        };
    }

    private static AvatarShape GetAvatarShape(
        int width,
        int height,
        double fillRatio,
        bool isNormalizedAvatarSlot,
        bool isFlatDefaultAvatar,
        double averageTexture,
        double averageChroma)
    {
        if (isFlatDefaultAvatar)
        {
            return AvatarShape.KakaoProfile;
        }

        if (isNormalizedAvatarSlot)
        {
            return AvatarShape.KakaoProfile;
        }

        var aspectRatio = width / (double)height;
        if (aspectRatio >= 0.88 && aspectRatio <= 1.12 && fillRatio < 0.78)
        {
            return AvatarShape.Circle;
        }

        return AvatarShape.RoundedRect;
    }

    private static bool IsInsideRoundedRect(int x, int y, int left, int top, int width, int height, int radius)
    {
        return IsInsideRoundedRectPoint(x + 0.5, y + 0.5, left, top, width, height, radius);
    }

    private static bool IsInsideRoundedRectPoint(double x, double y, int left, int top, int width, int height, int radius)
    {
        if (radius <= 0)
        {
            return true;
        }

        var right = left + width - 1;
        var bottom = top + height - 1;

        if (x >= left + radius && x <= right - radius)
        {
            return true;
        }

        if (y >= top + radius && y <= bottom - radius)
        {
            return true;
        }

        var cornerCenterX = x < left + radius ? left + radius : right - radius;
        var cornerCenterY = y < top + radius ? top + radius : bottom - radius;
        var dx = x - cornerCenterX;
        var dy = y - cornerCenterY;

        return (dx * dx) + (dy * dy) <= radius * radius;
    }

    private static bool IsInsideSquircle(int x, int y, int left, int top, int width, int height, double exponent)
    {
        return IsInsideSquirclePoint(x + 0.5, y + 0.5, left, top, width, height, exponent);
    }

    private static bool IsInsideSquirclePoint(double x, double y, int left, int top, int width, int height, double exponent)
    {
        var radiusX = width / 2.0;
        var radiusY = height / 2.0;
        if (radiusX <= 0 || radiusY <= 0)
        {
            return false;
        }

        var centerX = left + radiusX;
        var centerY = top + radiusY;
        var normalizedX = Math.Abs((x - centerX) / radiusX);
        var normalizedY = Math.Abs((y - centerY) / radiusY);

        return Math.Pow(normalizedX, exponent) + Math.Pow(normalizedY, exponent) <= 1.0;
    }

    private static bool IsInsideEllipse(int x, int y, int left, int top, int width, int height)
    {
        return IsInsideEllipsePoint(x + 0.5, y + 0.5, left, top, width, height);
    }

    private static bool IsInsideEllipsePoint(double x, double y, int left, int top, int width, int height)
    {
        var radiusX = width / 2.0;
        var radiusY = height / 2.0;
        if (radiusX <= 0 || radiusY <= 0)
        {
            return false;
        }

        var centerX = left + radiusX;
        var centerY = top + radiusY;
        var dx = (x - centerX) / radiusX;
        var dy = (y - centerY) / radiusY;
        return (dx * dx) + (dy * dy) <= 1.0;
    }

    private static bool IsInsideKakaoProfilePoint(double x, double y, int left, int top, int width, int height)
    {
        if (width <= 0 || height <= 0)
        {
            return false;
        }

        var localX = ((x - left) / width) * 40.0;
        var localY = ((y - top) / height) * 40.0;
        return KakaoProfileGeometry40.FillContains(new Point(localX, localY));
    }

    private enum AvatarShape
    {
        RoundedRect,
        Circle,
        KakaoProfile,
        SquircleSoft,
        SquircleStrong
    }

    private static Geometry CreateKakaoProfileGeometry40()
    {
        var geometry = Geometry.Parse("M0.5 20C0.5 12.5277 1.75183 7.70527 4.72855 4.72855C7.70527 1.75183 12.5277 0.5 20 0.5C27.4723 0.5 32.2947 1.75183 35.2715 4.72855C38.2482 7.70527 39.5 12.5277 39.5 20C39.5 27.4723 38.2482 32.2947 35.2715 35.2715C32.2947 38.2482 27.4723 39.5 20 39.5C12.5277 39.5 7.70527 38.2482 4.72855 35.2715C1.75183 32.2947 0.5 27.4723 0.5 20Z");
        geometry.Freeze();
        return geometry;
    }

    private static byte BlendChannel(byte baseValue, byte preservedValue, double preserveStrength)
    {
        return ClampToByte(baseValue + ((preservedValue - baseValue) * preserveStrength));
    }

    private static byte AdjustToneChannel(byte value, double brightnessOffset, double contrastScale, double gammaScale)
    {
        var normalized = value / 255.0;
        normalized = Math.Pow(normalized, gammaScale);
        normalized = ((normalized - 0.5) * contrastScale) + 0.5 + brightnessOffset;
        return ClampToByte(Math.Clamp(normalized, 0, 1) * 255.0);
    }

    private static void CopyPixel(byte[] pixels, int stride, int sourceX, int sourceY, int targetX, int targetY)
    {
        var sourceIndex = sourceY * stride + sourceX * 4;
        var targetIndex = targetY * stride + targetX * 4;
        pixels[targetIndex] = pixels[sourceIndex];
        pixels[targetIndex + 1] = pixels[sourceIndex + 1];
        pixels[targetIndex + 2] = pixels[sourceIndex + 2];
        pixels[targetIndex + 3] = pixels[sourceIndex + 3];
    }

    private delegate void PixelTransform(ref byte b, ref byte g, ref byte r, byte a);
}
