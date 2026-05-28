namespace PCT.Core;

public class NoteGroup
{
    public List<Note> Notes { get; set; } = new();
    public bool IsRest => Notes.Count == 0 || Notes.All(n => n.IsRest);

    // 박자 정보 (MusicXmlParser가 <divisions>/<duration>/<measure>에서 채운다)
    public int    MeasureNumber { get; set; } = 1;   // 1-based 마디 번호
    public double BeatPosition  { get; set; } = 0;   // 마디 내 위치 (0 = 1박, 1 = 2박 …)
    public double Duration      { get; set; } = 1;   // 음표 길이 (4분음표 = 1)

    public override string ToString()
    {
        if (IsRest) return "Rest";
        if (Notes.Count == 1) return Notes[0].ToString();
        return "[" + string.Join("+", Notes) + "]";
    }
}
