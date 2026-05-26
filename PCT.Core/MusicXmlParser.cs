using System.Xml;

namespace PCT.Core;

public class MusicXmlParser
{
    public List<NoteGroup> Parse(Stream stream)
    {
        using var reader = XmlReader.Create(stream, new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore });
        return ParseReader(reader);
    }

    public List<NoteGroup> ParseFile(string filePath)
    {
        using var reader = XmlReader.Create(filePath, new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore });
        return ParseReader(reader);
    }

    private List<NoteGroup> ParseReader(XmlReader reader)
    {
        var groups = new List<NoteGroup>();
        NoteGroup current = null;

        while (reader.Read())
        {
            if (reader.NodeType != XmlNodeType.Element || reader.LocalName != "note")
                continue;

            bool isChord = false;
            bool isRest = false;
            string step = "C";
            int octave = 4;
            int alter = 0;

            using var noteReader = reader.ReadSubtree();
            while (noteReader.Read())
            {
                if (noteReader.NodeType != XmlNodeType.Element) continue;

                switch (noteReader.LocalName)
                {
                    case "chord":
                        isChord = true;
                        break;
                    case "rest":
                        isRest = true;
                        break;
                    case "step":
                        step = noteReader.ReadElementContentAsString();
                        break;
                    case "octave":
                        if (int.TryParse(noteReader.ReadElementContentAsString(), out int o))
                            octave = o;
                        break;
                    case "alter":
                        if (int.TryParse(noteReader.ReadElementContentAsString(), out int a))
                            alter = a;
                        break;
                }
            }

            var note = new Note();
            if (isRest)
            {
                note.IsRest = true;
            }
            else
            {
                note.Step = step;
                note.Octave = octave;
                note.Alter = alter;
            }

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
}
