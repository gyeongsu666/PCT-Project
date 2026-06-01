namespace PCT.Core;

/// <summary>
/// 하나의 음표 이벤트 (단음 또는 화음).
/// 박자/마디 정보는 상위 MeasureBeat 가 보유한다.
/// </summary>
public class NoteGroup
{
    public List<Note> Notes { get; set; } = new();
    public bool IsRest => Notes.Count == 0 || Notes.All(n => n.IsRest);

    public override string ToString()
    {
        if (IsRest) return "Rest";
        if (Notes.Count == 1) return Notes[0].ToString();
        return "[" + string.Join("+", Notes) + "]";
    }
}
