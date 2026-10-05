using System.Windows;

namespace TransparentChatStarvios;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        // Una sola instancia: si ya está abierta, no abrimos otra ventana.
        _single = new Mutex(true, "TransparentChatStarvios.SingleInstance", out bool first);
        if (!first) { Shutdown(); return; }
        base.OnStartup(e);
    }

    Mutex? _single;
}
