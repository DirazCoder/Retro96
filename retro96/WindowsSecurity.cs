using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Threading;

namespace Retro96;

/// <summary>
/// Windows-enforced isolation primitives for Retro96 content workers.
/// The visible browser runs in a child process; its security boundary is the
/// AppContainer/LPAC token, a broker-only named pipe and a kill-on-close Job.
/// </summary>
internal static class WindowsSecurity
{
    private const int TokenIsAppContainer = 29;
    private const uint TokenQuery = 0x0008;

    private const uint ErrorInsufficientBuffer = 122;
    private const uint ExtendedStartupInfoPresent = 0x00080000;
    private const uint CreateSuspended = 0x00000004;
    private const uint CreateUnicodeEnvironment = 0x00000400;

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

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool OpenProcessToken(IntPtr processHandle, uint desiredAccess, out IntPtr tokenHandle);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool GetTokenInformation(
        IntPtr tokenHandle,
        int tokenInformationClass,
        out int tokenInformation,
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

    [DllImport("kernel32.dll", SetLastError = true)]
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
        System.Text.StringBuilder? commandLine,
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

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct STARTUPINFO
    {
        public uint cb;
        public string? lpReserved;
        public string? lpDesktop;
        public string? lpTitle;
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

        public TrustMode TrustMode { get; }
        public string? ProfileName { get; }
        public IntPtr JobHandle { get; private set; }
        public IntPtr ProcessHandle { get; private set; }
        public IntPtr ThreadHandle { get; private set; }
        public uint ProcessId { get; }
        public string RuntimeDirectory { get; }
        public bool OwnsRuntimeDirectory { get; }

        public bool HasExited(out uint exitCode)
        {
            exitCode = 0;
            if (ProcessHandle == IntPtr.Zero) return true;
            uint state = WaitForSingleObject(ProcessHandle, 0);
            if (state == 0)
            {
                GetExitCodeProcess(ProcessHandle, out exitCode);
                return true;
            }
            return false;
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return;

            try { if (JobHandle != IntPtr.Zero) TerminateJobObject(JobHandle, 0); } catch { }
            try { if (ProcessHandle != IntPtr.Zero) WaitForSingleObject(ProcessHandle, 5000); } catch { }
            try { if (ThreadHandle != IntPtr.Zero) CloseHandle(ThreadHandle); } catch { }
            try { if (ProcessHandle != IntPtr.Zero) CloseHandle(ProcessHandle); } catch { }
            try { if (JobHandle != IntPtr.Zero) CloseHandle(JobHandle); } catch { }

            if (!string.IsNullOrEmpty(ProfileName))
            {
                DeleteAppContainer(ProfileName);
            }

            if (OwnsRuntimeDirectory)
                TryDeleteDirectory(RuntimeDirectory);

            JobHandle = IntPtr.Zero;
            ProcessHandle = IntPtr.Zero;
            ThreadHandle = IntPtr.Zero;
        }
    }

    internal static string CurrentUserSid =>
        WindowsIdentity.GetCurrent().User?.Value
        ?? throw new InvalidOperationException("Unable to determine the current Windows user SID.");

    public static bool IsCurrentProcessAppContainer()
    {
        IntPtr token = IntPtr.Zero;
        try
        {
            if (!OpenProcessToken(Process.GetCurrentProcess().Handle, TokenQuery, out token))
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

    public static string GetHighModeStatusText()
    {
        if (!SandboxContext.IsWorker)
            return "Host broker active — each browser window is launched as its own isolated content worker.";

        if (!IsCurrentProcessAppContainer())
            return "Windows isolation: FAILED — this content worker is not running in an AppContainer.";

        bool nativeAotWorker = !File.Exists(
            Path.Combine(AppContext.BaseDirectory,
                Path.GetFileNameWithoutExtension(Environment.ProcessPath ?? "Retro96.exe") + ".runtimeconfig.json"));

        return nativeAotWorker
            ? "Windows isolation: ACTIVE — AppContainer + LPAC native worker + brokered resources + Job Object."
            : "Windows isolation: ACTIVE — AppContainer worker + brokered resources + Job Object. LPAC is enabled automatically for the native-AOT published worker.";
    }

    public static string PrepareWorkerRuntime(string sourceDirectory, string appContainerSid)
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
        try { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
        catch { }
    }

    public static WorkerProcess StartWorker(
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
        if ((mode is TrustMode.High or TrustMode.Medium) && string.IsNullOrWhiteSpace(appContainerSid))
            throw new InvalidOperationException("An AppContainer SID is required for High/Medium workers.");
        if (!File.Exists(workerExecutable))
            throw new FileNotFoundException("Retro96 worker executable was not found.", workerExecutable);

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
                ProcessMemoryLimit = new UIntPtr((ulong)GetWorkerMemoryLimit(mode))
            };

            StartupDiagnostics.Step("HOST", "Configuring Job Object limits.");
            if (!SetInformationJobObject(job, JobObjectExtendedLimitInformation, ref limits,
                    (uint)Marshal.SizeOf<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>()))
            {
                int e = Marshal.GetLastWin32Error();
                StartupDiagnostics.Win32Error("HOST", "SetInformationJobObject", e);
                throw new Win32Exception(e, "SetInformationJobObject failed.");
            }

            uint attributeCount = mode switch
            {
                TrustMode.High when useLpac => 4u,
                TrustMode.High => 3u,
                TrustMode.Medium => 3u,
                _ => 2u
            };

            IntPtr attrSize = IntPtr.Zero;
            InitializeProcThreadAttributeList(IntPtr.Zero, attributeCount, 0, ref attrSize);
            int queryError = Marshal.GetLastWin32Error();
            if (attrSize == IntPtr.Zero && queryError != (int)ErrorInsufficientBuffer)
            {
                StartupDiagnostics.Win32Error("HOST", "InitializeProcThreadAttributeList(size)", queryError);
                throw new Win32Exception(queryError, "InitializeProcThreadAttributeList size query failed.");
            }

            attrList = Marshal.AllocHGlobal(attrSize);
            if (!InitializeProcThreadAttributeList(attrList, attributeCount, 0, ref attrSize))
            {
                int e = Marshal.GetLastWin32Error();
                StartupDiagnostics.Win32Error("HOST", "InitializeProcThreadAttributeList", e);
                throw new Win32Exception(e, "InitializeProcThreadAttributeList failed.");
            }

            if (mode is TrustMode.High or TrustMode.Medium)
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

            if (mode == TrustMode.High && useLpac)
            {
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
            string commandLineText = string.IsNullOrWhiteSpace(workerArguments)
                ? $"\"{workerExecutable}\" --sandbox-worker \"{pipeName}\" {mode}"
                : $"\"{workerExecutable}\" {workerArguments}";
            var commandLine = new System.Text.StringBuilder(commandLineText);
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
            try { if (job != IntPtr.Zero) TerminateJobObject(job, 1); } catch { }
            try { if (process != IntPtr.Zero) CloseHandle(process); } catch { }
            try { if (thread != IntPtr.Zero) CloseHandle(thread); } catch { }
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

    public static void DeleteAppContainer(string? profileName)
    {
        if (string.IsNullOrWhiteSpace(profileName)) return;
        try { DeleteAppContainerProfile(profileName); } catch { }
    }

    public static (string ProfileName, string Sid) CreateAppContainer(TrustMode mode)
    {
        StartupDiagnostics.Step("HOST", "CreateAppContainer called. Mode=" + mode);
        if (mode == TrustMode.Low)
            throw new ArgumentOutOfRangeException(nameof(mode));

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