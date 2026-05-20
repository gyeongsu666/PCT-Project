namespace PCT.Core;

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
