namespace PCT.Core;

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
