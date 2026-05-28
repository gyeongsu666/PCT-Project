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

    public List<TabPositionGroup> Convert(List<NoteGroup> noteGroups)
    {
        var allCandidateGroups = noteGroups.Select(g => GenerateCandidates(g)).ToList();

        int n = allCandidateGroups.Count;
        if (n == 0) return new List<TabPositionGroup>();

        var dp = new double[n][];
        var parent = new int[n][];

        for (int i = 0; i < n; i++)
        {
            dp[i] = new double[allCandidateGroups[i].Count];
            parent[i] = new int[allCandidateGroups[i].Count];
            for (int j = 0; j < allCandidateGroups[i].Count; j++) dp[i][j] = double.MaxValue;
        }

        for (int j = 0; j < allCandidateGroups[0].Count; j++)
            dp[0][j] = CalculateCost(allCandidateGroups[0][j].Positions);

        for (int i = 1; i < n; i++)
        {
            for (int currIdx = 0; currIdx < allCandidateGroups[i].Count; currIdx++)
            {
                double currentSelfCost = CalculateCost(allCandidateGroups[i][currIdx].Positions);

                for (int prevIdx = 0; prevIdx < allCandidateGroups[i - 1].Count; prevIdx++)
                {
                    if (dp[i - 1][prevIdx] == double.MaxValue) continue;

                    double moveCost = HandMoveCostBetween(
                        allCandidateGroups[i - 1][prevIdx].Positions,
                        allCandidateGroups[i][currIdx].Positions);

                    double total = dp[i - 1][prevIdx] + moveCost + currentSelfCost;

                    if (total < dp[i][currIdx])
                    {
                        dp[i][currIdx] = total;
                        parent[i][currIdx] = prevIdx;
                    }
                }
            }
        }

        var result = new List<TabPositionGroup>();
        double minFinalCost = double.MaxValue;
        int lastIdx = 0;

        for (int j = 0; j < allCandidateGroups[n - 1].Count; j++)
        {
            if (dp[n - 1][j] < minFinalCost)
            {
                minFinalCost = dp[n - 1][j];
                lastIdx = j;
            }
        }

        for (int i = n - 1; i >= 0; i--)
        {
            result.Add(allCandidateGroups[i][lastIdx]);
            lastIdx = parent[i][lastIdx];
        }

        result.Reverse();

        // IsNewMeasure: 앞 그룹과 마디 번호가 달라지는 지점을 표시
        if (result.Count > 0) result[0].IsNewMeasure = true;
        for (int i = 1; i < result.Count; i++)
            result[i].IsNewMeasure = result[i].MeasureNumber != result[i - 1].MeasureNumber;

        return result;
    }

    private List<TabPositionGroup> GenerateCandidates(NoteGroup group)
    {
        var candidates = new List<TabPositionGroup>();
        if (group.IsRest)
        {
            candidates.Add(new TabPositionGroup
            {
                IsRest        = true,
                SourceGroup   = group,
                MeasureNumber = group.MeasureNumber,
                BeatPosition  = group.BeatPosition,
                Duration      = group.Duration,
            });
            return candidates;
        }

        var validArrangements = new List<List<TabPosition>>();
        FindArrangements(group.Notes, 0, new List<TabPosition>(), validArrangements);

        foreach (var arr in validArrangements)
        {
            candidates.Add(new TabPositionGroup
            {
                Positions     = arr,
                SourceGroup   = group,
                DroppedCount  = group.Notes.Count - arr.Count,
                MeasureNumber = group.MeasureNumber,
                BeatPosition  = group.BeatPosition,
                Duration      = group.Duration,
            });
        }

        if (candidates.Count == 0)
            candidates.Add(new TabPositionGroup
            {
                IsRest        = true,
                SourceGroup   = group,
                DroppedCount  = group.Notes.Count,
                MeasureNumber = group.MeasureNumber,
                BeatPosition  = group.BeatPosition,
                Duration      = group.Duration,
            });

        return candidates;
    }

    private void FindArrangements(List<Note> notes, int noteIdx, List<TabPosition> current, List<List<TabPosition>> results)
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
                FindArrangements(notes, noteIdx + 1, current, results);
                current.RemoveAt(current.Count - 1);
                foundAny = true;
            }
        }

        if (!foundAny) FindArrangements(notes, noteIdx + 1, current, results);
    }

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

        double avg = arr.Count > 0 ? arr.Average(a => (double)a.Fret) : 0;
        cost += avg * _s.AvgFretCost;

        return cost;
    }

    private double HandMoveCostBetween(List<TabPosition> prev, List<TabPosition> cur)
    {
        var prevFrets = prev.Where(p => p.Fret > 0).Select(p => (double)p.Fret).ToList();
        var curFrets = cur.Where(p => p.Fret > 0).Select(p => (double)p.Fret).ToList();

        if (prevFrets.Count == 0 || curFrets.Count == 0) return 0;

        return Math.Abs(curFrets.Average() - prevFrets.Average()) * _s.HandMoveCost;
    }
}
