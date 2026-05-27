using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using ImageMagick;
using PCT.Core;

//테스트용 주석

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddCors(o => o.AddDefaultPolicy(p => p.AllowAnyOrigin().AllowAnyMethod().AllowAnyHeader()));

var app = builder.Build();
app.UseCors();
app.UseDefaultFiles();
app.UseStaticFiles();

// Stop the sidecar Python process cleanly when the web server shuts down.
app.Lifetime.ApplicationStopping.Register(() => OemerSidecar.Instance.Dispose());

// === History ==============================================================
var wwwRoot = app.Environment.WebRootPath
    ?? Path.Combine(app.Environment.ContentRootPath, "wwwroot");
var historyPngDir  = Path.Combine(wwwRoot, "history");
var historyDataDir = Path.Combine(app.Environment.ContentRootPath, "history-data");
var historyMetaPath = Path.Combine(app.Environment.ContentRootPath, "history-meta.json");
Directory.CreateDirectory(historyPngDir);
Directory.CreateDirectory(historyDataDir);
var historyWriteLock = new SemaphoreSlim(1, 1);
var _histSerOpts = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

static bool IsImageExtension(string extension) =>
    extension is ".png" or ".jpg" or ".jpeg" or ".bmp" or ".tif" or ".tiff" or ".webp";

static bool IsMusicXmlExtension(string extension) =>
    extension is ".xml" or ".musicxml" or ".mxl";

static async Task<string> SaveUploadAsync(IFormFile file, string extension)
{
    var path = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}{extension}");
    await using var stream = new FileStream(path, FileMode.CreateNew);
    await file.CopyToAsync(stream);
    return path;
}

static string ConvertPdfFirstPageToPng(string pdfPath)
{
    var pngPath = Path.ChangeExtension(pdfPath, ".png");
    var settings = new MagickReadSettings { Density = new Density(200) };

    using var images = new MagickImageCollection();
    images.Read(pdfPath, settings);
    if (images.Count == 0)
        throw new InvalidOperationException("PDF에서 페이지를 찾을 수 없습니다.");

    images[0].Format = MagickFormat.Png;
    images[0].Write(pngPath);
    return pngPath;
}

// === Result cache ============================================================
// Same source bytes -> same musicxml. Saves us from re-running oemer when the
// user just tweaks the slider and re-converts the same PNG.

static string CacheDirectory()
{
    var dir = Path.Combine(Path.GetTempPath(), "pct_oemer_cache");
    Directory.CreateDirectory(dir);
    return dir;
}

static async Task<string> ComputeSha256HexAsync(string filePath)
{
    await using var stream = File.OpenRead(filePath);
    using var sha = SHA256.Create();
    var hash = await sha.ComputeHashAsync(stream);
    return Convert.ToHexString(hash).ToLowerInvariant();
}

static string CacheLookup(string hashHex)
{
    var path = Path.Combine(CacheDirectory(), hashHex + ".musicxml");
    return File.Exists(path) ? path : null;
}

static void CacheStore(string hashHex, string musicXmlPath)
{
    try
    {
        var dest = Path.Combine(CacheDirectory(), hashHex + ".musicxml");
        File.Copy(musicXmlPath, dest, overwrite: true);
    }
    catch
    {
        // Cache write failures are non-fatal: the response is already correct.
    }
}

// === Pipeline ================================================================

static List<NoteGroup> ParseMusicXml(Stream stream, string extension)
{
    var parser = new MusicXmlParser();

    if (extension != ".mxl")
        return parser.Parse(stream);

    using var archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true);
    var entry = archive.Entries.FirstOrDefault(e =>
        e.FullName.EndsWith(".xml", StringComparison.OrdinalIgnoreCase) &&
        !e.FullName.StartsWith("META-INF/", StringComparison.OrdinalIgnoreCase));

    if (entry is null)
        throw new InvalidDataException("MXL 안에서 MusicXML 파일을 찾지 못했습니다.");

    using var xmlStream = entry.Open();
    return parser.Parse(xmlStream);
}

