using System;
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

    public static bool IsWorker { get; private set; }
    public static TrustMode WorkerTrustMode { get; private set; } = TrustMode.High;
    public static string? PipeName { get; private set; }
    public static SandboxBrokerClient? Broker { get; private set; }

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
        PipeName = pipeName;
        WorkerTrustMode = trustMode;
        _settingsOverride = settings.Clone();
        Broker = broker;
        IsWorker = true;
        BrowserRuntime.Apply(settings);
    }

    public static async Task<bool> SaveSettingsThroughBrokerAsync(UserSettings settings)
    {
        if (!IsWorker || Broker == null)
            return false;

        try
        {
            return await Broker.SaveSettingsAsync(settings).ConfigureAwait(false);
        }
        catch
        {
            return false;
        }
    }

    public static bool TrySaveSettingsSynchronously(UserSettings settings)
    {
        if (!IsWorker || Broker == null)
            return false;

        try
        {
            return Broker.SaveSettingsAsync(settings).GetAwaiter().GetResult();
        }
        catch
        {
            return false;
        }
    }

    public static void Reset()
    {
        try { Broker?.Dispose(); } catch { }
        Broker = null;
        PipeName = null;
        _settingsOverride = null;
        IsWorker = false;
        WorkerTrustMode = TrustMode.High;
    }
}
