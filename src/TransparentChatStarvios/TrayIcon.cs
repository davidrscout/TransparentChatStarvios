using System.Runtime.InteropServices;

namespace TransparentChatStarvios;

/// <summary>
/// Icono en el área de notificación ("iconos ocultos") con Win32 puro, sin cargar WinForms,
/// para no sumar memoria. Los clics llegan a la ventana como el mensaje <c>callbackMessage</c>.
/// </summary>
sealed class TrayIcon : IDisposable
{
    public static readonly int TaskbarCreated = RegisterWindowMessage("TaskbarCreated");

    readonly IntPtr _hwnd;
    readonly int _callback;
    readonly IntPtr _icon;

    public TrayIcon(IntPtr hwnd, int callbackMessage)
    {
        _hwnd = hwnd;
        _callback = callbackMessage;
        var small = new IntPtr[1];
        if (Environment.ProcessPath is { } exe && ExtractIconEx(exe, 0, null, small, 1) > 0) _icon = small[0];
        Add();
    }

    public void Add()
    {
        var d = Data(NIF_MESSAGE | NIF_ICON | NIF_TIP);
        Shell_NotifyIcon(NIM_ADD, ref d);
    }

    public void ShowBalloon(string title, string text)
    {
        var d = Data(NIF_INFO);
        d.szInfoTitle = title;
        d.szInfo = text;
        d.dwInfoFlags = NIIF_INFO;
        Shell_NotifyIcon(NIM_MODIFY, ref d);
    }

    /// <summary>Muestra el menú junto al ratón y devuelve el id elegido (0 si se cierra sin elegir). Id 0 = separador.</summary>
    public int ShowMenu(IReadOnlyList<(int id, string text)> items)
    {
        var menu = CreatePopupMenu();
        foreach (var (id, text) in items)
            AppendMenu(menu, id == 0 ? MF_SEPARATOR : MF_STRING, (UIntPtr)id, id == 0 ? null : text);
        GetCursorPos(out var pt);
        SetForegroundWindow(_hwnd); // sin esto el menú no se cierra al hacer clic fuera
        int cmd = TrackPopupMenuEx(menu, TPM_RETURNCMD | TPM_RIGHTBUTTON | TPM_BOTTOMALIGN, pt.X, pt.Y, _hwnd, IntPtr.Zero);
        DestroyMenu(menu);
        return cmd;
    }

    NOTIFYICONDATA Data(int flags) => new()
    {
        cbSize = Marshal.SizeOf<NOTIFYICONDATA>(),
        hWnd = _hwnd,
        uID = 1,
        uFlags = flags,
        uCallbackMessage = _callback,
        hIcon = _icon,
        szTip = "Transparent Chat Starvios — clic: bloquear/desbloquear · clic derecho: menú",
        szInfo = "",
        szInfoTitle = "",
    };

    public void Dispose()
    {
        var d = Data(0);
        Shell_NotifyIcon(NIM_DELETE, ref d);
        if (_icon != IntPtr.Zero) DestroyIcon(_icon);
    }

    const int NIM_ADD = 0, NIM_MODIFY = 1, NIM_DELETE = 2;
    const int NIF_MESSAGE = 1, NIF_ICON = 2, NIF_TIP = 4, NIF_INFO = 0x10, NIIF_INFO = 1;
    const uint MF_STRING = 0, MF_SEPARATOR = 0x800;
    const uint TPM_RETURNCMD = 0x100, TPM_RIGHTBUTTON = 0x2, TPM_BOTTOMALIGN = 0x20;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct NOTIFYICONDATA
    {
        public int cbSize;
        public IntPtr hWnd;
        public int uID;
        public int uFlags;
        public int uCallbackMessage;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szTip;
        public int dwState;
        public int dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string szInfo;
        public int uVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string szInfoTitle;
        public int dwInfoFlags;
        public Guid guidItem;
        public IntPtr hBalloonIcon;
    }

    struct POINT { public int X, Y; }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] static extern bool Shell_NotifyIcon(int msg, ref NOTIFYICONDATA data);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] static extern uint ExtractIconEx(string file, int index, IntPtr[]? large, IntPtr[]? small, uint n);
    [DllImport("user32.dll")] static extern bool DestroyIcon(IntPtr h);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int RegisterWindowMessage(string name);
    [DllImport("user32.dll")] static extern IntPtr CreatePopupMenu();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern bool AppendMenu(IntPtr menu, uint flags, UIntPtr id, string? text);
    [DllImport("user32.dll")] static extern int TrackPopupMenuEx(IntPtr menu, uint flags, int x, int y, IntPtr hwnd, IntPtr tpm);
    [DllImport("user32.dll")] static extern bool DestroyMenu(IntPtr menu);
    [DllImport("user32.dll")] static extern bool GetCursorPos(out POINT p);
    [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr h);
}
