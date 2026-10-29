using System.ComponentModel;
using System.Runtime.InteropServices;
using JMD.Core;

namespace JMD.Windows;

public sealed class ComputerPowerController
{
    public void Execute(SystemPowerAction action)
    {
        switch (action)
        {
            case SystemPowerAction.Hibernate:
                SetSuspendState(hibernate: true);
                break;
            case SystemPowerAction.Suspend:
                SetSuspendState(hibernate: false);
                break;
            case SystemPowerAction.Lock:
                if (!LockWorkStation()) throw CreatePowerException("O Windows não conseguiu bloquear a sessão.");
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(action), action, "Ação de energia desconhecida.");
        }
    }

    private static void SetSuspendState(bool hibernate)
    {
        EnableShutdownPrivilege();
        if (!SetSuspendStateNative(hibernate, forceCritical: false, disableWakeEvent: false))
            throw CreatePowerException(hibernate
                ? "O Windows não conseguiu hibernar. Verifique se a hibernação está habilitada neste computador."
                : "O Windows não conseguiu suspender o computador.");
    }

    private static void EnableShutdownPrivilege()
    {
        const uint tokenAdjustPrivileges = 0x0020;
        const uint tokenQuery = 0x0008;
        const uint privilegeEnabled = 0x0002;
        if (!OpenProcessToken(GetCurrentProcess(), tokenAdjustPrivileges | tokenQuery, out var token))
            throw CreatePowerException("Não foi possível obter o token do Windows para habilitar hibernação/suspensão.");

        try
        {
            if (!LookupPrivilegeValue(null, "SeShutdownPrivilege", out var luid))
                throw CreatePowerException("Não foi possível localizar o privilégio de suspensão do Windows.");
            var privileges = new TokenPrivileges
            {
                PrivilegeCount = 1,
                Privilege = new LuidAndAttributes { Luid = luid, Attributes = privilegeEnabled }
            };
            SetLastError(0);
            if (!AdjustTokenPrivileges(token, disableAllPrivileges: false, ref privileges, 0, IntPtr.Zero, IntPtr.Zero))
                throw CreatePowerException("Não foi possível habilitar o privilégio de suspensão do Windows.");
            var error = Marshal.GetLastWin32Error();
            if (error != 0) throw new Win32Exception(error, "A conta atual não tem o privilégio de suspensão do Windows.");
        }
        finally
        {
            CloseHandle(token);
        }
    }

    private static Exception CreatePowerException(string message)
    {
        var error = Marshal.GetLastWin32Error();
        return error != 0 ? new Win32Exception(error, message) : new InvalidOperationException(message);
    }

    [DllImport("powrprof.dll", EntryPoint = "SetSuspendState", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.U1)]
    private static extern bool SetSuspendStateNative(
        [MarshalAs(UnmanagedType.U1)] bool hibernate,
        [MarshalAs(UnmanagedType.U1)] bool forceCritical,
        [MarshalAs(UnmanagedType.U1)] bool disableWakeEvent);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool LockWorkStation();

    [StructLayout(LayoutKind.Sequential)]
    private struct Luid
    {
        public uint LowPart;
        public int HighPart;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LuidAndAttributes
    {
        public Luid Luid;
        public uint Attributes;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TokenPrivileges
    {
        public uint PrivilegeCount;
        public LuidAndAttributes Privilege;
    }

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("kernel32.dll", EntryPoint = "SetLastError")]
    private static extern void SetLastError(uint errorCode);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenProcessToken(IntPtr processHandle, uint desiredAccess, out IntPtr tokenHandle);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool LookupPrivilegeValue(string? systemName, string name, out Luid luid);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AdjustTokenPrivileges(IntPtr tokenHandle, [MarshalAs(UnmanagedType.Bool)] bool disableAllPrivileges,
        ref TokenPrivileges newState, uint bufferLength, IntPtr previousState, IntPtr returnLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);
}
