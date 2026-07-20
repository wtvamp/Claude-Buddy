using Avalonia;

namespace ClaudeBuddy
{
    internal static class Program
    {
        [STAThread]
        public static void Main(string[] args) =>
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);

        public static AppBuilder BuildAvaloniaApp() =>
            AppBuilder.Configure<App>()
                .UsePlatformDetect()
                // The orbs are the whole UI; no Dock icon needed on macOS.
                .With(new MacOSPlatformOptions { ShowInDock = false })
                .LogToTrace();
    }
}
