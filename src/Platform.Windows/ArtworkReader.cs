using System.Runtime.InteropServices;
using System.Security.Cryptography;
using EasyMouse.Core.Abstractions;
using EasyMouse.Core.Protocol;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace EasyMouse.Platform.Windows;

/// <summary>
/// Turns an SMTC thumbnail into a small JPEG for the phone: at most 256x256, normally well under 64 KB.
/// Returns null for anything that cannot be read or does not fit; it never throws for COM, IO or decoding problems.
/// </summary>
internal static class ArtworkReader
{
    public const int MaxSide = 256;
    public const int MaxBytes = 64 * 1024;
    /// <summary>The thumbnail comes from whatever app or web page owns the media session: refuse sources that are huge on disk or in pixels before decoding them.</summary>
    public const ulong MaxSourceBytes = 8 * 1024 * 1024;
    public const ulong MaxSourcePixels = 64UL * 1024 * 1024;

    internal static bool Accepts(ulong sourceBytes, uint width, uint height)
        => width != 0 && height != 0 && sourceBytes is > 0 and <= MaxSourceBytes && (ulong)width * height <= MaxSourcePixels;

    private static readonly double[] Qualities = [0.8, 0.6, 0.4];

    public static async Task<Artwork?> ReadAsync(IRandomAccessStreamReference? thumbnail)
    {
        if (thumbnail is null) return null;
        try
        {
            using var source = await thumbnail.OpenReadAsync();
            var decoder = await BitmapDecoder.CreateAsync(source);
            if (!Accepts(source.Size, decoder.PixelWidth, decoder.PixelHeight)) return null;

            var side = MaxSide;
            for (int attempt = 0; attempt < 2; attempt++)
            {
                var jpeg = await EncodeAsync(decoder, side);
                if (jpeg is not null) return new Artwork(IdOf(jpeg), ArtworkType.Jpeg, jpeg);
                side = 160; // still too big at the lowest quality: try a smaller picture once
            }
            return null;
        }
        catch (Exception ex) when (ex is COMException or IOException or InvalidOperationException or ArgumentException or UnauthorizedAccessException or NotSupportedException)
        {
            return null;
        }
    }

    private static async Task<byte[]?> EncodeAsync(BitmapDecoder decoder, int maxSide)
    {
        var scale = Math.Min(1.0, (double)maxSide / Math.Max(decoder.PixelWidth, decoder.PixelHeight));
        var width = Math.Max(1u, (uint)Math.Round(decoder.PixelWidth * scale));
        var height = Math.Max(1u, (uint)Math.Round(decoder.PixelHeight * scale));
        var transform = new BitmapTransform { ScaledWidth = width, ScaledHeight = height, InterpolationMode = BitmapInterpolationMode.Fant };
        var pixels = await decoder.GetPixelDataAsync(
            BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore, transform,
            ExifOrientationMode.RespectExifOrientation, ColorManagementMode.DoNotColorManage);
        var data = pixels.DetachPixelData();

        foreach (var quality in Qualities)
        {
            using var output = new InMemoryRandomAccessStream();
            var options = new BitmapPropertySet { ["ImageQuality"] = new BitmapTypedValue((float)quality, global::Windows.Foundation.PropertyType.Single) };
            var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.JpegEncoderId, output, options);
            encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore, width, height, 96, 96, data);
            await encoder.FlushAsync();
            if (output.Size is 0 or > MaxBytes) continue;
            var bytes = new byte[output.Size];
            output.Seek(0);
            using var reader = new DataReader(output);
            await reader.LoadAsync((uint)output.Size);
            reader.ReadBytes(bytes);
            return bytes;
        }
        return null;
    }

    /// <summary>Content hash as the artwork id (lowercase hex, 16 chars): changes exactly when the image changes.</summary>
    internal static string IdOf(byte[] image) => Convert.ToHexString(SHA256.HashData(image).AsSpan(0, 8)).ToLowerInvariant();
}
