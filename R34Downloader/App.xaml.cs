using System.Globalization;
using System.Windows;

namespace R34Downloader;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var culture = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "ru"
            ? new CultureInfo("ru")
            : new CultureInfo("en");

        CultureInfo.DefaultThreadCurrentCulture = culture;
        CultureInfo.DefaultThreadCurrentUICulture = culture;

        new MainWindow().Show();
    }
}
