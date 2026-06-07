using SkiaSharp;

namespace PCT.Core;

public class TabImageRenderer
{
    private const int CanvasWidth      = 1200;
    private const int MarginLeft       = 50;
    private const int MarginTop        = 100;
    private const int MarginBottom     = 50;
    private const int StringLabelWidth = 30;
    private const int SystemPadding    = 15;
    private const int StringSpacing    = 20;
    private const int NoteSpacing      = 36;
    private const int SystemSpacing    = 80;
    private const int NotesPerSystem   = 24;
    private const int BeatRowHeight    = 22;
    private const float StringLineWidth = 1.2f;
    private const float BarLineWidth    = 1.8f;
    private const int   RhythmGap       = 7;     // 스태프 아래 리듬 표기까지 여백
    private const float StemLength      = 14f;   // 리듬 기둥 길이

    // 렌더링을 위해 flat하게 펼친 음표 단위
    private record RenderNote(
        TabPositionGroup Group,
        int  BeatNumber,
        bool IsBeatStart,    // 박의 첫 번째 음표인가
        bool IsMeasureStart, // 마디의 첫 번째 음표인가
        bool RepeatStart,    // 이 음표의 마디가 시작 도돌이표(‖:)를 가짐
        bool RepeatEnd,      // 이 음표의 마디가 끝 도돌이표(:‖)를 가짐
        int  TimeSigNum,     // 이 마디에 표시할 박자표 분자(>0이면 표시)
        int  TimeSigDen
    );

    // ── 공개 API ──────────────────────────────────────────────────────────────

