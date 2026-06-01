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

    // 렌더링을 위해 flat하게 펼친 음표 단위
    private record RenderNote(
        TabPositionGroup Group,
        int  BeatNumber,
        bool IsBeatStart,    // 박의 첫 번째 음표인가
        bool IsMeasureStart  // 마디의 첫 번째 음표인가
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
                    IsMeasureStart: isNewMeasure && i == 0
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
                float barX = x - NoteSpacing / 2f;
                canvas.DrawLine(barX, barTop, barX, barBottom, measureBarPaint);
            }

            // 박 번호: 박의 첫 번째 음표 위
            if (rn.IsBeatStart)
                canvas.DrawText(rn.BeatNumber.ToString(), x, beatTextY, beatPaint);

            // 프렛 번호
            if (rn.Group.IsRest) continue;
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
