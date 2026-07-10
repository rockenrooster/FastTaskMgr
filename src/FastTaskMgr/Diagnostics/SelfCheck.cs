using FastTaskMgr.App;
using FastTaskMgr.Core.Performance;
using FastTaskMgr.Core.Updates;
using System.Security.Cryptography;
using System.Text;

namespace FastTaskMgr.Diagnostics;

internal static class SelfCheck
{
    public static int Run()
    {
        try
        {
            using AppState state = AppState.Load();
            bool hasProcesses = ProcessSnapshotSelfCheck(state);
            bool hasMemory = state.Performance.Sample().TotalMemoryBytes > 0;
            bool updateTrustWorks = UpdateTrustSelfCheck();
            bool gpuEngineAggregationWorks = GpuEngineAggregationSelfCheck();
            _ = state.Services.ListServices();
            _ = state.Startup.ListStartupItems();
            return hasProcesses && hasMemory && updateTrustWorks && gpuEngineAggregationWorks ? 0 : 1;
        }
        catch
        {
            return 1;
        }
    }

    private static bool ProcessSnapshotSelfCheck(AppState state)
    {
        _ = state.Processes.Sample(includeDetails: true);
        Thread.Sleep(500);
        IReadOnlyList<Core.Processes.ProcessRow> rows = state.Processes.Sample(includeDetails: true);
        Core.Processes.ProcessRow? self = rows.FirstOrDefault(row => row.ProcessId == Environment.ProcessId);
        return rows.Count > 0
            && self is not null
            && self.WorkingSetBytes > 10 * 1024 * 1024
            && self.ThreadCount > 0
            && !string.IsNullOrEmpty(self.Path)
            && !string.IsNullOrEmpty(self.UserName)
            && !string.IsNullOrEmpty(self.CommandLine)
            && self.SessionId > 0
            && rows.All(row => row.CpuPercent >= 0 && row.CpuPercent <= 100);
    }

    private static bool UpdateTrustSelfCheck()
    {
        byte[] manifestBytes = Encoding.UTF8.GetBytes("""{"schemaVersion":1,"version":"1.2.3.4"}""");
        byte[] tamperedBytes = Encoding.UTF8.GetBytes("""{"schemaVersion":1,"version":"1.2.3.5"}""");

        using RSA rsa = RSA.Create(2048);
        byte[] signature = rsa.SignData(manifestBytes, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        string publicKey = rsa.ExportSubjectPublicKeyInfoPem();

        return UpdateTrust.VerifyManifestSignature(manifestBytes, signature, publicKey)
            && !UpdateTrust.VerifyManifestSignature(tamperedBytes, signature, publicKey);
    }

    private static bool GpuEngineAggregationSelfCheck() =>
        PerformanceSampler.BusiestGpuEnginePercent(
        [
            ("pid_1_luid_0_phys_0_eng_0_engtype_3D", 12),
            ("pid_2_luid_0_phys_0_eng_0_engtype_3D", 8),
            ("pid_3_luid_0_phys_0_eng_1_engtype_Compute_0", 55),
            ("pid_4_luid_0_phys_0_eng_2_engtype_Copy", -10)
        ]) == 55;
}
