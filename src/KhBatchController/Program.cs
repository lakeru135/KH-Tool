using System.Diagnostics;
using System.IO;
using System.Windows.Forms;
using KhBatchController.Auth;
using KhBatchRunner.Forma;
using KhBatchRunner.UI;

namespace KhBatchController;

/// <summary>
/// The standalone controller: drives the KH Batch tooling as an ordinary Windows program, with
/// Revit reduced to a background worker it starts when it needs one.
///
/// Usage:
///   KhBatchController.exe                 connect, then open the Forma model-set browser
///   KhBatchController.exe --probe         token check only, no window
///   KhBatchController.exe --keep          leave the broker running (for the orphan test)
///   KhBatchController.exe --show          don't hide Revit's window (for watching what it does)
///   KhBatchController.exe --revit 2026    pin a Revit release
///   KhBatchController.exe --no-pause      don't wait for Enter before closing
///   KhBatchController.exe --split 5       models per worker Revit (0 = one Revit), with --run
///
/// Opening the browser is the DEFAULT because this is a double-clickable program: a user who runs
/// it from Explorer must get a window, not a console that closes itself.
/// </summary>
internal static class Program
{
    /// <summary>
    /// Console flows still print even though this is a WinExe.
    ///
    /// A WinExe has no console of its own, so Console.WriteLine goes nowhere. Attaching to the
    /// PARENT console puts the output back in the terminal the user typed into; when there is no
    /// parent — a double-click, or a shortcut — a console is allocated only if the arguments say
    /// this is a console-shaped run. A plain launch gets no window at all, which is the point.
    /// </summary>
    private static void AttachConsoleForCliRun(string[] args)
    {
        bool consoleRun = args.Any(a =>
            a.Equals("--probe", StringComparison.OrdinalIgnoreCase) ||
            a.Equals("--run", StringComparison.OrdinalIgnoreCase) ||
            a.Equals("--render", StringComparison.OrdinalIgnoreCase) ||
            a.Equals("--notify-test", StringComparison.OrdinalIgnoreCase) ||
            a.Equals("--notify-persistence-test", StringComparison.OrdinalIgnoreCase) ||
            a.Equals("--notify-stopped-test", StringComparison.OrdinalIgnoreCase) ||
            a.Equals("--uitest", StringComparison.OrdinalIgnoreCase));
        if (!consoleRun) return;

        // ATTACH_PARENT_PROCESS (-1): use the terminal that launched us, if any.
        if (!AttachConsole(-1) && !AllocConsole()) return;

        // The streams were bound to "nowhere" at start-up; rebind them to the console we just got.
        var stdout = new System.IO.StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true };
        var stderr = new System.IO.StreamWriter(Console.OpenStandardError()) { AutoFlush = true };
        Console.SetOut(stdout);
        Console.SetError(stderr);
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    private static extern bool AttachConsole(int processId);

    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    private static extern bool AllocConsole();

    // WinForms requires STA; the Forma browser is a real WinForms dialog.
    [STAThread]
    private static int Main(string[] args)
    {
        AttachConsoleForCliRun(args);

        // Before ANY window is built. The About box otherwise describes the Revit add-in — its
        // version, its ribbon tab, its changelog — none of which is true here. Installed in Main
        // rather than alongside the broker because it needs nothing: putting it on the broker path
        // left --render (and any other window opened outside that path) showing the add-in's
        // identity. See ControllerProduct for why there are two versions.
        KhBatchRunner.UI.HostServices.About = () => new KhBatchRunner.UI.AboutFacts(
            ControllerProduct.Name,
            ControllerProduct.Version,
            ControllerProduct.Publisher,
            "Core engine", ControllerProduct.CoreEngineVersion,
            ControllerProduct.Description,
            ControllerProduct.Changelog);

        int code;
        try
        {
            code = Run(args);
        }
        catch (Exception ex)
        {
            // Nothing may escape silently: an unhandled exception in a double-clicked console app
            // closes the window instantly, which looks exactly like "it did nothing". It goes to the
            // LOG as well as the console — console text is lost the moment the window closes, and
            // this program is routinely run by double-click.
            ControllerLog.Write("UNHANDLED: " + ex);
            Console.Error.WriteLine();
            Console.Error.WriteLine("UNHANDLED: " + ex);
            code = 1;
        }

        ControllerLog.Write($"exit code {code}");

        // Pause only for the console-shaped modes. After the launcher closes there is nothing left
        // to read, and waiting for a keypress leaves an invisible process holding its own .exe —
        // which is exactly how a rebuild got blocked.
        bool guiRun = !args.Contains("--probe", StringComparer.OrdinalIgnoreCase)
                      && !args.Contains("--run", StringComparer.OrdinalIgnoreCase)
                      && !args.Contains("--notify-test", StringComparer.OrdinalIgnoreCase)
                      && !args.Contains("--notify-persistence-test", StringComparer.OrdinalIgnoreCase)
                      && !args.Contains("--notify-stopped-test", StringComparer.OrdinalIgnoreCase)
                      && !args.Contains("--uitest", StringComparer.OrdinalIgnoreCase);

        if (!guiRun && !args.Contains("--no-pause", StringComparer.OrdinalIgnoreCase))
        {
            Console.WriteLine();
            Console.WriteLine($"(exit code {code}) Press Enter to close…");
            Console.ReadLine();
        }
        return code;
    }

