namespace FastTaskMgr.Core.Users;

internal sealed record UserSessionRow(string UserName, int SessionId, string State);
