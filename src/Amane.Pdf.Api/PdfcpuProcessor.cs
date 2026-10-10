using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace Amane.Pdf.Api;

internal sealed class PdfcpuProcessor(IOptions<PdfOptions> options)
{
    internal const string JapaneseFont = "BIZUDPGothic-Regular";
    internal const string Version = "0.16.1";
    internal const long LayerFileLimitBytes = 8 * 1024 * 1024;
    internal const int MaxTextLength = 128; // UTF-16 code units per element
    internal const int MaxTextsPerPage = 4;
    internal const int MaxTextElements = 4000;
    internal const int MaxLineBreaks = 3;
    internal const int MaxTabs = 3;
    private const string Failure = "PDF描画処理に失敗しました。";
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private readonly PdfOptions settings = options.Value;

    internal static bool IsValid(PdfOptions value)
    {
        var memory = value.PdfcpuMemoryLimit;
        if (string.IsNullOrWhiteSpace(value.PdfcpuPath) || string.IsNullOrWhiteSpace(value.PdfcpuConfigDir) ||
            !Path.IsPathFullyQualified(value.PdfcpuConfigDir) || value.PdfcpuAddressSpaceLimitBytes <= 0 ||
            memory is null || !memory.EndsWith("MiB", StringComparison.Ordinal)) return false;
        var digits = memory.AsSpan(0, memory.Length - 3);
        if (digits.IsEmpty || digits.Length > 12) return false;
        foreach (var digit in digits) if (digit is < '0' or > '9') return false;
        return long.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out var mib) &&
            mib > 0 && mib <= value.PdfcpuAddressSpaceLimitBytes / (1024 * 1024);
    }

    internal async Task CreateAsync(TemporaryPdfFiles files, PdfcpuLayer layer,
        string blankPath, string outputPath, long jsonBudget, long pdfBudget, CancellationToken token)
    {
        if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException();
        if (jsonBudget is <= 0 or > LayerFileLimitBytes || pdfBudget is <= 0 or > LayerFileLimitBytes)
            throw new ArgumentOutOfRangeException(nameof(jsonBudget));
        RequireJobPath(files, blankPath);
        RequireJobPath(files, outputPath);
        if (Path.GetFullPath(blankPath) == Path.GetFullPath(files.InputPath) ||
            Path.GetFullPath(blankPath) == Path.GetFullPath(outputPath) || File.Exists(outputPath))
            throw new ArgumentException("描画用pathが不正です。");
        if (new FileInfo(blankPath).Length is <= 0 or > LayerFileLimitBytes)
            throw new InvalidOperationException(Failure);
        await WriteLayerAsync(files.PdfcpuLayerJsonPath, layer, jsonBudget, token);
        var result = await ExternalProcessRunner.RunAsync(CreateRequest(files,
            ["create", files.PdfcpuLayerJsonPath, blankPath, outputPath], pdfBudget + 1), token);
        if (result.ExitCode is 126 or 127) throw new InvalidOperationException(Failure);
        if (File.Exists(outputPath) && new FileInfo(outputPath).Length > pdfBudget)
            throw new PdfcpuCapacityException();
        if (result.ExitCode != 0 || !File.Exists(outputPath) || new FileInfo(outputPath).Length == 0)
            throw new InvalidOperationException(Failure);
        File.SetUnixFileMode(outputPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        await CheckOutputAsync(files, outputPath, layer.Pages.Count, token);
    }

    private ExternalProcessRequest CreateRequest(TemporaryPdfFiles files, string[] arguments,
        long fileBudget, int? stdoutLimit = null)
    {
        if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException();
        var home = Path.Combine(files.DirectoryPath, "pdfcpu-home");
        Directory.CreateDirectory(home, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var environment = new Dictionary<string, string>
        {
            ["HOME"] = home, ["XDG_CONFIG_HOME"] = home,
            ["GOMEMLIMIT"] = settings.PdfcpuMemoryLimit, ["GOGC"] = "100", ["GODEBUG"] = "",
            ["GOMAXPROCS"] = "1", ["GOTRACEBACK"] = "none"
        };
        return ProcessMemoryLimits.CreateRequest(settings.PrlimitPath, settings.PdfcpuPath,
            ["-c", settings.PdfcpuConfigDir, "--offline", .. arguments],
            settings.PdfcpuAddressSpaceLimitBytes, fileBudget, environment, stdoutLimit)
            with { WorkingDirectory = files.DirectoryPath };
    }

    internal async Task ImportImageAsync(TemporaryPdfFiles files, string imagePath, ImageInfo image,
        string outputPath, long budget, CancellationToken token)
    {
        if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException();
        RequireJobPath(files, imagePath); RequireJobPath(files, outputPath);
        if (budget is <= 0 or > 54 * 1048576 || File.Exists(outputPath) ||
            Path.GetFullPath(imagePath) == Path.GetFullPath(outputPath) ||
            Path.GetExtension(imagePath) != (image.Png ? ".png" : ".jpg") ||
            new FileInfo(imagePath).Length <= 0 || image.Width <= 0 || image.Height <= 0)
            throw new ArgumentException("Image import arguments are invalid.");
        // import appends to existing PDFs: its output must not be pre-created.
        var result = await ExternalProcessRunner.RunAsync(CreateRequest(files,
            ["import", "--", ImagePageLayout.For(image).Description, outputPath, imagePath], budget + 1), token);
        JpegNormalizer.CheckResult(result, outputPath, budget, token);
        File.SetUnixFileMode(outputPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }

    private Task<ExternalProcessResult> QpdfAsync(TemporaryPdfFiles files, string[] arguments,
        CancellationToken token, int? stdoutLimit = null)
        => ExternalProcessRunner.RunAsync(ProcessMemoryLimits.CreateRequest(settings.PrlimitPath,
            settings.QpdfPath, arguments, settings.QpdfAddressSpaceLimitBytes, LayerFileLimitBytes + 1,
            new Dictionary<string, string> { ["JPEGMEM"] = settings.QpdfJpegMemory }, stdoutLimit)
            with { WorkingDirectory = files.DirectoryPath }, token);

    private async Task CheckOutputAsync(TemporaryPdfFiles files, string path, int pages, CancellationToken token)
    {
        if ((await QpdfAsync(files, ["--is-encrypted", path], token)).ExitCode != 2 ||
            (await QpdfAsync(files, ["--check", path], token)).ExitCode != 0)
            throw new InvalidOperationException(Failure);
        var count = await QpdfAsync(files, ["--show-npages", path], token, 32);
        if (count.ExitCode != 0 || count.Stdout is null ||
            !int.TryParse(Encoding.ASCII.GetString(count.Stdout).Trim(), NumberStyles.None,
                CultureInfo.InvariantCulture, out var actual) || actual != pages)
            throw new InvalidOperationException(Failure);
    }

    private static void RequireJobPath(TemporaryPdfFiles files, string path)
    {
        if (!Path.IsPathFullyQualified(path) ||
            Path.GetDirectoryName(Path.GetFullPath(path)) != Path.GetFullPath(files.DirectoryPath))
            throw new ArgumentException("描画用pathが不正です。");
    }

    private static async Task WriteLayerAsync(string path, PdfcpuLayer layer, long budget, CancellationToken token)
    {
        if (layer.Pages.Count is < 1 or > 1000 ||
            Enumerable.Range(1, layer.Pages.Count).Any(page => !layer.Pages.ContainsKey(page)))
            throw new ArgumentException("描画ページが不正です。");
        var elements = 0;
        if (layer.PageSizes is { } sizes && (sizes.Count != layer.Pages.Count ||
            sizes.Any(size => !double.IsFinite(size.Width) || !double.IsFinite(size.Height) ||
                size.Width is <= 0 or > 14400 || size.Height is <= 0 or > 14400)))
            throw new ArgumentException("描画寸法が不正です。");
        foreach (var texts in layer.Pages.Values)
        {
            token.ThrowIfCancellationRequested();
            if (texts.Count > MaxTextsPerPage || (elements += texts.Count) > MaxTextElements)
                throw new ArgumentException("描画要素数が不正です。");
        }
        // Check before each flush: no more than budget bytes enter the managed buffer
        // or private file. One text value is also bounded before JSON escaping.
        using var buffer = new LayerBuffer(budget);
        using (var writer = new Utf8JsonWriter(buffer, new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
        {
            void Flush()
            {
                token.ThrowIfCancellationRequested();
                if (writer.BytesCommitted + writer.BytesPending > budget) throw new PdfcpuCapacityException();
                writer.Flush();
            }
            writer.WriteStartObject();
            writer.WriteString("origin", "LowerLeft");
            writer.WriteStartObject("pages");
            foreach (var (page, texts) in layer.Pages)
            {
                token.ThrowIfCancellationRequested();
                writer.WriteStartObject(page.ToString(CultureInfo.InvariantCulture));
                // create lays out against JSON geometry, not the existing blank's
                // MediaBox. Explicit crop supplies arbitrary mixed page dimensions.
                if (layer.PageSizes is { } pageSizes)
                {
                    var size = pageSizes[page - 1];
                    writer.WriteString("crop", string.Create(CultureInfo.InvariantCulture, $"[0 0 {size.Width:R} {size.Height:R}]"));
                }
                writer.WriteStartObject("content");
                writer.WriteStartArray("text");
                foreach (var text in texts)
                {
                    token.ThrowIfCancellationRequested();
                    ValidateText(text, budget);
                    writer.WriteStartObject();
                    writer.WriteString("value", text.Value);
                    if (text.Anchor is not null) writer.WriteString("anchor", text.Anchor);
                    else
                    {
                        writer.WriteStartArray("pos"); writer.WriteNumberValue(text.X); writer.WriteNumberValue(text.Y);
                        writer.WriteEndArray();
                    }
                    writer.WriteNumber("dx", text.Dx); writer.WriteNumber("dy", text.Dy);
                    writer.WriteNumber("rot", text.Rotation);
                    writer.WriteStartObject("font");
                    writer.WriteString("name", JapaneseFont); writer.WriteNumber("size", text.FontSize);
                    writer.WriteString("col", text.Color); writer.WriteEndObject();
                    writer.WriteEndObject();
                    Flush();
                }
                writer.WriteEndArray(); writer.WriteEndObject(); writer.WriteEndObject();
                Flush();
            }
            writer.WriteEndObject(); writer.WriteEndObject();
            Flush();
        }
        buffer.Position = 0;
        await using var file = TemporaryPdfFiles.CreatePrivateFile(path);
        await buffer.CopyToAsync(file, token);
    }

    private static void ValidateText(PdfcpuText text, long budget)
    {
        if (string.IsNullOrEmpty(text.Value) || text.Value.Length > MaxTextLength || text.Value.Contains('%') ||
            text.Value.Count(c => c == '\n') > MaxLineBreaks || text.Value.Count(c => c == '\t') > MaxTabs ||
            text.Value.Any(c => char.IsControl(c) && c is not ('\n' or '\t')) ||
            text.Anchor is not (null or "tl" or "tc" or "tr" or "l" or "c" or "r" or "bl" or "bc" or "br") ||
            (text.Anchor is not null && (text.X != 0 || text.Y != 0)) ||
            !double.IsFinite(text.FontSize) || text.FontSize is < 1 or > 14400 ||
            !double.IsFinite(text.Rotation) || text.Rotation is < -360 or > 360 ||
            new[] { text.X, text.Y, text.Dx, text.Dy }.Any(value => !double.IsFinite(value) || Math.Abs(value) > 14400) ||
            text.Color is null || text.Color.Length != 7 || text.Color[0] != '#' ||
            text.Color.AsSpan(1).ContainsAnyExcept("0123456789abcdefABCDEF"))
            throw new ArgumentException("描画要素が不正です。");
        if (text.Value.Length > budget) throw new PdfcpuCapacityException();
        try
        {
            if (StrictUtf8.GetByteCount(text.Value) > budget) throw new PdfcpuCapacityException();
        }
        catch (EncoderFallbackException) { throw new ArgumentException("描画要素が不正です。"); }
    }

    internal static async Task ValidateStartupAsync(PdfOptions value, CancellationToken token)
    {
        if (!OperatingSystem.IsLinux()) return;
        try
        {
            if (!IsValid(value)) throw new InvalidOperationException();
            foreach (var path in new[] { Path.Combine(value.PdfcpuConfigDir, "pdfcpu", "config.yml"),
                Path.Combine(value.PdfcpuConfigDir, "pdfcpu", "fonts", JapaneseFont + ".gob") })
                using (File.OpenRead(path)) { }
            using var files = new TemporaryPdfFiles(Path.GetFullPath(value.TempRoot));
            var processor = new PdfcpuProcessor(Options.Create(value));
            foreach (var (arguments, limit) in new (string[], int)[] { (["version"], 4096), (["fonts", "list"], 65536) })
            {
                var result = await ExternalProcessRunner.RunAsync(processor.CreateRequest(files, arguments,
                    LayerFileLimitBytes + 1, limit), token);
                if (result.ExitCode != 0 || result.Stdout is null) throw new InvalidOperationException();
                var output = Encoding.UTF8.GetString(result.Stdout);
                if (arguments[0] == "version"
                    ? !output.Split('\n').Any(line => line.Trim() == "version: " + Version)
                    : !output.Split('\n').Any(line => line.Trim().StartsWith(JapaneseFont + " (", StringComparison.Ordinal)))
                    throw new InvalidOperationException();
            }
            var blankJson = Path.Combine(files.DirectoryPath, "pdfcpu-blank.json");
            var blank = Path.Combine(files.DirectoryPath, "pdfcpu-blank.pdf");
            await using (var file = TemporaryPdfFiles.CreatePrivateFile(blankJson))
                await file.WriteAsync(Encoding.UTF8.GetBytes(StartupBlankJson), token);
            if ((await processor.QpdfAsync(files, ["--json-input", blankJson, blank], token)).ExitCode != 0)
                throw new InvalidOperationException();
            if (!File.Exists(blank) || new FileInfo(blank).Length is <= 0 or > LayerFileLimitBytes)
                throw new InvalidOperationException();
            await processor.CreateAsync(files, new(new Dictionary<int, IReadOnlyList<PdfcpuText>>
                { [1] = [new("起動確認", Dy: 12)] }), blank, files.OutputPath,
                LayerFileLimitBytes, LayerFileLimitBytes, token);
        }
        catch (Exception) { throw new PdfcpuStartupException(); }
    }

    internal const string StartupBlankJson = """
        {"qpdf":[{"jsonversion":2,"pdfversion":"1.7"},{"obj:1 0 R":{"value":{"/Type":"/Catalog","/Pages":"2 0 R"}},"obj:2 0 R":{"value":{"/Type":"/Pages","/Count":1,"/Kids":["3 0 R"]}},"obj:3 0 R":{"value":{"/Type":"/Page","/Parent":"2 0 R","/MediaBox":[0,0,595,842],"/Resources":{},"/Contents":"4 0 R"}},"obj:4 0 R":{"stream":{"dict":{},"data":""}},"trailer":{"value":{"/Root":"1 0 R","/Size":5}}}]}
        """;

    private sealed class LayerBuffer(long budget) : MemoryStream
    {
        public override void Write(ReadOnlySpan<byte> bytes)
        {
            if (Position > budget - bytes.Length) throw new PdfcpuCapacityException();
            base.Write(bytes);
        }
        public override void Write(byte[] bytes, int offset, int count)
        {
            if (Position > budget - count) throw new PdfcpuCapacityException();
            base.Write(bytes, offset, count);
        }
    }
}
