using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CorgiBlackboard.Infrastructure.Images;
using Xunit;

namespace CorgiBlackboard.Infrastructure.Tests;

public sealed class ImageLoaderTests
{
    [Fact]
    public async Task Oversized_header_is_rejected_before_pixel_allocation_and_releases_file()
    {
        var path = Path.Combine(Path.GetTempPath(), "corgi-oversized-" + Guid.NewGuid() + ".bmp");
        try
        {
            using (var stream = File.Create(path))
            using (var writer = new BinaryWriter(stream))
            {
                writer.Write((ushort)0x4D42);
                writer.Write(54 + 9000 * 8000 * 3);
                writer.Write(0);
                writer.Write(54);
                writer.Write(40);
                writer.Write(9000);
                writer.Write(8000);
                writer.Write((ushort)1);
                writer.Write((ushort)24);
                writer.Write(new byte[24]);
                stream.SetLength(54 + 9000L * 8000 * 3);
            }
            var error = await Assert.ThrowsAsync<ImageLoadException>(() => new ImageLoader().LoadAsync(path));
            Assert.Null(error.InnerException);
            using var exclusive = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task Cancellation_during_a_read_disposes_stream_and_completes_as_cancelled()
    {
        using var cancellation = new CancellationTokenSource();
        var reading = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stream = new CancelledReadStream(reading, cancellation.Token);
        var loader = new ImageLoader(_ => stream);
        var task = loader.LoadAsync("in-memory.png", cancellation.Token);
        try
        {
            await reading.Task.WaitAsync(TimeSpan.FromSeconds(10));
            cancellation.Cancel();
            var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
            Assert.Equal(cancellation.Token, error.CancellationToken);
            Assert.True(task.IsCanceled);
            Assert.True(stream.Disposed);
        }
        finally
        {
            cancellation.Cancel();
            try { await task; } catch { /* Observe the worker before disposing its token source. */ }
        }
    }

    private sealed class CancelledReadStream(TaskCompletionSource reading, CancellationToken token)
        : MemoryStream(new byte[64])
    {
        public bool Disposed { get; private set; }

        public override int Read(byte[] buffer, int offset, int count)
        {
            reading.TrySetResult();
            if (!token.WaitHandle.WaitOne(TimeSpan.FromSeconds(10)))
                throw new TimeoutException("Cancellation was not signalled.");
            throw new IOException("Read interrupted.");
        }

        public override int Read(Span<byte> buffer) => Read(new byte[buffer.Length], 0, buffer.Length);

        protected override void Dispose(bool disposing)
        {
            Disposed = true;
            base.Dispose(disposing);
        }
    }

    [Theory]
    [InlineData(1, "RGBY")]
    [InlineData(2, "GRYB")]
    [InlineData(3, "YBGR")]
    [InlineData(4, "BYRG")]
    [InlineData(5, "RBGY")]
    [InlineData(6, "BRYG")]
    [InlineData(7, "YGBR")]
    [InlineData(8, "GYRB")]
    public async Task Exif_orientation_maps_all_four_corners(int orientation, string corners)
    {
        using var fixtures = new ImageFixtures();
        var result = await new ImageLoader().LoadAsync(fixtures.OrientedJpeg((ushort)orientation));
        var width = orientation >= 5 ? 64 : 96;
        var height = orientation >= 5 ? 96 : 64;
        Assert.Equal(width, result.Pixels.PixelWidth);
        Assert.Equal(height, result.Pixels.PixelHeight);
        var pixels = new byte[width * height * 4];
        result.Pixels.CopyPixels(pixels, width * 4, 0);
        var points = new[] { (width / 4, height / 4), (3 * width / 4, height / 4),
            (width / 4, 3 * height / 4), (3 * width / 4, 3 * height / 4) };
        for (var i = 0; i < points.Length; i++)
        {
            var (x, y) = points[i];
            var offset = (y * width + x) * 4;
            var expected = corners[i] switch
            {
                'R' => Colors.Red, 'G' => Colors.Lime, 'B' => Colors.Blue, _ => Colors.Yellow
            };
            Assert.InRange(Math.Abs(pixels[offset] - expected.B), 0, 8);
            Assert.InRange(Math.Abs(pixels[offset + 1] - expected.G), 0, 8);
            Assert.InRange(Math.Abs(pixels[offset + 2] - expected.R), 0, 8);
        }
    }

    [Theory]
    [InlineData("png")]
    [InlineData("jpg")]
    [InlineData("bmp")]
    public async Task Oversized_header_is_rejected_before_pixel_payload_is_required(string format)
    {
        using var fixtures = new ImageFixtures();
        var path = fixtures.OversizedHeader(format);
        var error = await Assert.ThrowsAsync<ImageLoadException>(() => new ImageLoader().LoadAsync(path));
        Assert.Null(error.InnerException);
        using var exclusive = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
    }

    [Fact]
    public async Task Precancelled_request_does_not_open_a_missing_file()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var task = new ImageLoader().LoadAsync(Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".png"),
            cancellation.Token);
        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
        Assert.Equal(cancellation.Token, error.CancellationToken);
        Assert.True(task.IsCanceled);
    }

