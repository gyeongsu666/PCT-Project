namespace PCT.Core;

public class Note
{
    public string Step { get; set; } = "C";
    public int Alter { get; set; } = 0;
    public int Octave { get; set; } = 4;
    public bool IsRest { get; set; } = false;

    public int MidiNumber
    {
        get
        {
            int baseMidi = Step switch
            {
                "C" => 0, "D" => 2, "E" => 4, "F" => 5,
                "G" => 7, "A" => 9, "B" => 11,
                _ => 0
            };
            return (Octave + 1) * 12 + baseMidi + Alter;
        }
    }

    public override string ToString()
    {
        if (IsRest) return "Rest";
        string a = Alter switch { 1 => "#", -1 => "b", 2 => "##", -2 => "bb", _ => "" };
        return $"{Step}{a}{Octave}";
    }
}
