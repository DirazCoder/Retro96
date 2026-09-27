using System.Collections.ObjectModel;
using System.Text;

namespace Retro96;

/// <summary>
/// General MIDI Level 1 definitions and a small Standard MIDI File parser.
/// Playback is handed to the Windows MIDI sequencer after validation; the
/// parsed channel state makes the browser-side GM rules explicit and testable.
/// </summary>
public static class GeneralMidi
{
    public const int PercussionChannel = 9; // MIDI channel 10 in 1-based form.

    public static readonly IReadOnlyList<string> ProgramNames = Array.AsReadOnly(new[] {
        "Acoustic Grand Piano", "Bright Acoustic Piano", "Electric Grand Piano", "Honky-tonk Piano",
        "Electric Piano 1", "Electric Piano 2", "Harpsichord", "Clavinet",
        "Celesta", "Glockenspiel", "Music Box", "Vibraphone", "Marimba", "Xylophone", "Tubular Bells", "Dulcimer",
        "Drawbar Organ", "Percussive Organ", "Rock Organ", "Church Organ", "Reed Organ", "Accordion", "Harmonica", "Tango Accordion",
        "Acoustic Guitar (nylon)", "Acoustic Guitar (steel)", "Electric Guitar (jazz)", "Electric Guitar (clean)",
        "Electric Guitar (muted)", "Overdriven Guitar", "Distortion Guitar", "Guitar Harmonics",
        "Acoustic Bass", "Electric Bass (finger)", "Electric Bass (pick)", "Fretless Bass", "Slap Bass 1", "Slap Bass 2", "Synth Bass 1", "Synth Bass 2",
        "Violin", "Viola", "Cello", "Contrabass", "Tremolo Strings", "Pizzicato Strings", "Orchestral Harp", "Timpani",
        "String Ensemble 1", "String Ensemble 2", "Synth Strings 1", "Synth Strings 2", "Choir Aahs", "Voice Oohs", "Synth Choir", "Orchestra Hit",
        "Trumpet", "Trombone", "Tuba", "Muted Trumpet", "French Horn", "Brass Section", "Synth Brass 1", "Synth Brass 2",
        "Soprano Sax", "Alto Sax", "Tenor Sax", "Baritone Sax", "Oboe", "English Horn", "Bassoon", "Clarinet",
        "Piccolo", "Flute", "Recorder", "Pan Flute", "Blown Bottle", "Shakuhachi", "Whistle", "Ocarina",
        "Lead 1 (square)", "Lead 2 (sawtooth)", "Lead 3 (calliope)", "Lead 4 (chiff)", "Lead 5 (charang)", "Lead 6 (voice)", "Lead 7 (fifths)", "Lead 8 (bass + lead)",
        "Pad 1 (new age)", "Pad 2 (warm)", "Pad 3 (polysynth)", "Pad 4 (choir)", "Pad 5 (bowed)", "Pad 6 (metallic)", "Pad 7 (halo)", "Pad 8 (sweep)",
        "FX 1 (rain)", "FX 2 (soundtrack)", "FX 3 (crystal)", "FX 4 (atmosphere)", "FX 5 (brightness)", "FX 6 (goblins)", "FX 7 (echoes)", "FX 8 (sci-fi)",
        "Sitar", "Banjo", "Shamisen", "Koto", "Kalimba", "Bag pipe", "Fiddle", "Shanai",
        "Tinkle Bell", "Agogo", "Steel Drums", "Woodblock", "Taiko Drum", "Melodic Tom", "Synth Drum", "Reverse Cymbal",
        "Guitar Fret Noise", "Breath Noise", "Seashore", "Bird Tweet", "Telephone Ring", "Helicopter", "Applause", "Gunshot"
    });