    private static int Run(string[] args)
    {
        bool keep = args.Contains("--keep", StringComparer.OrdinalIgnoreCase);
        bool show = args.Contains("--show", StringComparer.OrdinalIgnoreCase);
        // Browsing is the default; --probe opts out of it. (--browse still accepted, harmlessly.)
        bool browse = !args.Contains("--probe", StringComparer.OrdinalIgnoreCase);
        string? version = ArgValue(args, "--revit");

        ControllerLog.Start();
        Console.WriteLine("=== KH Batch standalone controller ===");
        Console.WriteLine($"(diagnostic log: {ControllerLog.FilePath})");

        // Crashes on the UI thread or a background thread never reach Main's catch — and this
        // program's first real UI failure did exactly that, dying inside a constructor with an
        // empty log. Both are routed to the file before the process goes down.
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            ControllerLog.Write("APPDOMAIN UNHANDLED: " + e.ExceptionObject);
        Application.ThreadException += (_, e) =>
            ControllerLog.Write("UI THREAD UNHANDLED: " + e.Exception);

        // Offline UI check: build the Forma browser with a stub token source and no Revit at all.
        // Isolates "does this window construct and render" from anything to do with brokering,
        // which is the difference between a five-second test and a two-minute one.
        if (args.Contains("--uitest", StringComparer.OrdinalIgnoreCase))
            return RunUiTest(show: !args.Contains("--noshow", StringComparer.OrdinalIgnoreCase));

        // Offline render: save the launcher to PNGs (light + dark) so the layout can be INSPECTED
        // rather than assumed — clipped captions and cut-off glyphs are invisible from source.
        if (args.Contains("--render", StringComparer.OrdinalIgnoreCase))
            return RunRender(ArgValue(args, "--render") ?? ".");

        // Fires the two completion toasts so they can be checked without sitting through a batch.
        // Worth having as a flag: whether a retained Windows toast reaches Notification Center
        // depends on the machine (Focus Assist, per-app notification settings), and that is a
        // question about this PC, not about the code.
        if (args.Contains("--notify-test", StringComparer.OrdinalIgnoreCase))
        {
            ApplicationConfiguration.Initialize();

            // A real file, because the finished-with-errors toast only offers its "Open log" button
            // when the log actually exists — testing with a made-up path would prove nothing.
            string sampleLog = Path.Combine(Path.GetTempPath(), "KhBatchRunner", "notify-test-log.csv");
            Directory.CreateDirectory(Path.GetDirectoryName(sampleLog)!);
            File.WriteAllText(sampleLog,
                "Datetime (Finish),Model,Task,Status,Message\r\n" +
                $"{DateTime.Now:yyyy-MM-dd HH:mm:ss},SAMPLE.rvt,Notification test,ERROR,Sample failure row\r\n");

            Console.WriteLine("Raising a round-start toast…");
            KhBatchRunner.UI.RunNotification.ShowRoundStarted("Notification test", 2, 3, new[] { 4, 4, 2 });
            Console.WriteLine("Raising a run-finished-with-errors toast (click it to open the log)…");
            KhBatchRunner.UI.RunNotification.ShowRunFinishedAndWait("Notification test", 10, 2, sampleLog);
            Console.WriteLine("  log: " + sampleLog);
            Console.WriteLine("Done. Both retained toasts were handed to Notification Center.");
            Console.WriteLine("If nothing appeared, check Settings > System > Notifications for this app,");
            Console.WriteLine("and that Focus assist / Do not disturb is off.");
            return 0;
        }
        if (args.Contains("--notify-persistence-test", StringComparer.OrdinalIgnoreCase))
        {
            ApplicationConfiguration.Initialize();
            KhBatchRunner.UI.RunNotification.ShowAndWait(
                "KH Batch Notification Center test",
                "This reminder stays until you press Dismiss (subject to Windows' three-day local-notification limit).");
            Console.WriteLine("Test toast handed to Windows Notification Center.");
            return 0;
        }
        if (args.Contains("--notify-stopped-test", StringComparer.OrdinalIgnoreCase))
        {
            ApplicationConfiguration.Initialize();
            KhBatchRunner.UI.RunNotification.ShowRunStopped(
                "v1.3.4 attempt limit test", succeeded: 1, failed: 0, maxAttempts: 4,
                incompleteModels: new[] { "RT02.rvt" },
                logPath: Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "KhBatchTest", "attempt-limit-test.csv"));
            Console.WriteLine("Attempt-limit warning handed to Windows Notification Center.");
            return 0;
        }
        Console.WriteLine();

