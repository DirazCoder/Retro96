using System.Runtime.InteropServices;

namespace Retro96.Plugins;

internal sealed class PluginPcmMixer : IDisposable
{
    private const int BufferCount = 4;
    private const int BufferMilliseconds = 20;
    private readonly object _sync = new();
    private readonly Dictionary<string, Source> _sources = new(StringComparer.Ordinal);
    private readonly WaveOutCallback _callback;
    private readonly Dictionary<IntPtr, WaveBuffer> _buffers = new();
    private IntPtr _waveOut;
    private PluginPcmFormat? _format;
    private int _disposed;

    public PluginPcmMixer() => _callback = OnWaveOut;

    public Task PlayAsync(string key, byte[] pcm, PluginPcmFormat format, CancellationToken ct)
    {
        Validate(pcm, format);
        ct.ThrowIfCancellationRequested();
        lock (_sync)
        {
            EnsureFormat(format);
            var source = GetOrCreateSource(key, muted: false);
            source.Replace(pcm);
            source.Muted = false;
            EnsureOutput();
        }
        return Task.CompletedTask;
    }

    public Task PushAsync(string key, byte[] pcm, PluginPcmFormat format, CancellationToken ct, bool initiallyMuted)
    {
        Validate(pcm, format);
        ct.ThrowIfCancellationRequested();
        lock (_sync)
        {
            EnsureFormat(format);
            var source = GetOrCreateSource(key, initiallyMuted);
            source.Append(pcm);
            EnsureOutput();
        }
        return Task.CompletedTask;
    }

    public void SetMuted(string key, bool muted)
    {
        lock (_sync) GetOrCreateSource(key, muted).Muted = muted;
    }

    public void Unmute(string key)
    {
        lock (_sync)
        {
            if (_sources.TryGetValue(key, out var source)) source.Muted = false;
        }
    }

    public void Remove(string key)
    {
        lock (_sync) _sources.Remove(key);
    }

    private Source GetOrCreateSource(string key, bool muted)
    {
        if (!_sources.TryGetValue(key, out var source))
        {
            source = new Source(muted);
            _sources.Add(key, source);
        }
        return source;
    }

    private void EnsureFormat(PluginPcmFormat format)
    {
        if (_format is null)
        {
            _format = format;
            return;
        }
        if (_format != format)
            throw new InvalidOperationException("All active plugin audio sources must use the same PCM format.");
    }

    private void EnsureOutput()
    {
        if (_waveOut != IntPtr.Zero || _format is null) return;
        var f = _format ?? throw new InvalidOperationException("PCM format is unavailable.");
        int bytesPerSample = f.SampleFormat == PluginPcmSampleFormat.PcmS16Le ? 2 : 4;
        int blockAlign = checked(f.Channels * bytesPerSample);
        var wave = new WAVEFORMATEX
        {
            wFormatTag = 1,
            nChannels = checked((ushort)f.Channels),
            nSamplesPerSec = checked((uint)f.SampleRate),
            nAvgBytesPerSec = checked((uint)(f.SampleRate * blockAlign)),
            nBlockAlign = checked((ushort)blockAlign),
            wBitsPerSample = checked((ushort)(bytesPerSample * 8)),
            cbSize = 0
        };
        int error = waveOutOpen(out _waveOut, 0xffffffff, ref wave, Marshal.GetFunctionPointerForDelegate(_callback), IntPtr.Zero, 0x00030000);
        if (error != 0 || _waveOut == IntPtr.Zero)
            throw new InvalidOperationException($"waveOutOpen failed ({error}).");

        int frameBytes = checked(f.SampleRate / (1000 / BufferMilliseconds) * blockAlign);
        for (int i = 0; i < BufferCount; i++)
        {
            var buffer = new WaveBuffer(frameBytes);
            _buffers.Add(buffer.Header, buffer);
            PrepareAndWrite(buffer);
        }
    }

    private void PrepareAndWrite(WaveBuffer buffer)
    {
        if (_waveOut == IntPtr.Zero) return;
        if (waveOutPrepareHeader(_waveOut, buffer.Header, Marshal.SizeOf<WAVEHDR>()) != 0) return;
        Fill(buffer.Data);
        waveOutWrite(_waveOut, buffer.Header, Marshal.SizeOf<WAVEHDR>());
    }

    private void Fill(byte[] output)
    {
        Array.Clear(output);
        if (_format is null) return;
        var format = _format ?? throw new InvalidOperationException("PCM format is unavailable.");
        int bytesPerSample = format.SampleFormat == PluginPcmSampleFormat.PcmS16Le ? 2 : 4;
        int frames = output.Length / (format.Channels * bytesPerSample);
        lock (_sync)
        {
            foreach (var source in _sources.Values)
            {
                if (source.Muted) continue;
                source.MixInto(output, format, frames);
            }
        }
    }

    private void OnWaveOut(IntPtr hwo, uint msg, IntPtr instance, IntPtr header, IntPtr reserved1, IntPtr reserved2)
    {
        if (msg != 0x3fff) return; // WOM_DONE
        lock (_sync)
        {
            if (_disposed != 0 || !_buffers.TryGetValue(header, out var buffer)) return;
            Fill(buffer.Data);
            waveOutWrite(hwo, header, Marshal.SizeOf<WAVEHDR>());
        }
    }

