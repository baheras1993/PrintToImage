namespace PrintToImageSettings;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        // --settings <file>: open another settings file (e.g. a demo setup for screenshots) instead of the installed one.
        int set = Array.IndexOf(args, "--settings");
        using var form = new MainForm(set >= 0 && set + 1 < args.Length ? Path.GetFullPath(args[set + 1]) : null);
        int shot = Array.IndexOf(args, "--screenshots");
        if (shot >= 0 && shot + 1 < args.Length)
        {
            // Diagnostics: render each tab to a PNG without user interaction.
            form.StartPosition = FormStartPosition.Manual;
            form.Location = new Point(-4000, -4000);
            form.ShowInTaskbar = false;
            form.Show();
            form.SaveScreenshots(args[shot + 1]);
            return 0;
        }
        Application.Run(form);
        return 0;
    }
}