        string? revitExe = RevitLocator.Find(version, out string locateError);
        if (revitExe is null)
        {
            Console.Error.WriteLine("FAIL: " + locateError);
            return 2;
        }
        Console.WriteLine($"Revit         : {revitExe}");
        Console.WriteLine($"Controller pid: {Process.GetCurrentProcess().Id}");
        Console.WriteLine();

        using var broker = new BrokerTokenProvider(revitExe);
        var stopwatch = Stopwatch.StartNew();

        try
        {
            broker.Start(msg =>
            {
                Console.WriteLine($"  [{stopwatch.Elapsed:mm\\:ss}] {msg}");
                ControllerLog.Write("startup: " + msg);
            });
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine();
            Console.Error.WriteLine("FAIL: " + ex.Message);
            return 3;
        }

        if (show) broker.ShowBrokerWindow();

        Console.WriteLine();
        Console.WriteLine($"Broker ready in {stopwatch.Elapsed.TotalSeconds:F0}s " +
                          $"(Revit pid {broker.BrokerPid}, window hidden: {broker.IsHidden})");
        Console.WriteLine();

        ControllerLog.Write("requesting WHOAMI");
        string user = broker.GetLoginUser();
        ControllerLog.Write($"WHOAMI -> '{user}'");
        Console.WriteLine($"Signed in as  : {(user.Length > 0 ? user : "(not reported)")}");

        ControllerLog.Write("requesting TOKEN");
        string token = broker.GetAccessToken();
        ControllerLog.Write($"TOKEN -> length {token.Length}; LastError={broker.LastError ?? "(none)"}");
        if (token.Length == 0)
        {
            Console.Error.WriteLine($"Token         : FAILED — {broker.LastError}");
            Console.Error.WriteLine();
            Console.Error.WriteLine("If this says 'Not signed in to Autodesk in Revit', open Revit once,");
            Console.Error.WriteLine("sign in to the Forma account, close it, and re-run this probe.");
            return 4;
        }

        Console.WriteLine($"Token         : OK, length {token.Length}");
        Console.WriteLine();

        // Re-fetch to prove the broker keeps serving — the whole point of a persistent agent over a
        // one-shot fetch is that the second call costs no Revit start.
        var again = Stopwatch.StartNew();
        string second = broker.GetAccessToken();
        again.Stop();
        Console.WriteLine($"Second fetch  : {(second.Length > 0 ? "OK" : "FAILED")}, " +
                          $"{again.ElapsedMilliseconds} ms (no Revit start)");
        Console.WriteLine();
        Console.WriteLine("PASS — the controller authenticated to Forma with no visible Revit.");

        if (args.Contains("--run", StringComparer.OrdinalIgnoreCase))
        {
            int.TryParse(ArgValue(args, "--split"), out int split);
            int runCode = BatchRun.Run(broker, revitExe, ArgValue(args, "--set"),
                                       ArgValue(args, "--action"), split);
            ControllerLog.Write($"batch run finished with code {runCode}");
            return runCode;
        }

