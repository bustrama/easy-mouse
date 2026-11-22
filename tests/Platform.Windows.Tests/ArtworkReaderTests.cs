using System.Runtime.InteropServices.WindowsRuntime;
using EasyMouse.Core.Protocol;
using EasyMouse.Platform.Windows;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace EasyMouse.Platform.Windows.Tests;

public class ArtworkReaderTests
{
    private static async Task<RandomAccessStreamReference> PngAsync(uint width, uint height)
    {
        var pixels = new byte[width * height * 4];
        var rnd = new Random(7); // noisy on purpose: the JPEG must still fit the size budget
        rnd.NextBytes(pixels);
        for (int i = 3; i < pixels.Length; i += 4) pixels[i] = 255;
        var stream = new InMemoryRandomAccessStream();
        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream);
        encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore, width, height, 96, 96, pixels);
        await encoder.FlushAsync();
        return RandomAccessStreamReference.CreateFromStream(stream);
    }

    [Fact]
    public async Task Large_image_becomes_a_small_jpeg_with_a_stable_id()
    {
        var art = await ArtworkReader.ReadAsync(await PngAsync(900, 600));
        Assert.NotNull(art);
        Assert.Equal(ArtworkType.Jpeg, art.Type);
        Assert.True(art.Data.Length is > 0 and <= ArtworkReader.MaxBytes, $"size {art.Data.Length}");
        Assert.Equal([0xFF, 0xD8], art.Data[..2]);

        using var decoded = new InMemoryRandomAccessStream();
        await decoded.WriteAsync(art.Data.AsBuffer());
        decoded.Seek(0);
        var decoder = await BitmapDecoder.CreateAsync(decoded);
        Assert.True(decoder.PixelWidth <= ArtworkReader.MaxSide && decoder.PixelHeight <= ArtworkReader.MaxSide);
        Assert.True(decoder.PixelWidth > decoder.PixelHeight); // aspect ratio kept

        Assert.Matches("^[0-9a-f]{16}$", art.Id);
        Assert.Equal(art.Id, ArtworkReader.IdOf(art.Data));
    }

    [Fact]
    public async Task Small_image_is_not_upscaled()
    {
        var art = await ArtworkReader.ReadAsync(await PngAsync(64, 64));
        Assert.NotNull(art);
        using var s = new InMemoryRandomAccessStream();
        await s.WriteAsync(art.Data.AsBuffer());
        s.Seek(0);
        var decoder = await BitmapDecoder.CreateAsync(s);
        Assert.Equal(64u, decoder.PixelWidth);
    }

    [Fact]
    public async Task Missing_or_broken_thumbnails_give_null_not_exceptions()
    {
        Assert.Null(await ArtworkReader.ReadAsync(null));
        var junk = new InMemoryRandomAccessStream();
        await junk.WriteAsync(new byte[] { 1, 2, 3, 4, 5 }.AsBuffer());
        Assert.Null(await ArtworkReader.ReadAsync(RandomAccessStreamReference.CreateFromStream(junk)));
    }
}
