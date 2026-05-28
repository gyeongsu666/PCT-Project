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
        var groups  = new List<NoteGroup>();
        NoteGroup current = null;

        int    measureNumber    = 0;
        int    divisions        = 1;      // 4분음표 1박 = divisions 단위
        double beatAccumulator  = 0;      // 현재 마디 안에서의 누적 박수

        while (reader.Read())
        {
            if (reader.NodeType != XmlNodeType.Element) continue;

            switch (reader.LocalName)
            {
                // ── 마디 경계: 번호 올리고 박 누산기 초기화 ──────────────
                case "measure":
                    measureNumber++;
                    beatAccumulator = 0;
                    break;

                // ── divisions: 4분음표 1박이 몇 단위인지 ─────────────────
                case "divisions":
                    if (int.TryParse(reader.ReadElementContentAsString(), out int d) && d > 0)
                        divisions = d;
                    break;

                // ── note ─────────────────────────────────────────────────
                case "note":
                {
                    bool   isChord     = false;
                    bool   isRest      = false;
                    string step        = "C";
                    int    octave      = 4;
                    int    alter       = 0;
                    int    rawDuration = divisions; // 기본값 = 4분음표 1박
                    int    voice       = 1;         // MusicXML <voice> 번호

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
                            case "duration":
                                if (int.TryParse(noteReader.ReadElementContentAsString(), out int dur))
                                    rawDuration = dur;
                                break;
                            case "voice":
                                if (int.TryParse(noteReader.ReadElementContentAsString(), out int v))
                                    voice = v;
                                break;
                        }
                    }

                    double durationBeats = rawDuration > 0
                        ? (double)rawDuration / divisions
                        : 1.0;

                    var note = new Note();
                    if (isRest) { note.IsRest = true; }
                    else        { note.Step = step; note.Octave = octave; note.Alter = alter; }

                    if (isChord && current != null && !current.IsRest)
                    {
                        // 화음 음표는 같은 그룹에 추가. 박 누산기 건드리지 않음.
                        current.Notes.Add(note);
                    }
                    else
                    {
                        current = new NoteGroup
                        {
                            MeasureNumber = measureNumber,
                            BeatPosition  = beatAccumulator,
                            Duration      = durationBeats,
                        };
                        current.Notes.Add(note);
                        groups.Add(current);

                        // 박 누산기는 성부 1만 진행시킨다.
                        // 성부 2+ 는 <backup>으로 시간을 되돌린 뒤 쓰여지므로
                        // 누산기를 그대로 두면 성부 1 기준 박 위치를 그대로 상속한다.
                        if (voice == 1)
                            beatAccumulator += durationBeats;
                    }
                    break;
                }
            }
        }

        return groups;
    }
}