        if (browse)
        {
            Console.WriteLine();
            Console.WriteLine("Opening the controller…");
            Console.WriteLine("(Everything saved here lands in the same %AppData%\\KhBatchRunner");
            Console.WriteLine(" folders the add-in reads — the two see each other's sets.)");

            // The very same window the add-in's "Edit Model Set" button opens, unmodified. It only
            // ever needed a DataManagementClient; where that client's token comes from was always
            // an implementation detail behind ITokenProvider.
            // Fill in what the add-in's shared dialogs ask their host for. The action list comes from
            // the brokering Revit's registry; Confirm keeps its default message box, which is the
            // right prompt for a program that is not Revit.
            // The account the brokering Revit is signed in as, so the shared dialogs label model
            // sets against the same identity here as they do inside Revit.
            KhBatchRunner.UI.HostServices.SignedInUserId = user;
            KhBatchRunner.UI.HostServices.ListActions = () => broker.GetActions()
                .Select(a => new KhBatchRunner.UI.ActionInfo(
                    a.Name,
                    a.Category == "Check"
                        ? KhBatchRunner.Actions.ActionCategory.Check
                        : KhBatchRunner.Actions.ActionCategory.Action,
                    a.ModifiesModel,
                    Enum.TryParse(a.Worksets, out KhBatchRunner.Actions.WorksetPosture wp)
                        ? wp
                        : null))
                .ToList();

            ControllerLog.Write("constructing LauncherWindow");
            ApplicationConfiguration.Initialize();
            using var window = new LauncherWindow(broker, user.Length > 0 ? user : "(unknown)", revitExe);
            ControllerLog.Write("window constructed; entering Application.Run");
            Application.Run(window);
            ControllerLog.Write("Application.Run returned (window closed)");

            Console.WriteLine("Controller closed.");
        }

        if (keep)
        {
            Console.WriteLine();
            Console.WriteLine($"--keep: broker (pid {broker.BrokerPid}) left running.");
            Console.WriteLine("For the orphan test, kill THIS process from Task Manager and confirm");
            Console.WriteLine("that Revit pid above terminates itself within a few seconds.");
            Console.WriteLine("Otherwise press Enter to shut it down cleanly.");
            Console.ReadLine();
        }

