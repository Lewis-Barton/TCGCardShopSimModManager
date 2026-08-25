using TCGCardShopSimModManager.Core;
using TCGCardShopSimModManager.Cli;

if (args.Length > 0 && args[0] is "--version" or "-v")
{
    var version = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
    Console.WriteLine($"TCGCardShopSimModManager.Cli {version}");
    return;
}

if (args.Length == 0)
{
    PrintUsage();
    Environment.ExitCode = 2;
    return;
}

// The key (and anything like it) must never appear in the diagnostic log.
Diagnostic.Write($"command: {RedactArgs(args)}");

try
{
    switch (args[0])
    {
        case "detect":
            DetectCommand.Run(args.ElementAtOrDefault(1));
            break;
        case "validate":
            ValidateCommand.Run(args.ElementAtOrDefault(1), args.ElementAtOrDefault(2));
            break;
        case "plan":
            PlanCommand.Run(args.ElementAtOrDefault(1), args.ElementAtOrDefault(2), args.ElementAtOrDefault(3));
            break;
        case "download":
            await DownloadCommand.Run(
                args.ElementAtOrDefault(1),
                args.ElementAtOrDefault(2),
                args.ElementAtOrDefault(3),
                args.ElementAtOrDefault(4));
            break;
        case "serve":
            ServeCommand.Run(args.ElementAtOrDefault(1), args.ElementAtOrDefault(2));
            break;
        case "demo":
            await DemoCommand.Run(
                args.ElementAtOrDefault(1),
                args.ElementAtOrDefault(2),
                args.ElementAtOrDefault(3),
                args.ElementAtOrDefault(4),
                args.ElementAtOrDefault(5));
            break;
        case "nexus":
            await NexusCommand.Run(args.ElementAtOrDefault(1), args.ElementAtOrDefault(2), args.ElementAtOrDefault(3));
            break;
        case "nexus-demo":
            await NexusDemoCommand.Run(
                args.ElementAtOrDefault(1),
                args.ElementAtOrDefault(2),
                args.ElementAtOrDefault(3),
                args.ElementAtOrDefault(4),
                args.ElementAtOrDefault(5));
            break;
        case "update-check":
            await UpdateCommand.Run();
            break;
        case "support-bundle":
            SupportCommand.Run(args.ElementAtOrDefault(1));
            break;
        case "install":
            InstallCommand.Run(args.ElementAtOrDefault(1), args.ElementAtOrDefault(2), args.ElementAtOrDefault(3));
            break;
        case "uninstall":
            UninstallCommand.Run(args.ElementAtOrDefault(1), args.ElementAtOrDefault(2));
            break;
        case "profile":
            ProfileCommand.Run(
                args.ElementAtOrDefault(1),
                args.ElementAtOrDefault(2),
                args.ElementAtOrDefault(3),
                args.ElementAtOrDefault(4),
                args.ElementAtOrDefault(5));
            break;
        case "mods":
            ModsCommand.Run(args.ElementAtOrDefault(1), args.ElementAtOrDefault(2), args.ElementAtOrDefault(3));
            break;
        case "modpack":
            await ModpackCommand.Run(
                args.ElementAtOrDefault(1),
                args.ElementAtOrDefault(2),
                args.ElementAtOrDefault(3),
                args.ElementAtOrDefault(4));
            break;
        case "help" when args.Length == 1:
            PrintUsage();
            break;
        default:
            Console.WriteLine($"Unknown command: {args[0]}");
            Environment.ExitCode = 2;
            break;
    }

    Diagnostic.Write($"command completed: {args[0]}");
}
catch (Exception ex)
{
    // Crashes are written to the local diagnostic log.
    Diagnostic.Write($"unhandled exception: {ex.Message}", "error");
    Console.Error.WriteLine($"Unexpected error: {ex.Message}");
    Console.Error.WriteLine("Details were written to the diagnostic log. Export it with: support-bundle");
    Environment.ExitCode = 1;
}

static void PrintUsage()
{
    Console.WriteLine(
        "Usage: cardshopmodmanager <command> [args]\n" +
        "  detect <gameFolder?>          check a game folder, or auto-detect via Steam with no path\n" +
        "  validate <manifest> [game]    check the manifest and enabled list; print install order\n" +
        "  plan <manifest> <src> <game>  dry-run: show the file-by-file plan without touching the game\n" +
        "  download <manifest> <src> <cache> <out>   fetch archives (src: url | folder | nexus)\n" +
        "  serve <folder> [port]         host a folder over HTTP (run downloads from a second terminal)\n" +
        "  demo / nexus-demo             one-command end-to-end demos\n" +
        "  nexus set-key|login|logout|status|clear   manage Nexus auth (OAuth login preferred)\n" +
        "  update-check                  compare version with the latest GitHub release\n" +
        "  support-bundle [outDir]       export logs + environment info (never the API key)\n" +
        "  install <manifest> <src> <game>   verify, plan, install, journal\n" +
        "  uninstall <modName> <game>    remove a mod's files if they still match the journal\n" +
        "  profile list|use|enable|disable ...\n" +
        "  modpack list                  show modpacks hosted on GitHub\n" +
        "  modpack install <id> [game] [optionalIds|all]   install a hosted modpack\n" +
        "  modpack files <Nexus URL|modId>                 list stable Nexus file selectors\n" +
        "  modpack check-updates <packId|manifest>         check pinned Nexus files for updates\n" +
        "  modpack import <links.txt> <packFolder> [name]  create a manifest draft from Nexus links\n" +
        "  --version                     print the version");
}

static string RedactArgs(string[] args)
{
    var list = args.ToList();
    for (var i = 0; i < list.Count - 1; i++)
    {
        if (list[i].Equals("set-key", StringComparison.OrdinalIgnoreCase))
            list[i + 1] = "***";
    }

    return string.Join(' ', list);
}
