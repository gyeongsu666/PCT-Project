namespace PCT.Core;

public class TabConverterSettings
{
    public int MaxFingerSpan { get; set; } = 5;
    public double SpanCost { get; set; } = 1.0;
    public double OverSpanPenalty { get; set; } = 100.0;
    public double AvgFretCost { get; set; } = 0.3;
    public double HighFretPenalty { get; set; } = 1.2;
    public int HighFretThreshold { get; set; } = 9;
    public double HandMoveCost { get; set; } = 2.5;
    public double OpenStringBonus { get; set; } = -8.0;
    public double MelodyStringBonus { get; set; } = -3.0;
    public double BassStringBonus { get; set; } = -3.0;
}

public class TabConverter
{
    private readonly TabConverterSettings _s;

    public TabConverter(TabConverterSettings settings = null)
    {
        _s = settings ?? new TabConverterSettings();
    }

    /// <summary>
    /// MeasureBeat 목록을 받아 각 박 안의 모든 음표에 대해 DP 운지를 계산한 뒤
    /// TabMeasureBeat 목록으로 반환한다.
    /// DP는 음표 전체를 flat하게 돌려 마디/박 경계를 가로지르는 손 이동 비용도 최적화한다.
    /// </summary>
    public List<TabMeasureBeat> Convert(List<MeasureBeat> inputBeats)
    {
        // ① flat 목록 생성 (DP용)
        var flatNotes = inputBeats.SelectMany(b => b.Notes).ToList();

        if (flatNotes.Count == 0) return new List<TabMeasureBeat>();

        // ② 각 NoteGroup의 후보 운지 생성
        var allCandidateGroups = flatNotes.Select(GenerateCandidates).ToList();

        int n = allCandidateGroups.Count;
        var dp     = new double[n][];
        var parent = new int[n][];

        for (int i = 0; i < n; i++)
        {
            dp[i]     = new double[allCandidateGroups[i].Count];
            parent[i] = new int[allCandidateGroups[i].Count];
            for (int j = 0; j < allCandidateGroups[i].Count; j++) dp[i][j] = double.MaxValue;
        }

        for (int j = 0; j < allCandidateGroups[0].Count; j++)
            dp[0][j] = CalculateCost(allCandidateGroups[0][j].Positions);

        for (int i = 1; i < n; i++)
        {
            for (int currIdx = 0; currIdx < allCandidateGroups[i].Count; currIdx++)
            {
                double selfCost = CalculateCost(allCandidateGroups[i][currIdx].Positions);

                for (int prevIdx = 0; prevIdx < allCandidateGroups[i - 1].Count; prevIdx++)
                {
                    if (dp[i - 1][prevIdx] == double.MaxValue) continue;

                    double moveCost = HandMoveCostBetween(
                        allCandidateGroups[i - 1][prevIdx].Positions,
                        allCandidateGroups[i][currIdx].Positions);

                    double total = dp[i - 1][prevIdx] + moveCost + selfCost;
                    if (total < dp[i][currIdx])
                    {
                        dp[i][currIdx]     = total;
                        parent[i][currIdx] = prevIdx;
                    }
                }
            }
        }

        // ③ 역추적
        double minCost = double.MaxValue;
        int lastIdx = 0;
        for (int j = 0; j < allCandidateGroups[n - 1].Count; j++)
        {
            if (dp[n - 1][j] < minCost) { minCost = dp[n - 1][j]; lastIdx = j; }
        }

        var flatResult = new List<TabPositionGroup>(n);
        for (int i = n - 1; i >= 0; i--)
        {
            flatResult.Add(allCandidateGroups[i][lastIdx]);
            lastIdx = parent[i][lastIdx];
        }
        flatResult.Reverse();

        // ④ flat 결과를 MeasureBeat 구조로 재조립
        var result = new List<TabMeasureBeat>(inputBeats.Count);
        int idx = 0;
        foreach (var beat in inputBeats)
        {
            var tb = new TabMeasureBeat
            {
                MeasureNumber = beat.MeasureNumber,
                BeatNumber    = beat.BeatNumber,
                RepeatStart   = beat.RepeatStart,
                RepeatEnd     = beat.RepeatEnd,
                TimeSigNum    = beat.TimeSigNum,
                TimeSigDen    = beat.TimeSigDen,
            };
            for (int i = 0; i < beat.Notes.Count; i++, idx++)
            {
                if (idx < flatResult.Count)
                    tb.Notes.Add(flatResult[idx]);
            }
            result.Add(tb);
        }

        return result;
    }

    // ── 후보 운지 생성 ────────────────────────────────────────────────────────

