using System.IO;
using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace CorgiBlackboard.Infrastructure.Images;

public sealed record LoadedImage(BitmapSource Pixels, string MediaType, long FileLength, string Sha256);

public sealed class ImageLoadException(string message, Exception? inner = null) : Exception(message, inner);

public sealed class ImageLoader
{
    private readonly Func<string, Stream> openRead;

    public ImageLoader() : this(path => new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read)) { }

    internal ImageLoader(Func<string, Stream> openRead) => this.openRead = openRead;

    // Bounds decoded pixels, not compressed file size. WIC may still reject a file within this limit.
    public const long MaximumPixels = 64_000_000;

    public Task<LoadedImage> LoadAsync(string path, CancellationToken cancellationToken = default) =>
        Task.Run(() => Decode(path, cancellationToken), cancellationToken);

    private LoadedImage Decode(string path, CancellationToken cancellationToken)
    {
        int width = 0, height = 0;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var stream = openRead(path);
            (width, height) = ReadDimensions(stream, cancellationToken);
            CheckDimensions(width, height);
            cancellationToken.ThrowIfCancellationRequested();
            stream.Position = 0;
            var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat,
                BitmapCacheOption.None);
            var mediaType = decoder switch
            {
                PngBitmapDecoder => "image/png",
                JpegBitmapDecoder => "image/jpeg",
                BmpBitmapDecoder => "image/bmp",
                _ => throw new ImageLoadException("仅支持 PNG、JPEG 和 BMP 图片。")
            };
            var frame = decoder.Frames[0];
            width = frame.PixelWidth;
            height = frame.PixelHeight;
            CheckDimensions(width, height);
            cancellationToken.ThrowIfCancellationRequested();
            var orientation = ReadOrientation(frame.Metadata as BitmapMetadata);
            var converted = new FormatConvertedBitmap(frame, PixelFormats.Bgra32, null, 0);
            var stride = checked(width * 4);
            var source = new byte[checked(stride * height)];
            converted.CopyPixels(source, stride, 0);
            cancellationToken.ThrowIfCancellationRequested();
            var swapped = orientation is >= 5 and <= 8;
            var outputWidth = swapped ? height : width;
            var outputHeight = swapped ? width : height;
            var pixels = orientation == 1 ? source : new byte[source.Length];
            if (orientation != 1)
            {
                for (var y = 0; y < height; y++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    for (var x = 0; x < width; x++)
                    {
                        var (dx, dy) = orientation switch
                        {
                            2 => (width - 1 - x, y),
                            3 => (width - 1 - x, height - 1 - y),
                            4 => (x, height - 1 - y),
                            5 => (y, x),
                            6 => (height - 1 - y, x),
                            7 => (height - 1 - y, width - 1 - x),
                            8 => (y, width - 1 - x),
                            _ => (x, y)
                        };
                        Buffer.BlockCopy(source, (y * width + x) * 4,
                            pixels, (dy * outputWidth + dx) * 4, 4);
                    }
                }
            }
            var bitmap = BitmapSource.Create(outputWidth, outputHeight, 96, 96,
                PixelFormats.Bgra32, null, pixels, outputWidth * 4);
            bitmap.Freeze();
            cancellationToken.ThrowIfCancellationRequested();
            var sha256 = ImageFingerprint.Compute(stream, cancellationToken);
            return new LoadedImage(bitmap, mediaType, stream.Length, sha256);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException
            or NotSupportedException or FileFormatException or COMException or OutOfMemoryException
            or ArgumentException or OverflowException)
        {
            cancellationToken.ThrowIfCancellationRequested();
            throw new ImageLoadException($"无法读取图片（尺寸 {width} × {height}）：文件无效或解码资源不足。", error);
        }
    }

    private static void CheckDimensions(int width, int height)
    {
        if (width <= 0 || height <= 0)
            throw new ImageLoadException("图片尺寸无效。");
        if ((long)width * height > MaximumPixels)
            throw new ImageLoadException($"图片尺寸 {width} × {height} 超过本版本的 6400 万像素解码上限。");
    }

    private static (int Width, int Height) ReadDimensions(Stream stream, CancellationToken cancellationToken)
    {
        Span<byte> header = stackalloc byte[26];
        stream.ReadExactly(header[..2]);
        cancellationToken.ThrowIfCancellationRequested();
        if (header[0] == 137 && header[1] == 80)
        {
            stream.ReadExactly(header[2..24]);
            if (!header[..8].SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 })
                || !header[12..16].SequenceEqual("IHDR"u8))
                throw new ImageLoadException("PNG 图片文件头无效。");
            return (checked((int)BinaryPrimitives.ReadUInt32BigEndian(header[16..20])),
                checked((int)BinaryPrimitives.ReadUInt32BigEndian(header[20..24])));
        }
        if (header[0] == 'B' && header[1] == 'M')
        {
            stream.ReadExactly(header[2..26]);
            var dibSize = BinaryPrimitives.ReadUInt32LittleEndian(header[14..18]);
            if (dibSize == 12)
                return (BinaryPrimitives.ReadUInt16LittleEndian(header[18..20]),
                    BinaryPrimitives.ReadUInt16LittleEndian(header[20..22]));
            if (dibSize < 40)
                throw new ImageLoadException("BMP 图片文件头无效。");
            return (BinaryPrimitives.ReadInt32LittleEndian(header[18..22]),
                checked((int)Math.Abs((long)BinaryPrimitives.ReadInt32LittleEndian(header[22..26]))));
        }
        if (header[0] != 0xff || header[1] != 0xd8)
            throw new ImageLoadException("仅支持 PNG、JPEG 和 BMP 图片。");

        while (stream.Position < stream.Length)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (stream.ReadByte() != 0xff)
                throw new ImageLoadException("JPEG 图片文件头无效。");
            int marker;
            do { marker = stream.ReadByte(); } while (marker == 0xff);
            if (marker < 0 || marker is 0xd9 or 0xda)
                break;
            if (marker == 0x01 || marker is >= 0xd0 and <= 0xd7)
                continue;
            stream.ReadExactly(header[..2]);
            var length = BinaryPrimitives.ReadUInt16BigEndian(header[..2]);
            if (length < 2 || stream.Position + length - 2 > stream.Length)
                throw new ImageLoadException("JPEG 图片文件头无效。");
            if (marker is >= 0xc0 and <= 0xcf && marker is not (0xc4 or 0xc8 or 0xcc))
            {
                if (length < 8)
                    throw new ImageLoadException("JPEG 图片尺寸无效。");
                stream.ReadExactly(header[..5]);
                return (BinaryPrimitives.ReadUInt16BigEndian(header[3..5]),
                    BinaryPrimitives.ReadUInt16BigEndian(header[1..3]));
            }
            stream.Seek(length - 2, SeekOrigin.Current);
        }
        throw new ImageLoadException("JPEG 图片缺少尺寸信息。");
    }

    private static ushort ReadOrientation(BitmapMetadata? metadata)
    {
        if (metadata?.GetQuery("/app1/ifd/{ushort=274}") is ushort value && value is >= 1 and <= 8)
            return value;
        return 1;
    }
}