    [Fact]
    public async Task Generated_marker_fixtures_have_expected_intrinsic_sizes_and_exif_rotation()
    {
        using var fixtures = new ImageFixtures();
        var directory = fixtures.DirectoryPath;
        CorgiBlackboard.TestFixtures.ImageFixtureFactory.Create(directory);
        foreach (var (file, width, height) in new[] {
            ("long-grid-800x2400.png", 800, 2400),
            ("wide-grid-2400x800.png", 2400, 800),
            ("small-grid-200x100.png", 200, 100), ("oriented.jpg", 100, 200) })
        {
            var loaded = await new ImageLoader().LoadAsync(Path.Combine(directory, file));
            Assert.Equal(width, loaded.Pixels.PixelWidth);
            Assert.Equal(height, loaded.Pixels.PixelHeight);
            Assert.Equal(96, loaded.Pixels.DpiY);
            if (file != "oriented.jpg") continue;
            var pixels = new byte[width * height * 4];
            loaded.Pixels.CopyPixels(pixels, width * 4, 0);
            Assert.InRange(pixels[(50 * width + 50) * 4 + 2], 220, 255);
            Assert.InRange(pixels[(150 * width + 50) * 4], 220, 255);
        }
        await Assert.ThrowsAsync<ImageLoadException>(() =>
            new ImageLoader().LoadAsync(Path.Combine(directory, "invalid.png")));
    }

    [Theory]
    [InlineData("png")]
    [InlineData("jpg")]
    [InlineData("bmp")]
    public async Task Decode_detaches_pixels_from_source_and_ignores_file_dpi(string format)
    {
        var directory = Path.Combine(Path.GetTempPath(), "corgi-image-" + Guid.NewGuid());
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "grid." + format);
            var pixels = new byte[200 * 100 * 4];
            for (var y = 0; y < 100; y++)
                for (var x = 0; x < 200; x++)
                {
                    var i = (y * 200 + x) * 4;
                    pixels[i + 2] = x < 100 ? (byte)255 : (byte)0;
                    pixels[i] = x < 100 ? (byte)0 : (byte)255;
                    pixels[i + 3] = 255;
                }
            var bitmap = BitmapSource.Create(200, 100, 300, 300, PixelFormats.Bgra32, null, pixels, 800);
            BitmapEncoder encoder = format switch
            {
                "jpg" => new JpegBitmapEncoder(),
                "bmp" => new BmpBitmapEncoder(),
                _ => new PngBitmapEncoder()
            };
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using (var file = File.Create(path)) encoder.Save(file);

            var result = await new ImageLoader().LoadAsync(path);
            File.Move(path, path + ".moved");
            File.Delete(path + ".moved");
            Assert.True(result.Pixels.IsFrozen);
            Assert.Equal(200, result.Pixels.PixelWidth);
            Assert.Equal(100, result.Pixels.PixelHeight);
            Assert.Equal(96, result.Pixels.DpiX);
            Assert.Equal(96, result.Pixels.DpiY);
            Assert.Equal(format switch { "jpg" => "image/jpeg", "bmp" => "image/bmp", _ => "image/png" },
                result.MediaType);
            Assert.True(result.FileLength > 0);
            var actual = new byte[pixels.Length];
            result.Pixels.CopyPixels(actual, 800, 0);
            Assert.InRange(actual[(50 * 200 + 50) * 4 + 2], 240, 255);
            Assert.InRange(actual[(50 * 200 + 150) * 4], 240, 255);
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task Invalid_file_and_cancelled_load_return_no_image_and_release_file()
    {
        var path = Path.Combine(Path.GetTempPath(), "corgi-invalid-" + Guid.NewGuid() + ".png");
        try
        {
            await File.WriteAllBytesAsync(path, [1, 2, 3, 4]);
            await Assert.ThrowsAsync<ImageLoadException>(() => new ImageLoader().LoadAsync(path));
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                new ImageLoader().LoadAsync(path, cancellation.Token));
            using var exclusive = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        }
        finally { File.Delete(path); }
    }
}