    public static readonly IReadOnlyDictionary<int, string> DrumNotes = new ReadOnlyDictionary<int, string>(new Dictionary<int, string>
    {
        [35] = "Acoustic Bass Drum", [36] = "Bass Drum 1", [37] = "Side Stick", [38] = "Acoustic Snare",
        [39] = "Hand Clap", [40] = "Electric Snare", [41] = "Low Floor Tom", [42] = "Closed Hi-Hat",
        [43] = "High Floor Tom", [44] = "Pedal Hi-Hat", [45] = "Low Tom", [46] = "Open Hi-Hat",
        [47] = "Low-Mid Tom", [48] = "Hi-Mid Tom", [49] = "Crash Cymbal 1", [50] = "High Tom",
        [51] = "Ride Cymbal 1", [52] = "Chinese Cymbal", [53] = "Ride Bell", [54] = "Tambourine",
        [55] = "Splash Cymbal", [56] = "Cowbell", [57] = "Crash Cymbal 2", [58] = "Vibraslap",
        [59] = "Ride Cymbal 2", [60] = "Hi Bongo", [61] = "Low Bongo", [62] = "Mute Hi Conga",
        [63] = "Open Hi Conga", [64] = "Low Conga", [65] = "High Timbale", [66] = "Low Timbale",
        [67] = "High Agogo", [68] = "Low Agogo", [69] = "Cabasa", [70] = "Maracas",
        [71] = "Short Whistle", [72] = "Long Whistle", [73] = "Short Guiro", [74] = "Long Guiro",
        [75] = "Claves", [76] = "Hi Wood Block", [77] = "Low Wood Block", [78] = "Mute Cuica",
        [79] = "Open Cuica", [80] = "Mute Triangle", [81] = "Open Triangle"
    });

    public static string GetProgramName(int program) =>
        program is >= 0 and < 128 ? ProgramNames[program] : ProgramNames[0];

    public static string? GetDrumName(int note) => DrumNotes.TryGetValue(note, out var name) ? name : null;

    public static bool IsPercussionChannel(int zeroBasedChannel) => zeroBasedChannel == PercussionChannel;

    public static string ResolveNoteName(int zeroBasedChannel, int note, int program = 0) =>
        IsPercussionChannel(zeroBasedChannel)
            ? (GetDrumName(note) ?? "Unmapped percussion note")
            : GetProgramName(program);

    public static float VelocityGain(int velocity) => Math.Clamp(velocity, 0, 127) / 127f;
}

public sealed class StandardMidiFile
{
    public int Format { get; }
    public int TrackCount { get; }
    public int Division { get; }
    public IReadOnlyList<MidiChannelState> Channels { get; }
    public IReadOnlyList<MidiFileEvent> Events { get; }

    private StandardMidiFile(int format, int trackCount, int division,
                             IReadOnlyList<MidiChannelState> channels,
                             IReadOnlyList<MidiFileEvent> events)
    {
        Format = format;
        TrackCount = trackCount;
        Division = division;
        Channels = channels;
        Events = events;
    }

    public static StandardMidiFile Parse(byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        return Parse(bytes.AsSpan());
    }

    public static StandardMidiFile Parse(ReadOnlySpan<byte> data)
    {
        int p = 0;
        ExpectChunk(data, ref p, "MThd");
        int headerLength = ReadInt32(data, ref p);
        if (headerLength < 6 || headerLength > data.Length - p)
            throw new InvalidDataException("Invalid Standard MIDI File header length.");

        int format = ReadUInt16(data, ref p);
        int trackCount = ReadUInt16(data, ref p);
        int division = ReadInt16(data, ref p);
        p += headerLength - 6;

        if (format is not (0 or 1))
            throw new InvalidDataException("Retro96 supports Standard MIDI File format 0 and 1 only.");
        if (trackCount <= 0)
            throw new InvalidDataException("Standard MIDI File contains no tracks.");

        var events = new List<MidiFileEvent>();
        for (int track = 0; track < trackCount; track++)
            ParseTrack(data, ref p, track, events);

        var channels = Enumerable.Range(0, 16).Select(i => new MidiChannelState(i)).ToArray();
        foreach (var e in events.OrderBy(e => e.Tick).ThenBy(e => e.Track).ThenBy(e => e.Order))
        {
            if (!e.IsChannelEvent) continue;
            var ch = channels[e.Channel];
            switch (e.Command)
            {
                case 0x80: // note off
                    ch.ActiveNotes.Remove(e.Data1);
                    ch.ActiveNoteVelocities.Remove(e.Data1);
                    break;
                case 0x90: // note on; velocity 0 is note off
                    if (e.Data2 == 0)
                    {
                        ch.ActiveNotes.Remove(e.Data1);
                        ch.ActiveNoteVelocities.Remove(e.Data1);
                    }
                    else
                    {
                        ch.ActiveNotes.Add(e.Data1);
                        ch.ActiveNoteVelocities[e.Data1] = e.Data2;
                    }
                    break;
                case 0xB0: // controllers
                    switch (e.Data1)
                    {
                        case 7: ch.Volume = e.Data2; break;
                        case 10: ch.Pan = e.Data2; break;
                        case 121: ch.ResetControllers(); break;
                        case 123:
                            ch.ActiveNotes.Clear();
                            ch.ActiveNoteVelocities.Clear();
                            break;
                    }
                    break;
                case 0xC0:
                    // MIDI channel 10 is percussion regardless of program change.
                    if (!GeneralMidi.IsPercussionChannel(e.Channel))
                        ch.Program = e.Data1;
                    break;
            }
        }

        return new StandardMidiFile(format, trackCount, division,
            Array.AsReadOnly(channels), events.AsReadOnly());
    }

