using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text;

namespace TCGCardShopSimModManager.Core;

/// <summary>
/// Packages the information needed to diagnose a problem: environment, recent
/// diagnostic log lines, and (when a game folder is given) the journal and
/// profile files. Explicitly never includes the API key or anything that looks
/// like it — the bundle is meant to be shared.
/// </summary>
public static class SupportBundle
{
    public static string Create(string? gameFolder = null, string? outputDirectory = null)
    {
        var outDir = outputDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "TCGCardShopSimModManager",
            "bundles");
        Directory.CreateDirectory(outDir);

        var bundlePath = Path.Combine(outDir, $"support-{DateTime.Now:yyyyMMdd-HHmmss}.zip");

        using var zip = ZipFile.Open(bundlePath, ZipArchiveMode.Create);
        AddTextEntry(zip, "info.txt", BuildInfo(gameFolder));
        AddTextEntry(zip, "diagnostics.log", string.Join(Environment.NewLine, Diagnostic.RecentLines()));

        if (gameFolder is not null && Directory.Exists(gameFolder))
        {
            AddOptionalTextEntry(zip, "journal.json", Path.Combine(gameFolder, "cardshopmodmanager.journal.json"));
            AddOptionalTextEntry(zip, "modpacks.json", Path.Combine(gameFolder, "cardshopmodmanager.modpacks.json"));
            AddOptionalTextEntry(zip, "profiles.json", Path.Combine(gameFolder, "cardshopmodmanager.profiles.json"));
            AddRecoveryRecords(zip, gameFolder);
        }

        return bundlePath;
    }

    private static string BuildInfo(string? gameFolder)
    {
        var version = typeof(SupportBundle).Assembly.GetName().Version;
        var sb = new StringBuilder();
        sb.AppendLine($"TCG Card Shop Sim Mod Manager version: {version}");
        sb.AppendLine($"OS: {RuntimeInformation.OSDescription}");
        sb.AppendLine($"Architecture: {RuntimeInformation.OSArchitecture}");
        sb.AppendLine($".NET runtime: {RuntimeInformation.FrameworkDescription}");
        sb.AppendLine($"Game folder provided: {gameFolder ?? "(none)"}");
        sb.AppendLine($"Diagnostic log: {Diagnostic.LogFilePath}");
        return sb.ToString();
    }

    private static void AddTextEntry(ZipArchive zip, string name, string content)
    {
        var entry = zip.CreateEntry(name);
        using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
        writer.Write(content);
    }

    private static void AddOptionalTextEntry(ZipArchive zip, string name, string path)
    {
        if (File.Exists(path))
            AddTextEntry(zip, name, File.ReadAllText(path));
    }

    private static void AddRecoveryRecords(ZipArchive zip, string gameFolder)
    {
        var recoveryRoot = Path.Combine(gameFolder, ".cardshopmodmanager-recovery");
        if (!Directory.Exists(recoveryRoot))
            return;

        var index = 0;
        foreach (var transactionFolder in Directory.EnumerateDirectories(recoveryRoot))
        {
            if ((File.GetAttributes(transactionFolder) & FileAttributes.ReparsePoint) != 0)
                continue;
            var statePath = Path.Combine(transactionFolder, "transaction.json");
            AddOptionalTextEntry(zip, $"recovery/transaction-{++index}.json", statePath);
        }
    }
}
