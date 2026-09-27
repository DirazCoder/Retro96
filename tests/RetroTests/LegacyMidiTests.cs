using System.Buffers.Binary;

namespace RetroTests;

public class LegacyMidiTests
{
    [Fact]
    public void GeneralMidiProgramAndDrumMapsUseLevel1Ordering()
    {
        Check.That(GeneralMidi.GetProgramName(0) == "Acoustic Grand Piano", "program 0 = Acoustic Grand Piano");
        Check.That(GeneralMidi.GetProgramName(24) == "Acoustic Guitar (nylon)", "program 24 = nylon guitar");
        Check.That(GeneralMidi.GetProgramName(40) == "Violin", "program 40 = violin");
        Check.That(GeneralMidi.GetProgramName(56) == "Trumpet", "program 56 = trumpet");
        Check.That(GeneralMidi.GetDrumName(36) == "Bass Drum 1", "drum 36 = bass drum 1");
        Check.That(GeneralMidi.GetDrumName(38) == "Acoustic Snare", "drum 38 = acoustic snare");
        Check.That(GeneralMidi.GetDrumName(42) == "Closed Hi-Hat", "drum 42 = closed hi-hat");
        Check.That(GeneralMidi.IsPercussionChannel(9), "MIDI channel 10 is the percussion channel");
        Check.Done();
    }

    [Fact]
    public void ParseFormatZeroTracksDefaultProgramsVelocityZeroAndControllers()
    {
        byte[] track =
        [
            0x00, 0xC0, 0x28,             // program 40 -> violin
            0x00, 0xB0, 0x07, 0x40,       // channel volume 64
            0x00, 0xB0, 0x0A, 0x20,       // pan 32
            0x00, 0x90, 60, 100,          // note on
            0x10, 0x90, 60, 0,            // velocity-zero note on = note off
            0x00, 0xFF, 0x2F, 0x00
        ];
        byte[] midi = MakeSmf(0, 1, track);
        var parsed = StandardMidiFile.Parse(midi);
        var ch = parsed.Channels[0];
        Check.That(parsed.Format == 0 && parsed.TrackCount == 1, "format 0 file parsed");
        Check.That(ch.Program == 40, "program change selects GM violin");
        Check.That(ch.Volume == 0x40, "controller 7 updates channel volume");
        Check.That(ch.Pan == 0x20, "controller 10 updates pan");
        Check.That(ch.ActiveNotes.Count == 0, "velocity-zero note-on acts as note-off");
        Check.That(Math.Abs(parsed.Events.First(e => e.Command == 0x90).VelocityGain - 100f / 127f) < 0.001f,
            "note-on velocity scales amplitude");
        Check.Done();
    }

    [Fact]
    public void PercussionChannelIgnoresProgramChangeAndUsesDrumNotes()
    {
        byte[] track =
        [
            0x00, 0xC9, 0x28,             // ignored program change on channel 10
            0x00, 0x99, 36, 110,          // bass drum
            0x00, 0x99, 38, 0,            // velocity-zero note-off
            0x00, 0xB9, 123, 0,           // all notes off
            0x00, 0xFF, 0x2F, 0x00
        ];
        var parsed = StandardMidiFile.Parse(MakeSmf(0, 1, track));
        var drums = parsed.Channels[9];
        Check.That(drums.Program == 0, "channel 10 program remains default piano/unused");
        Check.That(drums.IsPercussion, "channel 10 is marked percussion");
        Check.That(GeneralMidi.GetDrumName(36) == "Bass Drum 1", "note 36 resolves through the GM drum map");
        Check.That(GeneralMidi.ResolveNoteName(9, 36) == "Bass Drum 1", "channel 10 note 36 is mapped to bass drum");
        Check.That(drums.ActiveNotes.Count == 0, "controller 123 clears active percussion notes");
        Check.Done();
    }

    [Fact]
    public void ResetAllControllersRestoresControllerDefaultsWithoutChangingProgram()
    {
        byte[] track =
        [
            0x00, 0xC1, 0x24,             // program 36
            0x00, 0xB1, 0x07, 0x10,
            0x00, 0xB1, 0x0A, 0x10,
            0x00, 0xB1, 121, 0,
            0x00, 0xFF, 0x2F, 0x00
        ];
        var ch = StandardMidiFile.Parse(MakeSmf(0, 1, track)).Channels[1];
        Check.That(ch.Program == 36, "reset all controllers does not reset the program");
        Check.That(ch.Volume == 100, "controller 121 restores GM channel volume default");
        Check.That(ch.Pan == 64, "controller 121 restores centered pan");
        Check.Done();
    }

    [Fact]
    public void LegacyLoopParsingMatchesSinglePlayDefaultsAndInfiniteModes()
    {
        Check.That(LegacyMidiLoop.ParseAutoStart(null), "embed autostart defaults true");
        Check.That(!LegacyMidiLoop.ParseAutoStart("false"), "embed autostart=false disables automatic play");
        Check.That(LegacyMidiLoop.ParseEmbedLoop("false") == 1, "embed loop=false plays once");
        Check.That(LegacyMidiLoop.ParseEmbedLoop("true") == -1, "embed loop=true is indefinite");
        Check.That(LegacyMidiLoop.ParseEmbedLoop("3") == 3, "embed integer loop count is supported");
        Check.That(LegacyMidiLoop.ParseBgSoundLoop("1") == 1, "bgsound loop=1 plays once");
        Check.That(LegacyMidiLoop.ParseBgSoundLoop("-1") == -1, "bgsound loop=-1 is indefinite");
        Check.That(LegacyMidiLoop.ParseBgSoundLoop("infinite") == -1, "bgsound loop=infinite is indefinite");
        Check.That(LegacyMidiLoop.ParseVolume("-5") == 0, "volume clamps to 0");
        Check.That(LegacyMidiLoop.ParseVolume("125") == 100, "volume clamps to 100");
        Check.Done();
    }

    private static byte[] MakeSmf(int format, int tracks, byte[] track)
    {
        byte[] result = new byte[14 + 8 + track.Length];
        result[0] = (byte)'M'; result[1] = (byte)'T'; result[2] = (byte)'h'; result[3] = (byte)'d';
        BinaryPrimitives.WriteUInt32BigEndian(result.AsSpan(4, 4), 6);
        BinaryPrimitives.WriteUInt16BigEndian(result.AsSpan(8, 2), (ushort)format);
        BinaryPrimitives.WriteUInt16BigEndian(result.AsSpan(10, 2), (ushort)tracks);
        BinaryPrimitives.WriteUInt16BigEndian(result.AsSpan(12, 2), 96);
        int p = 14;
        result[p++] = (byte)'M'; result[p++] = (byte)'T'; result[p++] = (byte)'r'; result[p++] = (byte)'k';
        BinaryPrimitives.WriteUInt32BigEndian(result.AsSpan(p, 4), (uint)track.Length); p += 4;
        Buffer.BlockCopy(track, 0, result, p, track.Length);
        return result;
    }
}
