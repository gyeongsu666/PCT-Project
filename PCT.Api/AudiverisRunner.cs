using System.Diagnostics;
using System.IO.Compression;
using System.Xml;

/// <summary>
/// Audiveris(OMR)로 악보 이미지를 MusicXML로 변환한다. (oemer 사이드카 대체)
/// Audiveris는 1회성 배치 프로세스이며 .mxl(압축 MusicXML)을 내보내므로,
/// 내부 XML을 풀어 .musicxml 경로에 기록해 기존 파이프라인에 그대로 흘려보낸다.
/// </summary>
public sealed class AudiverisRunner
{
    private static readonly Lazy<AudiverisRunner> _lazy = new(() => new AudiverisRunner());
    public static AudiverisRunner Instance => _lazy.Value;

    private readonly SemaphoreSlim _gate = new(1, 1);   // OMR은 무거워 직렬화
    private AudiverisRunner() { }

    /// <summary>
    /// imagePath를 변환해 outputMusicXmlPath에 MusicXML(비압축)을 쓰고 그 경로를 반환.
    /// oemer ConvertAsync와 동일한 계약.
    /// </summary>
    public async Task<string> ConvertAsync(string imagePath, string outputMusicXmlPath)
    {
        await _gate.WaitAsync();
        try
        {
            var exe = ResolveAudiverisExe();

            // Audiveris 전용 임시 출력 폴더 (.omr/.log/.mxl가 같이 생기므로 격리)
            var outDir = Path.Combine(Path.GetTempPath(), "pct_audiveris", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(outDir);

            try
            {
                await RunBatchAsync(exe, imagePath, outDir);

                // 산출된 .mxl 찾기 (이미지 이름 기반, 없으면 폴더 내 첫 .mxl)
                var baseName = Path.GetFileNameWithoutExtension(imagePath);
                var mxl = Path.Combine(outDir, baseName + ".mxl");
                if (!File.Exists(mxl))
                    mxl = Directory.EnumerateFiles(outDir, "*.mxl", SearchOption.AllDirectories).FirstOrDefault();
                if (mxl is null || !File.Exists(mxl))
                    throw new InvalidOperationException(
                        "Audiveris가 MusicXML(.mxl)을 생성하지 못했습니다. (악보 인식 실패 가능)");

                // .mxl(zip)에서 내부 MusicXML 추출 → outputMusicXmlPath에 기록
                var xmlBytes = ExtractMusicXmlFromMxl(mxl);
                if (File.Exists(outputMusicXmlPath))
                    File.Delete(outputMusicXmlPath);
                await File.WriteAllBytesAsync(outputMusicXmlPath, xmlBytes);
                return outputMusicXmlPath;
            }
            finally
            {
                try { Directory.Delete(outDir, recursive: true); } catch { /* best-effort */ }
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private static async Task RunBatchAsync(string exe, string imagePath, string outDir)
    {
        var psi = new ProcessStartInfo
        {
            FileName = exe,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add("-batch");
        psi.ArgumentList.Add("-export");
        psi.ArgumentList.Add("-output");
        psi.ArgumentList.Add(outDir);
        psi.ArgumentList.Add(imagePath);

        Process proc;
        try
        {
            proc = Process.Start(psi)
                ?? throw new InvalidOperationException("Audiveris 프로세스를 시작하지 못했습니다.");
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            throw new InvalidOperationException(
                $"Audiveris 실행 파일을 찾지 못했습니다 ('{exe}'). " +
                $"환경변수 PCT_AUDIVERIS에 Audiveris.exe 절대 경로를 지정하세요. ({ex.Message})");
        }

        // 로그를 콘솔로 흘려보냄 (개발 중 진행상황 가시성)
        _ = Task.Run(async () =>
        {
            try { string? l; while ((l = await proc.StandardError.ReadLineAsync()) != null) Console.Error.WriteLine("[audiveris] " + l); }
            catch { /* 프로세스 종료 */ }
        });
        _ = Task.Run(async () =>
        {
            try { string? l; while ((l = await proc.StandardOutput.ReadLineAsync()) != null) Console.WriteLine("[audiveris] " + l); }
            catch { /* 프로세스 종료 */ }
        });

        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        try
        {
            await proc.WaitForExitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            try { proc.Kill(entireProcessTree: true); } catch { }
            throw new InvalidOperationException("Audiveris 변환 시간이 초과되었습니다 (5분).");
        }

        if (proc.ExitCode != 0)
            throw new InvalidOperationException($"Audiveris가 비정상 종료했습니다 (exit {proc.ExitCode}).");
    }

    /// <summary>.mxl(zip)에서 실제 MusicXML 파트를 추출. META-INF/container.xml의 rootfile을 우선 사용.</summary>
    private static byte[] ExtractMusicXmlFromMxl(string mxlPath)
    {
        using var zip = ZipFile.OpenRead(mxlPath);

        // container.xml에서 rootfile(full-path) 찾기
        string? rootPath = null;
        var container = zip.GetEntry("META-INF/container.xml");
        if (container is not null)
        {
            using var cs = container.Open();
            using var reader = XmlReader.Create(cs, new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore });
            while (reader.Read())
            {
                if (reader.NodeType == XmlNodeType.Element && reader.LocalName == "rootfile")
                {
                    rootPath = reader.GetAttribute("full-path");
                    if (!string.IsNullOrEmpty(rootPath)) break;
                }
            }
        }

        ZipArchiveEntry? entry = null;
        if (!string.IsNullOrEmpty(rootPath))
            entry = zip.GetEntry(rootPath);
        // fallback: META-INF 밖의 첫 .xml
        entry ??= zip.Entries.FirstOrDefault(e =>
            e.FullName.EndsWith(".xml", StringComparison.OrdinalIgnoreCase) &&
            !e.FullName.StartsWith("META-INF/", StringComparison.OrdinalIgnoreCase));

        if (entry is null)
            throw new InvalidOperationException("Audiveris .mxl 안에서 MusicXML을 찾지 못했습니다.");

        using var es = entry.Open();
        using var ms = new MemoryStream();
        es.CopyTo(ms);
        return ms.ToArray();
    }

    /// <summary>Audiveris.exe 경로 해석: 환경변수 → 흔한 설치 경로 → PATH.</summary>
    private static string ResolveAudiverisExe()
    {
        var env = Environment.GetEnvironmentVariable("PCT_AUDIVERIS");
        if (!string.IsNullOrWhiteSpace(env) && File.Exists(env))
            return env;

        string[] candidates =
        {
            @"C:\Program Files\Audiveris\Audiveris.exe",
            @"C:\Program Files (x86)\Audiveris\Audiveris.exe",
        };
        foreach (var c in candidates)
            if (File.Exists(c)) return c;

        return "Audiveris"; // PATH에 등록돼 있으면 사용
    }
}