static async Task<(List<NoteGroup> Groups, List<string> TempFiles)> ReadUploadedScoreAsync(IFormFile file)
{
    var extension = Path.GetExtension(file.FileName).ToLowerInvariant();
    if (!IsMusicXmlExtension(extension) && extension != ".pdf" && !IsImageExtension(extension))
        throw new InvalidDataException("지원 형식: PNG, JPG, PDF, XML, MusicXML, MXL");

    var tempFiles = new List<string>();

    if (IsMusicXmlExtension(extension))
    {
        using var stream = file.OpenReadStream();
        return (ParseMusicXml(stream, extension), tempFiles);
    }

    var inputPath = await SaveUploadAsync(file, extension);
    tempFiles.Add(inputPath);

    // Cache key = SHA256 of the *upload* bytes (PNG or PDF, before any
    // conversion). Slider changes don't alter musicxml, so they hit cache.
    var hashHex = await ComputeSha256HexAsync(inputPath);
    var cached = CacheLookup(hashHex);
    if (cached is not null)
    {
        await using var cachedStream = File.OpenRead(cached);
        // NOTE: cached file is shared across runs — don't add to tempFiles.
        return (ParseMusicXml(cachedStream, ".musicxml"), tempFiles);
    }

    var imagePath = inputPath;
    if (extension == ".pdf")
    {
        imagePath = ConvertPdfFirstPageToPng(inputPath);
        tempFiles.Add(imagePath);
    }

    // Ask the sidecar to write directly to a known path (no cwd dance needed).
    var workingDir = Path.GetDirectoryName(imagePath) ?? Path.GetTempPath();
    var musicXmlPath = Path.Combine(workingDir, Path.GetFileNameWithoutExtension(imagePath) + ".musicxml");
    if (File.Exists(musicXmlPath))
        File.Delete(musicXmlPath);

    var producedPath = await OemerSidecar.Instance.ConvertAsync(imagePath, musicXmlPath);
    tempFiles.Add(producedPath);

    CacheStore(hashHex, producedPath);

    await using var producedStream = File.OpenRead(producedPath);
    return (ParseMusicXml(producedStream, ".musicxml"), tempFiles);
}

static TabConverterSettings ReadSettings(IFormCollection form) => new()
{
    MaxFingerSpan = int.TryParse(form["maxFingerSpan"], out var span) ? span : 5,
    HighFretThreshold = int.TryParse(form["highFretThreshold"], out var high) ? high : 9,
    HandMoveCost = double.TryParse(form["handMoveCost"], NumberStyles.Any, CultureInfo.InvariantCulture, out var move)
        ? move
        : 2.5
};

static List<TabPositionGroup> ApplyTranspose(List<TabPositionGroup> groups, int steps)
{
    if (steps == 0) return groups;
    return groups.Select(g => new TabPositionGroup
    {
        IsRest = g.IsRest,
        Positions = g.Positions.Select(p => new TabPosition
        {
            StringIndex = p.StringIndex,
            Fret = p.Fret + steps,
            IsUnplayable = p.IsUnplayable || (p.Fret + steps) < 0,
            SourceNote = p.SourceNote
        }).ToList()
    }).ToList();
}

static object BuildConvertResult(IFormFile file, List<NoteGroup> groups, TabConverterSettings settings, List<TabPositionGroup> tabGroups)
{
    var chordCount = groups.Count(g => !g.IsRest && g.Notes.Count > 1);
    var droppedCount = tabGroups.Sum(g => g.DroppedCount);

    return new
    {
        title = Path.GetFileNameWithoutExtension(file.FileName),
        beatCount = groups.Count,
        noteCount = groups.Sum(g => g.Notes.Count),
        chordCount,
        droppedCount,
        settings = new { settings.MaxFingerSpan, settings.HighFretThreshold, settings.HandMoveCost },
        groups = tabGroups.Select(g => new
        {
            isRest = g.IsRest,
            droppedCount = g.DroppedCount,
            positions = g.Positions.Where(p => !p.IsUnplayable)
                .Select(p => new { stringIndex = p.StringIndex, fret = p.Fret })
                .ToList()
        }).ToList()
    };
}

