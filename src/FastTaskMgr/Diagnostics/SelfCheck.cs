using FastTaskMgr.App;
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
            bool hasProcesses = state.Processes.Sample().Count > 0;
            bool hasMemory = state.Performance.Sample().TotalMemoryBytes > 0;
            bool updateTrustWorks = UpdateTrustSelfCheck();
            _ = state.Services.ListServices();
            _ = state.Startup.ListStartupItems();
            return hasProcesses && hasMemory && updateTrustWorks ? 0 : 1;
        }
        catch
        {
            return 1;
        }
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
}
