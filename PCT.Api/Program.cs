using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using ImageMagick;
using PCT.Core;


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

// 저해상도 이미지를 OMR 전에 업스케일한다. Audiveris는 ~300DPI(오선 간격이 충분)가 필요해
// 작은 이미지는 오선조차 인식 못 하므로, 긴 변이 기준 미만이면 키운다.
// (없는 디테일을 만들진 못하지만 '결과 없음'보다 낫고, 깔끔한 이미지는 거의 손실 없음)
static string UpscaleIfSmall(string imagePath, List<string> tempFiles)
{
    const uint minLongSide    = 2000;   // 긴 변이 이보다 작으면 업스케일
    const uint targetLongSide = 2400;

    try
    {
        using var image = new MagickImage(imagePath);
        uint longSide = Math.Max(image.Width, image.Height);
        if (longSide >= minLongSide)
            return imagePath;   // 이미 충분히 큼 → 그대로

        double scale = (double)targetLongSide / longSide;
        image.FilterType = FilterType.Lanczos;
        image.Resize((uint)Math.Round(image.Width * scale), (uint)Math.Round(image.Height * scale));
        image.Format = MagickFormat.Png;

        var upPath = Path.Combine(
            Path.GetDirectoryName(imagePath) ?? Path.GetTempPath(),
            Path.GetFileNameWithoutExtension(imagePath) + "_up.png");
        image.Write(upPath);
        tempFiles.Add(upPath);
        return upPath;
    }
    catch
    {
        // 업스케일 실패는 치명적이지 않다 — 원본 경로로 진행 (Audiveris가 판단)
        return imagePath;
    }
}

// === Result cache ============================================================
// Same source bytes -> same musicxml. Saves us from re-running OMR(Audiveris)
// when the user just tweaks the slider and re-converts the same PNG.

static string CacheDirectory()
{
    // 엔진별 캐시 분리: oemer 시절 캐시(pct_oemer_cache)를 버리고 Audiveris 전용 폴더 사용.
    // (해시가 같아도 엔진이 다르면 결과가 달라, stale 캐시를 재사용하면 안 됨)
    var dir = Path.Combine(Path.GetTempPath(), "pct_audiveris_cache");
    Directory.CreateDirectory(dir);
    return dir;
}

