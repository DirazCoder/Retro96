using System;
using System.Threading;
using System.Threading.Tasks;

namespace Retro96;

/// <summary>
/// Per-process security context. In worker mode all privileged operations are
/// delegated to the trusted host broker; the OS AppContainer/Job Object is the
/// enforcement boundary, not this class.
/// </summary>
internal static class SandboxContext
{
    private static UserSettings? _settingsOverride;
    private static string? _pipeName;
    private static SandboxBrokerClient? _broker;
    private static int _isWorker;
    private static int _workerTrustMode = (int)TrustMode.High; // safe default: strictest tier

    public static bool IsWorker => Volatile.Read(ref _isWorker) != 0;

    public static TrustMode WorkerTrustMode => (TrustMode)Volatile.Read(ref _workerTrustMode);

    public static string? PipeName => Volatile.Read(ref _pipeName);

    public static SandboxBrokerClient? Broker => Volatile.Read(ref _broker);

    public static bool IsOsIsolated =>
        IsWorker &&
        WorkerTrustMode is (TrustMode.High or TrustMode.Medium) &&
        WindowsSecurity.IsCurrentProcessAppContainer();

    public static bool BrokerAllNetwork =>
        IsWorker && (WorkerTrustMode is TrustMode.High or TrustMode.Medium);

    public static bool BrokerImages =>
        IsWorker && BrowserRuntime.HostImageChecking;

    public static bool DirectNetworkAllowed =>
        IsWorker && WorkerTrustMode == TrustMode.Low;

    public static bool DirectPageFileAccessAllowed =>
        IsWorker && WorkerTrustMode == TrustMode.Low;

    public static UserSettings InitialSettings =>
        _settingsOverride?.Clone() ?? UserSettings.Load();

    public static void InitializeWorker(
        string pipeName,
        TrustMode trustMode,
        UserSettings settings,
        SandboxBrokerClient broker)
    {
        if (string.IsNullOrWhiteSpace(pipeName))
            throw new ArgumentException("Worker pipe name is required.", nameof(pipeName));
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(broker);

        // Publish the worker flag last (volatile write = release fence) so any
        // reader that observes IsWorker == true also observes every other
        // field of the worker context.
        Volatile.Write(ref _pipeName, pipeName);
        Volatile.Write(ref _workerTrustMode, (int)trustMode);
        _settingsOverride = settings.Clone();
        Volatile.Write(ref _broker, broker);
        Volatile.Write(ref _isWorker, 1);

        BrowserRuntime.Apply(settings);
    }

    public static async Task<bool> SaveSettingsThroughBrokerAsync(UserSettings settings)
    {
        SandboxBrokerClient? broker = Broker;
        if (!IsWorker || broker is null)
            return false;

        try
        {
            return await broker.SaveSettingsAsync(settings).ConfigureAwait(false);
        }
        catch
        {
            return false;
        }
    }

    public static bool TrySaveSettingsSynchronously(UserSettings settings)
    {
        SandboxBrokerClient? broker = Broker;
        if (!IsWorker || broker is null)
            return false;

        try
        {
            // Blocking is safe here: every await inside the broker client uses
            // ConfigureAwait(false), so completing this call never needs to
            // re-enter the calling thread's synchronization context.
            return broker.SaveSettingsAsync(settings).GetAwaiter().GetResult();
        }
        catch
        {
            return false;
        }
    }

    public static void Reset()
    {
        // Unpublish first so no new caller starts using the broker while it is
        // being torn down.
        Volatile.Write(ref _isWorker, 0);
        Volatile.Write(ref _workerTrustMode, (int)TrustMode.High);

        SandboxBrokerClient? broker = Interlocked.Exchange(ref _broker, null);
        try { broker?.Dispose(); } catch { }

        Volatile.Write(ref _pipeName, null);
        _settingsOverride = null;
    }
}