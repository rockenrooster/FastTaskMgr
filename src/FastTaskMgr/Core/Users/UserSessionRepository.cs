using System.ComponentModel;
using System.Runtime.InteropServices;

namespace FastTaskMgr.Core.Users;

internal sealed class UserSessionRepository
{
    public IReadOnlyList<WtsSession> ListSessions()
    {
        List<WtsSession> sessions = [];
        if (!WTSEnumerateSessions(IntPtr.Zero, 0, 1, out IntPtr sessionInfo, out int count))
        {
            return sessions;
        }

        try
        {
            int size = Marshal.SizeOf<WtsSessionInfo>();
            for (int index = 0; index < count; index++)
            {
                WtsSessionInfo info = Marshal.PtrToStructure<WtsSessionInfo>(IntPtr.Add(sessionInfo, index * size));
                string user = QuerySessionString(info.SessionId, WtsUserName);
                if (string.IsNullOrWhiteSpace(user))
                {
                    continue;
                }

                string domain = QuerySessionString(info.SessionId, WtsDomainName);
                sessions.Add(new WtsSession(
                    info.SessionId,
                    string.IsNullOrWhiteSpace(domain) ? user : $"{domain}\\{user}",
                    StateName(info.State)));
            }
        }
        finally
        {
            WTSFreeMemory(sessionInfo);
        }

        return sessions;
    }

    public void Disconnect(int sessionId)
    {
        if (!WTSDisconnectSession(IntPtr.Zero, sessionId, false))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }
    }

    public void Logoff(int sessionId)
    {
        if (!WTSLogoffSession(IntPtr.Zero, sessionId, false))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }
    }

    private static string QuerySessionString(int sessionId, int infoClass)
    {
        if (!WTSQuerySessionInformation(IntPtr.Zero, sessionId, infoClass, out IntPtr buffer, out int bytes) || buffer == IntPtr.Zero)
        {
            return "";
        }

        try
        {
            return bytes <= 2 ? "" : Marshal.PtrToStringUni(buffer) ?? "";
        }
        finally
        {
            WTSFreeMemory(buffer);
        }
    }

    private static string StateName(int state) => state switch
    {
        0 => "Active",
        1 => "Connected",
        2 => "Connect query",
        3 => "Shadow",
        4 => "Disconnected",
        5 => "Idle",
        6 => "Listen",
        7 => "Reset",
        8 => "Down",
        9 => "Init",
        _ => "Unknown"
    };

    private const int WtsUserName = 5;
    private const int WtsDomainName = 7;

    [StructLayout(LayoutKind.Sequential)]
    private struct WtsSessionInfo
    {
        public int SessionId;
        public IntPtr WinStationName;
        public int State;
    }

    [DllImport("wtsapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool WTSEnumerateSessions(IntPtr hServer, int reserved, int version, out IntPtr ppSessionInfo, out int pCount);

    [DllImport("wtsapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool WTSQuerySessionInformation(IntPtr hServer, int sessionId, int wtsInfoClass, out IntPtr ppBuffer, out int pBytesReturned);

    [DllImport("wtsapi32.dll")]
    private static extern void WTSFreeMemory(IntPtr pMemory);

    [DllImport("wtsapi32.dll", SetLastError = true)]
    private static extern bool WTSDisconnectSession(IntPtr hServer, int sessionId, bool bWait);

    [DllImport("wtsapi32.dll", SetLastError = true)]
    private static extern bool WTSLogoffSession(IntPtr hServer, int sessionId, bool bWait);
}

internal sealed record WtsSession(int SessionId, string UserName, string State);
