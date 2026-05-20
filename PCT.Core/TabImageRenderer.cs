using SkiaSharp;

namespace PCT.Core;

public class TabImageRenderer
{
    private const int CanvasWidth = 1200;
    private const int MarginLeft = 50;
    private const int MarginTop = 100;
    private const int MarginBottom = 50;
    private const int StringLabelWidth = 30;
    private const int SystemPadding = 15;
    private const int StringSpacing = 20;
    private const int NoteSpacing = 36;
    private const int SystemSpacing = 80;
    private const int NotesPerSystem = 24;
    private const float StringLineWidth = 1.2f;
    private const float BarLineWidth = 1.8f;

    public byte[] RenderToPngBytes(List<TabPositionGroup> groups, string title)
    {
        int count = Math.Max(1, groups.Count);
        int numSystems = (int)Math.Ceiling((double)count / NotesPerSystem);
        int systemHeight = 5 * StringSpacing;
        int totalHeight = MarginTop + numSystems * (systemHeight + SystemSpacing) + MarginBottom;

        var info = new SKImageInfo(CanvasWidth, totalHeight);
        using var surface = SKSurface.Create(info);
        var canvas = surface.Canvas;
        canvas.Clear(SKColors.White);

        DrawHeader(canvas, title);

        for (int sysIdx = 0; sysIdx < numSystems; sysIdx++)
        {
            int startNote = sysIdx * NotesPerSystem;
            int endNote = Math.Min(startNote + NotesPerSystem, groups.Count);
            float systemY = MarginTop + sysIdx * (systemHeight + SystemSpacing);
            DrawSystem(canvas, groups, startNote, endNote, systemY);
        }

        using var image = surface.Snapshot();
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    public void RenderToPng(List<TabPositionGroup> groups, string title, string outputPath)
    {
        var bytes = RenderToPngBytes(groups, title);
        File.WriteAllBytes(outputPath, bytes);
    }

    private void DrawHeader(SKCanvas canvas, string title)
    {
        using var titlePaint = CreateTextPaint(SKColors.Black, 28, true);
        titlePaint.TextAlign = SKTextAlign.Center;
        canvas.DrawText(title, CanvasWidth / 2f, 45, titlePaint);

        using var subtitlePaint = CreateTextPaint(new SKColor(100, 100, 100), 14);
        subtitlePaint.TextAlign = SKTextAlign.Center;
        canvas.DrawText("Standard Tuning (EADGBE)", CanvasWidth / 2f, 70, subtitlePaint);
    }

    private void DrawSystem(SKCanvas canvas, List<TabPositionGroup> groups,
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
        using var labelPaint = CreateTextPaint(SKColors.Black, 14, true);
        labelPaint.TextAlign = SKTextAlign.Right;
        using var fretPaint = CreateTextPaint(SKColors.Black, 13, true);
        fretPaint.TextAlign = SKTextAlign.Center;
        using var whiteBg = new SKPaint
        {
            Color = SKColors.White, Style = SKPaintStyle.Fill, IsAntialias = true
        };

        int noteCount = endNote - startNote;
        float systemStartX = MarginLeft + StringLabelWidth;
        float systemEndX = systemStartX + SystemPadding * 2 + noteCount * NoteSpacing;

        for (int s = 0; s < 6; s++)
        {
            float y = systemY + s * StringSpacing;
            canvas.DrawLine(systemStartX, y, systemEndX, y, stringPaint);

            var m = labelPaint.FontMetrics;
            float labelBaseY = y - (m.Ascent + m.Descent) / 2;
            canvas.DrawText(GuitarTuning.StringNames[s], systemStartX - 8, labelBaseY, labelPaint);
        }

        float barTop = systemY;
        float barBottom = systemY + 5 * StringSpacing;
        canvas.DrawLine(systemStartX, barTop, systemStartX, barBottom, barPaint);
        canvas.DrawLine(systemEndX, barTop, systemEndX, barBottom, barPaint);

        for (int i = startNote; i < endNote; i++)
        {
            var group = groups[i];
            if (group.IsRest) continue;

            int localIdx = i - startNote;
            float x = systemStartX + SystemPadding + localIdx * NoteSpacing + NoteSpacing / 2f;

            foreach (var pos in group.Positions)
            {
                if (pos.IsUnplayable) continue;
                float stringY = systemY + pos.StringIndex * StringSpacing;
                DrawFretNumber(canvas, pos.Fret.ToString(), x, stringY, fretPaint, whiteBg);
            }
        }
    }

    private void DrawFretNumber(SKCanvas canvas, string fretStr, float x, float stringY,
                                 SKPaint fretPaint, SKPaint whiteBg)
    {
        var metrics = fretPaint.FontMetrics;
        float textHeight = metrics.Descent - metrics.Ascent;
        float baselineY = stringY - (metrics.Ascent + metrics.Descent) / 2;

        var bounds = new SKRect();
        fretPaint.MeasureText(fretStr, ref bounds);
        float textWidth = bounds.Width;

        const float padX = 3f;
        const float padY = 1f;
        var bgRect = new SKRect(
            x - textWidth / 2 - padX, stringY - textHeight / 2 - padY,
            x + textWidth / 2 + padX, stringY + textHeight / 2 + padY);
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