    public byte[] RenderToPngBytes(List<TabMeasureBeat> beats, string title)
    {
        var notes = Flatten(beats);

        int count       = Math.Max(1, notes.Count);
        int numSystems  = (int)Math.Ceiling((double)count / NotesPerSystem);
        int systemHeight = BeatRowHeight + 5 * StringSpacing;
        int totalHeight  = MarginTop + numSystems * (systemHeight + SystemSpacing) + MarginBottom;

        var info = new SKImageInfo(CanvasWidth, totalHeight);
        using var surface = SKSurface.Create(info);
        var canvas = surface.Canvas;
        canvas.Clear(SKColors.White);

        DrawHeader(canvas, title);

        for (int sysIdx = 0; sysIdx < numSystems; sysIdx++)
        {
            int startNote = sysIdx * NotesPerSystem;
            int endNote   = Math.Min(startNote + NotesPerSystem, notes.Count);
            float systemY = MarginTop + sysIdx * (systemHeight + SystemSpacing);
            DrawSystem(canvas, notes, startNote, endNote, systemY);
        }

        using var image = surface.Snapshot();
        using var data  = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    public void RenderToPng(List<TabMeasureBeat> beats, string title, string outputPath)
    {
        File.WriteAllBytes(outputPath, RenderToPngBytes(beats, title));
    }

    // ── 내부 헬퍼 ─────────────────────────────────────────────────────────────

    private static List<RenderNote> Flatten(List<TabMeasureBeat> beats)
    {
        var result = new List<RenderNote>();
        int prevMeasure = -1;

        foreach (var beat in beats)
        {
            bool isNewMeasure = beat.MeasureNumber != prevMeasure;
            prevMeasure = beat.MeasureNumber;

            for (int i = 0; i < beat.Notes.Count; i++)
            {
                result.Add(new RenderNote(
                    Group:          beat.Notes[i],
                    BeatNumber:     beat.BeatNumber,
                    IsBeatStart:    i == 0,
                    IsMeasureStart: isNewMeasure && i == 0,
                    RepeatStart:    beat.RepeatStart,
                    RepeatEnd:      beat.RepeatEnd,
                    TimeSigNum:     beat.TimeSigNum,
                    TimeSigDen:     beat.TimeSigDen
                ));
                isNewMeasure = false;
            }
        }
        return result;
    }

    private void DrawHeader(SKCanvas canvas, string title)
    {
        using var titlePaint = CreateTextPaint(SKColors.Black, 28, bold: true);
        titlePaint.TextAlign = SKTextAlign.Center;
        canvas.DrawText(title, CanvasWidth / 2f, 45, titlePaint);

        using var subPaint = CreateTextPaint(new SKColor(100, 100, 100), 14);
        subPaint.TextAlign = SKTextAlign.Center;
        canvas.DrawText("Standard Tuning (EADGBE)", CanvasWidth / 2f, 70, subPaint);
    }

    private void DrawSystem(SKCanvas canvas, List<RenderNote> notes,
                             int startNote, int endNote, float systemY)
    {
        using var stringPaint = new SKPaint
        {
            Color = SKColors.Black, IsAntialias = true,
            StrokeWidth = StringLineWidth, Style = SKPaintStyle.Stroke
        };
        using var barPaint = new SKPaint
        {
            Color = SKColors.Black, IsAntialias = true,
            StrokeWidth = BarLineWidth, Style = SKPaintStyle.Stroke
        };
        using var measureBarPaint = new SKPaint
        {
            Color = new SKColor(80, 80, 80), IsAntialias = true,
            StrokeWidth = 1.4f, Style = SKPaintStyle.Stroke
        };
        using var labelPaint = CreateTextPaint(SKColors.Black, 14, bold: true);
        labelPaint.TextAlign = SKTextAlign.Right;
        using var fretPaint  = CreateTextPaint(SKColors.Black, 13, bold: true);
        fretPaint.TextAlign  = SKTextAlign.Center;
        using var beatPaint  = CreateTextPaint(new SKColor(110, 130, 170), 10);
        beatPaint.TextAlign  = SKTextAlign.Center;
        using var whiteBg    = new SKPaint
        {
            Color = SKColors.White, Style = SKPaintStyle.Fill, IsAntialias = true
        };
        using var rhythmStemPaint = new SKPaint
        {
            Color = new SKColor(70, 70, 70), IsAntialias = true,
            StrokeWidth = 1.2f, Style = SKPaintStyle.Stroke
        };
        using var rhythmFillPaint = new SKPaint
        {
            Color = new SKColor(70, 70, 70), IsAntialias = true, Style = SKPaintStyle.Fill
        };
        using var rhythmOpenPaint = new SKPaint
        {
            Color = new SKColor(70, 70, 70), IsAntialias = true,
            StrokeWidth = 1.2f, Style = SKPaintStyle.Stroke
        };
        using var restPaint = new SKPaint
        {
            Color = new SKColor(120, 120, 120), IsAntialias = true, Style = SKPaintStyle.Fill
        };
        using var dotPaint = new SKPaint   // 도돌이표 점
        {
            Color = SKColors.Black, IsAntialias = true, Style = SKPaintStyle.Fill
        };
        using var timeSigPaint = CreateTextPaint(new SKColor(40, 40, 40), 13, bold: true);
        timeSigPaint.TextAlign = SKTextAlign.Center;

        int   noteCount    = endNote - startNote;
        float systemStartX = MarginLeft + StringLabelWidth;
        float systemEndX   = systemStartX + SystemPadding * 2 + noteCount * NoteSpacing;
        float stringAreaY  = systemY + BeatRowHeight;

        // ── 기타 줄 ───────────────────────────────────────────────────────────
        for (int s = 0; s < 6; s++)
        {
            float y = stringAreaY + s * StringSpacing;
            canvas.DrawLine(systemStartX, y, systemEndX, y, stringPaint);

            var m = labelPaint.FontMetrics;
            float labelBaseY = y - (m.Ascent + m.Descent) / 2;
            canvas.DrawText(GuitarTuning.StringNames[s], systemStartX - 8, labelBaseY, labelPaint);
        }

        // ── 시작/끝 세로선 ─────────────────────────────────────────────────────
        float barTop    = systemY;
        float barBottom = stringAreaY + 5 * StringSpacing;
        canvas.DrawLine(systemStartX, barTop, systemStartX, barBottom, barPaint);
        canvas.DrawLine(systemEndX,   barTop, systemEndX,   barBottom, barPaint);

        // 도돌이표: 시스템 첫 마디가 시작 도돌이(‖:) / 끝 마디가 끝 도돌이(:‖)
        if (noteCount > 0)
        {
            if (notes[startNote].RepeatStart && notes[startNote].IsMeasureStart)
                DrawRepeatDots(canvas, systemStartX + 6f, stringAreaY, dotPaint);
            if (notes[endNote - 1].RepeatEnd &&
                (endNote >= notes.Count || notes[endNote].IsMeasureStart))
                DrawRepeatDots(canvas, systemEndX - 6f, stringAreaY, dotPaint);
        }

        // ── 박 번호 Y 기준 ─────────────────────────────────────────────────────
        var bm = beatPaint.FontMetrics;
        float beatTextY = systemY + BeatRowHeight / 2f - (bm.Ascent + bm.Descent) / 2f;

        // ── 음표 렌더링 ────────────────────────────────────────────────────────
        for (int i = startNote; i < endNote; i++)
        {
            var   rn       = notes[i];
            int   localIdx = i - startNote;
            float x        = systemStartX + SystemPadding + localIdx * NoteSpacing + NoteSpacing / 2f;

            // 마디선: 시스템 첫 음표 아닌 새 마디에만
            if (rn.IsMeasureStart && i > startNote)
            {
                float barX     = x - NoteSpacing / 2f;
                bool  repEnd   = notes[i - 1].RepeatEnd;   // 이전 마디가 끝 도돌이(:‖)
                bool  repStart = rn.RepeatStart;           // 이 마디가 시작 도돌이(‖:)
                if (repEnd || repStart)
                {
                    canvas.DrawLine(barX, barTop, barX, barBottom, barPaint);   // 굵은 마디선
                    if (repEnd)   DrawRepeatDots(canvas, barX - 6f, stringAreaY, dotPaint);
                    if (repStart) DrawRepeatDots(canvas, barX + 6f, stringAreaY, dotPaint);
                }
                else
                {
                    canvas.DrawLine(barX, barTop, barX, barBottom, measureBarPaint);
                }
            }

            // 박자표: 박자가 바뀌는(또는 곡 첫) 마디 시작에 분자/분모를 세로로 표시
            if (rn.IsMeasureStart && rn.TimeSigNum > 0)
            {
                float tsX = (i > startNote) ? x - NoteSpacing / 2f + 7f : systemStartX + 9f;
                DrawTimeSig(canvas, tsX, stringAreaY, rn.TimeSigNum, rn.TimeSigDen, timeSigPaint);
            }

            // 박 번호: 박의 첫 번째 음표 위
            if (rn.IsBeatStart)
                canvas.DrawText(rn.BeatNumber.ToString(), x, beatTextY, beatPaint);

            // 리듬 표기 (음표·쉼표 공통): 스태프 아래 기둥/꼬리
            DrawRhythm(canvas, x, barBottom + RhythmGap, rn.Group.DurationInQN,
                       rhythmStemPaint, rhythmFillPaint, rhythmOpenPaint);

            // 쉼표: 스태프 중앙에 기호
            if (rn.Group.IsRest)
            {
                DrawRest(canvas, x, stringAreaY, restPaint);
                continue;
            }

            // 프렛 번호
            foreach (var pos in rn.Group.Positions)
            {
                if (pos.IsUnplayable) continue;
                float stringY = stringAreaY + pos.StringIndex * StringSpacing;
                DrawFretNumber(canvas, pos.Fret.ToString(), x, stringY, fretPaint, whiteBg);
            }
        }
    }

    private void DrawFretNumber(SKCanvas canvas, string fretStr, float x, float stringY,
                                 SKPaint fretPaint, SKPaint whiteBg)
    {
        var metrics = fretPaint.FontMetrics;
        float textHeight = metrics.Descent - metrics.Ascent;
        float baselineY  = stringY - (metrics.Ascent + metrics.Descent) / 2;

        var bounds = new SKRect();
        fretPaint.MeasureText(fretStr, ref bounds);

        const float padX = 3f, padY = 1f;
        var bgRect = new SKRect(
            x - bounds.Width / 2 - padX, stringY - textHeight / 2 - padY,
            x + bounds.Width / 2 + padX, stringY + textHeight / 2 + padY);
        canvas.DrawRect(bgRect, whiteBg);
        canvas.DrawText(fretStr, x, baselineY, fretPaint);
    }

    // 음표 길이(4분음표 단위) → 리듬 표기 분류 (꼬리 수, 점음표, 빈머리, 기둥유무)
    private static (int flags, bool dotted, bool hollow, bool stem) ClassifyDuration(double qn)
    {
        (double dur, int flags, bool dotted, bool hollow, bool stem)[] table =
        {
            (4.0,    0, false, true,  false), // 온음표
            (3.0,    0, true,  true,  true),  // 점2분음표
            (2.0,    0, false, true,  true),  // 2분음표
            (1.5,    0, true,  false, true),  // 점4분음표
            (1.0,    0, false, false, true),  // 4분음표
            (0.75,   1, true,  false, true),  // 점8분음표
            (0.5,    1, false, false, true),  // 8분음표
            (0.375,  2, true,  false, true),  // 점16분음표
            (0.25,   2, false, false, true),  // 16분음표
            (0.1875, 3, true,  false, true),  // 점32분음표
            (0.125,  3, false, false, true),  // 32분음표
        };

        var    best     = table[0];
        double bestDiff = double.MaxValue;
        foreach (var t in table)
        {
            double diff = Math.Abs(t.dur - qn);
            if (diff < bestDiff) { bestDiff = diff; best = t; }
        }
        return (best.flags, best.dotted, best.hollow, best.stem);
    }

    // 스태프 아래에 리듬(기둥·꼬리·점)을 그린다. 프렛 숫자가 음표 머리 역할.
    private void DrawRhythm(SKCanvas canvas, float x, float topY, double durationInQN,
                            SKPaint stemPaint, SKPaint fillPaint, SKPaint openPaint)
    {
        var (flags, dotted, hollow, stem) = ClassifyDuration(durationInQN);
        const float headR = 2.6f;

        // 머리: 2분·온음표는 빈 원, 그 외는 채운 원
        if (hollow) canvas.DrawCircle(x, topY, headR, openPaint);
        else        canvas.DrawCircle(x, topY, headR, fillPaint);

        if (stem)
        {
            float stemBottom = topY + StemLength;
            canvas.DrawLine(x, topY, x, stemBottom, stemPaint);

            // 꼬리: 8분음표=1, 16분음표=2 ...
            for (int f = 0; f < flags; f++)
            {
                float fy = stemBottom - f * 4f;
                canvas.DrawLine(x, fy, x + 6f, fy - 4f, stemPaint);
            }
        }

        // 점음표
        if (dotted)
            canvas.DrawCircle(x + headR + 4f, topY, 1.4f, fillPaint);
    }

    // 쉼표 표식 (스태프 중앙). 길이는 리듬 레인의 꼬리로 구분.
    private void DrawRest(SKCanvas canvas, float x, float stringAreaY, SKPaint restPaint)
    {
        float midY = stringAreaY + 2.5f * StringSpacing;
        var   rect = new SKRect(x - 3.5f, midY - 5f, x + 3.5f, midY + 5f);
        canvas.DrawRect(rect, restPaint);
    }

    // 도돌이표의 점 2개 (스태프 중앙 위·아래). x는 마디선에서 좌/우로 오프셋된 위치.
    private void DrawRepeatDots(SKCanvas canvas, float x, float stringAreaY, SKPaint dotPaint)
    {
        float center = stringAreaY + 2.5f * StringSpacing;
        canvas.DrawCircle(x, center - StringSpacing, 2.2f, dotPaint);
        canvas.DrawCircle(x, center + StringSpacing, 2.2f, dotPaint);
    }

    // 박자표: 분자(위)·분모(아래)를 스태프에 세로로 그린다.
    private void DrawTimeSig(SKCanvas canvas, float x, float stringAreaY, int num, int den, SKPaint paint)
    {
        var fm = paint.FontMetrics;
        float upperY = stringAreaY + 1.5f * StringSpacing - (fm.Ascent + fm.Descent) / 2;
        float lowerY = stringAreaY + 3.5f * StringSpacing - (fm.Ascent + fm.Descent) / 2;
        canvas.DrawText(num.ToString(), x, upperY, paint);
        canvas.DrawText(den.ToString(), x, lowerY, paint);
    }

    private SKPaint CreateTextPaint(SKColor color, float size, bool bold = false)
    {
        return new SKPaint
        {
            Color = color, IsAntialias = true, TextSize = size,
            Typeface = SKTypeface.FromFamilyName("Arial",
                bold ? SKFontStyle.Bold : SKFontStyle.Normal)
        };
    }
}
