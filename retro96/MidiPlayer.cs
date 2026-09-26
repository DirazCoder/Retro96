using System.Runtime.InteropServices;
using System.Text;

namespace Retro96;

internal sealed class MidiPlayer : IDisposable
{
    private const string MciDevice = "sequencer";
    private readonly object _sync = new();
    private string? _alias;
    private string? _tempPath;
    private bool _isPlaying;
    private bool _loop;
    private System.Threading.Timer? _completionTimer;

    public bool IsPlaying => _isPlaying;
    public bool Loop { get => _loop; set => _loop = value; }
    private float _volume = 1f;
    public float Volume { get => _volume; set { lock (_sync) { _volume = Math.Clamp(value, 0f, 1f); ApplyVolumeLocked(); } StateChanged?.Invoke(this, EventArgs.Empty); } }
    public string? CurrentFileName { get; private set; }
    public event EventHandler? StateChanged;
    public event EventHandler? PlaybackComplete;

    public Task PlayBytesAsync(byte[] bytes, string fileName, bool loop = false) => Task.Run(() =>
    {
        Stop();
        string tempDir = Path.Combine(Path.GetTempPath(), "Retro96", "MIDI");
        Directory.CreateDirectory(tempDir);
        string temp = Path.Combine(tempDir, Guid.NewGuid().ToString("N") + Path.GetExtension(fileName));
        try
        {
            File.WriteAllBytes(temp, bytes);
            PlayFile(temp, fileName, loop);
        }
        catch
        {
            try { File.Delete(temp); } catch { }
            throw;
        }
    });

    public void PlayFile(string path, string? displayName = null, bool loop = false)
    {
        lock (_sync)
        {
            StopLocked();
            if (!File.Exists(path)) throw new FileNotFoundException("Audio file was not found.", path);
            string alias = "Retro96Audio" + Guid.NewGuid().ToString("N");
            string extension = Path.GetExtension(path);
            string device = extension.Equals(".wav", StringComparison.OrdinalIgnoreCase) ? "waveaudio" : MciDevice;
            string command = $"open \"{path.Replace("\"", "\\\"")}\" type {device} alias {alias}";
            if (MciSendString(command, null, 0, IntPtr.Zero) != 0)
                throw new InvalidOperationException("Windows audio playback could not open the file.");
            _alias = alias;
            _tempPath = path.StartsWith(Path.GetTempPath(), StringComparison.OrdinalIgnoreCase) ? path : null;
            CurrentFileName = displayName ?? Path.GetFileName(path);
            _loop = loop;
            ApplyVolumeLocked();
            _isPlaying = true;
            string play = loop ? $"play {alias} repeat" : $"play {alias}";
            if (MciSendString(play, null, 0, IntPtr.Zero) != 0)
            {
                StopLocked();
                throw new InvalidOperationException("Windows audio playback could not start the file.");
            }
            _completionTimer = new System.Threading.Timer(_ => PollCompletion(), null, 250, 250);
        }
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    private void ApplyVolumeLocked()
    {
        if (_alias == null) return;
        int level = Math.Clamp((int)Math.Round(Volume * 1000f), 0, 1000);
        try { MciSendString($"setaudio {_alias} volume to {level}", null, 0, IntPtr.Zero); } catch { }
    }

    private void PollCompletion()
    {
        EventHandler? complete = null;
        lock (_sync)
        {
            if (_alias == null || !_isPlaying || _loop) return;
            var sb = new StringBuilder(64);
            int result = MciSendString($"status {_alias} mode", sb, sb.Capacity, IntPtr.Zero);
            if (result != 0 || !sb.ToString().Trim().Equals("stopped", StringComparison.OrdinalIgnoreCase)) return;
            StopLocked();
            complete = PlaybackComplete;
        }
        StateChanged?.Invoke(this, EventArgs.Empty);
        complete?.Invoke(this, EventArgs.Empty);
    }

    public void Pause()
    {
        lock (_sync) { if (_alias != null) MciSendString("pause " + _alias, null, 0, IntPtr.Zero); _isPlaying = false; _completionTimer?.Change(Timeout.Infinite, Timeout.Infinite); }
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Resume()
    {
        lock (_sync)
        {
            if (_alias == null) return;
            string cmd = _loop ? $"play {_alias} repeat" : $"resume {_alias}";
            MciSendString(cmd, null, 0, IntPtr.Zero);
            _isPlaying = true;
            _completionTimer ??= new System.Threading.Timer(_ => PollCompletion(), null, 250, 250);
            _completionTimer.Change(250, 250);
        }
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Stop()
    {
        lock (_sync) StopLocked();
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    private void StopLocked()
    {
        _completionTimer?.Change(Timeout.Infinite, Timeout.Infinite);
        _completionTimer?.Dispose();
        _completionTimer = null;
        if (_alias != null)
        {
            try { MciSendString("stop " + _alias, null, 0, IntPtr.Zero); } catch { }
            try { MciSendString("close " + _alias, null, 0, IntPtr.Zero); } catch { }
        }
        _alias = null;
        _isPlaying = false;
        CurrentFileName = null;
        if (_tempPath != null)
        {
            try { File.Delete(_tempPath); } catch { }
        }
        _tempPath = null;
    }

    public void Dispose() { lock (_sync) StopLocked(); }

    [DllImport("winmm.dll", CharSet = CharSet.Unicode, EntryPoint = "mciSendStringW")]
    private static extern int MciSendString(string command, StringBuilder? returnString, int returnLength, IntPtr callback);
}