static void DeleteTempFiles(IEnumerable<string> paths)
{
    foreach (var path in paths.Distinct())
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch
        {
            // Temp cleanup is best-effort.
        }
    }
}

app.MapPost("/api/convert", async (HttpRequest request) =>
{
    if (!request.HasFormContentType)
        return Results.BadRequest("multipart/form-data 요청이 필요합니다.");

    var form = await request.ReadFormAsync();
    var file = form.Files.GetFile("file");
    if (file is null || file.Length == 0)
        return Results.BadRequest("파일이 없습니다.");

    List<string> tempFiles = new();

    try
    {
        var parsed = await ReadUploadedScoreAsync(file);
        tempFiles = parsed.TempFiles;

        var settings = ReadSettings(form);
        var converter = new TabConverter(settings);
        var tabGroups = converter.Convert(parsed.Groups);

        var result = BuildConvertResult(file, parsed.Groups, settings, tabGroups);

        // Save to history (non-fatal)
        try
        {
            var hId = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var pngFileName = $"{hId}.png";

            var renderer = new TabImageRenderer();
            var pngBytes = renderer.RenderToPngBytes(tabGroups,
                Path.GetFileNameWithoutExtension(file.FileName));
            await File.WriteAllBytesAsync(Path.Combine(historyPngDir, pngFileName), pngBytes);

            await File.WriteAllTextAsync(
                Path.Combine(historyDataDir, $"{hId}.json"),
                JsonSerializer.Serialize(result, _histSerOpts));

            await historyWriteLock.WaitAsync();
            try
            {
                var metaList = new List<HistoryMeta>();
                if (File.Exists(historyMetaPath))
                {
                    try
                    {
                        metaList = JsonSerializer.Deserialize<List<HistoryMeta>>(
                            await File.ReadAllTextAsync(historyMetaPath),
                            _histSerOpts) ?? new();
                    }
                    catch { }
                }
                metaList.Insert(0, new HistoryMeta(
                    hId, file.FileName, DateTime.UtcNow.ToString("o"),
                    0, $"/history/{pngFileName}"));
                if (metaList.Count > 100) metaList = metaList.Take(100).ToList();
                await File.WriteAllTextAsync(historyMetaPath,
                    JsonSerializer.Serialize(metaList, _histSerOpts));
            }
            finally { historyWriteLock.Release(); }
        }
        catch { /* history save is non-fatal */ }

        return Results.Ok(result);
    }
    catch (Exception ex)
    {
        return Results.BadRequest(ex.Message);
    }
    finally
    {
        DeleteTempFiles(tempFiles);
    }
});

app.MapPost("/api/render-png", async (HttpRequest request) =>
{
    if (!request.HasFormContentType)
        return Results.BadRequest("multipart/form-data 요청이 필요합니다.");

    var form = await request.ReadFormAsync();
    var file = form.Files.GetFile("file");
    if (file is null || file.Length == 0)
        return Results.BadRequest("파일이 없습니다.");

    List<string> tempFiles = new();

    try
    {
        var parsed = await ReadUploadedScoreAsync(file);
        tempFiles = parsed.TempFiles;

        var settings = ReadSettings(form);
        var converter = new TabConverter(settings);
        var tabGroups = converter.Convert(parsed.Groups);
        var transposeSteps = int.TryParse(form["transposeSteps"], out var ts) ? ts : 0;
        var transposedGroups = ApplyTranspose(tabGroups, transposeSteps);
        var renderer = new TabImageRenderer();
        var bytes = renderer.RenderToPngBytes(transposedGroups, Path.GetFileNameWithoutExtension(file.FileName));

        return Results.File(bytes, "image/png", $"{Path.GetFileNameWithoutExtension(file.FileName)}-tab.png");
    }
    catch (Exception ex)
    {
        return Results.BadRequest(ex.Message);
    }
    finally
    {
        DeleteTempFiles(tempFiles);
    }
});

