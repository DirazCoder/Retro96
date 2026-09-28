using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using Microsoft.Win32;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using System.Threading;

namespace Retro96;

/// <summary>
/// Windows-enforced isolation primitives for Retro96 content workers.
/// The visible browser runs in a child process; its security boundary is the
/// AppContainer/LPAC token, a broker-only named pipe and a kill-on-close Job.
/// </summary>
internal static class WindowsSecurity
{
    private const int TokenIsAppContainer = 29; // TOKEN_INFORMATION_CLASS
    private const int TokenAppContainerSid = 31;
    private const uint TokenQuery = 0x0008;

    private const uint ErrorInsufficientBuffer = 122;
    private const uint ExtendedStartupInfoPresent = 0x00080000;
    private const uint CreateSuspended = 0x00000004;
    private const uint CreateUnicodeEnvironment = 0x00000400;

    private const uint WaitObject0 = 0x00000000;
    private const uint WorkerExitWaitMilliseconds = 5000;

    private static readonly IntPtr ProcThreadAttributeMitigationPolicy = (IntPtr)0x00020007;
    private static readonly IntPtr ProcThreadAttributeSecurityCapabilities = (IntPtr)0x00020009;
    private static readonly IntPtr ProcThreadAttributeChildProcessPolicy = (IntPtr)0x0002000E;
    private static readonly IntPtr ProcThreadAttributeAllApplicationPackagesPolicy = (IntPtr)0x0002000F;

    private const uint ProcessCreationChildProcessRestricted = 0x00000001;
    private const uint ProcessCreationAllApplicationPackagesOptOut = 0x00000001;

    private const uint JobObjectExtendedLimitInformation = 9;
    private const uint JobObjectLimitKillOnJobClose = 0x00002000;
    private const uint JobObjectLimitActiveProcess = 0x00000008;
    private const uint JobObjectLimitProcessMemory = 0x00000100;

    private const ulong MitigationDepEnable = 0x0000000000000001UL;
    private const ulong MitigationForceRelocateImages = 0x0000000000000100UL;
    private const ulong MitigationBottomUpAslr = 0x0000000000010000UL;
    private const ulong MitigationHighEntropyAslr = 0x0000000000100000UL;
    private const ulong MitigationStrictHandleChecks = 0x0000000001000000UL;
    private const ulong MitigationExtensionPointDisable = 0x0000000100000000UL;

    // The pipe name is embedded in a quoted --sandbox-worker argument; quotes
    // or control characters would let it break out of the quoting.
    private static readonly char[] InvalidPipeNameCharacters = { '"', '\r', '\n', '\0' };

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool OpenProcessToken(IntPtr processHandle, uint desiredAccess, out IntPtr tokenHandle);

