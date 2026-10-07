// Batch image resizer for web use (SkiaSharp version, MIT licensed, no license key).
//
// Usage:
//   dotnet run -- <inputDir> [--out <outputDir>] [--width 1920] [--height 1920]
//                 [--quality 80] [--recursive] [--overwrite]
//
// By default originals are left untouched and results go to <inputDir>/resized.
// Images are only ever shrunk, never enlarged. EXIF orientation is applied
// and metadata is dropped by re-encoding.

using SkiaSharp;

if (args.Length == 0 || args[0] is "-h" or "--help")
{
    Console.WriteLine("Usage: <inputDir> [--out <dir>] [--width N] [--height N] [--quality N] [--recursive] [--overwrite]");
    return 1;
}

string inputDir = Path.GetFullPath(args[0]);
string? outputArg = null;
int maxWidth = 1920, maxHeight = 1920, quality = 80;
bool recursive = false, overwrite = false;

for (int i = 1; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--out": outputArg = args[++i]; break;
        case "--width": maxWidth = int.Parse(args[++i]); break;
        case "--height": maxHeight = int.Parse(args[++i]); break;
        case "--quality": quality = int.Parse(args[++i]); break;
        case "--recursive": recursive = true; break;
        case "--overwrite": overwrite = true; break;
        default:
            Console.Error.WriteLine($"Unknown option: {args[i]}");
            return 1;
    }
}

if (!Directory.Exists(inputDir))
{
    Console.Error.WriteLine($"Directory not found: {inputDir}");
    return 1;
}

string outputDir = overwrite
    ? inputDir
    : Path.GetFullPath(outputArg ?? Path.Combine(inputDir, "resized"));

string[] extensions = [".jpg", ".jpeg", ".png", ".webp"];

