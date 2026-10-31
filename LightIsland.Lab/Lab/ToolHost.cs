using System;
using System.IO;
using System.Windows;

namespace LightIsland;

/// <summary>
/// Every tool runs in its own short-lived process ("LightIslandLab.exe --tool NAME"), so the island itself stays
/// small. Closing the tool window ends the process and gives all of its memory back.
/// </summary>
internal static class ToolHost
{
    public static readonly string DataDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "LightIslandLab", "data");

    public static IntPtr PreviousForeground;

    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();

    public static string DataFile(string name)
    {
        Directory.CreateDirectory(DataDir);
        return Path.Combine(DataDir, name);
    }

    public static void Launch(string tool, string arg = "")
    {
        try
        {
            string? exe = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exe)) return;
            string args = "--tool " + tool + (arg.Length > 0 ? " \"" + arg.Replace("\"", "'") + "\"" : "");
            var psi = new System.Diagnostics.ProcessStartInfo(exe, args) { UseShellExecute = false };
            // "dotnet run" case: the host is dotnet.exe and needs the dll first
            if (Path.GetFileNameWithoutExtension(exe).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
                psi = new System.Diagnostics.ProcessStartInfo(exe, "\"" + Path.Combine(AppContext.BaseDirectory, "LightIslandLab.dll") + "\" " + args) { UseShellExecute = false };
            System.Diagnostics.Process.Start(psi);
        }
        catch { }
    }

    public static int Run(string[] args)
    {
        string name = args.Length > 1 ? args[1].ToLowerInvariant() : "";
        string arg = args.Length > 2 ? args[2] : "";
        PreviousForeground = GetForegroundWindow();

        var app = new Application { ShutdownMode = ShutdownMode.OnLastWindowClose };
        app.DispatcherUnhandledException += (_, e) =>
        {
            try
            {
                Directory.CreateDirectory(DataDir);
                File.AppendAllText(Path.Combine(DataDir, "tool-error.log"), DateTime.Now + " " + name + "\n" + e.Exception + "\n\n");
            }
            catch { }
            e.Handled = true;
        };
        Ui.Init(AppSettings.Load());

        Window? w = null;
        try
        {
            w = name switch
            {
                "ocr" => new OcrTool(arg),
                "color" => new ColorTool(arg),
                "timer" => new TimerTool(arg),
                "translate" => new TranslateTool(arg),
                "remind" => new RemindTool(),
                "remind-alert" => new RemindAlert(arg),
                "qr" => new QrTool(arg),
                "convert" => new ConvertTool(),
                "mail" => new MailTool(),
                "ram" => new RamTool(),
                "net" => new NetTool(),
                "snippets" => new SnippetsTool(arg),
                "crypto" => new CryptoTool(),
                "ticker" => TickerWindow.Create(),
                "ask" => AskTool.Create(),
                _ => null
            };
        }
        catch (Exception ex)
        {
            MessageBox.Show("Could not open the tool: " + ex.Message, "LightIsland Lab");
            return 2;
        }
        if (w == null) return 1;
        w.Show();
        w.Activate();
        return app.Run();
    }
}

/// <summary>Tiny preferences of the tools (kept apart from the island settings, so a tool can never overwrite them).</summary>
internal static class Pref
{
    private static string F => ToolHost.DataFile("prefs.json");

    private static System.Collections.Generic.Dictionary<string, string> Load()
    {
        try
        {
            if (File.Exists(F))
                return System.Text.Json.JsonSerializer.Deserialize<System.Collections.Generic.Dictionary<string, string>>(File.ReadAllText(F)) ?? new();
        }
        catch { }
        return new();
    }

    public static string Get(string key, string def) => Load().TryGetValue(key, out string? v) && v != null ? v : def;

    public static void Set(string key, string value)
    {
        try
        {
            var d = Load();
            d[key] = value;
            File.WriteAllText(F, System.Text.Json.JsonSerializer.Serialize(d));
        }
        catch { }
    }
}