    // NOTE: this signature is only valid for token information classes whose
    // native payload is a single DWORD (e.g. TokenIsAppContainer).
    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool GetTokenInformation(
        IntPtr tokenHandle,
        int tokenInformationClass,
        out int tokenInformation,
        int tokenInformationLength,
        out int returnLength);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool GetTokenInformation(
        IntPtr tokenHandle,
        int tokenInformationClass,
        IntPtr tokenInformation,
        int tokenInformationLength,
        out int returnLength);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern IntPtr FreeSid(IntPtr sid);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool ConvertStringSidToSid(string stringSid, out IntPtr sid);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetExitCodeProcess(IntPtr process, out uint exitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr LocalFree(IntPtr memory);

    [DllImport("ole32.dll")]
    private static extern void CoTaskMemFree(IntPtr memory);

    [DllImport("userenv.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int GetAppContainerFolderPath(
        string appContainerSid,
        out IntPtr path);

    [DllImport("userenv.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int CreateAppContainerProfile(
        string appContainerName,
        string displayName,
        string description,
        IntPtr capabilities,
        uint capabilityCount,
        out IntPtr appContainerSid);

    [DllImport("userenv.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int DeleteAppContainerProfile(string appContainerName);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateJobObject(IntPtr jobAttributes, string? name);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetInformationJobObject(
        IntPtr job,
        uint jobObjectInformationClass,
        ref JOBOBJECT_EXTENDED_LIMIT_INFORMATION jobObjectInformation,
        uint jobObjectInformationLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool TerminateJobObject(IntPtr job, uint exitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint ResumeThread(IntPtr thread);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool TerminateProcess(IntPtr process, uint exitCode);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CreateProcess(
        string? applicationName,
        StringBuilder? commandLine,
        IntPtr processAttributes,
        IntPtr threadAttributes,
        bool inheritHandles,
        uint creationFlags,
        IntPtr environment,
        string? currentDirectory,
        ref STARTUPINFOEX startupInfo,
        out PROCESS_INFORMATION processInformation);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool InitializeProcThreadAttributeList(
        IntPtr attributeList,
        uint attributeCount,
        uint flags,
        ref IntPtr size);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool UpdateProcThreadAttribute(
        IntPtr attributeList,
        uint flags,
        IntPtr attribute,
        IntPtr value,
        IntPtr size,
        IntPtr previousValue,
        IntPtr returnSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern void DeleteProcThreadAttributeList(IntPtr attributeList);

    [StructLayout(LayoutKind.Sequential)]
    private struct SECURITY_CAPABILITIES
    {
        public IntPtr AppContainerSid;
        public IntPtr Capabilities;
        public uint CapabilityCount;
        public uint Reserved;
    }

    // String members are declared as IntPtr (they are always null here) so the
    // struct is blittable and marshals as an exact STARTUPINFOW.
    [StructLayout(LayoutKind.Sequential)]
    private struct STARTUPINFO
    {
        public uint cb;
        public IntPtr lpReserved;
        public IntPtr lpDesktop;
        public IntPtr lpTitle;
        public uint dwX;
        public uint dwY;
        public uint dwXSize;
        public uint dwYSize;
        public uint dwXCountChars;
        public uint dwYCountChars;
        public uint dwFillAttribute;
        public uint dwFlags;
        public short wShowWindow;
        public short cbReserved2;
        public IntPtr lpReserved2;
        public IntPtr hStdInput;
        public IntPtr hStdOutput;
        public IntPtr hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct STARTUPINFOEX
    {
        public STARTUPINFO StartupInfo;
        public IntPtr lpAttributeList;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_INFORMATION
    {
        public IntPtr hProcess;
        public IntPtr hThread;
        public uint dwProcessId;
        public uint dwThreadId;
    }

    [Flags]
    private enum JobLimitFlags : uint
    {
        KillOnJobClose = JobObjectLimitKillOnJobClose,
        ActiveProcess = JobObjectLimitActiveProcess,
        ProcessMemory = JobObjectLimitProcessMemory
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_BASIC_LIMIT_INFORMATION
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public JobLimitFlags LimitFlags;
        public UIntPtr MinimumWorkingSetSize;
        public UIntPtr MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IO_COUNTERS
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
    {
        public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
        public IO_COUNTERS IoInfo;
        public UIntPtr ProcessMemoryLimit;
        public UIntPtr JobMemoryLimit;
        public UIntPtr PeakProcessMemoryUsed;
        public UIntPtr PeakJobMemoryUsed;
    }

    /// <summary>
    /// A launched content worker together with the Windows objects that keep
    /// it contained. Dispose terminates the Job (killing the worker), closes
    /// all handles, removes the worker's AppContainer profile and (when owned)
    /// its runtime copy.
    /// Not thread-safe beyond dispose-once semantics; synchronize external
    /// access to the handle properties if needed.
    /// </summary>
    internal sealed class WorkerProcess : IDisposable
    {
        private int _disposed;

        internal WorkerProcess(
            TrustMode mode,
            string profileName,
            IntPtr job,
            IntPtr process,
            IntPtr thread,
            uint processId,
            string runtimeDirectory,
            bool ownsRuntimeDirectory)
        {
            TrustMode = mode;
            ProfileName = mode == TrustMode.Low ? null : profileName;
            JobHandle = job;
            ProcessHandle = process;
            ThreadHandle = thread;
            ProcessId = processId;
            RuntimeDirectory = runtimeDirectory;
            OwnsRuntimeDirectory = ownsRuntimeDirectory;
        }

        /// <summary>Isolation tier the worker was launched with.</summary>
        internal TrustMode TrustMode { get; }

        /// <summary>AppContainer profile owned by this worker (null in Low mode).</summary>
        internal string? ProfileName { get; }

        internal IntPtr JobHandle { get; private set; }
        internal IntPtr ProcessHandle { get; private set; }
        internal IntPtr ThreadHandle { get; private set; }
        internal uint ProcessId { get; }

        /// <summary>Directory the worker runs from; deleted on Dispose when owned.</summary>
        internal string RuntimeDirectory { get; }
        internal bool OwnsRuntimeDirectory { get; }

        internal bool HasExited(out uint exitCode)
        {
            exitCode = 0;
            IntPtr handle = ProcessHandle; // snapshot; Dispose may race
            if (handle == IntPtr.Zero) return true;

            uint state = WaitForSingleObject(handle, 0);
            if (state == WaitObject0)
            {
                GetExitCodeProcess(handle, out exitCode);
                return true;
            }
            // WAIT_TIMEOUT (still running) or WAIT_FAILED (unexpected, invalid
            // handle): report the worker as alive so callers keep polling or
            // tear it down explicitly.
            return false;
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return;

            try { if (JobHandle != IntPtr.Zero) TerminateJobObject(JobHandle, 0); } catch { }
            try { if (ProcessHandle != IntPtr.Zero) WaitForSingleObject(ProcessHandle, WorkerExitWaitMilliseconds); } catch { }
            try { if (ThreadHandle != IntPtr.Zero) CloseHandle(ThreadHandle); } catch { }
            try { if (ProcessHandle != IntPtr.Zero) CloseHandle(ProcessHandle); } catch { }
            try { if (JobHandle != IntPtr.Zero) CloseHandle(JobHandle); } catch { }

            JobHandle = IntPtr.Zero;
            ProcessHandle = IntPtr.Zero;
            ThreadHandle = IntPtr.Zero;

            // Cleanup happens after the worker has exited so it is not holding
            // files open inside the profile / runtime directory.
            if (!string.IsNullOrEmpty(ProfileName))
            {
                DeleteAppContainer(ProfileName);
            }

            if (OwnsRuntimeDirectory)
                TryDeleteDirectory(RuntimeDirectory);

            GC.SuppressFinalize(this);
        }
    }

    internal static string CurrentUserSid
    {
        get
        {
            using WindowsIdentity identity = WindowsIdentity.GetCurrent();
            return identity.User?.Value
                ?? throw new InvalidOperationException("Unable to determine the current Windows user SID.");
        }
    }

    internal static bool IsCurrentProcessAppContainer()
    {
        IntPtr token = IntPtr.Zero;
        try
        {
            using Process current = Process.GetCurrentProcess();
            if (!OpenProcessToken(current.Handle, TokenQuery, out token))
                return false;

            return GetTokenInformation(token, TokenIsAppContainer, out int value,
                                       sizeof(int), out _) && value != 0;
        }
        catch
        {
            return false;
        }
        finally
        {
            if (token != IntPtr.Zero) CloseHandle(token);
        }
    }

    internal static string GetHighModeStatusText()
    {
        if (!SandboxContext.IsWorker)
            return "Host broker active — each browser window is launched as its own isolated content worker.";

        if (!IsCurrentProcessAppContainer())
            return "Windows isolation: FAILED — this content worker is not running in an AppContainer.";

        // Heuristic: native-AOT publishes do not ship a .runtimeconfig.json,
        // and the broker enables the LPAC-style opt-out for those workers.
        // The worker cannot verify this from its token, so this text reflects
        // the broker's launch policy rather than measured token state.
        bool nativeAotWorker = !File.Exists(
            Path.Combine(AppContext.BaseDirectory,
                Path.GetFileNameWithoutExtension(Environment.ProcessPath ?? "Retro96.exe") + ".runtimeconfig.json"));

        return nativeAotWorker
            ? "Windows isolation: ACTIVE — AppContainer + LPAC native worker + brokered resources + Job Object."
            : "Windows isolation: ACTIVE — AppContainer worker + brokered resources + Job Object. LPAC is enabled automatically for the native-AOT published worker.";
    }

    /// <summary>
    /// Copies the worker binaries into the AppContainer's own local-data
    /// folder so the lowbox token can execute them without re-ACLing anything
    /// in the user's AppData tree. Returns the new runtime directory.
    /// </summary>
    internal static string PrepareWorkerRuntime(string sourceDirectory, string appContainerSid)
    {
        StartupDiagnostics.Step("HOST", "PrepareWorkerRuntime source=" + sourceDirectory + " SID=" + appContainerSid);
        if (!Directory.Exists(sourceDirectory))
            throw new DirectoryNotFoundException($"Retro96 build output was not found: {sourceDirectory}");

        string containerRoot = GetContainerLocalAppData(appContainerSid);
        StartupDiagnostics.Step("HOST", "AppContainer local root=" + containerRoot);
        string runtimeRoot = Path.Combine(
            containerRoot,
            "Retro96",
            "WorkerRuntime",
            Guid.NewGuid().ToString("N"));

        Directory.CreateDirectory(runtimeRoot);
        StartupDiagnostics.Step("HOST", "Worker runtime directory created=" + runtimeRoot);
        try
        {
            // The AppContainer profile directory is created by Windows for this
            // exact package SID. Keeping the worker below it avoids changing
            // ACLs on the user's AppData tree just to make a lowbox executable
            // runnable. The container's own storage permissions carry through.
            CopyDirectory(sourceDirectory, runtimeRoot);
            StartupDiagnostics.Step("HOST", "Worker runtime copy completed.");
            return runtimeRoot;
        }
        catch
        {
            TryDeleteDirectory(runtimeRoot);
            throw;
        }
    }

    private static string GetContainerLocalAppData(string appContainerSid)
    {
        int hr = GetAppContainerFolderPath(appContainerSid, out IntPtr rawPath);
        if (hr != 0 || rawPath == IntPtr.Zero)
            throw new Win32Exception(hr, $"GetAppContainerFolderPath failed (HRESULT 0x{hr:X8}).");

        try
        {
            string? path = Marshal.PtrToStringUni(rawPath);
            if (string.IsNullOrWhiteSpace(path))
                throw new InvalidOperationException("Windows returned an empty AppContainer local-data path.");
            return path;
        }
        finally
        {
            CoTaskMemFree(rawPath);
        }
    }

    internal static void SweepStaleAppContainerProfiles()
    {
        const string mappingsPath = @"Software\Classes\Local Settings\Software\Microsoft\Windows\CurrentVersion\AppContainer\Mappings";
        const string profilePrefix = "Retro96.Content.";

        try
        {
            using RegistryKey? mappings = Registry.CurrentUser.OpenSubKey(mappingsPath);
            if (mappings == null) return;

            var activeSids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (Process process in Process.GetProcesses())
            {
                using (process)
                {
                    try
                    {
                        if (!string.Equals(process.ProcessName, "Retro96", StringComparison.OrdinalIgnoreCase))
                            continue;
                        if (!TryGetProcessAppContainerSid(process, out string? sid))
                            return;
                        if (!string.IsNullOrEmpty(sid)) activeSids.Add(sid);
                    }
                    catch
                    {
                        return;
                    }
                }
            }

            foreach (string mappingName in mappings.GetSubKeyNames())
            {
                try
                {
                    using RegistryKey? mapping = mappings.OpenSubKey(mappingName);
                    string? profileName = mapping?.GetValue("AppContainerName") as string;
                    if (string.IsNullOrEmpty(profileName) ||
                        !profileName.StartsWith(profilePrefix, StringComparison.OrdinalIgnoreCase))
                        continue;

                    string sid = new SecurityIdentifier(mappingName).Value;
                    if (activeSids.Contains(sid)) continue;

                    string localAppData = GetContainerLocalAppData(sid);
                    TryDeleteDirectory(Path.Combine(localAppData, "Retro96", "WorkerRuntime"));
                    DeleteAppContainer(profileName);
                }
                catch { }
            }
        }
        catch { }
    }

    private static bool TryGetProcessAppContainerSid(Process process, out string? sid)
    {
        sid = null;
        IntPtr token = IntPtr.Zero;
        IntPtr tokenInfo = IntPtr.Zero;
        try
        {
            if (!OpenProcessToken(process.Handle, TokenQuery, out token))
                return false;
            GetTokenInformation(token, TokenAppContainerSid, IntPtr.Zero, 0, out int required);
            if (required < IntPtr.Size)
                return false;

            tokenInfo = Marshal.AllocHGlobal(required);
            if (!GetTokenInformation(token, TokenAppContainerSid, tokenInfo, required, out _))
                return false;

            IntPtr appContainerSid = Marshal.ReadIntPtr(tokenInfo);
            if (appContainerSid != IntPtr.Zero)
                sid = new SecurityIdentifier(appContainerSid).Value;
            return true;
        }
        catch
        {
            return false;
        }
        finally
        {
            if (tokenInfo != IntPtr.Zero) Marshal.FreeHGlobal(tokenInfo);
            if (token != IntPtr.Zero) CloseHandle(token);
        }
    }

    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (string file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            string relative = Path.GetRelativePath(source, file);
            string target = Path.Combine(destination, relative);
            string? parent = Path.GetDirectoryName(target);
            if (!string.IsNullOrEmpty(parent)) Directory.CreateDirectory(parent);
            File.Copy(file, target, overwrite: true);
        }
    }

    private static void TryDeleteDirectory(string directory)
    {
        if (string.IsNullOrWhiteSpace(directory)) return;

        // Retry briefly: search indexers and antivirus can hold transient
        // handles inside the tree, which makes a single recursive delete fail.
        for (int attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                if (Directory.Exists(directory))
                    Directory.Delete(directory, recursive: true);
                return;
            }
            catch { }
            Thread.Sleep(150 * (attempt + 1));
        }
    }

    /// <summary>
    /// Launches an isolated content worker. The worker is created suspended,
    /// placed on a kill-on-close Job, then resumed.
    /// </summary>
    /// <remarks>
    /// On any failure after argument validation, the worker is terminated, all
    /// handles are closed, and the per-worker AppContainer profile and owned
    /// runtime copy are deleted — the same cleanup WorkerProcess.Dispose would
    /// perform — so a failed launch leaves no isolation artifacts behind.
    /// <paramref name="useLpac"/> only takes effect for <see cref="TrustMode.High"/>.
    /// </remarks>
    internal static WorkerProcess StartWorker(
        TrustMode mode,
        string pipeName,
        string? appContainerSid,
        string profileName,
        string workerExecutable,
        string workerWorkingDirectory,
        bool useLpac,
        bool ownsRuntimeDirectory,
        string? workerArguments = null)
    {
        StartupDiagnostics.Step("HOST", "StartWorker called. Mode=" + mode + " exe=" + workerExecutable + " cwd=" + workerWorkingDirectory + " LPAC=" + useLpac);

        bool applyAppContainer = mode is TrustMode.High or TrustMode.Medium;
        bool applyLpac = mode == TrustMode.High && useLpac;

        if (applyAppContainer && string.IsNullOrWhiteSpace(appContainerSid))
            throw new InvalidOperationException("An AppContainer SID is required for High/Medium workers.");

        bool useDefaultArguments = string.IsNullOrWhiteSpace(workerArguments);
        if (useDefaultArguments)
        {
            if (string.IsNullOrWhiteSpace(pipeName))
                throw new ArgumentException("A broker pipe name is required when default worker arguments are used.", nameof(pipeName));
            if (pipeName.IndexOfAny(InvalidPipeNameCharacters) >= 0)
                throw new ArgumentException("The broker pipe name contains characters that cannot be embedded in the worker command line.", nameof(pipeName));
        }

        if (!File.Exists(workerExecutable))
            throw new FileNotFoundException("Retro96 worker executable was not found.", workerExecutable);

        if (!Directory.Exists(workerWorkingDirectory))
            throw new DirectoryNotFoundException($"Worker working directory was not found: {workerWorkingDirectory}");

        // Commit-based per-process limits. The Low-tier limit exceeds 4 GiB,
        // which UIntPtr cannot represent in a 32-bit host.
        long memoryLimit = GetWorkerMemoryLimit(mode);
        if (!Environment.Is64BitProcess && (ulong)memoryLimit > uint.MaxValue)
            throw new PlatformNotSupportedException("This trust mode's worker memory limit exceeds 4 GiB and requires a 64-bit broker process.");

        IntPtr job = IntPtr.Zero;
        IntPtr process = IntPtr.Zero;
        IntPtr thread = IntPtr.Zero;
        IntPtr attrList = IntPtr.Zero;
        IntPtr capsSid = IntPtr.Zero;
        IntPtr securityCaps = IntPtr.Zero;
        IntPtr mitigation = IntPtr.Zero;
        IntPtr childPolicy = IntPtr.Zero;
        IntPtr lpacPolicy = IntPtr.Zero;

        try
        {
            job = CreateJobObject(IntPtr.Zero, null);
            StartupDiagnostics.Step("HOST", "CreateJobObject result=" + job);
            if (job == IntPtr.Zero)
                throw new Win32Exception(Marshal.GetLastWin32Error(), "CreateJobObject failed.");

            var limits = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION
            {
                BasicLimitInformation = new JOBOBJECT_BASIC_LIMIT_INFORMATION
                {
                    LimitFlags = JobLimitFlags.KillOnJobClose | JobLimitFlags.ActiveProcess | JobLimitFlags.ProcessMemory,
                    ActiveProcessLimit = 1
                },
                ProcessMemoryLimit = new UIntPtr((ulong)memoryLimit)
            };

            StartupDiagnostics.Step("HOST", "Configuring Job Object limits.");
            if (!SetInformationJobObject(job, JobObjectExtendedLimitInformation, ref limits,
                    (uint)Marshal.SizeOf<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>()))
            {
                int e = Marshal.GetLastWin32Error();
                StartupDiagnostics.Win32Error("HOST", "SetInformationJobObject", e);
                throw new Win32Exception(e, "SetInformationJobObject failed.");
            }

            // Keep the declared count in lockstep with the attributes actually
            // added below: mitigation policy + child-process policy always,
            // security capabilities for AppContainer modes, AAP opt-out for
            // LPAC-style High workers.
            uint attributeCount = 2u;
            if (applyAppContainer) attributeCount++;
            if (applyLpac) attributeCount++;

            IntPtr attrSize = IntPtr.Zero;
            InitializeProcThreadAttributeList(IntPtr.Zero, attributeCount, 0, ref attrSize);
            int queryError = Marshal.GetLastWin32Error();
            if (attrSize == IntPtr.Zero)
            {
                StartupDiagnostics.Win32Error("HOST", "InitializeProcThreadAttributeList(size)", queryError);
                throw new Win32Exception(
                    queryError != 0 ? queryError : unchecked((int)ErrorInsufficientBuffer),
                    "InitializeProcThreadAttributeList size query failed.");
            }

            attrList = Marshal.AllocHGlobal(attrSize);
            if (!InitializeProcThreadAttributeList(attrList, attributeCount, 0, ref attrSize))
            {
                int e = Marshal.GetLastWin32Error();
                StartupDiagnostics.Win32Error("HOST", "InitializeProcThreadAttributeList", e);
                throw new Win32Exception(e, "InitializeProcThreadAttributeList failed.");
            }

            if (applyAppContainer)
            {
                StartupDiagnostics.Step("HOST", "Applying AppContainer process attribute.");
                if (!ConvertStringSidToSid(appContainerSid!, out capsSid))
                {
                    int e = Marshal.GetLastWin32Error();
                    StartupDiagnostics.Win32Error("HOST", "ConvertStringSidToSid(AppContainer)", e);
                    throw new Win32Exception(e, "ConvertStringSidToSid(AppContainer) failed.");
                }

                var security = new SECURITY_CAPABILITIES
                {
                    AppContainerSid = capsSid,
                    Capabilities = IntPtr.Zero,
                    CapabilityCount = 0,
                    Reserved = 0
                };
                securityCaps = Marshal.AllocHGlobal(Marshal.SizeOf<SECURITY_CAPABILITIES>());
                Marshal.StructureToPtr(security, securityCaps, fDeleteOld: false);

                if (!UpdateProcThreadAttribute(attrList, 0, ProcThreadAttributeSecurityCapabilities,
                        securityCaps, (IntPtr)Marshal.SizeOf<SECURITY_CAPABILITIES>(), IntPtr.Zero, IntPtr.Zero))
                {
                    int e = Marshal.GetLastWin32Error();
                    StartupDiagnostics.Win32Error("HOST", "UpdateProcThreadAttribute(SECURITY_CAPABILITIES)", e);
                    throw new Win32Exception(e, "UpdateProcThreadAttribute(SECURITY_CAPABILITIES) failed.");
                }
            }

            StartupDiagnostics.Step("HOST", "Applying mitigation policy.");
            ulong mitigationPolicy = BuildMitigationPolicy(mode);
            mitigation = Marshal.AllocHGlobal(sizeof(ulong));
            Marshal.WriteInt64(mitigation, unchecked((long)mitigationPolicy));
            if (!UpdateProcThreadAttribute(attrList, 0, ProcThreadAttributeMitigationPolicy,
                    mitigation, (IntPtr)sizeof(ulong), IntPtr.Zero, IntPtr.Zero))
            {
                int e = Marshal.GetLastWin32Error();
                StartupDiagnostics.Win32Error("HOST", "UpdateProcThreadAttribute(MITIGATION_POLICY)", e);
                throw new Win32Exception(e, "UpdateProcThreadAttribute(MITIGATION_POLICY) failed.");
            }

            StartupDiagnostics.Step("HOST", "Applying child-process restriction.");
            childPolicy = Marshal.AllocHGlobal(sizeof(uint));
            Marshal.WriteInt32(childPolicy, unchecked((int)ProcessCreationChildProcessRestricted));
            if (!UpdateProcThreadAttribute(attrList, 0, ProcThreadAttributeChildProcessPolicy,
                    childPolicy, (IntPtr)sizeof(uint), IntPtr.Zero, IntPtr.Zero))
            {
                int e = Marshal.GetLastWin32Error();
                StartupDiagnostics.Win32Error("HOST", "UpdateProcThreadAttribute(CHILD_PROCESS_POLICY)", e);
                throw new Win32Exception(e, "UpdateProcThreadAttribute(CHILD_PROCESS_POLICY) failed.");
            }

            if (applyLpac)
            {
                // Removes the worker's claim on the "ALL APPLICATION PACKAGES"
                // (S-1-15-2-1) ACE so only resources ACLed for this specific
                // package SID (plus brokered resources) remain reachable.
                StartupDiagnostics.Step("HOST", "Applying all-application-packages opt-out (LPAC-style).");
                lpacPolicy = Marshal.AllocHGlobal(sizeof(uint));
                Marshal.WriteInt32(lpacPolicy, unchecked((int)ProcessCreationAllApplicationPackagesOptOut));
                if (!UpdateProcThreadAttribute(attrList, 0, ProcThreadAttributeAllApplicationPackagesPolicy,
                        lpacPolicy, (IntPtr)sizeof(uint), IntPtr.Zero, IntPtr.Zero))
                {
                    int e = Marshal.GetLastWin32Error();
                    StartupDiagnostics.Win32Error("HOST", "UpdateProcThreadAttribute(ALL_APPLICATION_PACKAGES_POLICY)", e);
                    throw new Win32Exception(e, "UpdateProcThreadAttribute(ALL_APPLICATION_PACKAGES_POLICY) failed.");
                }
            }

            StartupDiagnostics.Step("HOST", "Creating suspended worker process.");
            string commandLineText = useDefaultArguments
                ? $"\"{workerExecutable}\" --sandbox-worker \"{pipeName}\" {mode}"
                : $"\"{workerExecutable}\" {workerArguments}";
            // CreateProcessW is permitted to modify the command-line buffer in
            // place, so give the marshaled StringBuilder room beyond its text.
            var commandLine = new StringBuilder(commandLineText, commandLineText.Length + 32);
            var startup = new STARTUPINFOEX
            {
                StartupInfo = new STARTUPINFO
                {
                    cb = (uint)Marshal.SizeOf<STARTUPINFOEX>()
                },
                lpAttributeList = attrList
            };

            if (!CreateProcess(workerExecutable, commandLine, IntPtr.Zero, IntPtr.Zero, false,
                    ExtendedStartupInfoPresent | CreateSuspended | CreateUnicodeEnvironment,
                    IntPtr.Zero, workerWorkingDirectory, ref startup, out var pi))
            {
                int e = Marshal.GetLastWin32Error();
                StartupDiagnostics.Win32Error("HOST", "CreateProcess(worker)", e);
                throw new Win32Exception(e, "CreateProcess(worker) failed.");
            }

            process = pi.hProcess;
            thread = pi.hThread;
            StartupDiagnostics.Step("HOST", "CreateProcess succeeded. PID=" + pi.dwProcessId);

            if (!AssignProcessToJobObject(job, process))
            {
                uint error = (uint)Marshal.GetLastWin32Error();
                TerminateProcess(process, 1);
                StartupDiagnostics.Win32Error("HOST", "AssignProcessToJobObject", (int)error);
                throw new Win32Exception((int)error, "AssignProcessToJobObject failed; worker was terminated.");
            }

            if (ResumeThread(thread) == uint.MaxValue)
            {
                int e = Marshal.GetLastWin32Error();
                StartupDiagnostics.Win32Error("HOST", "ResumeThread(worker)", e);
                throw new Win32Exception(e, "ResumeThread(worker) failed.");
            }
            StartupDiagnostics.Step("HOST", "Worker resumed.");

            return new WorkerProcess(mode, profileName, job, process, thread, pi.dwProcessId,
                                     workerWorkingDirectory, ownsRuntimeDirectory);
        }
        catch (Exception ex)
        {
            StartupDiagnostics.Error("HOST", "StartWorker failed", ex);

            // Full teardown: kill the Job, wait for the worker so it releases
            // files, close every handle (including the Job), and clean up the
            // isolation artifacts exactly as WorkerProcess.Dispose would.
            try { if (job != IntPtr.Zero) TerminateJobObject(job, 1); } catch { }
            try { if (process != IntPtr.Zero) WaitForSingleObject(process, WorkerExitWaitMilliseconds); } catch { }
            try { if (thread != IntPtr.Zero) CloseHandle(thread); } catch { }
            try { if (process != IntPtr.Zero) CloseHandle(process); } catch { }
            try { if (job != IntPtr.Zero) CloseHandle(job); } catch { }

            if (mode is TrustMode.High or TrustMode.Medium)
                DeleteAppContainer(profileName);
            if (ownsRuntimeDirectory)
                TryDeleteDirectory(workerWorkingDirectory);

            throw;
        }
        finally
        {
            try { if (attrList != IntPtr.Zero) DeleteProcThreadAttributeList(attrList); } catch { }
            try { if (attrList != IntPtr.Zero) Marshal.FreeHGlobal(attrList); } catch { }
            try { if (securityCaps != IntPtr.Zero) Marshal.FreeHGlobal(securityCaps); } catch { }
            try { if (capsSid != IntPtr.Zero) LocalFree(capsSid); } catch { }
            try { if (mitigation != IntPtr.Zero) Marshal.FreeHGlobal(mitigation); } catch { }
            try { if (childPolicy != IntPtr.Zero) Marshal.FreeHGlobal(childPolicy); } catch { }
            try { if (lpacPolicy != IntPtr.Zero) Marshal.FreeHGlobal(lpacPolicy); } catch { }
        }
    }

    /// <summary>Best-effort deletion of an AppContainer profile; never throws.</summary>
    internal static void DeleteAppContainer(string? profileName)
    {
        if (string.IsNullOrWhiteSpace(profileName)) return;

        // Retry once: profile deletion can fail transiently while the worker's
        // files are still being released by the OS.
        try
        {
            if (DeleteAppContainerProfile(profileName) == 0)
                return;
        }
        catch { }

        Thread.Sleep(250);
        try { DeleteAppContainerProfile(profileName); } catch { }
    }

    /// <summary>
    /// Creates a per-worker AppContainer profile. The returned profile is
    /// owned by the resulting WorkerProcess and deleted when it is disposed.
    /// </summary>
    internal static (string ProfileName, string Sid) CreateAppContainer(TrustMode mode)
    {
        StartupDiagnostics.Step("HOST", "CreateAppContainer called. Mode=" + mode);
        if (mode == TrustMode.Low)
            throw new ArgumentOutOfRangeException(nameof(mode));

        // Profile names are limited to 64 characters; the Guid suffix keeps them unique.
        string profile = "Retro96.Content." + mode + "." + Guid.NewGuid().ToString("N");
        profile = profile[..Math.Min(64, profile.Length)];

        IntPtr packageSid = IntPtr.Zero;
        try
        {
            int hr = CreateAppContainerProfile(profile, "Retro96 content worker",
                "Per-window Retro96 renderer isolation container", IntPtr.Zero, 0, out packageSid);
            if (hr != 0)
            {
                StartupDiagnostics.Win32Error("HOST", "CreateAppContainerProfile", hr);
                throw new Win32Exception(hr, $"CreateAppContainerProfile failed (HRESULT 0x{hr:X8}).");
            }

            string sid = new SecurityIdentifier(packageSid).Value;
            StartupDiagnostics.Step("HOST", "CreateAppContainerProfile succeeded. SID=" + sid);
            return (profile, sid);
        }
        finally
        {
            if (packageSid != IntPtr.Zero)
                FreeSid(packageSid);
        }
    }

    private static ulong BuildMitigationPolicy(TrustMode mode) => mode switch
    {
        TrustMode.Low => MitigationDepEnable | MitigationBottomUpAslr | MitigationHighEntropyAslr,
        _ => MitigationDepEnable | MitigationForceRelocateImages | MitigationBottomUpAslr |
             MitigationHighEntropyAslr | MitigationStrictHandleChecks | MitigationExtensionPointDisable
    };

    private static long GetWorkerMemoryLimit(TrustMode mode) => mode switch
    {
        TrustMode.High => 1536L * 1024 * 1024,
        TrustMode.Medium => 2048L * 1024 * 1024,
        _ => 4096L * 1024 * 1024
    };
}