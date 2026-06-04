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
// index.html은 캐시하지 않는다 — JS 변경 시 브라우저가 항상 최신 버전을 받도록
app.UseStaticFiles(new StaticFileOptions
{
    OnPrepareResponse = ctx =>
    {
        if (ctx.File.Name.Equals("index.html", StringComparison.OrdinalIgnoreCase))
        {
            ctx.Context.Response.Headers["Cache-Control"] = "no-cache, no-store, must-revalidate";
            ctx.Context.Response.Headers["Pragma"]        = "no-cache";
            ctx.Context.Response.Headers["Expires"]       = "0";
        }
    }
});

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

static bool IsMidiExtension(string extension) =>
    extension is ".mid" or ".midi";

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

static (List<MeasureBeat> Beats, double Bpm, double BeatSizeInQN) ParseMusicXml(Stream stream, string extension)
{
    var parser = new MusicXmlParser();

    if (extension != ".mxl")
    {
        var b = parser.Parse(stream);
        return (b, parser.Bpm, parser.BeatSizeInQN);
    }

    using var archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true);
    var entry = archive.Entries.FirstOrDefault(e =>
        e.FullName.EndsWith(".xml", StringComparison.OrdinalIgnoreCase) &&
        !e.FullName.StartsWith("META-INF/", StringComparison.OrdinalIgnoreCase));

    if (entry is null)
        throw new InvalidDataException("MXL 안에서 MusicXML 파일을 찾지 못했습니다.");

    using var xmlStream = entry.Open();
    var beats = parser.Parse(xmlStream);
    return (beats, parser.Bpm, parser.BeatSizeInQN);
}

