namespace PCT.Core;

public class TabPosition
{
    public int StringIndex { get; set; }
    public int Fret { get; set; }
    public bool IsUnplayable { get; set; }
    public Note SourceNote { get; set; }

    public override string ToString()
    {
        if (IsUnplayable) return "X";
        return $"{GuitarTuning.StringNames[StringIndex]}:{Fret}";
    }
}
