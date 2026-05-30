using System.Xml;

namespace PCT.Core;

public class MusicXmlParser
{
    public List<MeasureBeat> Parse(Stream stream)
    {
        using var reader = XmlReader.Create(stream, new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore });
        return ParseReader(reader);
    }

    public List<MeasureBeat> ParseFile(string filePath)
    {
        using var reader = XmlReader.Create(filePath, new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore });
        return ParseReader(reader);
    }

    private List<MeasureBeat> ParseReader(XmlReader reader)
    {
        var beats        = new List<MeasureBeat>();
        MeasureBeat currentBeat      = null;
        NoteGroup   currentNoteGroup = null;   // 화음 누적용

        int    measureNumber = 0;
        int    divisions     = 1;    // 4분음표 1박 = divisions 단위
        int    beatType      = 4;    // 박자표 분모 (4/4 → 4, 6/8 → 8)
        double beatSizeInQN  = 1.0;  // 1박의 크기 (4분음표 단위)
        double qnAccumulator = 0;    // 마디 내 4분음표 누산기 (voice 1 전용)

        while (reader.Read())
        {
            if (reader.NodeType != XmlNodeType.Element) continue;

            switch (reader.LocalName)
            {
                // ── 마디 경계 ──────────────────────────────────────────────────
                case "measure":
                    measureNumber++;
                    qnAccumulator    = 0;
                    currentNoteGroup = null;
                    break;

                // ── divisions ─────────────────────────────────────────────────
                case "divisions":
                    if (int.TryParse(reader.ReadElementContentAsString(), out int d) && d > 0)
                        divisions = d;
                    break;

                // ── 박자표: <time><beats>N</beats><beat-type>D</beat-type></time>
                case "time":
                {
                    using var timeReader = reader.ReadSubtree();
                    while (timeReader.Read())
                    {
                        if (timeReader.NodeType != XmlNodeType.Element) continue;
                        if (timeReader.LocalName == "beat-type" &&
                            int.TryParse(timeReader.ReadElementContentAsString(), out int bt) && bt > 0)
                        {
                            beatType     = bt;
                            beatSizeInQN = 4.0 / beatType;  // 1박 = 4분음표 몇 개
                        }
                    }
                    break;
                }

                // ── backup: 다성부 표기에서 이전 위치로 되감기 ──────────────
                // Voice 1 끝난 뒤 <backup>으로 마디 처음으로 돌아가고 Voice 2를 씀
                case "backup":
                {
                    using var bkReader = reader.ReadSubtree();
                    while (bkReader.Read())
                    {
                        if (bkReader.NodeType != XmlNodeType.Element) continue;
                        if (bkReader.LocalName == "duration" &&
                            int.TryParse(bkReader.ReadElementContentAsString(), out int bkDur) && bkDur > 0)
                        {
                            qnAccumulator = Math.Max(0, qnAccumulator - (double)bkDur / divisions);
                            currentNoteGroup = null;  // 성부 전환 시 화음 누적 리셋
                        }
                    }
                    break;
                }

                // ── forward: 쉼표 없이 위치만 앞으로 이동 ────────────────────
                case "forward":
                {
                    using var fwdReader = reader.ReadSubtree();
                    while (fwdReader.Read())
                    {
                        if (fwdReader.NodeType != XmlNodeType.Element) continue;
                        if (fwdReader.LocalName == "duration" &&
                            int.TryParse(fwdReader.ReadElementContentAsString(), out int fwdDur) && fwdDur > 0)
                        {
                            qnAccumulator += (double)fwdDur / divisions;
                        }
                    }
                    break;
                }

                // ── note ──────────────────────────────────────────────────────
                case "note":
                {
                    bool   isChord     = false;
                    bool   isRest      = false;
                    string step        = "C";
                    int    octave      = 4;
                    int    alter       = 0;
                    int    rawDuration = divisions;
                    int    voice       = 1;

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

                    double durationInQN = rawDuration > 0
                        ? (double)rawDuration / divisions
                        : 1.0;

                    var note = new Note();
                    if (isRest) note.IsRest = true;
                    else        { note.Step = step; note.Octave = octave; note.Alter = alter; }

                    if (isChord && currentNoteGroup != null && !currentNoteGroup.IsRest)
                    {
                        // 화음 — 같은 NoteGroup에 추가, 박 누산기 건드리지 않음
                        currentNoteGroup.Notes.Add(note);
                    }
                    else
                    {
                        // 새 음표 이벤트: 정수 박 번호 계산
                        // 1e-9 epsilon: 부동소수점 오차로 박 경계를 살짝 넘는 경우 방지
                        int beatNumber = (int)(qnAccumulator / beatSizeInQN + 1e-9) + 1;

                        // 해당 (마디, 박)의 MeasureBeat를 찾거나 새로 만든다
                        if (currentBeat == null ||
                            currentBeat.MeasureNumber != measureNumber ||
                            currentBeat.BeatNumber    != beatNumber)
                        {
                            currentBeat = new MeasureBeat
                            {
                                MeasureNumber = measureNumber,
                                BeatNumber    = beatNumber,
                            };
                            beats.Add(currentBeat);
                        }

                        var ng = new NoteGroup();
                        ng.Notes.Add(note);
                        currentBeat.Notes.Add(ng);
                        currentNoteGroup = ng;

                        // <backup>이 성부 전환을 처리하므로 모든 성부가 누산기를 진행
                        qnAccumulator += durationInQN;
                    }
                    break;
                }
            }
        }

        return beats;
    }
}