    private static void ParseTrack(ReadOnlySpan<byte> data, ref int p, int track, List<MidiFileEvent> events)
    {
        ExpectChunk(data, ref p, "MTrk");
        int length = ReadInt32(data, ref p);
        if (length < 0 || length > data.Length - p)
            throw new InvalidDataException("Invalid MIDI track length.");
        int end = p + length;
        byte runningStatus = 0;
        long tick = 0;
        int order = 0;

        while (p < end)
        {
            uint delta = ReadVlq(data, ref p, end);
            tick += delta;
            if (p >= end) throw new InvalidDataException("Truncated MIDI event.");

            byte status = data[p++];
            byte firstData = 0;
            bool hasFirstData = false;
            if (status < 0x80)
            {
                if (runningStatus < 0x80 || runningStatus >= 0xF0)
                    throw new InvalidDataException("Invalid MIDI running status.");
                firstData = status;
                hasFirstData = true;
                status = runningStatus;
            }
            else if (status < 0xF0)
            {
                runningStatus = status;
            }
            else
            {
                runningStatus = 0;
            }

            if (status >= 0xF8 && status <= 0xFE)
            {
                // Real-time bytes may legally occur in a MIDI stream. Ignore them.
                continue;
            }

            if ((status & 0xF0) is 0x80 or 0x90 or 0xA0 or 0xB0 or 0xE0)
            {
                byte d1 = hasFirstData ? firstData : ReadByte(data, ref p, end);
                byte d2 = ReadByte(data, ref p, end);
                events.Add(MidiFileEvent.FromChannelEvent(tick, track, order++, (byte)(status & 0xF0), (byte)(status & 0x0F), d1, d2));
                continue;
            }

            if ((status & 0xF0) is 0xC0 or 0xD0)
            {
                byte d1 = hasFirstData ? firstData : ReadByte(data, ref p, end);
                events.Add(MidiFileEvent.FromChannelEvent(tick, track, order++, (byte)(status & 0xF0), (byte)(status & 0x0F), d1, 0));
                continue;
            }

            switch (status)
            {
                case 0xF0:
                case 0xF7:
                    {
                        int len = checked((int)ReadVlq(data, ref p, end));
                        if (len > end - p) throw new InvalidDataException("Truncated MIDI SysEx event.");
                        p += len;
                        break;
                    }
                case 0xFF:
                    {
                        byte metaType = ReadByte(data, ref p, end);
                        int len = checked((int)ReadVlq(data, ref p, end));
                        if (len > end - p) throw new InvalidDataException("Truncated MIDI meta event.");
                        events.Add(MidiFileEvent.Meta(tick, track, order++, metaType, data.Slice(p, len).ToArray()));
                        p += len;
                        if (metaType == 0x2F) p = end;
                        break;
                    }
                default:
                    throw new InvalidDataException($"Unsupported MIDI status byte 0x{status:X2}.");
            }
        }
    }

    private static void ExpectChunk(ReadOnlySpan<byte> data, ref int p, string expected)
    {
        if (p + 4 > data.Length || Encoding.ASCII.GetString(data.Slice(p, 4)) != expected)
            throw new InvalidDataException($"Expected {expected} chunk.");
        p += 4;
    }

    private static byte ReadByte(ReadOnlySpan<byte> data, ref int p, int end)
    {
        if (p >= end) throw new InvalidDataException("Truncated MIDI event data.");
        return data[p++];
    }

