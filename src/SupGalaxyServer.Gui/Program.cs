namespace SupGalaxyServer.Gui;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        var dataDir = args.Length >= 2 && args[0] == "--data" ? args[1] : ServerSettings.DefaultDataDirectory;
        Application.Run(new MainForm(dataDir));
    }
}