// 결과 캐시 전체 비우기. 변환 기록을 지울 때 함께 호출해, 같은 이미지를 다시 올리면
// 캐시된 옛 결과 대신 새로 변환되도록 한다.
static void ClearResultCache()
{
    try
    {
        foreach (var f in Directory.EnumerateFiles(CacheDirectory(), "*.musicxml"))
        {
            try { File.Delete(f); } catch { /* 개별 삭제 실패는 무시 */ }
        }
    }
    catch { /* best-effort */ }
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

    // 저해상도 이미지는 OMR 전에 업스케일 (Audiveris가 오선을 인식하도록)
    imagePath = UpscaleIfSmall(imagePath, tempFiles);

    // Audiveris가 .musicxml을 쓸 알려진 경로를 정한다.
    var workingDir = Path.GetDirectoryName(imagePath) ?? Path.GetTempPath();
    var musicXmlPath = Path.Combine(workingDir, Path.GetFileNameWithoutExtension(imagePath) + ".musicxml");
    if (File.Exists(musicXmlPath))
        File.Delete(musicXmlPath);

    var producedPath = await AudiverisRunner.Instance.ConvertAsync(imagePath, musicXmlPath);
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
        RepeatStart   = b.RepeatStart,
        RepeatEnd     = b.RepeatEnd,
        TimeSigNum    = b.TimeSigNum,
        TimeSigDen    = b.TimeSigDen,
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

// 옥타브 자동 보정: 변환된 프렛이 비정상적으로 높으면(예: 기타 treble-8 음자리표를
// Audiveris가 한 옥타브 높게 읽은 경우) 음을 한 옥타브 내려 다시 변환한다.
// 정상 악보(피아노·MIDI 등, 평균 프렛 낮음)는 건드리지 않으며, 내렸을 때
// 오히려 연주불가가 크게 늘면 원복한다 (근거가 있을 때만 보정).
static List<TabMeasureBeat> ConvertWithOctaveFix(List<MeasureBeat> beats, TabConverterSettings settings)
{
    var converter = new TabConverter(settings);
    var tab = converter.Convert(beats);

    static double AvgFret(List<TabMeasureBeat> t)
    {
        var frets = t.SelectMany(b => b.Notes).SelectMany(g => g.Positions)
                     .Where(p => !p.IsUnplayable).Select(p => (double)p.Fret).ToList();
        return frets.Count == 0 ? 0 : frets.Average();
    }
    static int Lost(List<TabMeasureBeat> t) =>
        t.Sum(b => b.Notes.Sum(g => g.DroppedCount + g.Positions.Count(p => p.IsUnplayable)));

    if (AvgFret(tab) > 7.0)   // 프렛이 비정상적으로 높을 때만 시도
    {
        foreach (var mb in beats)
            foreach (var ng in mb.Notes)
                foreach (var n in ng.Notes)
                    if (!n.IsRest) n.Octave -= 1;

        var tab2 = converter.Convert(beats);
        int totalNotes = beats.Sum(b => b.Notes.Count(ng => !ng.IsRest));
        int allowExtraLoss = Math.Max(2, totalNotes / 20);

        // 충분히 낮아지고 손실이 크게 늘지 않으면 보정본 채택
        if (AvgFret(tab2) + 1.5 < AvgFret(tab) && Lost(tab2) <= Lost(tab) + allowExtraLoss)
            return tab2;

        // 아니면 원복
        foreach (var mb in beats)
            foreach (var ng in mb.Notes)
                foreach (var n in ng.Notes)
                    if (!n.IsRest) n.Octave += 1;
    }
    return tab;
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
            repeatStart   = b.RepeatStart,
            repeatEnd     = b.RepeatEnd,
            timeSigNum    = b.TimeSigNum,
            timeSigDen    = b.TimeSigDen,
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
        var tabBeats = ConvertWithOctaveFix(parsed.Beats, settings);

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
        var tabBeats = ConvertWithOctaveFix(parsed.Beats, settings);
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

// 프론트에서 직접 편집한 beats JSON → PNG 재생성
app.MapPost("/api/render-tab-beats-png", async (HttpRequest request) =>
{
    try
    {
        using var sr = new StreamReader(request.Body);
        var json = await sr.ReadToEndAsync();
        var opts = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        var payload = JsonSerializer.Deserialize<RenderBeatsPayload>(json, opts);

        if (payload?.Beats == null || payload.Beats.Count == 0)
            return Results.BadRequest("beats가 없습니다.");

        var tabBeats = payload.Beats.Select(b => new TabMeasureBeat
        {
            MeasureNumber = b.MeasureNumber,
            BeatNumber    = b.BeatNumber,
            RepeatStart   = b.RepeatStart,
            RepeatEnd     = b.RepeatEnd,
            TimeSigNum    = b.TimeSigNum,
            TimeSigDen    = b.TimeSigDen,
            Notes = (b.Notes ?? []).Select(n => new TabPositionGroup
            {
                IsRest       = n.IsRest,
                DroppedCount = n.DroppedCount,
                Positions    = (n.Positions ?? []).Select(p => new TabPosition
                {
                    StringIndex = p.StringIndex,
                    Fret        = p.Fret,
                }).ToList()
            }).ToList()
        }).ToList();

        var renderer = new TabImageRenderer();
        var bytes    = renderer.RenderToPngBytes(tabBeats, payload.Title ?? "tab");
        var fileName = $"{payload.Title ?? "tab"}-tab.png";
        return Results.File(bytes, "image/png", fileName);
    }
    catch (Exception ex)
    {
        return Results.BadRequest(ex.Message);
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

    ClearResultCache();   // 기록을 비우면 결과 캐시도 함께 비운다
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
        var tabBeats = ConvertWithOctaveFix(beats, settings);
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

// /api/render-tab-beats-png 용 DTO
public class RenderBeatsPayload
{
    public string? Title { get; set; }
    public List<BeatDto> Beats { get; set; } = [];
}
public class BeatDto
{
    public int MeasureNumber { get; set; }
    public int BeatNumber    { get; set; }
    public bool RepeatStart  { get; set; }
    public bool RepeatEnd    { get; set; }
    public int TimeSigNum    { get; set; }
    public int TimeSigDen    { get; set; }
    public List<NoteDto> Notes { get; set; } = [];
}
public class NoteDto
{
    public bool IsRest       { get; set; }
    public int  DroppedCount { get; set; }
    public List<PositionDto> Positions { get; set; } = [];
}
public class PositionDto
{
    public int StringIndex { get; set; }
    public int Fret        { get; set; }
}
