namespace PCT.Core;

public class TabPositionGroup
{
    public List<TabPosition> Positions { get; set; } = new();
    public bool IsRest { get; set; } = false;
    public int DroppedCount { get; set; } = 0;
    public NoteGroup SourceGroup { get; set; }

    // 박자 정보 (NoteGroup에서 그대로 전달)
    public int    MeasureNumber { get; set; } = 1;
    public double BeatPosition  { get; set; } = 0;
    public double Duration      { get; set; } = 1;
    /// <summary>이전 그룹과 마디 번호가 달라질 때 true. TabConverter가 DP 후에 설정한다.</summary>
    public bool   IsNewMeasure  { get; set; } = false;

    public double HandCenter()
    {
        var nonOpen = Positions.Where(p => p.Fret > 0).ToList();
        if (nonOpen.Count == 0) return -1;
        return nonOpen.Average(p => (double)p.Fret);
    }
}