app.MapGet("/api/history", async () =>
{
    if (!File.Exists(historyMetaPath))
        return Results.Ok(Array.Empty<object>());
    try
    {
        var json = await File.ReadAllTextAsync(historyMetaPath);
        return Results.Content(json, "application/json");
    }
    catch { return Results.Ok(Array.Empty<object>()); }
});

app.MapGet("/api/history/{id}", async (long id) =>
{
    var jsonPath = Path.Combine(historyDataDir, $"{id}.json");
    if (!File.Exists(jsonPath))
        return Results.NotFound("기록을 찾을 수 없습니다.");
    var json = await File.ReadAllTextAsync(jsonPath);
    return Results.Content(json, "application/json");
});

app.Run();


public record HistoryMeta(long Id, string FileName, string ConvertedAt, int TransposeSteps, string PngUrl);

// === Sidecar singleton =======================================================
// Keeps a single Python process running so we don't pay the 15-30s
// "import oemer + load TF + load 5 model checkpoints" cost on every conversion.
// Communicates over stdin/stdout with a tab-delimited line protocol — see
// oemer_sidecar.py for the wire format.

public sealed class OemerSidecar : IDisposable
{
    private static readonly Lazy<OemerSidecar> _lazy = new(() => new OemerSidecar());
    public static OemerSidecar Instance => _lazy.Value;

    private readonly SemaphoreSlim _gate = new(1, 1);
    private Process _process;
    private bool _ready;
    private bool _disposed;

    private OemerSidecar() { }