        return 0;
    }

    /// <summary>A token source that returns nothing — enough to construct the REST client so the
    /// window can be built and rendered without a Revit anywhere in the picture.</summary>
    private sealed class NoTokenProvider : KhBatchRunner.Auth.ITokenProvider
    {
        public string GetAccessToken() => "";
    }

    /// <summary>
    /// Renders EVERY window this process can construct to PNG, in both themes, without starting
    /// Revit or reaching Forma. Follows the <c>tools/ThemePreview</c> precedent: a layout claim you
    /// have not looked at is a guess — and clipped buttons and stunted tables only show up in a
    /// picture.
    ///
    /// Each window is rendered at its natural size, then again at its minimum size, because the two
    /// failure modes are different: content that overflows the default, and content that collapses
    /// when the window is dragged small.
    /// </summary>
    private static int RunRender(string outputFolder)
    {
        // The real registry's actions, so the panels are as crowded as they will be in practice.
        var actions = new List<KhBatchController.Auth.BrokerAction>
        {
            new("Get Site Location & PBP", "Check", false),
            new("Get Phase & Workset", "Check", false),
            new("Get Design Option", "Check", false),
            new("Get Link Workset", "Check", false),
            new("Drawing Progress", "Check", false),
            new("Type Mark Text Note", "Check", false),
            new("Purge Import CAD", "Action", true),
            new("Isolate Warning Elements", "Action", true),
        };

        int failures = 0;

        try
        {
            System.IO.Directory.CreateDirectory(outputFolder);
            ApplicationConfiguration.Initialize();

            // Sanity-check the selection summary against real saved sets at a few group sizes. The
            // rendered dialogs show it with nothing ticked, which proves only that it draws.
            var realSets = KhBatchRunner.Presets.PresetStore.LoadAll()
                .Where(s => s.Models.Count > 0)
                .OrderByDescending(s => s.Models.Count).Take(2).ToList();
            if (realSets.Count > 0)
            {
                Console.WriteLine("selection summary check:");
                foreach (bool split in new[] { false, true })
                    Console.WriteLine("  split=" + split + " -> " + KhBatchRunner.UI.SelectionSummary.Describe(
                        realSets, Array.Empty<KhBatchRunner.Presets.CloudModel>(), 2, split));
            }

            foreach (string theme in new[] { "light", "dark" })
            {
                Environment.SetEnvironmentVariable("KHBATCH_FORCE_THEME", theme);

                // Nothing here is ever started or connected: the broker is never launched and the
                // REST client is handed a token source that returns "", so every window builds its
                // chrome and stops at the first request.
                var broker = new BrokerTokenProvider(@"C:\not-launched\Revit.exe");
                var dm = new KhBatchRunner.Forma.DataManagementClient(new NoTokenProvider());
                var md = new KhBatchRunner.Forma.ModelDerivativeClient(new NoTokenProvider());
                var dc = new KhBatchRunner.Forma.DataConnectorClient(new NoTokenProvider());

                var windows = new (string Name, Func<Form> Make)[]
                {
                    ("launcher",        () => new LauncherWindow(broker, "RENDER-PREVIEW", "", actions)),
                    ("batch-run",       () => new BatchRunWindow(broker, "")),
                    ("schedule-run",    () => new KhBatchRunner.UI.ScheduleRunWindow(dm)),
                    ("forma-browser",     () => new KhBatchRunner.UI.FormaBrowserWindow(dm)),
                    ("forma-picker",      () => new KhBatchRunner.UI.FormaPickerWindow(dm)),
                    ("forma-folder",      () => new KhBatchRunner.UI.FormaFolderPickerWindow(
                                                  dm, "Choose a folder", "Pick the folder to scan:", true)),
                    ("forma-extract",     () => new KhBatchRunner.UI.FormaExtractWindow(dm)),
                    ("data-extract",    () => new KhBatchRunner.UI.DataExtractWindow(dm, dc)),
                    ("check-publish",   () => new KhBatchRunner.UI.CheckSheetPublishWindow(dm, md)),
                    ("edit-script-set", () => new KhBatchRunner.UI.EditScriptSetWindow()),
                    ("about",           () => new KhBatchRunner.UI.AboutWindow()),
                    ("run-watch",       () => new KhBatchRunner.UI.RunWatchWindow(SampleManifest())),
                    ("action-results",  () => SampleResultsWindow()),
                };

                foreach ((string name, Func<Form> make) in windows)
                {
                    try
                    {
                        // Construction time, printed per window: "this dialog is slow to open" is a
                        // measurement, not a feeling, and the constructor is where it would be.
                        var built = Stopwatch.StartNew();
                        using Form window = make();
                        built.Stop();
                        Console.WriteLine($"  built {name}-{theme} in {built.ElapsedMilliseconds} ms");

                        Shoot(window, System.IO.Path.Combine(outputFolder, $"{name}-{theme}.png"));
                        Size natural = window.Size;

                        // Again at the smallest size the user can drag it to.
                        Size min = window.MinimumSize;
                        if (min.Width > 0 && min.Height > 0 && min != window.Size)
                        {
                            window.Size = min;
                            Application.DoEvents();
                            Shoot(window, System.IO.Path.Combine(outputFolder, $"{name}-{theme}-min.png"));
                        }

                        // And once WIDE. Reflowing layouts fail differently when there is too much
                        // room: a wrapping button row fits more per line than its height was
                        // reserved for, and leaves a dead band under itself. Only a wide render
                        // shows that — the default size hid it completely.
                        window.Size = new Size(1180, Math.Max(760, natural.Height));
                        Application.DoEvents();
                        Shoot(window, System.IO.Path.Combine(outputFolder, $"{name}-{theme}-wide.png"));
                    }
                    catch (Exception ex)
                    {
                        failures++;
                        Console.Error.WriteLine($"  {name}-{theme}: FAILED — {ex.GetType().Name}: {ex.Message}");
                    }
                }
            }
            return failures == 0 ? 0 : 9;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("render FAILED: " + ex);
            return 9;
        }
        finally
        {
            Environment.SetEnvironmentVariable("KHBATCH_FORCE_THEME", null);
        }
    }

    /// <summary>Lays a window out off-screen and writes it to PNG. Shown rather than merely created,
    /// because WinForms does not run a real layout pass until the handle exists.</summary>
    private static void Shoot(Form window, string path)
    {
        window.StartPosition = FormStartPosition.Manual;
        window.Location = new Point(-4000, -4000);
        window.ShowInTaskbar = false;
        window.Show();
        Application.DoEvents();

        using var bitmap = new Bitmap(Math.Max(1, window.Width), Math.Max(1, window.Height));
        window.DrawToBitmap(bitmap, new Rectangle(0, 0, bitmap.Width, bitmap.Height));
        bitmap.Save(path, System.Drawing.Imaging.ImageFormat.Png);
        window.Hide();

        Console.WriteLine($"rendered {System.IO.Path.GetFileName(path)}  ({bitmap.Width}x{bitmap.Height})");
    }

    /// <summary>A manifest describing a plausible split run, so the progress window renders with
    /// realistic counts instead of zeroes.</summary>
    private static KhBatchRunner.Schedule.RunManifest SampleManifest()
    {
        var models = new List<KhBatchRunner.Presets.CloudModel>();
        foreach (string n in new[] { "T1A-AR", "T1A-EE", "T1A-ST", "T2A-AR", "T2A-EE",
                                     "T2A-ST", "T3A-AR", "T3A-EE", "T3A-ST", "T4A-AR" })
            models.Add(new KhBatchRunner.Presets.CloudModel { DisplayName = n + ".rvt" });

        var job = new KhBatchRunner.Schedule.ScheduledJob { Name = "Batch Run" };
        job.RunItems.Add(new KhBatchRunner.Schedule.RunItem(
            "Get Phase & Workset", KhBatchRunner.Schedule.RunItemKind.Action, null));

        var manifest = new KhBatchRunner.Schedule.RunManifest
        {
            Name = "Batch Run",
            BaseJob = job,
            GroupSize = 4,
            AllModels = models,
            TrackingLogPath = @"C:\Users\You\Desktop\Test Forma Log\Log\20260726_Batch Run.csv"
        };
        manifest.Rounds.Add(new KhBatchRunner.Schedule.RunRound
        {
            Round = 1,
            StartedAt = DateTime.Now,
            Groups = new List<KhBatchRunner.Schedule.RunGroup>
            {
                new() { Models = models.GetRange(0, 4) },
                new() { Models = models.GetRange(4, 4) },
                new() { Models = models.GetRange(8, 2) },
            }
        });
        return manifest;
    }

    /// <summary>The per-model results grid, filled with enough columns to show whether it scrolls
    /// rather than clipping.</summary>
    private static Form SampleResultsWindow()
    {
        var columns = new List<string> { "Workset", "Phase", "Design Option", "Elements", "Note" };
        var rows = new List<IReadOnlyDictionary<string, string>>();
        for (int i = 1; i <= 12; i++)
            rows.Add(new Dictionary<string, string>
            {
                ["Workset"] = $"Shared Levels and Grids {i}",
                ["Phase"] = i % 2 == 0 ? "New Construction" : "Existing",
                ["Design Option"] = i % 3 == 0 ? "Option 1 (primary)" : "(none)",
                ["Elements"] = (i * 137).ToString(),
                ["Note"] = i % 4 == 0 ? "more than one phase on this workset" : ""
            });

        return new KhBatchRunner.UI.ActionResultsWindow(
            "Get Phase & Workset", "T1A-AR.rvt", columns, rows,
            "12 workset(s) read. Nothing was changed.", false);
    }

    private static int RunUiTest(bool show)
    {
        Console.WriteLine($"UI test — theme: {(Theme.IsDark() ? "DARK" : "LIGHT")} " +
                          $"(KHBATCH_FORCE_THEME={Environment.GetEnvironmentVariable("KHBATCH_FORCE_THEME") ?? "unset"})");
        ControllerLog.Write($"uitest: IsDark={Theme.IsDark()}");

        try
        {
            ControllerLog.Write("uitest: constructing FormaBrowserWindow");
            ApplicationConfiguration.Initialize();
            using var window = new FormaBrowserWindow(new DataManagementClient(new NoTokenProvider()));
            ControllerLog.Write("uitest: constructed OK");
            Console.WriteLine("Window constructed OK.");
            if (!show)
            {
                Console.WriteLine("PASS (construction only, --noshow).");
                return 0;
            }
            Console.WriteLine("Showing it — close the window to finish.");
            Application.Run(window);
            ControllerLog.Write("uitest: closed cleanly");
            Console.WriteLine("PASS — window rendered and closed with no Revit involved.");
            return 0;
        }
        catch (Exception ex)
        {
            ControllerLog.Write("uitest FAILED: " + ex);
            Console.Error.WriteLine("FAIL: " + ex);
            return 5;
        }
    }

    private static string? ArgValue(string[] args, string name)
    {
        int i = Array.FindIndex(args, a => string.Equals(a, name, StringComparison.OrdinalIgnoreCase));
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }
}
