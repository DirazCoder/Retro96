using System.Runtime.InteropServices;
using System.Text;

namespace Retro96;

internal sealed class MidiPlayer : IDisposable
{
    private const string MciDevice = "sequencer";
    private readonly object _sync = new();
    private string? _alias;
    private string? _sourcePath;
    private string? _tempPath;
    private bool _isPlaying;
    private int _totalPlays = 1;      // -1 = indefinite; positive = total plays.
    private int _remainingPlays;
    private System.Threading.Timer? _completionTimer;

    public bool IsPlaying => _isPlaying;

    /// <summary>Compatibility flag used by the shell/plugin toolbar.</summary>
    public bool Loop
    {
        get => _totalPlays == -1;
        set
        {
            lock (_sync)
            {
                _totalPlays = value ? -1 : 1;
                if (_isPlaying)
                    _remainingPlays = value ? -1 : 1;
            }
            StateChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>Total plays requested for the current source: 1 = once, -1 = indefinitely.</summary>
    public int RepeatCount
    {
        get => _totalPlays;
        set
        {
            int count = value < 0 ? -1 : Math.Max(1, value);
            lock (_sync)
            {
                _totalPlays = count;
                if (_isPlaying)
                    _remainingPlays = count;
            }
            StateChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private float _volume = 1f;
    public float Volume
    {
        get => _volume;
        set
        {
            lock (_sync)
            {
                _volume = Math.Clamp(value, 0f, 1f);
                ApplyVolumeLocked();
            }
            StateChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public string? CurrentFileName { get; private set; }
    public event EventHandler? StateChanged;
    public event EventHandler? PlaybackComplete;

    public Task PlayBytesAsync(byte[] bytes, string fileName, bool loop = false) =>
        PlayBytesAsync(bytes, fileName, loop ? -1 : 1);

    public Task PlayBytesAsync(byte[] bytes, string fileName, int repeatCount)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        return Task.Run(() =>
        {
            // Validate the SMF and apply the browser-side GM rules before the
            // native sequencer sees it. The sequencer itself supplies the actual
            // General MIDI synthesis/program-change/drum rendering.
            StandardMidiFile.Parse(bytes);

            Stop();
            string tempDir = Path.Combine(Path.GetTempPath(), "Retro96", "MIDI");
            Directory.CreateDirectory(tempDir);
            string temp = Path.Combine(tempDir, Guid.NewGuid().ToString("N") + ".mid");
            try
            {
                File.WriteAllBytes(temp, bytes);
                PlayFile(temp, fileName, repeatCount);
            }
            catch
            {
                try { File.Delete(temp); } catch { }
                throw;
            }
        });
    }

    public void PlayFile(string path, string? displayName = null, bool loop = false) =>
        PlayFile(path, displayName, loop ? -1 : 1);

    public void PlayFile(string path, string? displayName, int repeatCount)
    {
        lock (_sync)
        {
            StopLocked();
            if (!File.Exists(path)) throw new FileNotFoundException("Audio file was not found.", path);

            if (Path.GetExtension(path).Equals(".mid", StringComparison.OrdinalIgnoreCase) ||
                Path.GetExtension(path).Equals(".midi", StringComparison.OrdinalIgnoreCase))
            {
                StandardMidiFile.Parse(File.ReadAllBytes(path));
            }

            _sourcePath = path;
            _tempPath = path.StartsWith(Path.GetTempPath(), StringComparison.OrdinalIgnoreCase) ? path : null;
            _totalPlays = repeatCount < 0 ? -1 : Math.Max(1, repeatCount);
            _remainingPlays = _totalPlays;
            CurrentFileName = displayName ?? Path.GetFileName(path);
            StartDeviceLocked();
            _isPlaying = true;
            EnsureCompletionTimerLocked();
            if (MciSendString($"play {_alias}", null, 0, IntPtr.Zero) != 0)
            {
                StopLocked();
                throw new InvalidOperationException("Windows audio playback could not start the file.");
            }
        }
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    private void StartDeviceLocked()
    {
        if (_sourcePath == null) throw new InvalidOperationException("No MIDI source is loaded.");
        string alias = "Retro96Audio" + Guid.NewGuid().ToString("N");
        string extension = Path.GetExtension(_sourcePath);
        string device = extension.Equals(".wav", StringComparison.OrdinalIgnoreCase) ? "waveaudio" : MciDevice;
        string escapedPath = _sourcePath.Replace("\"", "\\\"");
        string command = $"open \"{escapedPath}\" type {device} alias {alias}";
        if (MciSendString(command, null, 0, IntPtr.Zero) != 0)
            throw new InvalidOperationException("Windows audio playback could not open the file.");
        _alias = alias;
        ApplyVolumeLocked();
    }

    private void EnsureCompletionTimerLocked()
    {
        _completionTimer ??= new System.Threading.Timer(_ => PollCompletion(), null, 250, 250);
        _completionTimer.Change(250, 250);
    }

    private void ApplyVolumeLocked()
    {
        if (_alias == null) return;
        int level = Math.Clamp((int)Math.Round(_volume * 1000f), 0, 1000);
        try { MciSendString($"setaudio {_alias} volume to {level}", null, 0, IntPtr.Zero); } catch { }
    }

    private void PollCompletion()
    {
        EventHandler? complete = null;
        bool restarted = false;
        lock (_sync)
        {
            if (_alias == null || !_isPlaying) return;
            var sb = new StringBuilder(64);
            int result = MciSendString($"status {_alias} mode", sb, sb.Capacity, IntPtr.Zero);
            if (result != 0 || !sb.ToString().Trim().Equals("stopped", StringComparison.OrdinalIgnoreCase)) return;

            // Close and reopen between passes so the synthesizer is reset to
            // tick 0. This is the native equivalent of All Notes Off/Reset
            // before a loop and prevents hanging drum/melodic notes bleeding
            // into the next pass.
            if (_remainingPlays == -1 || _remainingPlays > 1)
            {
                if (_remainingPlays > 1) _remainingPlays--;
                CloseAliasLocked();
                StartDeviceLocked();
                _isPlaying = true;
                restarted = true;
                try
                {
                    if (MciSendString($"play {_alias}", null, 0, IntPtr.Zero) != 0)
                        throw new InvalidOperationException("Windows audio playback could not restart the file.");
                }
                catch
                {
                    StopLocked();
                    complete = PlaybackComplete;
                }
            }
            else
            {
                StopLocked();
                complete = PlaybackComplete;
            }
        }

        StateChanged?.Invoke(this, EventArgs.Empty);
        if (restarted)
            return;
        complete?.Invoke(this, EventArgs.Empty);
    }

    public void Pause()
    {
        lock (_sync)
        {
            if (_alias != null)
                MciSendString("pause " + _alias, null, 0, IntPtr.Zero);
            _isPlaying = false;
            _completionTimer?.Change(Timeout.Infinite, Timeout.Infinite);
        }
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Resume()
    {
        lock (_sync)
        {
            if (_alias == null || _sourcePath == null) return;
            if (MciSendString("resume " + _alias, null, 0, IntPtr.Zero) != 0)
                MciSendString("play " + _alias, null, 0, IntPtr.Zero);
            _isPlaying = true;
            EnsureCompletionTimerLocked();
        }
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Stop()
    {
        lock (_sync) StopLocked();
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    private void CloseAliasLocked()
    {
        if (_alias == null) return;
        try { MciSendString("stop " + _alias, null, 0, IntPtr.Zero); } catch { }
        try { MciSendString("close " + _alias, null, 0, IntPtr.Zero); } catch { }
        _alias = null;
    }

    private void StopLocked()
    {
        _completionTimer?.Change(Timeout.Infinite, Timeout.Infinite);
        _completionTimer?.Dispose();
        _completionTimer = null;
        CloseAliasLocked();
        _isPlaying = false;
        CurrentFileName = null;
        _sourcePath = null;
        _remainingPlays = 0;
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