    private List<TabPositionGroup> GenerateCandidates(NoteGroup group)
    {
        var candidates = new List<TabPositionGroup>();

        if (group.IsRest)
        {
            candidates.Add(new TabPositionGroup { IsRest = true, SourceGroup = group });
            return candidates;
        }

        // ① 전음 배치 시도 (탈락 없음)
        var fullArrangements = new List<List<TabPosition>>();
        FindArrangements(group.Notes, 0, new List<TabPosition>(), fullArrangements, allowDrops: false);

        if (fullArrangements.Count > 0)
        {
            foreach (var arr in fullArrangements)
                candidates.Add(new TabPositionGroup
                {
                    Positions    = arr,
                    SourceGroup  = group,
                    DroppedCount = 0,
                });
            return candidates;
        }

        // ② 물리적으로 불가능한 경우 — 최소 탈락 폴백
        var fallbackArrangements = new List<List<TabPosition>>();
        FindArrangements(group.Notes, 0, new List<TabPosition>(), fallbackArrangements, allowDrops: true);

        foreach (var arr in fallbackArrangements)
            candidates.Add(new TabPositionGroup
            {
                Positions    = arr,
                SourceGroup  = group,
                DroppedCount = group.Notes.Count - arr.Count,
            });

        if (candidates.Count == 0)
            candidates.Add(new TabPositionGroup
            {
                IsRest       = true,
                SourceGroup  = group,
                DroppedCount = group.Notes.Count,
            });

        return candidates;
    }

    /// <param name="allowDrops">
    /// false: 모든 음을 줄에 배치할 수 있을 때만 결과로 추가 (탈락 없음).
    /// true : 배치 불가 음은 건너뛰어 부분 배치도 결과로 허용 (최소 탈락).
    /// </param>
    private void FindArrangements(List<Note> notes, int noteIdx, List<TabPosition> current,
                                   List<List<TabPosition>> results, bool allowDrops)
    {
        if (noteIdx == notes.Count)
        {
            results.Add(new List<TabPosition>(current));
            return;
        }

        var note = notes[noteIdx];
        bool foundAny = false;

        for (int s = 0; s < 6; s++)
        {
            if (current.Any(p => p.StringIndex == s)) continue;
            int fret = note.MidiNumber - GuitarTuning.StringMidi[s];
            if (fret >= 0 && fret <= GuitarTuning.MaxFret)
            {
                current.Add(new TabPosition { StringIndex = s, Fret = fret, SourceNote = note });
                FindArrangements(notes, noteIdx + 1, current, results, allowDrops);
                current.RemoveAt(current.Count - 1);
                foundAny = true;
            }
        }

        if (!foundAny && allowDrops)
            FindArrangements(notes, noteIdx + 1, current, results, allowDrops);
        // allowDrops == false 이면 이 배치는 결과에 추가하지 않음 (탈락 없이 전음 배치 실패)
    }

    // ── 비용 함수 ─────────────────────────────────────────────────────────────

    private double CalculateCost(List<TabPosition> arr)
    {
        if (arr.Count == 0) return 0;

        double cost = 0;
        var frets = arr.Where(a => a.Fret > 0).Select(a => a.Fret).ToList();
        if (frets.Count > 0)
        {
            int span = frets.Max() - frets.Min();
            cost += span * _s.SpanCost;
            if (span > _s.MaxFingerSpan) cost += (span - _s.MaxFingerSpan) * _s.OverSpanPenalty;
        }

        foreach (var pos in arr)
        {
            if (pos.Fret == 0) cost += _s.OpenStringBonus;
            if (pos.SourceNote.MidiNumber >= 64 && pos.StringIndex <= 1) cost += _s.MelodyStringBonus;
            if (pos.SourceNote.MidiNumber <= 50 && pos.StringIndex >= 4) cost += _s.BassStringBonus;
            if (pos.Fret > _s.HighFretThreshold)
                cost += (pos.Fret - _s.HighFretThreshold) * _s.HighFretPenalty;
        }

        cost += arr.Average(a => (double)a.Fret) * _s.AvgFretCost;
        return cost;
    }

    private double HandMoveCostBetween(List<TabPosition> prev, List<TabPosition> cur)
    {
        var pf = prev.Where(p => p.Fret > 0).Select(p => (double)p.Fret).ToList();
        var cf = cur.Where(p  => p.Fret > 0).Select(p => (double)p.Fret).ToList();
        if (pf.Count == 0 || cf.Count == 0) return 0;
        return Math.Abs(cf.Average() - pf.Average()) * _s.HandMoveCost;
    }
}
