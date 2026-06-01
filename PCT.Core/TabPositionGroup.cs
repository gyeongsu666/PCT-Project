namespace PCT.Core;

/// <summary>
/// 하나의 음표 이벤트에 대한 타브 운지 결과.
/// 박자/마디 정보는 상위 TabMeasureBeat 가 보유한다.
/// </summary>
public class TabPositionGroup
{
    public List<TabPosition> Positions { get; set; } = new();
    public bool IsRest { get; set; } = false;
    public int DroppedCount { get; set; } = 0;
    public NoteGroup SourceGroup { get; set; }

    public double HandCenter()
    {
        var nonOpen = Positions.Where(p => p.Fret > 0).ToList();
        if (nonOpen.Count == 0) return -1;
        return nonOpen.Average(p => (double)p.Fret);
    }
}
