using System.Xml.Linq;

namespace PCT.Core;

public class MusicXmlParser
{
    public List<NoteGroup> Parse(Stream stream)
    {
        var doc = XDocument.Load(stream);
        return ParseDoc(doc);
    }

    public List<NoteGroup> ParseFile(string filePath)
    {
        var doc = XDocument.Load(filePath);
        return ParseDoc(doc);
    }

    private List<NoteGroup> ParseDoc(XDocument doc)
    {
        var groups = new List<NoteGroup>();
        NoteGroup current = null;

        foreach (var noteElem in doc.Descendants("note"))
        {
            bool isChord = noteElem.Element("chord") != null;
            var note = ParseSingleNote(noteElem);
            if (note == null) continue;

            if (isChord && current != null && !current.IsRest)
            {
                current.Notes.Add(note);
            }
            else
            {
                current = new NoteGroup();
                current.Notes.Add(note);
                groups.Add(current);
            }
        }

        return groups;
    }

    private Note ParseSingleNote(XElement noteElem)
    {
        var note = new Note();

        if (noteElem.Element("rest") != null)
        {
            note.IsRest = true;
            return note;
        }

        var pitch = noteElem.Element("pitch");
        if (pitch == null) return null;

        note.Step = pitch.Element("step")?.Value ?? "C";

        if (int.TryParse(pitch.Element("octave")?.Value, out int oct))
            note.Octave = oct;

        var alterElem = pitch.Element("alter");
        if (alterElem != null && int.TryParse(alterElem.Value, out int alter))
            note.Alter = alter;

        return note;
    }
}
