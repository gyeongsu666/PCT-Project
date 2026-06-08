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
    private const float StemLength      = 22f;   // 리듬 기둥 길이
    private const float BeamThickness   = 3.0f;  // 빔(굵은 가로선) 두께
    private const float BeamSpacing     = 2.0f;  // 빔 레벨 간 빈 간격

    // 렌더링을 위해 flat하게 펼친 음표 단위
    private record RenderNote(
        TabPositionGroup Group,
        int  BeatNumber,
        bool IsBeatStart,    // 박의 첫 번째 음표인가
        bool IsMeasureStart, // 마디의 첫 번째 음표인가
        bool RepeatStart,    // 이 음표의 마디가 시작 도돌이표(‖:)를 가짐
        bool RepeatEnd,      // 이 음표의 마디가 끝 도돌이표(:‖)를 가짐
        int  TimeSigNum,     // 이 마디에 표시할 박자표 분자(>0이면 표시)
        int  TimeSigDen,
        double MeasureQN,    // 이 음표가 속한 마디의 총 길이(4분음표 단위). 4/4=4, 2/4=2
        double StartQN       // 마디 안에서 이 음표의 시작 시각(4분음표 단위)
    );

    // ── 공개 API ──────────────────────────────────────────────────────────────

    public byte[] RenderToPngBytes(List<TabMeasureBeat> beats, string title)
    {
        var (notes, measureRanges) = Flatten(beats);

        int totalMeasures = measureRanges.Count;
        int numSystems    = totalMeasures == 0 ? 1 : (int)Math.Ceiling(totalMeasures / 4.0);
        int systemHeight  = BeatRowHeight + 5 * StringSpacing;
        int totalHeight   = MarginTop + numSystems * (systemHeight + SystemSpacing) + MarginBottom;

        var info = new SKImageInfo(CanvasWidth, totalHeight);
        using var surface = SKSurface.Create(info);
        var canvas = surface.Canvas;
        canvas.Clear(SKColors.White);

        DrawHeader(canvas, title);

        for (int sysIdx = 0; sysIdx < numSystems && totalMeasures > 0; sysIdx++)
        {
            int firstM    = sysIdx * 4;
            int lastM     = Math.Min(firstM + 4, totalMeasures) - 1;
            int startNote = measureRanges[firstM].Start;
            int endNote   = measureRanges[lastM].Start + measureRanges[lastM].Len;
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

    private record MeasureRange(int Start, int Len, double QN);

    private static (List<RenderNote> Notes, List<MeasureRange> MeasureRanges) Flatten(List<TabMeasureBeat> beats)
    {
        var result        = new List<RenderNote>();
        var measureRanges = new List<MeasureRange>();
        if (beats == null || beats.Count == 0) return (result, measureRanges);

        // 마디별 그룹화
        var measureGroups = beats
            .GroupBy(b => b.MeasureNumber)
            .OrderBy(g => g.Key)
            .ToList();

        // 박자표 carry-forward (분자·분모 모두). 마디 총 길이 = num × 4 / den (4분음표 단위)
        int effNum = 4, effDen = 4;

        for (int mIdx = 0; mIdx < measureGroups.Count; mIdx++)
        {
            var group = measureGroups[mIdx];
            foreach (var b in group)
            {
                if (b.TimeSigNum > 0) { effNum = b.TimeSigNum; if (b.TimeSigDen > 0) effDen = b.TimeSigDen; break; }
            }
            double measureQN = effNum * 4.0 / effDen;

            int    startIdx   = result.Count;
            double startQN    = 0;
            bool   newMeasure = true;

            foreach (var beat in group)
            {
                for (int i = 0; i < beat.Notes.Count; i++)
                {
                    var g = beat.Notes[i];
                    result.Add(new RenderNote(
                        Group:          g,
                        BeatNumber:     beat.BeatNumber,
                        IsBeatStart:    i == 0,
                        IsMeasureStart: newMeasure && i == 0 && mIdx > 0,
                        RepeatStart:    beat.RepeatStart,
                        RepeatEnd:      beat.RepeatEnd,
                        TimeSigNum:     beat.TimeSigNum,
                        TimeSigDen:     beat.TimeSigDen,
                        MeasureQN:      measureQN,
                        StartQN:        startQN
                    ));
                    if (i == 0) newMeasure = false;
                    startQN += g.DurationInQN;
                }
            }
            measureRanges.Add(new MeasureRange(startIdx, result.Count - startIdx, measureQN));
        }

        return (result, measureRanges);
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
        using var dotPaint = new SKPaint   // 도돌이표 점
        {
            Color = SKColors.Black, IsAntialias = true, Style = SKPaintStyle.Fill
        };
        using var timeSigPaint = CreateTextPaint(new SKColor(40, 40, 40), 20, bold: true);
        timeSigPaint.TextAlign = SKTextAlign.Center;

        int   noteCount    = endNote - startNote;
        float systemStartX = MarginLeft + StringLabelWidth;
        // 모든 시스템이 동일한 너비 → 줄선이 항상 한 줄로 연결됨
        float systemEndX   = CanvasWidth - MarginLeft;
        float contentWidth = systemEndX - systemStartX - SystemPadding * 2f;
        float stringAreaY  = systemY + BeatRowHeight;

        // ── 마디별 너비(박자 비례) + 음표별 x(박 위치 비례) 계산 ──────────────────
        // 1) 시스템 내 마디 경계와 각 마디 길이(QN) 수집
        var measLocalStart = new List<int>();
        var measLen        = new List<double>();
        for (int k = 0; k < noteCount; k++)
        {
            var rn = notes[startNote + k];
            if (k == 0 || rn.IsMeasureStart)
            {
                measLocalStart.Add(k);
                measLen.Add(rn.MeasureQN > 0 ? rn.MeasureQN : 4.0);
            }
        }
        int    measCount = measLocalStart.Count;
        double totalQN   = 0;
        foreach (var q in measLen) totalQN += q;
        if (totalQN <= 0) totalQN = 1;

        // 2) 마디별 시작 x·너비 (시스템 너비를 QN 비례로 배분)
        var measX0 = new float[measCount];
        var measW  = new float[measCount];
        float accX = systemStartX + SystemPadding;
        for (int m = 0; m < measCount; m++)
        {
            measW[m]  = (float)(contentWidth * measLen[m] / totalQN);
            measX0[m] = accX;
            accX += measW[m];
        }

        // 3) 각 음표의 중심 x(자기 시간 구간 중앙)·왼쪽 경계 x(마디선·박자표용)
        var xCenter = new float[noteCount];
        var xLeft   = new float[noteCount];
        for (int m = 0; m < measCount; m++)
        {
            int    s   = measLocalStart[m];
            int    e   = (m + 1 < measCount) ? measLocalStart[m + 1] : noteCount;
            double mQN = measLen[m];
            for (int k = s; k < e; k++)
            {
                var    rn    = notes[startNote + k];
                double dur   = rn.Group.DurationInQN;
                double cFrac = mQN > 0 ? (rn.StartQN + dur / 2.0) / mQN : (k - s + 0.5) / (double)(e - s);
                double lFrac = mQN > 0 ?  rn.StartQN              / mQN : (k - s)       / (double)(e - s);
                xCenter[k] = measX0[m] + (float)(Math.Clamp(cFrac, 0, 1) * measW[m]);
                xLeft[k]   = measX0[m] + (float)(Math.Clamp(lFrac, 0, 1) * measW[m]);
            }
        }

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
            float x        = xCenter[localIdx];

            // 마디선: 시스템 첫 음표 아닌 새 마디에만 (패딩 슬롯도 마디선은 그림)
            if (rn.IsMeasureStart && i > startNote)
            {
                float barX     = xLeft[localIdx];          // 슬롯 왼쪽 경계 = 마디선 위치
                bool  repEnd   = notes[i - 1].RepeatEnd;
                bool  repStart = rn.RepeatStart;
                if (repEnd || repStart)
                {
                    canvas.DrawLine(barX, barTop, barX, barBottom, barPaint);
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
                float tsX = (i > startNote) ? xLeft[localIdx] + 7f : systemStartX + 9f;
                DrawTimeSig(canvas, tsX, stringAreaY, rn.TimeSigNum, rn.TimeSigDen, timeSigPaint);
            }

            // 박 번호: 박의 첫 번째 음표 위
            if (rn.IsBeatStart)
                canvas.DrawText(rn.BeatNumber.ToString(), x, beatTextY, beatPaint);

            if (rn.Group.IsRest) continue;

            // 프렛 번호
            foreach (var pos in rn.Group.Positions)
            {
                if (pos.IsUnplayable) continue;
                float stringY = stringAreaY + pos.StringIndex * StringSpacing;
                DrawFretNumber(canvas, pos.Fret.ToString(), x, stringY, fretPaint, whiteBg);
            }
        }

        // ── 리듬 표기: 스태프 아래 기둥/빔 (박 단위 빔 기보) ──────────────────────
        DrawBeamedRhythm(canvas, notes, startNote, endNote, xCenter, barBottom + RhythmGap,
                         rhythmStemPaint, rhythmFillPaint, rhythmOpenPaint);
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

    // 리듬 음표 한 개의 표기 정보 (프렛 숫자가 머리 역할, 머리 원은 거의 안 그림)
    private record struct RhythmItem(float X, int Flags, bool Dotted, bool Hollow, bool Stem,
                                     bool IsRest, bool BeatStart);

    // 스태프 아래에 리듬을 빔(beam) 기보법으로 그린다.
    // 박(beat) 단위로 8분음표 이하를 굵은 가로 빔으로 묶고, 4분음표 이상은 단독 기둥.
    private void DrawBeamedRhythm(SKCanvas canvas, List<RenderNote> notes,
                                  int startNote, int endNote, float[] xCenter, float topY,
                                  SKPaint stemPaint, SKPaint fillPaint, SKPaint openPaint)
    {
        float stemBottom = topY + StemLength;
        using var beamPaint = new SKPaint
        {
            Color = new SKColor(70, 70, 70), IsAntialias = true, Style = SKPaintStyle.Fill
        };

        // 실제 음표(패딩 제외) 수집
        var items = new List<RhythmItem>();
        for (int i = startNote; i < endNote; i++)
        {
            var rn = notes[i];
            var (flags, dotted, hollow, stem) = ClassifyDuration(rn.Group.DurationInQN);
            items.Add(new RhythmItem(xCenter[i - startNote], flags, dotted, hollow, stem,
                                     rn.Group.IsRest, rn.IsBeatStart));
        }

        int n = items.Count, g = 0;
        while (g < n)
        {
            var it = items[g];
            // 쉼표 또는 4분음표 이상 → 빔 불가, 단독 표기
            if (it.IsRest || it.Flags == 0)
            {
                DrawSingleStem(canvas, it, topY, stemBottom, stemPaint, fillPaint, openPaint);
                g++;
                continue;
            }
            // 빔 그룹 확장: 같은 박 안에서 연속된 8분음표 이하(쉼표 아님)
            int h = g + 1;
            while (h < n && !items[h].BeatStart && items[h].Flags >= 1 && !items[h].IsRest)
                h++;

            if (h - g == 1)   // 단독 8분 이하 → 깃발
                DrawFlaggedStem(canvas, items[g], topY, stemBottom, stemPaint, fillPaint);
            else              // 2개 이상 → 빔으로 묶음
                DrawBeamGroup(canvas, items, g, h, topY, stemBottom, stemPaint, fillPaint, beamPaint);

            g = h;
        }
    }

    // 빔으로 묶인 그룹: 각 음표 기둥 + primary/secondary 빔
    private void DrawBeamGroup(SKCanvas canvas, List<RhythmItem> items, int start, int end,
                               float topY, float stemBottom,
                               SKPaint stemPaint, SKPaint fillPaint, SKPaint beamPaint)
    {
        // 기둥 + 점
        for (int k = start; k < end; k++)
        {
            canvas.DrawLine(items[k].X, topY, items[k].X, stemBottom, stemPaint);
            if (items[k].Dotted)
                canvas.DrawCircle(items[k].X + 5f, topY, 1.8f, fillPaint);
        }

        int maxLevel = 0;
        for (int k = start; k < end; k++) maxLevel = Math.Max(maxLevel, items[k].Flags);

        // primary 빔(레벨1, 8분): 그룹 전체 연결
        DrawBeamLine(canvas, items[start].X, items[end - 1].X, stemBottom, beamPaint);

        // secondary 빔(레벨2~, 16분·32분): flags>=level 연속 구간만, 단독은 짧은 부분 빔
        for (int level = 2; level <= maxLevel; level++)
        {
            float yb = stemBottom - (level - 1) * (BeamThickness + BeamSpacing);
            int k = start;
            while (k < end)
            {
                if (items[k].Flags < level) { k++; continue; }
                int j = k;
                while (j + 1 < end && items[j + 1].Flags >= level) j++;
                if (j > k)
                    DrawBeamLine(canvas, items[k].X, items[j].X, yb, beamPaint);
                else
                {
                    const float stub = 7f;   // 단독 16분 등 → 부분 빔
                    if (k > start) DrawBeamLine(canvas, items[k].X - stub, items[k].X, yb, beamPaint);
                    else           DrawBeamLine(canvas, items[k].X, items[k].X + stub, yb, beamPaint);
                }
                k = j + 1;
            }
        }
    }

    // 굵은 가로 빔 (yBottom 기준 위로 두께만큼)
    private void DrawBeamLine(SKCanvas canvas, float x0, float x1, float yBottom, SKPaint beamPaint)
        => canvas.DrawRect(new SKRect(x0, yBottom - BeamThickness, x1, yBottom), beamPaint);

    // 단독 8분음표 이하: 기둥 + 깃발(flag)
    private void DrawFlaggedStem(SKCanvas canvas, RhythmItem it, float topY, float stemBottom,
                                 SKPaint stemPaint, SKPaint fillPaint)
    {
        canvas.DrawLine(it.X, topY, it.X, stemBottom, stemPaint);
        for (int f = 0; f < it.Flags; f++)
        {
            float fy = stemBottom - f * 5f;
            canvas.DrawLine(it.X, fy, it.X + 9f, fy - 6f, stemPaint);
        }
        if (it.Dotted) canvas.DrawCircle(it.X + 5f, topY, 1.8f, fillPaint);
    }

    // 단독 음표(4분 이상) 또는 쉼표
    private void DrawSingleStem(SKCanvas canvas, RhythmItem it, float topY, float stemBottom,
                                SKPaint stemPaint, SKPaint fillPaint, SKPaint openPaint)
    {
        if (it.IsRest)
        {
            DrawRestSymbol(canvas, it.X, topY, stemBottom, fillPaint);
            return;
        }
        if (!it.Stem)   // 온음표: 빈 원만 (기둥 없음)
        {
            canvas.DrawCircle(it.X, topY, 3.2f, openPaint);
            return;
        }
        canvas.DrawLine(it.X, topY, it.X, stemBottom, stemPaint);
        if (it.Hollow) canvas.DrawCircle(it.X, topY, 3.2f, openPaint);  // 2분음표 빈 원으로 구분
        if (it.Dotted) canvas.DrawCircle(it.X + 5f, topY, 1.8f, fillPaint);
    }

    // 쉼표 기호 (리듬 레인 중앙). 사선 + 점으로 간략 표기.
    private void DrawRestSymbol(SKCanvas canvas, float x, float topY, float stemBottom, SKPaint fillPaint)
    {
        float midY = (topY + stemBottom) / 2f;
        using var p = new SKPaint
        {
            Color = fillPaint.Color, IsAntialias = true,
            StrokeWidth = 2.2f, Style = SKPaintStyle.Stroke, StrokeCap = SKStrokeCap.Round
        };
        canvas.DrawLine(x - 3f, midY + 4f, x + 3f, midY - 4f, p);
        canvas.DrawCircle(x + 3f, midY - 4f, 1.7f, fillPaint);
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
        // 6줄 스태프(높이=5×StringSpacing) 기준: 상단 절반 중앙(1.25), 하단 절반 중앙(3.75)
        float upperY = stringAreaY + 1.25f * StringSpacing - (fm.Ascent + fm.Descent) / 2;
        float lowerY = stringAreaY + 3.75f * StringSpacing - (fm.Ascent + fm.Descent) / 2;
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