var files = Directory
    .EnumerateFiles(inputDir, "*.*", recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly)
    .Where(f => extensions.Contains(Path.GetExtension(f), StringComparer.OrdinalIgnoreCase))
    .Where(f => overwrite || !f.StartsWith(outputDir + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
    .ToList();

if (files.Count == 0)
{
    Console.WriteLine("No supported images found (jpg, jpeg, png, webp).");
    return 0;
}

Console.WriteLine($"Found {files.Count} image(s). Max size {maxWidth}x{maxHeight}, quality {quality}.");
Console.WriteLine($"Output folder: {outputDir}");

long savedBytes = 0;
int done = 0, failed = 0;

Parallel.ForEach(
    files,
    new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount },
    file =>
    {
        try
        {
            string relative = Path.GetRelativePath(inputDir, file);
            string target = Path.Combine(outputDir, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);

            long originalSize = new FileInfo(file).Length;
            string tempTarget = target + ".tmp";

            SKEncodedImageFormat format = Path.GetExtension(file).ToLowerInvariant() switch
            {
                ".png" => SKEncodedImageFormat.Png,
                ".webp" => SKEncodedImageFormat.Webp,
                _ => SKEncodedImageFormat.Jpeg
            };

            using (var codec = SKCodec.Create(file) ?? throw new InvalidDataException("Unsupported or corrupt image."))
            using (var decoded = SKBitmap.Decode(codec))
            using (var oriented = new OrientedBitmap(decoded, codec.EncodedOrigin))
            {
                SKBitmap current = oriented.Bitmap;
                SKBitmap? resized = null;

                try
                {
                    if (current.Width > maxWidth || current.Height > maxHeight)
                    {
                        double scale = Math.Min((double)maxWidth / current.Width, (double)maxHeight / current.Height);
                        int newW = Math.Max(1, (int)Math.Round(current.Width * scale));
                        int newH = Math.Max(1, (int)Math.Round(current.Height * scale));

                        resized = current.Resize(
                            new SKImageInfo(newW, newH, current.ColorType, current.AlphaType),
                            new SKSamplingOptions(SKCubicResampler.Mitchell));

                        if (resized != null) current = resized;
                    }

                    using var image = SKImage.FromBitmap(current);
                    using var data = image.Encode(format, quality); // quality is ignored for PNG
                    using var outStream = File.Create(tempTarget);
                    data.SaveTo(outStream);
                }
                finally
                {
                    resized?.Dispose();
                }
            }

            // Keep whichever is smaller.
            long newSize = new FileInfo(tempTarget).Length;
            if (newSize >= originalSize)
            {
                File.Delete(tempTarget);
                if (!overwrite) File.Copy(file, target, true);
                newSize = originalSize;
            }
            else
            {
                File.Move(tempTarget, target, true);
            }

            Interlocked.Add(ref savedBytes, originalSize - newSize);
            int n = Interlocked.Increment(ref done);
            Console.WriteLine($"[{n}/{files.Count}] {relative}: {originalSize / 1024} KB -> {newSize / 1024} KB");
        }
        catch (Exception ex)
        {
            Interlocked.Increment(ref failed);
            Console.Error.WriteLine($"FAILED {file}: {ex.Message}");
        }
    });

Console.WriteLine($"\nDone. {done} processed, {failed} failed, {savedBytes / 1024 / 1024.0:F1} MB saved.");
return failed == 0 ? 0 : 2;

// Applies the EXIF orientation so photos taken on phones aren't sideways.
sealed class OrientedBitmap : IDisposable
{
    private readonly SKBitmap? _owned;
    public SKBitmap Bitmap { get; }

    public OrientedBitmap(SKBitmap src, SKEncodedOrigin origin)
    {
        if (origin == SKEncodedOrigin.TopLeft || origin == SKEncodedOrigin.Default)
        {
            Bitmap = src; // nothing to do, and we don't own it
            return;
        }

        bool swap = origin is SKEncodedOrigin.LeftTop or SKEncodedOrigin.RightTop
                           or SKEncodedOrigin.RightBottom or SKEncodedOrigin.LeftBottom;

        int sw = src.Width, sh = src.Height;
        int w = swap ? sh : sw;
        int h = swap ? sw : sh;

        var dst = new SKBitmap(w, h, src.ColorType, src.AlphaType);
        using (var canvas = new SKCanvas(dst))
        {
            var m = SKMatrix.CreateIdentity();
            switch (origin)
            {
                case SKEncodedOrigin.TopRight:    // mirror horizontal
                    m = new SKMatrix { ScaleX = -1, TransX = sw, ScaleY = 1, Persp2 = 1 }; break;
                case SKEncodedOrigin.BottomRight: // rotate 180
                    m = new SKMatrix { ScaleX = -1, TransX = sw, ScaleY = -1, TransY = sh, Persp2 = 1 }; break;
                case SKEncodedOrigin.BottomLeft:  // mirror vertical
                    m = new SKMatrix { ScaleX = 1, ScaleY = -1, TransY = sh, Persp2 = 1 }; break;
                case SKEncodedOrigin.LeftTop:     // transpose
                    m = new SKMatrix { SkewX = 1, SkewY = 1, Persp2 = 1 }; break;
                case SKEncodedOrigin.RightTop:    // rotate 90 clockwise
                    m = new SKMatrix { SkewX = -1, TransX = sh, SkewY = 1, Persp2 = 1 }; break;
                case SKEncodedOrigin.RightBottom: // transverse
                    m = new SKMatrix { SkewX = -1, TransX = sh, SkewY = -1, TransY = sw, Persp2 = 1 }; break;
                case SKEncodedOrigin.LeftBottom:  // rotate 270 clockwise
                    m = new SKMatrix { SkewX = 1, SkewY = -1, TransY = sw, Persp2 = 1 }; break;
            }
            canvas.SetMatrix(m);
            canvas.DrawBitmap(src, 0, 0, new SKSamplingOptions(SKFilterMode.Nearest));
        }

        _owned = dst;
        Bitmap = dst;
    }

    public void Dispose() => _owned?.Dispose();
}