    public async Task<string> ConvertAsync(string imagePath, string outputPath)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(OemerSidecar));

        await _gate.WaitAsync();
        try
        {
            await EnsureStartedAsync();

            // Drop the previous output if it exists so we never read stale data
            // when the sidecar reports success but didn't actually write.
            if (File.Exists(outputPath))
                File.Delete(outputPath);

            var line = imagePath + "\t" + outputPath;
            await _process.StandardInput.WriteLineAsync(line);
            await _process.StandardInput.FlushAsync();

            var response = await _process.StandardOutput.ReadLineAsync();
            if (response is null)
            {
                // Process died mid-conversation. Tear down so the next request
                // re-spawns it instead of hanging forever.
                ResetProcess();
                throw new InvalidOperationException(
                    "Oemer 사이드카가 응답 전에 종료되었습니다. (stderr를 확인하세요)");
            }

            var parts = response.Split('\t', 2);
            if (parts.Length == 2 && parts[0] == "OK")
                return parts[1];
            if (parts.Length == 2 && parts[0] == "ERR")
                throw new InvalidOperationException("Oemer 변환 실패: " + parts[1]);

            throw new InvalidOperationException("Oemer 사이드카에서 알 수 없는 응답: " + response);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task EnsureStartedAsync()
    {
        if (_process != null && !_process.HasExited && _ready)
            return;

        ResetProcess();

        var pythonExe = ResolvePythonExe();

        var scriptPath = ResolveSidecarScript();

        var psi = new ProcessStartInfo
        {
            FileName = pythonExe,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(scriptPath) ?? Environment.CurrentDirectory,
        };
        psi.ArgumentList.Add("-u"); // unbuffered stdio so READY arrives immediately
        psi.ArgumentList.Add(scriptPath);
        psi.EnvironmentVariables["PYTHONIOENCODING"] = "utf-8";
        // CUDA_VISIBLE_DEVICES is set inside the script too, but belt+suspenders.
        psi.EnvironmentVariables["CUDA_VISIBLE_DEVICES"] = "-1";
        psi.EnvironmentVariables["MPLCONFIGDIR"] = Path.Combine(Path.GetTempPath(), "pct_matplotlib");

        try
        {
            _process = Process.Start(psi)
                ?? throw new InvalidOperationException("Oemer 사이드카 프로세스를 시작하지 못했습니다.");
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            throw new InvalidOperationException(
                $"Python 실행 파일을 찾지 못했습니다 ('{pythonExe}'). " +
                $"PATH에 python이 등록되어 있는지 확인하거나 환경변수 PCT_PYTHON에 절대 경로를 지정하세요. ({ex.Message})");
        }

        // Forward stderr to the API console so oemer/TF logs are visible during dev.
        _ = Task.Run(async () =>
        {
            try
            {
                string errLine;
                while ((errLine = await _process.StandardError.ReadLineAsync()) != null)
                    Console.Error.WriteLine(errLine);
            }
            catch { /* process exited */ }
        });

        // Wait for the READY line. This is where the heavy "import oemer" cost is paid.
        var startupTimeout = Task.Delay(TimeSpan.FromMinutes(3));
        var firstLineTask = _process.StandardOutput.ReadLineAsync();
        var winner = await Task.WhenAny(firstLineTask, startupTimeout);
        if (winner == startupTimeout)
        {
            ResetProcess();
            throw new InvalidOperationException("Oemer 사이드카 기동 시간이 초과되었습니다 (3분).");
        }

        var firstLine = await firstLineTask;
        if (firstLine is null)
        {
            ResetProcess();
            throw new InvalidOperationException(
                "Oemer 사이드카가 READY 신호 전에 종료되었습니다. (stderr를 확인하세요)");
        }

        if (firstLine.StartsWith("ERR\t", StringComparison.Ordinal))
        {
            var msg = firstLine.Substring(4);
            ResetProcess();
            throw new InvalidOperationException("Oemer 사이드카 기동 실패: " + msg);
        }

        if (firstLine != "READY")
        {
            ResetProcess();
            throw new InvalidOperationException(
                "Oemer 사이드카가 예상치 못한 응답으로 시작했습니다: " + firstLine);
        }

        _ready = true;
        Console.WriteLine("[oemer-sidecar] ready (model preloaded)");
    }

    private static string ResolveSidecarScript()
    {
        const string fileName = "oemer_sidecar.py";

        // 1) Next to the API binary (works for `dotnet run` and published builds).
        var beside = Path.Combine(AppContext.BaseDirectory, fileName);
        if (File.Exists(beside)) return beside;

        // 2) Project source folder (works when AppContext.BaseDirectory points
        //    at bin/Debug/... and the .py file isn't copied yet).
        var projectGuess = Path.GetFullPath(
            Path.Combine(AppContext.BaseDirectory, "..", "..", "..", fileName));
        if (File.Exists(projectGuess)) return projectGuess;

        // 3) Current directory fallback.
        var cwdGuess = Path.Combine(Environment.CurrentDirectory, fileName);
        if (File.Exists(cwdGuess)) return cwdGuess;

        throw new FileNotFoundException(
            $"oemer_sidecar.py를 찾지 못했습니다. (검색 경로: '{beside}', '{projectGuess}', '{cwdGuess}')");
    }

    private static string ResolvePythonExe()
    {
        var configured = Environment.GetEnvironmentVariable("PCT_PYTHON");
        if (!string.IsNullOrWhiteSpace(configured))
            return configured;

        const string localPython = @".venv\Scripts\python.exe";
        var candidates = new[]
        {
            Path.Combine(Environment.CurrentDirectory, localPython),
            Path.GetFullPath(Path.Combine(Environment.CurrentDirectory, "..", localPython)),
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", localPython)),
        };

        foreach (var candidate in candidates)
        {
            if (File.Exists(candidate))
                return candidate;
        }

        return "python";
    }

    private void ResetProcess()
    {
        _ready = false;
        try
        {
            if (_process != null && !_process.HasExited)
            {
                try { _process.StandardInput.Close(); } catch { }
                if (!_process.WaitForExit(1500))
                    _process.Kill(entireProcessTree: true);
            }
        }
        catch { }
        try { _process?.Dispose(); } catch { }
        _process = null;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        ResetProcess();
        _gate.Dispose();
    }
}
