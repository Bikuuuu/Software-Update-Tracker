using IconGen;
using SkiaSharp;
using Svg.Skia;

var mode = args.FirstOrDefault() ?? "generate";
var root = FindRepoRoot();
var iconDir = Path.Combine(root, "assets", "icon");
var outDir = Path.Combine(iconDir, "generated");
int[] appSizes = [16, 20, 24, 32, 40, 48, 64, 256];
int[] traySizes = [16, 20, 24, 32, 40, 48];

switch (mode)
{
    case "generate":
        Directory.CreateDirectory(outDir);
        IcoWriter.Write(Path.Combine(outDir, "app.ico"), appSizes.Select(s => (s, Png(Render(s)))).ToList());
        IcoWriter.Write(Path.Combine(outDir, "tray.ico"), traySizes.Select(s => (s, Png(Render(s)))).ToList());
        IcoWriter.Write(Path.Combine(outDir, "tray-badge.ico"), traySizes.Select(s => (s, Png(Badge.Apply(Render(s))))).ToList());
        for (var frame = 0; frame < Spinner.Frames; frame++)
            IcoWriter.Write(Path.Combine(outDir, $"tray-work-{frame}.ico"), traySizes.Select(s => (s, Png(Spinner.Apply(Render(s), frame)))).ToList());
        foreach (var s in new[] { 64, 256 }) File.WriteAllBytes(Path.Combine(outDir, $"app-{s}.png"), Png(Render(s)));
        Console.WriteLine($"Icons written to {outDir}");
        return 0;
    case "verify":
        foreach (var name in IconNames())
            Console.WriteLine($"{name}: {string.Join(",", IcoWriter.Read(Path.Combine(outDir, name)).Select(e => e.Size))}");
        return 0;
    case "dump" when args.Length == 2:
        Directory.CreateDirectory(args[1]);
        foreach (var name in IconNames())
            foreach (var (size, png) in IcoWriter.Read(Path.Combine(outDir, name)))
                File.WriteAllBytes(Path.Combine(args[1], $"{Path.GetFileNameWithoutExtension(name)}-{size}.png"), png);
        Console.WriteLine($"Dumped to {args[1]}");
        return 0;
    default:
        Console.Error.WriteLine("usage: generate | verify | dump <folder>");
        return 2;
}

SKBitmap Render(int size)
{
    var file = Path.Combine(iconDir, size <= 24 ? "hamster-small.svg" : "hamster.svg");
    using var svg = new SKSvg();
    var picture = svg.Load(file) ?? throw new InvalidDataException($"Cannot load {file}");
    var bitmap = new SKBitmap(new SKImageInfo(size, size, SKColorType.Bgra8888, SKAlphaType.Premul));
    using var canvas = new SKCanvas(bitmap);
    canvas.Clear(SKColors.Transparent);
    canvas.Scale(size / picture.CullRect.Width, size / picture.CullRect.Height);
    canvas.DrawPicture(picture);
    canvas.Flush();
    return bitmap;
}

static IEnumerable<string> IconNames() =>
    ["app.ico", "tray.ico", "tray-badge.ico", .. Enumerable.Range(0, Spinner.Frames).Select(f => $"tray-work-{f}.ico")];

static byte[] Png(SKBitmap bitmap)
{
    using var image = SKImage.FromBitmap(bitmap);
    using var data = image.Encode(SKEncodedImageFormat.Png, 100);
    return data.ToArray();
}

static string FindRepoRoot()
{
    var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
    while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "TinyTracker.slnx"))) dir = dir.Parent;
    return dir?.FullName ?? throw new DirectoryNotFoundException("Run from inside the repository.");
}
