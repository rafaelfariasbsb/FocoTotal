namespace FocoTotal.App;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        using var mutex = new Mutex(true, @"Local\FocoTotal.App", out var first);
        if (!first)
        {
            // Já existe uma janela aberta nesta sessão: pede para ela aparecer.
            NativeMethods.BroadcastShow();
            return;
        }

        ApplicationConfiguration.Initialize();
        var startInTray = args.Any(a => a.Equals("--bandeja", StringComparison.OrdinalIgnoreCase));
        Application.Run(new MainForm(startInTray));
    }
}

internal static partial class NativeMethods
{
    public static readonly int WmShowMe = RegisterWindowMessage("FocoTotal.ShowMe");
    private static readonly IntPtr HwndBroadcast = new(0xffff);

    [System.Runtime.InteropServices.LibraryImport("user32.dll", EntryPoint = "RegisterWindowMessageW", StringMarshalling = System.Runtime.InteropServices.StringMarshalling.Utf16)]
    private static partial int RegisterWindowMessage(string message);

    [System.Runtime.InteropServices.LibraryImport("user32.dll", EntryPoint = "PostMessageW")]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static partial bool PostMessage(IntPtr hwnd, int msg, IntPtr wparam, IntPtr lparam);

    public static void BroadcastShow() => PostMessage(HwndBroadcast, WmShowMe, IntPtr.Zero, IntPtr.Zero);
}
