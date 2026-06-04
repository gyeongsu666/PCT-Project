namespace PCT.Core;

/// <summary>
/// 하나의 음표 이벤트 (단음 또는 화음).
/// 박자/마디 정보는 상위 MeasureBeat 가 보유한다.
/// </summary>
public class NoteGroup
{
    public List<Note> Notes { get; set; } = new();
    public bool IsRest => Notes.Count == 0 || Notes.All(n => n.IsRest);

    /// <summary>이 음표 이벤트의 길이 (4분음표 단위). 4분음표=1.0, 8분음표=0.5, 2분음표=2.0.</summary>
    public double DurationInQN { get; set; } = 1.0;

    public override string ToString()
    {
        if (IsRest) return "Rest";
        if (Notes.Count == 1) return Notes[0].ToString();
        return "[" + string.Join("+", Notes) + "]";
    }
}
