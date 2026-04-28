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
    private static readonly Geometry KakaoProfileSquircleGeometry = CreateKakaoProfileSquircleGeometry();

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
        var (photoPreserveStrengthMap, defaultAvatarMask, normalizedAvatarMask, thumbnailAvatarMask, mainAvatarMask, logoLikeMainAvatarMask) = BuildPhotoPreserveMaps(photoCandidateMask, originalPixels, converted.PixelWidth, converted.PixelHeight, stride);

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
                var blue = originalPixels[index];
                var green = originalPixels[index + 1];
                var red = originalPixels[index + 2];
                var preserveIndex = y * converted.PixelWidth + x;
                var photoPreserveStrength = Math.Clamp(
                    GetPreserveStrength(photoPreserveStrengthMap, converted.PixelWidth, converted.PixelHeight, x, y) * photoComponentStrength,
                    0.0,
                    1.0);
                var isDefaultAvatarPixel = defaultAvatarMask[preserveIndex];
                var isNormalizedAvatarPixel = normalizedAvatarMask[preserveIndex];
                var isThumbnailAvatarPixel = thumbnailAvatarMask[preserveIndex];
                var isMainAvatarPixel = mainAvatarMask[preserveIndex];
                var isLogoLikeMainAvatarPixel = logoLikeMainAvatarMask[preserveIndex];
                var activeAvatarMask = isDefaultAvatarPixel
                    ? defaultAvatarMask
                    : isNormalizedAvatarPixel
                        ? normalizedAvatarMask
                        : isThumbnailAvatarPixel
                            ? thumbnailAvatarMask
                            : isLogoLikeMainAvatarPixel
                                ? logoLikeMainAvatarMask
                                : isMainAvatarPixel
                                    ? mainAvatarMask
                                    : photoCandidateMask;
                var coveredAvatarNeighbors = 0;
                foreach (var (nx, ny) in EnumerateNeighborCoordinates(x, y, converted.PixelWidth, converted.PixelHeight, 1))
                {
                    if (activeAvatarMask[ny * converted.PixelWidth + nx])
                    {
                        coveredAvatarNeighbors++;
                    }
                }

                if (coveredAvatarNeighbors < 8)
                {
                    var edgeOpacity = Math.Pow(Math.Clamp(coveredAvatarNeighbors / 8.0, 0.0, 1.0), 1.8);
                    photoPreserveStrength *= edgeOpacity;
                }

                if (isDefaultAvatarPixel &&
                    ShouldSkipSmallAvatarEdgePixel(
                        originalPixels,
                        converted.PixelWidth,
                        converted.PixelHeight,
                        x,
                        y,
                        stride,
                        photoComponentStrength))
                {
                    GetBoundaryCorrectedColor(
                        originalPixels,
                        defaultAvatarMask,
                        converted.PixelWidth,
                        converted.PixelHeight,
                        x,
                        y,
                        stride,
                        0.92,
                        0.72,
                        true,
                        ref red,
                        ref green,
                        ref blue);
                }
                else if (isNormalizedAvatarPixel &&
                    ShouldSkipNormalizedAvatarFringePixel(
                        originalPixels,
                        converted.PixelWidth,
                        converted.PixelHeight,
                        x,
                        y,
                        stride,
                        photoComponentStrength))
                {
                    GetBoundaryCorrectedColor(
                        originalPixels,
                        normalizedAvatarMask,
                        converted.PixelWidth,
                        converted.PixelHeight,
                        x,
                        y,
                        stride,
                        0.90,
                        0.66,
                        true,
                        ref red,
                        ref green,
                        ref blue);
                }
                else if (isThumbnailAvatarPixel &&
                    ShouldSkipThumbnailAvatarFringePixel(
                        originalPixels,
                        thumbnailAvatarMask,
                        converted.PixelWidth,
                        converted.PixelHeight,
                        x,
                        y,
                        stride))
                {
                    GetBoundaryCorrectedColor(
                        originalPixels,
                        thumbnailAvatarMask,
                        converted.PixelWidth,
                        converted.PixelHeight,
                        x,
                        y,
                        stride,
                        0.88,
                        0.62,
                        true,
                        ref red,
                        ref green,
                        ref blue);
                }
                else if (isLogoLikeMainAvatarPixel &&
                    ShouldSkipLogoLikeMainAvatarFringePixel(
                        originalPixels,
                        logoLikeMainAvatarMask,
                        converted.PixelWidth,
                        converted.PixelHeight,
                        x,
                        y,
                        stride))
                {
                    GetBoundaryCorrectedColor(
                        originalPixels,
                        logoLikeMainAvatarMask,
                        converted.PixelWidth,
                        converted.PixelHeight,
                        x,
                        y,
                        stride,
                        0.84,
                        0.58,
                        true,
                        ref red,
                        ref green,
                        ref blue);
                }
                else if (isMainAvatarPixel &&
                    ShouldSkipMainAvatarFringePixel(
                        originalPixels,
                        mainAvatarMask,
                        converted.PixelWidth,
                        converted.PixelHeight,
                        x,
                        y,
                        stride,
                        photoComponentStrength))
                {
                    GetBoundaryCorrectedColor(
                        originalPixels,
                        mainAvatarMask,
                        converted.PixelWidth,
                        converted.PixelHeight,
                        x,
                        y,
                        stride,
                        0.86,
                        0.60,
                        true,
                        ref red,
                        ref green,
                        ref blue);
                }
                else if (ShouldSkipPhotoPreservePixel(
                    originalPixels,
                    converted.PixelWidth,
                    converted.PixelHeight,
                    x,
                    y,
                    stride,
                    photoComponentStrength))
                {
                    GetBoundaryCorrectedColor(
                        originalPixels,
                        photoCandidateMask,
                        converted.PixelWidth,
                        converted.PixelHeight,
                        x,
                        y,
                        stride,
                        0.82,
                        0.54,
                        true,
                        ref red,
                        ref green,
                        ref blue);
                }

                if (photoPreserveStrength >= 0.995)
                {
                    outputPixels[index] = blue;
                    outputPixels[index + 1] = green;
                    outputPixels[index + 2] = red;
                }
                else
                {
                    outputPixels[index] = BlendChannel(outputPixels[index], blue, photoPreserveStrength);
                    outputPixels[index + 1] = BlendChannel(outputPixels[index + 1], green, photoPreserveStrength);
                    outputPixels[index + 2] = BlendChannel(outputPixels[index + 2], red, photoPreserveStrength);
                }
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

        var (_, defaultAvatarMask, normalizedAvatarMask, thumbnailAvatarMask, mainAvatarMask, logoLikeMainAvatarMask) = BuildPhotoPreserveMaps(
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
        SavePng(
            CreateMaskBitmap(normalizedAvatarMask, converted.PixelWidth, converted.PixelHeight, converted.DpiX, converted.DpiY),
            Path.Combine(outputDirectory, "debug-normalized-avatar-mask.png"));
        SavePng(
            CreateMaskBitmap(thumbnailAvatarMask, converted.PixelWidth, converted.PixelHeight, converted.DpiX, converted.DpiY),
            Path.Combine(outputDirectory, "debug-thumbnail-avatar-mask.png"));
        SavePng(
            CreateMaskBitmap(mainAvatarMask, converted.PixelWidth, converted.PixelHeight, converted.DpiX, converted.DpiY),
            Path.Combine(outputDirectory, "debug-main-avatar-mask.png"));
        SavePng(
            CreateMaskBitmap(logoLikeMainAvatarMask, converted.PixelWidth, converted.PixelHeight, converted.DpiX, converted.DpiY),
            Path.Combine(outputDirectory, "debug-logo-main-avatar-mask.png"));
        File.WriteAllText(
            Path.Combine(outputDirectory, "debug-photo-components.txt"),
            GetPhotoComponentDebugReport(photoCandidateMask, pixels, converted.PixelWidth, converted.PixelHeight, stride));
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

                foreach (var (nx, ny) in EnumerateNeighborCoordinates(x, y, width, height, 2))
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

    private static (double[] preserveStrengthMap, bool[] defaultAvatarMask, bool[] normalizedAvatarMask, bool[] thumbnailAvatarMask, bool[] mainAvatarMask, bool[] logoLikeMainAvatarMask) BuildPhotoPreserveMaps(bool[] candidateMask, byte[] pixels, int width, int height, int stride)
    {
        var preserveStrengthMap = new double[candidateMask.Length];
        var defaultAvatarMask = new bool[candidateMask.Length];
        var normalizedAvatarMask = new bool[candidateMask.Length];
        var thumbnailAvatarMask = new bool[candidateMask.Length];
        var mainAvatarMask = new bool[candidateMask.Length];
        var logoLikeMainAvatarMask = new bool[candidateMask.Length];
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

                foreach (var (nx, ny) in EnumerateNeighborCoordinates(x, y, width, height, 2))
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
                    minY,
                    maxY,
                    componentWidth,
                    componentHeight,
                    width,
                    height,
                    fillRatio,
                    textureSum / component.Count,
                    chromaSum / component.Count))
            {
                continue;
            }

            var averageTexture = textureSum / component.Count;
            var averageChroma = chromaSum / component.Count;
            var slotKind = GetAvatarSlotKind(minX, maxX, minY, maxY, componentWidth, componentHeight, width, height, fillRatio);
            if (slotKind == AvatarSlotKind.LeftList &&
                HasPeerAvatarCandidateOnSameRow(candidateMask, width, height, minX, maxX, minY, maxY, componentWidth, componentHeight))
            {
                slotKind = AvatarSlotKind.ThumbnailRow;
            }

            if (slotKind == AvatarSlotKind.LeftList)
            {
                var centerY = (minY + maxY) / 2.0;
                if (!HasAdjacentAvatarRowText(pixels, width, height, stride, centerY))
                {
                    continue;
                }
            }

            var isThumbnailRowSlot = slotKind == AvatarSlotKind.ThumbnailRow;
            var isLayoutAvatarSlot = slotKind != AvatarSlotKind.None;
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
                isFlatDefaultAvatar,
                slotKind);
            var maskWidth = maskMaxX - maskMinX + 1;
            var maskHeight = maskMaxY - maskMinY + 1;
            var isNormalizedAvatarSlot = IsNormalizedAvatarSlot(maskWidth, maskHeight, componentWidth, componentHeight);
            var isMainAvatarSlot = IsMainAvatarSlot(maskWidth, maskHeight, componentWidth, componentHeight);
            var isLogoLikeMainAvatar =
                isMainAvatarSlot &&
                !isFlatDefaultAvatar &&
                averageTexture < 20 &&
                averageChroma >= 120;
            if (isFlatDefaultAvatar)
            {
                componentStrength = 1.0;
            }
            else if (isThumbnailRowSlot)
            {
                componentStrength = 1.0;
            }
            else if (isMainAvatarSlot)
            {
                componentStrength = 1.0;
            }
            var shape = GetAvatarShape(
                maskWidth,
                maskHeight,
                fillRatio,
                isNormalizedAvatarSlot,
                isThumbnailRowSlot,
                isMainAvatarSlot,
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
                innerLeft = maskMinX + 3;
                innerTop = maskMinY + 3;
                innerWidth = Math.Max(1, maskWidth - 6);
                innerHeight = Math.Max(1, maskHeight - 6);
            }
            else if (isThumbnailRowSlot)
            {
                innerLeft = maskMinX + 1;
                innerTop = maskMinY + 1;
                innerWidth = Math.Max(1, maskWidth - 2);
                innerHeight = Math.Max(1, maskHeight - 2);
            }
            else if (isMainAvatarSlot)
            {
                innerLeft = maskMinX + 2;
                innerTop = maskMinY + 2;
                innerWidth = Math.Max(1, maskWidth - 4);
                innerHeight = Math.Max(1, maskHeight - 4);
                if (isLogoLikeMainAvatar)
                {
                    innerLeft += 1;
                    innerTop += 1;
                    innerWidth = Math.Max(1, innerWidth - 2);
                    innerHeight = Math.Max(1, innerHeight - 2);
                }
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
            else if (isThumbnailRowSlot)
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
            }
            else if (isMainAvatarSlot)
            {
                ExpandMainAvatarComponentMask(
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

                if (isLogoLikeMainAvatar)
                {
                    // Main logo avatars were preserving their anti-aliased outer ring too literally.
                    // Tighten the component mask before coverage is applied so the preserved area
                    // follows the actual logo body rather than the bright 1px fringe.
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
            var requiresComponentProximity = !isFlatDefaultAvatar && !isLayoutAvatarSlot;

            for (var y = maskMinY; y <= maskMaxY; y++)
            {
                for (var x = maskMinX; x <= maskMaxX; x++)
                {
                    var shapeCoverage = isFlatDefaultAvatar
                        ? GetComponentMaskCoverage(componentMask, maskMinX, maskMinY, maskWidth, maskHeight, x, y)
                        : GetAvatarShapeCoverage(x, y, innerLeft, innerTop, innerWidth, innerHeight, cornerRadius, shape);
                    if (isNormalizedAvatarSlot && !isFlatDefaultAvatar && !isLayoutAvatarSlot)
                    {
                        var componentCoverage = GetComponentMaskCoverage(
                            componentMask,
                            maskMinX,
                            maskMinY,
                            maskWidth,
                            maskHeight,
                            x,
                            y);
                        shapeCoverage *= componentCoverage;
                    }
                    else if (isThumbnailRowSlot && !isLayoutAvatarSlot)
                    {
                        var componentCoverage = GetComponentMaskCoverage(
                            componentMask,
                            maskMinX,
                            maskMinY,
                            maskWidth,
                            maskHeight,
                            x,
                            y);
                        if (componentCoverage <= 0)
                        {
                            continue;
                        }

                        shapeCoverage *= Math.Max(0.72, componentCoverage);
                    }
                    else if (isMainAvatarSlot && !isFlatDefaultAvatar && !isLayoutAvatarSlot)
                    {
                        if (isLogoLikeMainAvatar)
                        {
                            var componentCoverage = GetComponentMaskCoverage(
                                componentMask,
                                maskMinX,
                                maskMinY,
                                maskWidth,
                                maskHeight,
                                x,
                                y);
                            if (componentCoverage < 0.82)
                            {
                                continue;
                            }

                            shapeCoverage = Math.Min(shapeCoverage, componentCoverage);
                        }
                    }
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
                    else if (isThumbnailRowSlot)
                    {
                        thumbnailAvatarMask[preserveIndex] = true;
                    }
                    else if (isMainAvatarSlot)
                    {
                        mainAvatarMask[preserveIndex] = true;
                        if (isLogoLikeMainAvatar)
                        {
                            logoLikeMainAvatarMask[preserveIndex] = true;
                        }
                    }
                }
            }
        }

        AddMonochromeLeftListAvatarSlots(
            pixels,
            width,
            height,
            stride,
            preserveStrengthMap,
            mainAvatarMask);

        var tightenedLogoLikeMainAvatarMask = (bool[])logoLikeMainAvatarMask.Clone();
        ErodeSmallLogoAvatarMask(tightenedLogoLikeMainAvatarMask, width, height);
        for (var i = 0; i < logoLikeMainAvatarMask.Length; i++)
        {
            if (!logoLikeMainAvatarMask[i] || tightenedLogoLikeMainAvatarMask[i])
            {
                continue;
            }

            logoLikeMainAvatarMask[i] = false;
            mainAvatarMask[i] = false;
            preserveStrengthMap[i] = 0;
        }

        return (preserveStrengthMap, defaultAvatarMask, normalizedAvatarMask, thumbnailAvatarMask, mainAvatarMask, logoLikeMainAvatarMask);
    }

    private static void AddMonochromeLeftListAvatarSlots(
        byte[] pixels,
        int width,
        int height,
        int stride,
        double[] preserveStrengthMap,
        bool[] mainAvatarMask)
    {
        var minScanX = Math.Max(0, (int)Math.Round(width * 0.15));
        var maxScanX = Math.Min(width - 1, (int)Math.Round(width * 0.29));
        var minScanY = Math.Max(120, (int)Math.Round(height * 0.15));
        var candidateMask = new bool[width * height];

        for (var y = minScanY; y < height; y++)
        {
            for (var x = minScanX; x <= maxScanX; x++)
            {
                if (IsMonochromeAvatarContentPixel(pixels, width, height, x, y, stride))
                {
                    candidateMask[y * width + x] = true;
                }
            }
        }

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

                foreach (var (nx, ny) in EnumerateNeighborCoordinates(x, y, width, height, 1))
                {
                    if (nx < minScanX || nx > maxScanX || ny < minScanY)
                    {
                        continue;
                    }

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
            var centerX = (minX + maxX) / 2.0;
            var centerY = (minY + maxY) / 2.0;
            if (!ShouldInferLeftListAvatarSlot(component.Count, componentWidth, componentHeight, centerX, centerY, width, height) ||
                !HasAdjacentAvatarRowText(pixels, width, height, stride, centerY))
            {
                continue;
            }

            var centerIndex = Math.Clamp((int)Math.Round(centerY), 0, height - 1) * width + Math.Clamp((int)Math.Round(centerX), 0, width - 1);
            if (preserveStrengthMap[centerIndex] >= 0.75)
            {
                continue;
            }

            var slotSize = centerX >= width * 0.222 ? 55 : 50;
            var slotCenterX = slotSize >= 55 ? 134.0 : 131.5;
            if (width != 594)
            {
                slotCenterX = centerX;
            }

            var left = Math.Clamp((int)Math.Round(slotCenterX - ((slotSize - 1) / 2.0)), 0, width - slotSize);
            var top = Math.Clamp((int)Math.Round(centerY - ((slotSize - 1) / 2.0)), 0, height - slotSize);
            for (var y = top; y < top + slotSize; y++)
            {
                for (var x = left; x < left + slotSize; x++)
                {
                    var coverage = GetAvatarShapeCoverage(x, y, left, top, slotSize, slotSize, 0, AvatarShape.KakaoProfile);
                    if (coverage <= 0)
                    {
                        continue;
                    }

                    var preserveIndex = y * width + x;
                    preserveStrengthMap[preserveIndex] = Math.Max(preserveStrengthMap[preserveIndex], coverage);
                    mainAvatarMask[preserveIndex] = true;
                }
            }
        }
    }

    private static bool HasPeerAvatarCandidateOnSameRow(
        bool[] candidateMask,
        int frameWidth,
        int frameHeight,
        int minX,
        int maxX,
        int minY,
        int maxY,
        int componentWidth,
        int componentHeight)
    {
        if (componentWidth < 36 || componentWidth > 58 || componentHeight < 36 || componentHeight > 58)
        {
            return false;
        }

        var centerX = (minX + maxX) / 2.0;
        var centerY = (minY + maxY) / 2.0;
        var yRatio = centerY / frameHeight;
        if (yRatio < 0.12 || yRatio > 0.42)
        {
            return false;
        }

        foreach (var offset in new[] { -75, 75, -150, 150 })
        {
            if (HasDenseAvatarCandidateBox(candidateMask, frameWidth, frameHeight, centerX + offset, centerY, 50))
            {
                return true;
            }
        }

        return false;
    }

    private static bool HasDenseAvatarCandidateBox(bool[] candidateMask, int width, int height, double centerX, double centerY, int boxSize)
    {
        var half = boxSize / 2;
        var left = Math.Max(0, (int)Math.Round(centerX) - half);
        var right = Math.Min(width - 1, (int)Math.Round(centerX) + half);
        var top = Math.Max(0, (int)Math.Round(centerY) - half);
        var bottom = Math.Min(height - 1, (int)Math.Round(centerY) + half);
        var count = 0;
        var minX = width;
        var maxX = -1;
        var minY = height;
        var maxY = -1;

        for (var y = top; y <= bottom; y++)
        {
            for (var x = left; x <= right; x++)
            {
                if (!candidateMask[y * width + x])
                {
                    continue;
                }

                count++;
                if (x < minX) minX = x;
                if (x > maxX) maxX = x;
                if (y < minY) minY = y;
                if (y > maxY) maxY = y;
            }
        }

        var area = Math.Max(1, (right - left + 1) * (bottom - top + 1));
        if (count / (double)area < 0.26)
        {
            return false;
        }

        return maxX - minX + 1 >= 28 && maxY - minY + 1 >= 28;
    }
    private static bool HasAdjacentAvatarRowText(byte[] pixels, int width, int height, int stride, double centerY)
    {
        var minY = Math.Max(0, (int)Math.Round(centerY) - 22);
        var maxY = Math.Min(height - 1, (int)Math.Round(centerY) + 22);
        var minX = Math.Min(width - 1, 174);
        var maxX = Math.Min(width - 1, 430);
        var textLikePixels = 0;

        for (var y = minY; y <= maxY; y++)
        {
            for (var x = minX; x <= maxX; x++)
            {
                var index = y * stride + x * 4;
                var blue = pixels[index];
                var green = pixels[index + 1];
                var red = pixels[index + 2];
                var luminance = GetLuminance(red, green, blue);
                var chroma = GetChroma(red, green, blue);
                if (luminance <= 175 || chroma >= 42)
                {
                    textLikePixels++;
                    if (textLikePixels >= 18)
                    {
                        return true;
                    }
                }
            }
        }

        return false;
    }
    private static bool IsMonochromeAvatarContentPixel(byte[] pixels, int width, int height, int x, int y, int stride)
    {
        var index = y * stride + x * 4;
        var blue = pixels[index];
        var green = pixels[index + 1];
        var red = pixels[index + 2];
        var luminance = GetLuminance(red, green, blue);
        var chroma = GetChroma(red, green, blue);
        if (chroma >= 24 || luminance <= 226)
        {
            return true;
        }

        return GetLocalTexture(pixels, width, height, x, y, stride) >= 14;
    }

    private static bool ShouldInferLeftListAvatarSlot(
        int area,
        int componentWidth,
        int componentHeight,
        double centerX,
        double centerY,
        int frameWidth,
        int frameHeight)
    {
        if (area < 18 || componentWidth < 4 || componentHeight < 4)
        {
            return false;
        }

        if (componentWidth > 62 || componentHeight > 62)
        {
            return false;
        }

        var xRatio = centerX / frameWidth;
        var yRatio = centerY / frameHeight;
        if (xRatio < 0.16 || xRatio > 0.27 || yRatio < 0.16 || yRatio > 0.94)
        {
            return false;
        }

        var expectedCenterX = centerX >= frameWidth * 0.222 ? 134.0 : 131.5;
        if (frameWidth != 594)
        {
            expectedCenterX = frameWidth * 0.225;
        }

        if (Math.Abs(centerX - expectedCenterX) > 9.5 && componentWidth < 38 && componentHeight < 38)
        {
            return false;
        }

        return componentWidth >= 10 || componentHeight >= 10 || area >= 48;
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

    private static string GetPhotoComponentDebugReport(bool[] candidateMask, byte[] pixels, int width, int height, int stride)
    {
        var lines = new List<string>();
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
                textureSum += GetLocalTexture(pixels, width, height, x, y, stride);
                chromaSum += GetChroma(pixels[pixelIndex + 2], pixels[pixelIndex + 1], pixels[pixelIndex]);

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
            var averageTexture = textureSum / component.Count;
            var averageChroma = chromaSum / component.Count;
            var keep = ShouldKeepPhotoComponent(
                component.Count,
                minX,
                maxX,
                minY,
                maxY,
                componentWidth,
                componentHeight,
                width,
                height,
                fillRatio,
                averageTexture,
                averageChroma);

            if (!keep)
            {
                continue;
            }

            var slotKind = GetAvatarSlotKind(minX, maxX, minY, maxY, componentWidth, componentHeight, width, height, fillRatio);
            var isFlatDefaultAvatar = IsFlatDefaultAvatarComponent(averageTexture, averageChroma);
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
                isFlatDefaultAvatar,
                slotKind);
            var maskWidth = maskMaxX - maskMinX + 1;
            var maskHeight = maskMaxY - maskMinY + 1;
            var isNormalizedAvatarSlot = IsNormalizedAvatarSlot(maskWidth, maskHeight, componentWidth, componentHeight);
            var isMainAvatarSlot = IsMainAvatarSlot(maskWidth, maskHeight, componentWidth, componentHeight);
            var centerX = (minX + maxX) / 2.0;
            var centerY = (minY + maxY) / 2.0;

            lines.Add(
                $"area={component.Count}, bbox=({minX},{minY})-({maxX},{maxY}), size={componentWidth}x{componentHeight}, mask={maskWidth}x{maskHeight}, fill={fillRatio:F3}, tex={averageTexture:F2}, chroma={averageChroma:F2}, center=({centerX:F1},{centerY:F1}), slot={slotKind}, flat={isFlatDefaultAvatar}, normalized={isNormalizedAvatarSlot}, main={isMainAvatarSlot}");
        }

        return string.Join(Environment.NewLine, lines);
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
        int minY,
        int maxY,
        int width,
        int height,
        int frameWidth,
        int frameHeight,
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

        var slotKind = GetAvatarSlotKind(minX, maxX, minY, maxY, width, height, frameWidth, frameHeight, fillRatio);
        if (slotKind == AvatarSlotKind.None)
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

    private enum AvatarSlotKind
    {
        None,
        LeftList,
        ThumbnailRow
    }

    private static AvatarSlotKind GetAvatarSlotKind(
        int minX,
        int maxX,
        int minY,
        int maxY,
        int width,
        int height,
        int frameWidth,
        int frameHeight,
        double fillRatio)
    {
        var centerX = (minX + maxX) / 2.0;
        var centerY = (minY + maxY) / 2.0;
        var xRatio = centerX / frameWidth;
        var yRatio = centerY / frameHeight;
        var aspectRatio = width / (double)height;
        var isAvatarLikeShape =
            width >= 18 &&
            width <= 76 &&
            height >= 18 &&
            height <= 76 &&
            aspectRatio >= 0.74 &&
            aspectRatio <= 1.30 &&
            fillRatio >= 0.40;

        if (!isAvatarLikeShape)
        {
            return AvatarSlotKind.None;
        }

        var isLeftListAvatar =
            xRatio >= 0.13 &&
            xRatio <= 0.29 &&
            width >= 40 &&
            height >= 40;
        if (isLeftListAvatar)
        {
            return AvatarSlotKind.LeftList;
        }

        var isThumbnailRowAvatar =
            width <= 56 &&
            height <= 56 &&
            xRatio >= 0.12 &&
            xRatio <= 0.62 &&
            yRatio >= 0.12 &&
            yRatio <= 0.54;

        return isThumbnailRowAvatar ? AvatarSlotKind.ThumbnailRow : AvatarSlotKind.None;
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
        bool isFlatDefaultAvatar,
        AvatarSlotKind slotKind)
    {
        var aspectRatio = componentWidth / (double)componentHeight;
        var shouldNormalizeToSlot =
            componentWidth >= 18 &&
            componentWidth <= 52 &&
            componentHeight >= 18 &&
            componentHeight <= 52 &&
            aspectRatio >= 0.74 &&
            aspectRatio <= 1.30;

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

        if (slotKind == AvatarSlotKind.ThumbnailRow)
        {
            var thumbnailSlotSize = Math.Max(24, Math.Min(54, Math.Max(componentWidth, componentHeight) + 2));
            var centerX = (minX + maxX) / 2.0;
            var centerY = (minY + maxY) / 2.0;
            var fittedMinX = Math.Max(0, (int)Math.Round(centerX - ((thumbnailSlotSize - 1) / 2.0)));
            var fittedMinY = Math.Max(0, (int)Math.Round(centerY - ((thumbnailSlotSize - 1) / 2.0)));
            var fittedMaxX = Math.Min(frameWidth - 1, fittedMinX + thumbnailSlotSize - 1);
            var fittedMaxY = Math.Min(frameHeight - 1, fittedMinY + thumbnailSlotSize - 1);
            return (fittedMinX, fittedMaxX, fittedMinY, fittedMaxY);
        }

        if (shouldNormalizeToSlot)
        {
            var avatarSlotSize = slotKind == AvatarSlotKind.LeftList && (componentWidth >= 48 || componentHeight >= 48)
                ? Math.Max(componentWidth, componentHeight)
                : 40;
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
        return maskWidth >= 36 &&
               maskWidth <= 44 &&
               maskHeight >= 36 &&
               maskHeight <= 44 &&
               componentWidth <= 52 &&
               componentHeight <= 52;
    }

    private static bool IsMainAvatarSlot(int maskWidth, int maskHeight, int componentWidth, int componentHeight)
    {
        return maskWidth >= 48 &&
               maskWidth <= 72 &&
               maskHeight >= 48 &&
               maskHeight <= 72 &&
               componentWidth >= 48 &&
               componentWidth <= 72 &&
               componentHeight >= 48 &&
               componentHeight <= 72;
    }

    private static bool IsFlatDefaultAvatarComponent(double averageTexture, double averageChroma)
    {
        return averageTexture < 8.5 && averageChroma >= 48 && averageChroma < 110;
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

    private static bool ShouldSkipThumbnailAvatarFringePixel(
        byte[] pixels,
        bool[] thumbnailAvatarMask,
        int width,
        int height,
        int x,
        int y,
        int stride)
    {
        var coveredNeighbors = 0;
        foreach (var (nx, ny) in EnumerateNeighborCoordinates(x, y, width, height, 1))
        {
            if (thumbnailAvatarMask[ny * width + nx])
            {
                coveredNeighbors++;
            }
        }

        if (coveredNeighbors >= 8)
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

        return luminance >= 180 && texture < 22 && chroma < 88;
    }

    private static bool ShouldSkipMainAvatarFringePixel(
        byte[] pixels,
        bool[] mainAvatarMask,
        int width,
        int height,
        int x,
        int y,
        int stride,
        double preserveStrength)
    {
        var coveredNeighbors = 0;
        foreach (var (nx, ny) in EnumerateNeighborCoordinates(x, y, width, height, 1))
        {
            if (mainAvatarMask[ny * width + nx])
            {
                coveredNeighbors++;
            }
        }

        var isBoundaryPixel = coveredNeighbors < 8;
        if (!isBoundaryPixel)
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

        if (luminance >= 192 && texture < 24 && chroma < 124)
        {
            return true;
        }

        if (preserveStrength >= 0.99)
        {
            return false;
        }

        return luminance >= 162 && texture < 19 && chroma < 96;
    }

    private static bool ShouldSkipLogoLikeMainAvatarFringePixel(
        byte[] pixels,
        bool[] logoLikeMainAvatarMask,
        int width,
        int height,
        int x,
        int y,
        int stride)
    {
        var coveredNeighbors = 0;
        foreach (var (nx, ny) in EnumerateNeighborCoordinates(x, y, width, height, 1))
        {
            if (logoLikeMainAvatarMask[ny * width + nx])
            {
                coveredNeighbors++;
            }
        }

        if (coveredNeighbors >= 8)
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

        return luminance >= 142 && texture < 34 && chroma < 176;
    }

    private static void GetBoundaryCorrectedColor(
        byte[] pixels,
        bool[] mask,
        int width,
        int height,
        int x,
        int y,
        int stride,
        double strongBlend,
        double softBlend,
        bool replaceBrightBoundary,
        ref byte red,
        ref byte green,
        ref byte blue)
    {
        var coveredNeighbors = 0;
        foreach (var (nx, ny) in EnumerateNeighborCoordinates(x, y, width, height, 1))
        {
            if (mask[ny * width + nx])
            {
                coveredNeighbors++;
            }
        }

        if (coveredNeighbors >= 8)
        {
            return;
        }

        var sampleCount = 0;
        var redSum = 0.0;
        var greenSum = 0.0;
        var blueSum = 0.0;

        foreach (var (nx, ny) in EnumerateNeighborCoordinates(x, y, width, height, 3))
        {
            if (!mask[ny * width + nx])
            {
                continue;
            }

            var neighborCovered = 0;
            foreach (var (nnx, nny) in EnumerateNeighborCoordinates(nx, ny, width, height, 1))
            {
                if (mask[nny * width + nnx])
                {
                    neighborCovered++;
                }
            }

            if (neighborCovered < 8)
            {
                continue;
            }

            var neighborIndex = ny * stride + nx * 4;
            blueSum += pixels[neighborIndex];
            greenSum += pixels[neighborIndex + 1];
            redSum += pixels[neighborIndex + 2];
            sampleCount++;
        }

        if (sampleCount == 0)
        {
            foreach (var (nx, ny) in EnumerateNeighborCoordinates(x, y, width, height, 2))
            {
                if (!mask[ny * width + nx])
                {
                    continue;
                }

                var neighborCovered = 0;
                foreach (var (nnx, nny) in EnumerateNeighborCoordinates(nx, ny, width, height, 1))
                {
                    if (mask[nny * width + nnx])
                    {
                        neighborCovered++;
                    }
                }

                if (neighborCovered < 7)
                {
                    continue;
                }

                var neighborIndex = ny * stride + nx * 4;
                blueSum += pixels[neighborIndex];
                greenSum += pixels[neighborIndex + 1];
                redSum += pixels[neighborIndex + 2];
                sampleCount++;
            }
        }

        if (sampleCount == 0)
        {
            return;
        }

        var averageRed = ClampToByte(redSum / sampleCount);
        var averageGreen = ClampToByte(greenSum / sampleCount);
        var averageBlue = ClampToByte(blueSum / sampleCount);
        var currentLuminance = GetLuminance(red, green, blue);
        var averageLuminance = GetLuminance(averageRed, averageGreen, averageBlue);
        var currentChroma = GetChroma(red, green, blue);
        var averageChroma = GetChroma(averageRed, averageGreen, averageBlue);
        var boundarySeverity = 1.0 - (coveredNeighbors / 8.0);

        if (replaceBrightBoundary && currentLuminance >= averageLuminance + 1)
        {
            var matteAlphaSum = 0.0;
            var matteAlphaCount = 0;
            foreach (var (current, average) in new[]
                     {
                         ((double)red, (double)averageRed),
                         ((double)green, (double)averageGreen),
                         ((double)blue, (double)averageBlue)
                     })
            {
                if (average >= 250 || current < average)
                {
                    continue;
                }

                var alpha = (255.0 - current) / (255.0 - average);
                if (double.IsNaN(alpha) || double.IsInfinity(alpha))
                {
                    continue;
                }

                matteAlphaSum += Math.Clamp(alpha, 0.0, 1.0);
                matteAlphaCount++;
            }

            var matteAlpha = matteAlphaCount > 0
                ? Math.Clamp(matteAlphaSum / matteAlphaCount, 0.0, 1.0)
                : 1.0;
            matteAlpha *= matteAlpha;
            var targetRed = ClampToByte(averageRed * matteAlpha);
            var targetGreen = ClampToByte(averageGreen * matteAlpha);
            var targetBlue = ClampToByte(averageBlue * matteAlpha);
            red = targetRed;
            green = targetGreen;
            blue = targetBlue;
            return;
        }

        var luminanceGap = Math.Max(0.0, currentLuminance - averageLuminance);
        var chromaGap = Math.Max(0.0, averageChroma - currentChroma);
        var blend = softBlend + boundarySeverity * (strongBlend - softBlend);
        if (luminanceGap >= 6 || chromaGap >= 10)
        {
            blend = Math.Max(blend, strongBlend);
        }
        else
        {
            blend = Math.Max(blend, softBlend + 0.16);
        }

        blend = Math.Clamp(blend, 0.0, 1.0);
        red = BlendChannel(red, averageRed, blend);
        green = BlendChannel(green, averageGreen, blend);
        blue = BlendChannel(blue, averageBlue, blend);
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

                if (!HasNearbyComponentPixel(componentMask, 0, 0, width, height, localX, localY, 2))
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

    private static void ExpandMainAvatarComponentMask(
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
                if (luminance < 20 || luminance > 250)
                {
                    continue;
                }

                var chroma = GetChroma(red, green, blue);
                var redDiff = red - averageRed;
                var greenDiff = green - averageGreen;
                var blueDiff = blue - averageBlue;
                var colorDistance = Math.Sqrt((redDiff * redDiff) + (greenDiff * greenDiff) + (blueDiff * blueDiff));
                var isBrightNeutralDetail = luminance >= 118 && chroma < 110;

                if (colorDistance <= 108 || isBrightNeutralDetail)
                {
                    expanded[localIndex] = true;
                }
            }
        }

        Array.Copy(expanded, componentMask, componentMask.Length);
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
        bool isThumbnailRowSlot,
        bool isMainAvatarSlot,
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

        if (isThumbnailRowSlot)
        {
            return AvatarShape.Circle;
        }

        if (isMainAvatarSlot)
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

        var localX = ((x - left) / width) * 384.0;
        var localY = ((y - top) / height) * 384.0;
        return KakaoProfileSquircleGeometry.FillContains(new System.Windows.Point(localX, localY));
    }

    private enum AvatarShape
    {
        RoundedRect,
        Circle,
        KakaoProfile,
        SquircleSoft,
        SquircleStrong
    }

    private static Geometry CreateKakaoProfileSquircleGeometry()
    {
        // KakaoTalk PC ships this exact profile shape at:
        // skin/default/image/profileShapeSquircleSVGs/Combined/profileShpeSquircleOne.svg
        var geometry = Geometry.Parse("M384 192C384 333.333 333.333 384 192 384C50.667 384 0 333.333 0 192C0 56 50.667 0 192 0C333.333 0 384 50.667 384 192Z");
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