    private static int ReadUInt16(ReadOnlySpan<byte> data, ref int p)
    {
        if (p + 2 > data.Length) throw new InvalidDataException("Truncated MIDI header.");
        int value = (data[p] << 8) | data[p + 1];
        p += 2;
        return value;
    }

    private static int ReadInt16(ReadOnlySpan<byte> data, ref int p)
    {
        short value = (short)ReadUInt16(data, ref p);
        return value;
    }

    private static int ReadInt32(ReadOnlySpan<byte> data, ref int p)
    {
        if (p + 4 > data.Length) throw new InvalidDataException("Truncated MIDI chunk length.");
        int value = (data[p] << 24) | (data[p + 1] << 16) | (data[p + 2] << 8) | data[p + 3];
        p += 4;
        return value;
    }

    private static uint ReadVlq(ReadOnlySpan<byte> data, ref int p, int end)
    {
        uint value = 0;
        for (int i = 0; i < 4; i++)
        {
            byte b = ReadByte(data, ref p, end);
            value = (value << 7) | (uint)(b & 0x7F);
            if ((b & 0x80) == 0) return value;
        }
        throw new InvalidDataException("Invalid MIDI variable-length quantity.");
    }
}

public sealed class MidiChannelState
{
    public int Channel { get; }
    public byte Program { get; internal set; }
    public byte Volume { get; internal set; } = 127;
    public byte Pan { get; internal set; } = 64;
    public HashSet<int> ActiveNotes { get; } = new();
    public Dictionary<int, byte> ActiveNoteVelocities { get; } = new();
    public bool IsPercussion => GeneralMidi.IsPercussionChannel(Channel);

    internal MidiChannelState(int channel) { Channel = channel; }

    internal void ResetControllers()
    {
        Volume = 100;
        Pan = 64;
    }
}

public sealed record MidiFileEvent(
    long Tick,
    int Track,
    int Order,
    bool IsChannelEvent,
    int Channel,
    byte Command,
    byte Data1,
    byte Data2,
    byte? MetaType,
    byte[]? MetaData)
{
    public static MidiFileEvent FromChannelEvent(long tick, int track, int order, byte command, byte channel, byte data1, byte data2) =>
        new(tick, track, order, true, channel, command, data1, data2, null, null);

    public static MidiFileEvent Meta(long tick, int track, int order, byte metaType, byte[] data) =>
        new(tick, track, order, false, 0, 0, 0, 0, metaType, data);

    /// <summary>Amplitude factor for note-on velocity, 0..1; zero means note-off.</summary>
    public float VelocityGain => Command == 0x90 ? GeneralMidi.VelocityGain(Data2) : 0f;

    public string? SoundName(byte program = 0) =>
        !IsChannelEvent || Command is not (0x80 or 0x90)
            ? null
            : GeneralMidi.IsPercussionChannel(Channel)
                ? GeneralMidi.GetDrumName(Data1)
                : GeneralMidi.GetProgramName(program);
}

public static class LegacyMidiLoop
{
    /// <summary>Returns the total number of plays: 1 = once, -1 = indefinitely.</summary>
    public static int ParseEmbedLoop(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Equals("false", StringComparison.OrdinalIgnoreCase))
            return 1;
        if (value.Equals("true", StringComparison.OrdinalIgnoreCase))
            return -1;
        return int.TryParse(value.Trim(), out int count) && count > 1 ? count : 1;
    }

    public static int ParseBgSoundLoop(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return 1;
        if (value.Equals("-1", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("infinite", StringComparison.OrdinalIgnoreCase))
            return -1;
        return int.TryParse(value.Trim(), out int count) && count > 1 ? count : 1;
    }

    public static bool ParseAutoStart(string? value) =>
        string.IsNullOrWhiteSpace(value) || value.Equals("true", StringComparison.OrdinalIgnoreCase);

    public static bool IsTrue(string? value) => value?.Trim().Equals("true", StringComparison.OrdinalIgnoreCase) == true;

    public static int ParseVolume(string? value, int fallback = 100)
    {
        if (!int.TryParse(value?.Trim(), out int volume)) return Math.Clamp(fallback, 0, 100);
        return Math.Clamp(volume, 0, 100);
    }
}
