using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace CorgiBlackboard.TestFixtures;

public static class ImageFixtureFactory
{
    public static void Create(string directory)
    {
        Directory.CreateDirectory(directory);
        SaveGrid(directory, "long-grid-800x2400.png", 800, 2400);
        SaveGrid(directory, "wide-grid-2400x800.png", 2400, 800);
        SaveGrid(directory, "small-grid-200x100.png", 200, 100);
        SaveGrid(directory, "oriented.jpg", 200, 100, true);
        File.WriteAllBytes(Path.Combine(directory, "invalid.png"), [0, 1, 2, 3]);
    }

    private static void SaveGrid(string directory, string name, int width, int height, bool oriented = false)
    {
        var pixels = new byte[width * height * 4];
        for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x++)
            {
                var index = (y * width + x) * 4;
                var grid = x % 100 < 2 || y % 100 < 2;
                pixels[index] = grid ? (byte)0 : x < width / 2 ? (byte)32 : (byte)240;
                pixels[index + 1] = grid ? (byte)0 : (byte)32;
                pixels[index + 2] = grid ? (byte)0 : x < width / 2 ? (byte)240 : (byte)32;
                pixels[index + 3] = 255;
            }
        var image = BitmapSource.Create(width, height, 300, 300, PixelFormats.Bgra32, null, pixels, width * 4);
        BitmapEncoder encoder;
        BitmapMetadata? metadata = null;
        if (oriented)
        {
            encoder = new JpegBitmapEncoder { QualityLevel = 95 };
            metadata = new BitmapMetadata("jpg");
            metadata.SetQuery("/app1/ifd/{ushort=274}", (ushort)6);
        }
        else encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(image, null, metadata, null));
        using var stream = File.Create(Path.Combine(directory, name));
        encoder.Save(stream);
    }
}
