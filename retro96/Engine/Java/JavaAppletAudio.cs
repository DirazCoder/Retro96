using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Text;
#if WINDOWS
using System.Media;
#endif

namespace Retro96.Engine.Java;

public interface IJavaAudioClipPlayer : IDisposable
{
    void Play(byte[] waveData, bool loop);
    void PlayMidi(byte[] midiData, bool loop) =>
        throw new NotSupportedException("This audio backend does not support MIDI playback.");
    void Stop();
}

public sealed class JavaAudioClipState : IDisposable
{
    private readonly Func<CancellationToken, Task<byte[]?>> _load;
    private readonly Func<IJavaAudioClipPlayer> _playerFactory;
    private readonly object _gate = new();
    private IJavaAudioClipPlayer? _player;
    private CancellationTokenSource? _loadCancellation;
    private int _generation;
    private bool _disposed;

    public JavaAudioClipState(
        Func<CancellationToken, Task<byte[]?>> load,
        Func<IJavaAudioClipPlayer>? playerFactory = null)
    {
        _load = load ?? throw new ArgumentNullException(nameof(load));
        _playerFactory = playerFactory ?? (() => new SoundPlayerAudioClipPlayer());
    }

    public void Play(bool loop)
    {
        int generation;
        CancellationTokenSource cancellation;
        lock (_gate)
        {
            if (_disposed) return;
            generation = ++_generation;
            _loadCancellation?.Cancel();
            _loadCancellation?.Dispose();
            cancellation = new CancellationTokenSource();
            _loadCancellation = cancellation;
            StopPlayerLocked();
        }
        _ = LoadAndPlayAsync(generation, loop, cancellation.Token);
    }

