using System.Buffers.Binary;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace CorgiBlackboard.Infrastructure.Tests;

internal sealed class ImageFixtures : IDisposable
{
    public string DirectoryPath { get; } = Path.Combine(Path.GetTempPath(), "corgi-images-" + Guid.NewGuid());

    public ImageFixtures() => Directory.CreateDirectory(DirectoryPath);

    public string OrientedJpeg(ushort orientation)
    {
        const int width = 96, height = 64;
        var pixels = new byte[width * height * 4];
        for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x++)
            {
                var index = (y * width + x) * 4;
                var color = (x < width / 2, y < height / 2) switch
                {
                    (true, true) => Colors.Red,
                    (false, true) => Colors.Lime,
                    (true, false) => Colors.Blue,
                    _ => Colors.Yellow
                };
                pixels[index] = color.B;
                pixels[index + 1] = color.G;
                pixels[index + 2] = color.R;
                pixels[index + 3] = 255;
            }
        var metadata = new BitmapMetadata("jpg");
        metadata.SetQuery("/app1/ifd/{ushort=274}", orientation);
        var bitmap = BitmapSource.Create(width, height, 300, 300, PixelFormats.Bgra32, null, pixels, width * 4);
        var encoder = new JpegBitmapEncoder { QualityLevel = 100 };
        encoder.Frames.Add(BitmapFrame.Create(bitmap, null, metadata, null));
        var path = Path.Combine(DirectoryPath, $"orientation-{orientation}.jpg");
        using var stream = File.Create(path);
        encoder.Save(stream);
        return path;
    }

    public string OversizedHeader(string format)
    {
        // Deliberately no pixel payload: the dimension guard must precede WIC decoding.
        var bytes = format switch
        {
            "png" => new byte[33],
            "bmp" => new byte[54],
            _ => new byte[] { 0xff, 0xd8, 0xff, 0xc0, 0, 17, 8, 0x23, 0x28, 0x23, 0x28, 3,
                1, 0x11, 0, 2, 0x11, 0, 3, 0x11, 0, 0xff, 0xd9 }
        };
        if (format == "png")
        {
            new byte[] { 137, 80, 78, 71, 13, 10, 26, 10, 0, 0, 0, 13, 73, 72, 68, 82 }.CopyTo(bytes, 0);
            BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(16), 9000);
            BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(20), 9000);
            bytes[24] = 8;
            bytes[25] = 2;
        }
        else if (format == "bmp")
        {
            bytes[0] = (byte)'B';
            bytes[1] = (byte)'M';
            BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(14), 40);
            BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(18), 9000);
            BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(22), 9000);
        }
        var path = Path.Combine(DirectoryPath, "oversized." + format);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    public void Dispose() => Directory.Delete(DirectoryPath, true);
}
