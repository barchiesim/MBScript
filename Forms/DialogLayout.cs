namespace MBScript.Forms;

/// <summary>
/// Helper comuni ai dialoghi costruiti con TableLayoutPanel/FlowLayoutPanel.
/// Stesso approccio di LoginForm: AutoScaleMode.None e misure ricavate dal testo
/// reale (già in pixel al DPI corrente). La scalatura automatica di WinForms su
/// coordinate assolute, in alcune sessioni (es. desktop remoto al 250%), scala i
/// font ma non posizioni e dimensioni, sovrapponendo i controlli.
/// </summary>
internal static class DialogLayout
{
    #region Misure

    /// <summary>Spaziatura proporzionale all'altezza del font (equivale a ~4px a 96 DPI).</summary>
    public static int Gap(Control control, float factor = 0.25f)
    {
        return Math.Max(2, (int)Math.Round(control.Font.Height * factor));
    }

    /// <summary>Larghezza minima uniforme per i pulsanti di conferma.</summary>
    public static int ButtonWidth(Control control, params string[] texts)
    {
        int max = 0;
        foreach (string text in texts)
        {
            max = Math.Max(max, TextRenderer.MeasureText(text, control.Font).Width);
        }

        return max + control.Font.Height * 2;
    }

    #endregion Misure

    #region Dimensionamento

    /// <summary>
    /// Il form parte con AutoSize per calcolare la dimensione dal contenuto; dopo il
    /// Load AutoSize viene spento (altrimenti impedirebbe il resize manuale), la
    /// dimensione calcolata diventa il minimo e la finestra viene contenuta
    /// nell'area di lavoro dello schermo.
    /// </summary>
    public static void LockSizeAfterLoad(Form form)
    {
        form.AutoSize = true;
        form.AutoSizeMode = AutoSizeMode.GrowAndShrink;
        form.Load += (_, _) =>
        {
            form.PerformLayout();
            Size contentSize = form.Size;
            Rectangle area = Screen.FromControl(form.Owner ?? form).WorkingArea;
            Size bounded = new Size(Math.Min(contentSize.Width, area.Width), Math.Min(contentSize.Height, area.Height));
            form.AutoSize = false;
            form.MinimumSize = bounded;
            form.Size = bounded;

            // La posizione CenterParent viene calcolata sulla dimensione precedente:
            // ricentriamo sulla finestra proprietaria restando dentro lo schermo.
            Rectangle reference = form.Owner?.Bounds ?? area;
            int x = reference.Left + (reference.Width - bounded.Width) / 2;
            int y = reference.Top + (reference.Height - bounded.Height) / 2;
            form.Location = new Point(
                Math.Clamp(x, area.Left, Math.Max(area.Left, area.Right - bounded.Width)),
                Math.Clamp(y, area.Top, Math.Max(area.Top, area.Bottom - bounded.Height)));
        };
    }

    #endregion Dimensionamento
}
