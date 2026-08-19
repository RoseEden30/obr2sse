using System.Runtime.InteropServices;

namespace Obr2SseApp;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        // A headless self-check for the path detection, so the whole thing can be verified without
        // opening the window.
        if (args.Contains("--detect"))
        {
            AttachConsole(-1);
            Console.WriteLine($"oblivion: {GameDetect.FindOblivion() ?? "(not found)"}");
            Console.WriteLine($"skyrim:   {GameDetect.FindSkyrim() ?? "(not found)"}");
            return;
        }

        // Headless run of the exact conversion the button performs, for testing the whole workflow.
        // --run <obr> <skyrim> <output> <standalone|replacer> <zip|loose> [cancelAfterN]
        if (args.Length >= 6 && args[0] == "--run")
        {
            AttachConsole(-1);
            var mode = args[4] == "replacer" ? ConversionMode.Replacer : ConversionMode.Standalone;
            var format = args[5] == "loose" ? OutputFormat.Loose : OutputFormat.Zip;
            int cancelAfter = args.Length >= 7 && int.TryParse(args[6], out var n) ? n : -1;

            using var cts = new CancellationTokenSource();
            void Progress(int done, int total, string name)
            {
                Console.WriteLine($"  {done}/{total} {name}");
                if (cancelAfter > 0 && done >= cancelAfter) cts.Cancel();
            }

            try
            {
                var result = Conversion.Run(args[1], args[2], args[3], mode, format,
                    Obr2Sse.ObrMesh.MeshQuality.High, Progress, cts.Token);
                Console.WriteLine(result.Message);
                Console.WriteLine($"reveal: {result.Reveal}");
            }
            catch (OperationCanceledException)
            {
                Console.WriteLine("cancelled cleanly");
            }
            return;
        }

        ApplicationConfiguration.Initialize();
        Application.Run(new MainForm());
    }

    // WinExe detaches from the console it was launched from; reattach so --detect can print.
    [DllImport("kernel32.dll")]
    private static extern bool AttachConsole(int dwProcessId);
}