static async Task<(List<MeasureBeat> Beats, double Bpm, double BeatSizeInQN, List<string> TempFiles, byte[]? MusicXmlBytes)> ReadUploadedScoreAsync(IFormFile file)
{
    var extension = Path.GetExtension(file.FileName).ToLowerInvariant();
    if (!IsMusicXmlExtension(extension) && extension != ".pdf" && !IsImageExtension(extension) && !IsMidiExtension(extension))
        throw new InvalidDataException("지원 형식: PNG, JPG, PDF, XML, MusicXML, MXL, MIDI");

    var tempFiles = new List<string>();

    if (IsMidiExtension(extension))
    {
        // MIDI: 음높이만 추출해 박자 격자에 양자화 → TabConverter가 운지 재계산.
        // 타브 정보가 없으므로 history 재변환용 musicxml은 저장하지 않는다.
        var ms = new MemoryStream();
        await using (var midiStream = file.OpenReadStream())
            await midiStream.CopyToAsync(ms);
        ms.Position = 0;

        var midiParser = new MidiParser();
        var midiBeats  = midiParser.Parse(ms);
        return (midiBeats, midiParser.Bpm, midiParser.BeatSizeInQN, tempFiles, null);
    }

    if (IsMusicXmlExtension(extension))
    {
        // MXL: zip에서 내부 XML 추출 / XML: 그대로 읽기
        // 바이트로 보관해 history에 .musicxml로 저장할 수 있게 한다.
        byte[] xmlBytes;
        if (extension == ".mxl")
        {
            await using var mxlStream = file.OpenReadStream();
            using var archive = new ZipArchive(mxlStream, ZipArchiveMode.Read, leaveOpen: true);
            var entry = archive.Entries.FirstOrDefault(e =>
                e.FullName.EndsWith(".xml", StringComparison.OrdinalIgnoreCase) &&
                !e.FullName.StartsWith("META-INF/", StringComparison.OrdinalIgnoreCase));
            if (entry is null)
                throw new InvalidDataException("MXL 안에서 MusicXML 파일을 찾지 못했습니다.");
            var ms = new MemoryStream();
            using var xmlStream = entry.Open();
            await xmlStream.CopyToAsync(ms);
            xmlBytes = ms.ToArray();
        }
        else
        {
            var ms = new MemoryStream();
            await using var xmlStream = file.OpenReadStream();
            await xmlStream.CopyToAsync(ms);
            xmlBytes = ms.ToArray();
        }
        var p = new MusicXmlParser();
        var mxBeats = p.Parse(new MemoryStream(xmlBytes));
        return (mxBeats, p.Bpm, p.BeatSizeInQN, tempFiles, xmlBytes);
    }

    var inputPath = await SaveUploadAsync(file, extension);
    tempFiles.Add(inputPath);

    // Cache key = SHA256 of the *upload* bytes (PNG or PDF, before any
    // conversion). Slider changes don't alter musicxml, so they hit cache.
    var hashHex = await ComputeSha256HexAsync(inputPath);
    var cached = CacheLookup(hashHex);
    if (cached is not null)
    {
        // NOTE: cached file is shared across runs — don't add to tempFiles.
        var cachedBytes = await File.ReadAllBytesAsync(cached);
        var (cBeats, cBpm, cBsq) = ParseMusicXml(new MemoryStream(cachedBytes), ".musicxml");
        return (cBeats, cBpm, cBsq, tempFiles, cachedBytes);
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

    var musicXmlBytes = await File.ReadAllBytesAsync(producedPath);
    var (pBeats, pBpm, pBsq) = ParseMusicXml(new MemoryStream(musicXmlBytes), ".musicxml");
    return (pBeats, pBpm, pBsq, tempFiles, musicXmlBytes);
}

static TabConverterSettings ReadSettings(IFormCollection form) => new()
{
    MaxFingerSpan = int.TryParse(form["maxFingerSpan"], out var span) ? span : 5,
    HighFretThreshold = int.TryParse(form["highFretThreshold"], out var high) ? high : 9,
    HandMoveCost = double.TryParse(form["handMoveCost"], NumberStyles.Any, CultureInfo.InvariantCulture, out var move)
        ? move
        : 2.5
};

static List<TabMeasureBeat> ApplyTranspose(List<TabMeasureBeat> beats, int steps)
{
    if (steps == 0) return beats;
    return beats.Select(b => new TabMeasureBeat
    {
        MeasureNumber = b.MeasureNumber,
        BeatNumber    = b.BeatNumber,
        Notes = b.Notes.Select(g => new TabPositionGroup
        {
            IsRest       = g.IsRest,
            DroppedCount = g.DroppedCount,
            SourceGroup  = g.SourceGroup,
            Positions    = g.Positions.Select(p => new TabPosition
            {
                StringIndex  = p.StringIndex,
                Fret         = p.Fret + steps,
                IsUnplayable = p.IsUnplayable || (p.Fret + steps) < 0,
                SourceNote   = p.SourceNote
            }).ToList()
        }).ToList()
    }).ToList();
}

static object BuildConvertResult(string title, List<MeasureBeat> inputBeats, double bpm, double beatSizeInQN, TabConverterSettings settings, List<TabMeasureBeat> tabBeats)
{
    int noteCount    = inputBeats.Sum(b => b.Notes.Sum(ng => ng.Notes.Count));
    int chordCount   = inputBeats.Sum(b => b.Notes.Count(ng => !ng.IsRest && ng.Notes.Count > 1));
    int droppedCount = tabBeats.Sum(b => b.Notes.Sum(g => g.DroppedCount));

    return new
    {
        title,
        bpm,
        beatSizeInQN,
        beatCount    = inputBeats.Count,
        noteCount,
        chordCount,
        droppedCount,
        settings = new { settings.MaxFingerSpan, settings.HighFretThreshold, settings.HandMoveCost },
        beats = tabBeats.Select(b => new
        {
            measureNumber = b.MeasureNumber,
            beatNumber    = b.BeatNumber,
            notes = b.Notes.Select(g => new
            {
                isRest       = g.IsRest,
                droppedCount = g.DroppedCount,
                positions    = g.Positions.Where(p => !p.IsUnplayable)
                    .Select(p => new { stringIndex = p.StringIndex, fret = p.Fret })
                    .ToList()
            }).ToList()
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
        var tabBeats = converter.Convert(parsed.Beats);

        var result = BuildConvertResult(Path.GetFileNameWithoutExtension(file.FileName), parsed.Beats, parsed.Bpm, parsed.BeatSizeInQN, settings, tabBeats);

        // Save to history (non-fatal)
        try
        {
            var hId = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var pngFileName = $"{hId}.png";

            var renderer = new TabImageRenderer();
            var pngBytes = renderer.RenderToPngBytes(tabBeats,
                Path.GetFileNameWithoutExtension(file.FileName));
            await File.WriteAllBytesAsync(Path.Combine(historyPngDir, pngFileName), pngBytes);

            await File.WriteAllTextAsync(
                Path.Combine(historyDataDir, $"{hId}.json"),
                JsonSerializer.Serialize(result, _histSerOpts));

            // MusicXML 저장 — 나중에 재변환할 때 사용
            if (parsed.MusicXmlBytes is not null)
                await File.WriteAllBytesAsync(
                    Path.Combine(historyDataDir, $"{hId}.musicxml"),
                    parsed.MusicXmlBytes);

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
        var tabBeats = converter.Convert(parsed.Beats);
        var transposeSteps = int.TryParse(form["transposeSteps"], out var ts) ? ts : 0;
        var transposedBeats = ApplyTranspose(tabBeats, transposeSteps);
        var renderer = new TabImageRenderer();
        var bytes = renderer.RenderToPngBytes(transposedBeats, Path.GetFileNameWithoutExtension(file.FileName));

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

app.MapDelete("/api/history/{id}", async (long id) =>
{
    await historyWriteLock.WaitAsync();
    try
    {
        // meta 목록에서 제거
        if (File.Exists(historyMetaPath))
        {
            var metaList = new List<HistoryMeta>();
            try { metaList = JsonSerializer.Deserialize<List<HistoryMeta>>(
                    await File.ReadAllTextAsync(historyMetaPath), _histSerOpts) ?? new(); } catch { }
            metaList.RemoveAll(m => m.Id == id);
            await File.WriteAllTextAsync(historyMetaPath,
                JsonSerializer.Serialize(metaList, _histSerOpts));
        }
    }
    finally { historyWriteLock.Release(); }

    // 연관 파일 삭제 (non-fatal)
    foreach (var path in new[]
    {
        Path.Combine(historyDataDir, $"{id}.json"),
        Path.Combine(historyDataDir, $"{id}.musicxml"),
        Path.Combine(historyPngDir,  $"{id}.png"),
    })
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }

    return Results.Ok();
});

app.MapDelete("/api/history", async () =>
{
    await historyWriteLock.WaitAsync();
    try
    {
        if (File.Exists(historyMetaPath))
        {
            List<HistoryMeta> metaList = new();
            try { metaList = JsonSerializer.Deserialize<List<HistoryMeta>>(
                    await File.ReadAllTextAsync(historyMetaPath), _histSerOpts) ?? new(); } catch { }

            // 연관 파일 전체 삭제
            foreach (var m in metaList)
            {
                foreach (var path in new[]
                {
                    Path.Combine(historyDataDir, $"{m.Id}.json"),
                    Path.Combine(historyDataDir, $"{m.Id}.musicxml"),
                    Path.Combine(historyPngDir,  $"{m.Id}.png"),
                })
                {
                    try { if (File.Exists(path)) File.Delete(path); } catch { }
                }
            }

            File.Delete(historyMetaPath);
        }
    }
    finally { historyWriteLock.Release(); }

    return Results.Ok();
});

app.MapPost("/api/history/{id}/reconvert", async (long id, HttpRequest request) =>
{
    var musicXmlPath = Path.Combine(historyDataDir, $"{id}.musicxml");
    if (!File.Exists(musicXmlPath))
        return Results.NotFound("원본 악보 파일을 찾을 수 없습니다. 새로 업로드하여 변환해 주세요.");

    // 기존 결과에서 title 꺼내기
    string? title = null;
    var jsonPath = Path.Combine(historyDataDir, $"{id}.json");
    if (File.Exists(jsonPath))
    {
        try
        {
            var existing = JsonSerializer.Deserialize<JsonElement>(
                await File.ReadAllTextAsync(jsonPath), _histSerOpts);
            if (existing.TryGetProperty("title", out var t))
                title = t.GetString();
        }
        catch { }
    }

    if (!request.HasFormContentType)
        return Results.BadRequest("multipart/form-data 요청이 필요합니다.");

    var form = await request.ReadFormAsync();
    var settings = ReadSettings(form);

    try
    {
        var xmlBytes = await File.ReadAllBytesAsync(musicXmlPath);
        var reconvertParser = new MusicXmlParser();
        var beats = reconvertParser.Parse(new MemoryStream(xmlBytes));
        var converter = new TabConverter(settings);
        var tabBeats = converter.Convert(beats);
        var finalTitle = title ?? id.ToString();
        var result = BuildConvertResult(finalTitle, beats, reconvertParser.Bpm, reconvertParser.BeatSizeInQN, settings, tabBeats);

        // JSON·PNG 덮어쓰기 (non-fatal)
        try
        {
            await File.WriteAllTextAsync(jsonPath,
                JsonSerializer.Serialize(result, _histSerOpts));

            var renderer = new TabImageRenderer();
            var pngBytes = renderer.RenderToPngBytes(tabBeats, finalTitle);
            await File.WriteAllBytesAsync(
                Path.Combine(historyPngDir, $"{id}.png"), pngBytes);
        }
        catch { }

        return Results.Ok(result);
    }
    catch (Exception ex)
    {
        return Results.BadRequest(ex.Message);
    }
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