    public void Stop()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _generation++;
            _loadCancellation?.Cancel();
            _loadCancellation?.Dispose();
            _loadCancellation = null;
            StopPlayerLocked();
            _player?.Dispose();
            _player = null;
        }
    }

    private async Task LoadAndPlayAsync(int generation, bool loop, CancellationToken cancellationToken)
    {
        try
        {
            byte[]? bytes = await _load(cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (bytes == null || bytes.Length == 0)
                throw new InvalidDataException("Applet audio resource is empty or unavailable.");

            bool midi = IsStandardMidi(bytes);
            if (midi) StandardMidiFile.Parse(bytes);
            byte[] playableData = midi ? bytes : NormalizeToWave(bytes);
            var player = _playerFactory();

            lock (_gate)
            {
                if (_disposed || generation != _generation || cancellationToken.IsCancellationRequested)
                {
                    player.Dispose();
                    return;
                }

                _player?.Dispose();
                _player = player;
                try
                {
                    if (midi) player.PlayMidi(playableData, loop);
                    else player.Play(playableData, loop);
                }
                catch
                {
                    _player = null;
                    player.Dispose();
                    throw;
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            Retro96.DebugLog.WriteException("JavaApplet.audio", ex);
        }
    }

    private void StopPlayerLocked()
    {
        _player?.Stop();
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _generation++;
            _loadCancellation?.Cancel();
            _loadCancellation?.Dispose();
            _loadCancellation = null;
            StopPlayerLocked();
            _player?.Dispose();
            _player = null;
        }
    }

    public static byte[] NormalizeToWave(byte[] data)
    {
        if (data.Length > 16 * 1024 * 1024)
            throw new InvalidDataException("Applet audio exceeds the 16 MiB decode limit.");
        if (data.Length >= 12 &&
            data.AsSpan(0, 4).SequenceEqual("RIFF"u8) &&
            data.AsSpan(8, 4).SequenceEqual("WAVE"u8))
            return NormalizeWave(data);

        if (data.Length >= 12 &&
            data.AsSpan(0, 4).SequenceEqual("FORM"u8) &&
            (data.AsSpan(8, 4).SequenceEqual("AIFF"u8) ||
             data.AsSpan(8, 4).SequenceEqual("AIFC"u8)))
            return NormalizeAiff(data);

        if (data.Length < 24 || !data.AsSpan(0, 4).SequenceEqual(".snd"u8))
            throw new InvalidDataException("Applet audio must be RIFF/WAVE or Sun/NeXT AU data.");

        uint offset = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(4, 4));
        uint declaredSize = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(8, 4));
        uint encoding = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(12, 4));
        uint sampleRate = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(16, 4));
        uint channels = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(20, 4));
        if (offset < 24 || offset > data.Length || channels is < 1 or > 2 ||
            sampleRate is < 4000 or > 192000)
            throw new InvalidDataException("Applet AU audio header is invalid.");

        int available = data.Length - checked((int)offset);
        if (declaredSize != uint.MaxValue && declaredSize > available)
            throw new InvalidDataException("Applet AU data chunk is truncated.");
        int payloadLength = declaredSize == uint.MaxValue
            ? available
            : Math.Min(available, checked((int)Math.Min(declaredSize, int.MaxValue)));
        int bitsPerSample = encoding switch { 1 or 2 or 3 or 4 or 5 or 6 => 16, _ => 0 };
        if (bitsPerSample == 0)
            throw new InvalidDataException($"Applet AU encoding {encoding} is not supported.");

        int sourceBytesPerSample = encoding switch { 1 or 2 => 1, 3 => 2, 4 => 3, 5 or 6 => 4, _ => 0 };
        int inputBlockAlign = checked((int)channels * sourceBytesPerSample);
        if (payloadLength % inputBlockAlign != 0)
            throw new InvalidDataException("Applet AU audio data ends in a partial sample frame.");
        int outputLength = checked(payloadLength / sourceBytesPerSample * 2);
        var wave = CreatePcmWave((int)channels, (int)sampleRate, outputLength);

        var source = data.AsSpan((int)offset, payloadLength);
        var destination = wave.AsSpan(44);
        if (encoding == 1)
        {
            for (int i = 0; i < source.Length; i++)
                BinaryPrimitives.WriteInt16LittleEndian(destination.Slice(i * 2, 2), DecodeMuLaw(source[i]));
        }
        else if (encoding == 2)
        {
            for (int i = 0; i < source.Length; i++)
                BinaryPrimitives.WriteInt16LittleEndian(destination.Slice(i * 2, 2),
                    (short)(unchecked((sbyte)source[i]) << 8));
        }
        else if (encoding == 3)
        {
            for (int i = 0; i + 1 < source.Length; i += 2)
                BinaryPrimitives.WriteInt16LittleEndian(destination.Slice(i, 2),
                    BinaryPrimitives.ReadInt16BigEndian(source.Slice(i, 2)));
        }
        else if (encoding == 4)
        {
            for (int i = 0, o = 0; i < source.Length; i += 3, o += 2)
            {
                int sample = (source[i] << 24) | (source[i + 1] << 16) | (source[i + 2] << 8);
                BinaryPrimitives.WriteInt16LittleEndian(destination.Slice(o, 2), (short)(sample >> 16));
            }
        }
        else if (encoding == 5)
        {
            for (int i = 0, o = 0; i < source.Length; i += 4, o += 2)
            {
                int sample = BinaryPrimitives.ReadInt32BigEndian(source.Slice(i, 4));
                BinaryPrimitives.WriteInt16LittleEndian(destination.Slice(o, 2), (short)(sample >> 16));
            }
        }
        else
        {
            for (int i = 0, o = 0; i < source.Length; i += 4, o += 2)
            {
                float sample = BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32BigEndian(source.Slice(i, 4)));
                BinaryPrimitives.WriteInt16LittleEndian(destination.Slice(o, 2), FloatToPcm16(sample));
            }
        }
        return wave;
    }

    private static byte[] NormalizeWave(byte[] data)
    {
        uint riffLength = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(4, 4));
        if (riffLength < 4 || riffLength > data.Length - 8)
            throw new InvalidDataException("Applet WAVE RIFF size is invalid.");
        int riffEnd = checked(8 + (int)riffLength);
        int offset = 12;
        ushort format = 0, channels = 0, bits = 0;
        uint sampleRate = 0;
        bool extensible = false;
        ReadOnlySpan<byte> samples = default;
        while (offset <= riffEnd - 8)
        {
            var id = data.AsSpan(offset, 4);
            uint chunkLength = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset + 4, 4));
            offset += 8;
            if (chunkLength > riffEnd - offset)
                throw new InvalidDataException("Applet WAVE chunk extends past the end of the file.");
            int length = (int)chunkLength;
            if (id.SequenceEqual("fmt "u8))
            {
                if (length < 16) throw new InvalidDataException("Applet WAVE format chunk is truncated.");
                format = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(offset, 2));
                channels = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(offset + 2, 2));
                sampleRate = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset + 4, 4));
                bits = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(offset + 14, 2));
                if (format == 0xFFFE && length >= 40)
                {
                    extensible = true;
                    format = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(offset + 24, 2));
                }
            }
            else if (id.SequenceEqual("data"u8))
            {
                samples = data.AsSpan(offset, length);
            }
            offset = checked(offset + length + (length & 1));
        }

        if (!extensible && format == 1 && bits == 16 && channels is 1 or 2 &&
            sampleRate is >= 4000 and <= 192000 && samples.Length > 0)
        {
            if (samples.Length % (channels * 2) != 0)
                throw new InvalidDataException("Applet WAVE data ends in a partial sample frame.");
            return data;
        }
        if (format is not (1 or 3) || channels is < 1 or > 2 ||
            sampleRate is < 4000 or > 192000 || samples.Length == 0 ||
            bits is not (8 or 16 or 24 or 32) ||
            (format == 3 && bits != 32))
            throw new InvalidDataException("Applet WAVE encoding or format is not supported.");

        int bytesPerSample = bits / 8;
        int inputAlign = bytesPerSample * channels;
        if (samples.Length % inputAlign != 0)
            throw new InvalidDataException("Applet WAVE data ends in a partial sample frame.");
        int outputLength = checked(samples.Length / bytesPerSample * 2);
        var wave = CreatePcmWave(channels, (int)sampleRate, outputLength);
        var output = wave.AsSpan(44);
        for (int i = 0, o = 0; i < samples.Length; i += bytesPerSample, o += 2)
        {
            short sample = format == 3
                ? FloatToPcm16(BitConverter.Int32BitsToSingle(
                    BinaryPrimitives.ReadInt32LittleEndian(samples.Slice(i, 4))))
                : ConvertIntegerSample(samples.Slice(i, bytesPerSample), bits, bigEndian: false, unsigned8: true);
            BinaryPrimitives.WriteInt16LittleEndian(output.Slice(o, 2), sample);
        }
        return wave;
    }

    private static byte[] NormalizeAiff(byte[] data)
    {
        uint formLength = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(4, 4));
        if (formLength < 4 || formLength > data.Length - 8)
            throw new InvalidDataException("Applet AIFF FORM size is invalid.");
        int formEnd = checked(8 + (int)formLength);
        int offset = 12;
        int channels = 0, bits = 0, sampleRate = 0;
        bool littleEndian = false, floatingPoint = false, muLaw = false;
        byte[]? sampleData = null;
        while (offset <= formEnd - 8)
        {
            var id = data.AsSpan(offset, 4);
            uint rawLength = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(offset + 4, 4));
            offset += 8;
            if (rawLength > formEnd - offset)
                throw new InvalidDataException("Applet AIFF chunk extends past the end of the file.");
            int length = (int)rawLength;
            if (id.SequenceEqual("COMM"u8))
            {
                if (length < 18) throw new InvalidDataException("Applet AIFF common chunk is truncated.");
                channels = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(offset, 2));
                bits = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(offset + 6, 2));
                sampleRate = DecodeExtended80(data.AsSpan(offset + 8, 10));
                if (data.AsSpan(8, 4).SequenceEqual("AIFC"u8))
                {
                    if (length < 22) throw new InvalidDataException("Applet AIFC compression type is missing.");
                    var compression = data.AsSpan(offset + 18, 4);
                    littleEndian = compression.SequenceEqual("sowt"u8);
                    muLaw = compression.SequenceEqual("ulaw"u8);
                    floatingPoint = compression.SequenceEqual("fl32"u8) ||
                        compression.SequenceEqual("FL32"u8) ||
                        compression.SequenceEqual("fl64"u8);
                    bool supported = littleEndian || muLaw || floatingPoint ||
                        compression.SequenceEqual("NONE"u8) ||
                        compression.SequenceEqual("twos"u8);
                    if (!supported) throw new InvalidDataException("Applet AIFC compression is not supported.");
                    if (muLaw) bits = 8;
                    if (floatingPoint) bits = compression.SequenceEqual("fl64"u8) ? 64 : 32;
                }
            }
            else if (id.SequenceEqual("SSND"u8))
            {
                if (length < 8) throw new InvalidDataException("Applet AIFF sound chunk is truncated.");
                uint blockOffset = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(offset, 4));
                if (blockOffset > length - 8)
                    throw new InvalidDataException("Applet AIFF sound offset is outside the data chunk.");
                int soundOffset = offset + 8 + checked((int)blockOffset);
                sampleData = data.AsSpan(soundOffset, length - 8 - checked((int)blockOffset)).ToArray();
            }
            offset = checked(offset + length + (length & 1));
        }
        if (channels is < 1 or > 2 || bits is not (8 or 16 or 24 or 32 or 64) ||
            sampleRate is < 4000 or > 192000 || sampleData == null)
            throw new InvalidDataException("Applet AIFF encoding or format is not supported.");
        if (floatingPoint && bits is not (32 or 64))
            throw new InvalidDataException("Applet AIFC floating-point precision is not supported.");
        int bytesPerSample = muLaw ? 1 : bits / 8;
        if (sampleData.Length % (bytesPerSample * channels) != 0)
            throw new InvalidDataException("Applet AIFF data ends in a partial sample frame.");
        int outputLength = checked(sampleData.Length / bytesPerSample * 2);
        var wave = CreatePcmWave(channels, sampleRate, outputLength);
        var output = wave.AsSpan(44);
        for (int i = 0, o = 0; i < sampleData.Length; i += bytesPerSample, o += 2)
        {
            short sample;
            if (muLaw)
                sample = DecodeMuLaw(sampleData[i]);
            else if (floatingPoint)
            {
                double fp = bits == 32
                    ? BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32BigEndian(sampleData.AsSpan(i, 4)))
                    : BitConverter.Int64BitsToDouble(BinaryPrimitives.ReadInt64BigEndian(sampleData.AsSpan(i, 8)));
                sample = FloatToPcm16((float)fp);
            }
            else
                sample = ConvertIntegerSample(sampleData.AsSpan(i, bytesPerSample), bits,
                    bigEndian: !littleEndian, unsigned8: false);
            BinaryPrimitives.WriteInt16LittleEndian(output.Slice(o, 2), sample);
        }
        return wave;
    }

    private static short ConvertIntegerSample(ReadOnlySpan<byte> sample, int bits, bool bigEndian, bool unsigned8)
    {
        if (bits == 8)
            return unsigned8 ? (short)((sample[0] - 128) << 8) : (short)(unchecked((sbyte)sample[0]) << 8);
        int value = 0;
        if (bigEndian)
        {
            for (int i = 0; i < sample.Length; i++) value = (value << 8) | sample[i];
            value <<= 32 - bits;
            value >>= 16;
        }
        else if (bits == 16)
            value = BinaryPrimitives.ReadInt16LittleEndian(sample);
        else if (bits == 24)
        {
            value = sample[0] | sample[1] << 8 | sample[2] << 16;
            if ((value & 0x800000) != 0) value |= unchecked((int)0xFF000000);
            value >>= 8;
        }
        else
            value = BinaryPrimitives.ReadInt32LittleEndian(sample) >> 16;
        return (short)value;
    }

    private static short FloatToPcm16(float sample) =>
        float.IsNaN(sample) ? (short)0 :
            (short)Math.Round(Math.Clamp(sample, -1f, 1f) * (sample < 0 ? 32768 : 32767));

    private static int DecodeExtended80(ReadOnlySpan<byte> bytes)
    {
        ushort signExponent = BinaryPrimitives.ReadUInt16BigEndian(bytes);
        ulong mantissa = BinaryPrimitives.ReadUInt64BigEndian(bytes.Slice(2, 8));
        int exponent = (signExponent & 0x7FFF) - 16383;
        double rate = Math.ScaleB((double)mantissa / 9223372036854775808d, exponent);
        if ((signExponent & 0x8000) != 0) rate = -rate;
        if (!double.IsFinite(rate) || rate <= 0 || rate > int.MaxValue)
            throw new InvalidDataException("Applet AIFF sample rate is invalid.");
        return (int)Math.Round(rate);
    }

    private static byte[] CreatePcmWave(int channels, int sampleRate, int dataLength)
    {
        int blockAlign = checked(channels * 2);
        int byteRate = checked(sampleRate * blockAlign);
        var wave = new byte[checked(44 + dataLength)];
        "RIFF"u8.CopyTo(wave);
        BinaryPrimitives.WriteInt32LittleEndian(wave.AsSpan(4, 4), wave.Length - 8);
        "WAVEfmt "u8.CopyTo(wave.AsSpan(8));
        BinaryPrimitives.WriteInt32LittleEndian(wave.AsSpan(16, 4), 16);
        BinaryPrimitives.WriteUInt16LittleEndian(wave.AsSpan(20, 2), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(wave.AsSpan(22, 2), checked((ushort)channels));
        BinaryPrimitives.WriteInt32LittleEndian(wave.AsSpan(24, 4), sampleRate);
        BinaryPrimitives.WriteInt32LittleEndian(wave.AsSpan(28, 4), byteRate);
        BinaryPrimitives.WriteUInt16LittleEndian(wave.AsSpan(32, 2), checked((ushort)blockAlign));
        BinaryPrimitives.WriteUInt16LittleEndian(wave.AsSpan(34, 2), 16);
        "data"u8.CopyTo(wave.AsSpan(36));
        BinaryPrimitives.WriteInt32LittleEndian(wave.AsSpan(40, 4), dataLength);
        return wave;
    }

    public static bool IsStandardMidi(ReadOnlySpan<byte> data) =>
        data.Length >= 14 && data[..4].SequenceEqual("MThd"u8)
        && BinaryPrimitives.ReadInt32BigEndian(data.Slice(4, 4)) >= 6;

    private static short DecodeMuLaw(byte value)
    {
        value = (byte)~value;
        int magnitude = ((value & 0x0F) << 3) + 0x84;
        magnitude <<= (value & 0x70) >> 4;
        int sample = (value & 0x80) != 0 ? 0x84 - magnitude : magnitude - 0x84;
        return (short)Math.Clamp(sample, short.MinValue, short.MaxValue);
    }

#if WINDOWS
    private sealed class SoundPlayerAudioClipPlayer : IJavaAudioClipPlayer
    {
        private SoundPlayer? _player;
        private MemoryStream? _stream;
        private string? _midiAlias;
        private string? _midiTempPath;
        private System.Threading.Timer? _midiCompletionTimer;
        private readonly object _midiGate = new();

        public void Play(byte[] waveData, bool loop)
        {
            Stop();
            _stream = new MemoryStream(waveData, writable: false);
            _player = new SoundPlayer(_stream);
            _player.Load();
            if (loop) _player.PlayLooping();
            else _player.Play();
        }

        public void Stop()
        {
            _player?.Stop();
            _player?.Dispose();
            _player = null;
            _stream?.Dispose();
            _stream = null;
            lock (_midiGate) StopMidiLocked();
        }

        public void PlayMidi(byte[] midiData, bool loop)
        {
            if (!IsStandardMidi(midiData))
                throw new InvalidDataException("Applet MIDI resource has an invalid Standard MIDI header.");
            lock (_midiGate)
            {
                StopMidiLocked();
                string path = Path.Combine(Path.GetTempPath(), "Retro96", "AppletMIDI",
                    Guid.NewGuid().ToString("N") + ".mid");
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllBytes(path, midiData);
                string alias = "Retro96AppletMidi" + Guid.NewGuid().ToString("N");
                if (MciSendString($"open \"{path}\" type sequencer alias {alias}", null, 0, IntPtr.Zero) != 0)
                {
                    File.Delete(path);
                    throw new InvalidOperationException("Windows MIDI playback could not open the applet audio resource.");
                }
                _midiAlias = alias;
                _midiTempPath = path;
                if (MciSendString(loop ? $"play {alias} repeat" : $"play {alias}", null, 0, IntPtr.Zero) != 0)
                {
                    StopMidiLocked();
                    throw new InvalidOperationException("Windows MIDI playback could not start the applet audio resource.");
                }
                if (!loop)
                    _midiCompletionTimer = new System.Threading.Timer(
                        _ => PollMidiCompletion(), null, 250, 250);
            }
        }

        private void PollMidiCompletion()
        {
            lock (_midiGate)
            {
                if (_midiAlias == null) return;
                var mode = new StringBuilder(32);
                if (MciSendString($"status {_midiAlias} mode", mode, mode.Capacity, IntPtr.Zero) == 0 &&
                    string.Equals(mode.ToString(), "stopped", StringComparison.OrdinalIgnoreCase))
                    StopMidiLocked();
            }
        }

        private void StopMidiLocked()
        {
            _midiCompletionTimer?.Dispose();
            _midiCompletionTimer = null;
            if (_midiAlias != null)
            {
                _ = MciSendString($"stop {_midiAlias}", null, 0, IntPtr.Zero);
                _ = MciSendString($"close {_midiAlias}", null, 0, IntPtr.Zero);
                _midiAlias = null;
            }
            if (_midiTempPath != null)
            {
                try { File.Delete(_midiTempPath); }
                catch (IOException ex) { Retro96.DebugLog.WriteException("JavaApplet.audio.cleanup", ex); }
                _midiTempPath = null;
            }
        }

        public void Dispose() => Stop();

        [DllImport("winmm.dll", CharSet = CharSet.Unicode, EntryPoint = "mciSendStringW")]
        private static extern int MciSendString(string command, StringBuilder? returnString, int returnLength, IntPtr callback);
    }
#else
    private sealed class SoundPlayerAudioClipPlayer : IJavaAudioClipPlayer
    {
        public void Play(byte[] waveData, bool loop) =>
            throw new PlatformNotSupportedException("Applet audio playback requires the Windows audio backend.");
        public void Stop() { }
        public void Dispose() { }
    }
#endif
}