    private static void Validate(byte[] pcm, PluginPcmFormat format)
    {
        ArgumentNullException.ThrowIfNull(pcm);
        if (pcm.Length > 1024 * 1024) throw new ArgumentOutOfRangeException(nameof(pcm), "Plugin audio pushes are capped at 1 MiB.");
        if (format.SampleRate is < 8000 or > 48000) throw new ArgumentOutOfRangeException(nameof(format));
        if (format.Channels is < 1 or > 2) throw new ArgumentOutOfRangeException(nameof(format));
        if (format.SampleFormat is not (PluginPcmSampleFormat.PcmS16Le or PluginPcmSampleFormat.Float32Le)) throw new ArgumentOutOfRangeException(nameof(format));
        int bytesPerSample = format.SampleFormat == PluginPcmSampleFormat.PcmS16Le ? 2 : 4;
        if (pcm.Length % checked(format.Channels * bytesPerSample) != 0) throw new ArgumentException("PCM byte count must align to a complete audio frame.", nameof(pcm));
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        lock (_sync)
        {
            if (_waveOut != IntPtr.Zero)
            {
                waveOutReset(_waveOut);
                foreach (var buffer in _buffers.Values)
                {
                    try { waveOutUnprepareHeader(_waveOut, buffer.Header, Marshal.SizeOf<WAVEHDR>()); } catch { }
                    buffer.Dispose();
                }
                _buffers.Clear();
                waveOutClose(_waveOut);
                _waveOut = IntPtr.Zero;
            }
            _sources.Clear();
        }
    }

    private sealed class Source
    {
        private readonly Queue<byte[]> _chunks = new();
        private int _offset;
        public bool Muted;
        public Source(bool muted) => Muted = muted;
        public void Replace(byte[] bytes) { _chunks.Clear(); _offset = 0; if (bytes.Length > 0) _chunks.Enqueue(bytes); }
        public void Append(byte[] bytes) { if (bytes.Length > 0) _chunks.Enqueue(bytes); while (_chunks.Count > 8) { _chunks.Dequeue(); _offset = 0; } }
        public void MixInto(byte[] destination, PluginPcmFormat format, int frames)
        {
            int bytesPerSample = format.SampleFormat == PluginPcmSampleFormat.PcmS16Le ? 2 : 4;
            int frameBytes = format.Channels * bytesPerSample;
            int needed = checked(frames * frameBytes);
            int outOffset = 0;
            while (outOffset < needed && _chunks.Count > 0)
            {
                byte[] chunk = _chunks.Peek();
                int available = Math.Min(chunk.Length - _offset, needed - outOffset);
                MixChunk(destination, outOffset, chunk, _offset, available, format);
                outOffset += available;
                _offset += available;
                if (_offset >= chunk.Length) { _chunks.Dequeue(); _offset = 0; }
            }
        }

        private static void MixChunk(byte[] dst, int dstOffset, byte[] src, int srcOffset, int count, PluginPcmFormat format)
        {
            if (format.SampleFormat == PluginPcmSampleFormat.PcmS16Le)
            {
                for (int i = 0; i < count; i += 2)
                {
                    short a = BitConverter.ToInt16(dst, dstOffset + i);
                    short b = BitConverter.ToInt16(src, srcOffset + i);
                    int mix = Math.Clamp(a + b, short.MinValue, short.MaxValue);
                    BitConverter.TryWriteBytes(dst.AsSpan(dstOffset + i, 2), (short)mix);
                }
            }
            else
            {
                for (int i = 0; i < count; i += 4)
                {
                    float a = BitConverter.ToSingle(dst, dstOffset + i);
                    float b = BitConverter.ToSingle(src, srcOffset + i);
                    BitConverter.TryWriteBytes(dst.AsSpan(dstOffset + i, 4), Math.Clamp(a + b, -1f, 1f));
                }
            }
        }
    }

    private sealed class WaveBuffer : IDisposable
    {
        public readonly byte[] Data;
        public readonly GCHandle Pin;
        public readonly IntPtr Header;
        public WaveBuffer(int size)
        {
            Data = new byte[size];
            Pin = GCHandle.Alloc(Data, GCHandleType.Pinned);
            Header = Marshal.AllocHGlobal(Marshal.SizeOf<WAVEHDR>());
            Marshal.StructureToPtr(new WAVEHDR { lpData = Pin.AddrOfPinnedObject(), dwBufferLength = checked((uint)size) }, Header, false);
        }
        public void Dispose() { try { Pin.Free(); } catch { } try { Marshal.FreeHGlobal(Header); } catch { } }
    }

    [StructLayout(LayoutKind.Sequential)] private struct WAVEFORMATEX { public ushort wFormatTag, nChannels; public uint nSamplesPerSec, nAvgBytesPerSec; public ushort nBlockAlign, wBitsPerSample, cbSize; }
    [StructLayout(LayoutKind.Sequential)] private struct WAVEHDR { public IntPtr lpData; public uint dwBufferLength, dwBytesRecorded, dwUser, dwFlags, dwLoops; public IntPtr lpNext, reserved; }
    private delegate void WaveOutCallback(IntPtr hwo, uint uMsg, IntPtr dwInstance, IntPtr dwParam1, IntPtr dwParam2, IntPtr dwParam3);

    [DllImport("winmm.dll", EntryPoint = "waveOutOpen")] private static extern int waveOutOpen(out IntPtr phwo, uint uDeviceID, ref WAVEFORMATEX pwfx, IntPtr dwCallback, IntPtr dwInstance, uint fdwOpen);
    [DllImport("winmm.dll")] private static extern int waveOutPrepareHeader(IntPtr hwo, IntPtr pwh, int cbwh);
    [DllImport("winmm.dll")] private static extern int waveOutWrite(IntPtr hwo, IntPtr pwh, int cbwh);
    [DllImport("winmm.dll")] private static extern int waveOutUnprepareHeader(IntPtr hwo, IntPtr pwh, int cbwh);
    [DllImport("winmm.dll")] private static extern int waveOutReset(IntPtr hwo);
    [DllImport("winmm.dll")] private static extern int waveOutClose(IntPtr hwo);
}
