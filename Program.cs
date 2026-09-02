using MBScript.Forms;

namespace MBScript;

internal static class Program
{
    /// <summary>
    /// STAThread è obbligatorio: le chiamate OLE (Clipboard, drag&amp;drop, dialoghi shell)
    /// richiedono un thread in single thread apartment. Con i top-level statements il
    /// punto di ingresso generato dal compilatore non ha l'attributo e Ctrl+C/Ctrl+V
    /// terminano con "Current thread must be set to single thread apartment (STA) mode".
    /// </summary>
    [STAThread]
    private static void Main()
    {
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.SetHighDpiMode(HighDpiMode.SystemAware);
        Application.Run(new LoginForm());
    }
}
