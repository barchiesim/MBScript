namespace MBScript.Forms;

public class AuditFilterDialog : Form
{
    private TextBox txtFilter  = null!;
    private TextBox txtExclude = null!;

    public string AuditFilter  => txtFilter.Text;
    public string AuditExclude => txtExclude.Text;

    public AuditFilterDialog(string selectedDb, string initialFilter, string initialExclude)
    {
        Text            = "Configurazione Filtri Audit";
        Size            = new Size(640, 300);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox     = false;
        MinimizeBox     = false;
        StartPosition   = FormStartPosition.CenterParent;
        Font            = new Font("Segoe UI", 9f);

        int pad = 16, y = 14;

        Controls.Add(new Label { Text = $"Database: {selectedDb}  →  {selectedDb}_UPD", Location = new Point(pad, y), AutoSize = true, Font = new Font("Segoe UI", 9f, FontStyle.Bold) });
        y += 28;

        Controls.Add(new Label { Text = "FILTRO1 – Condizione SQL per includere tabelle:", Location = new Point(pad, y), AutoSize = true, Font = new Font("Segoe UI", 9f, FontStyle.Bold) });
        y += 20;
        txtFilter = new TextBox { Text = initialFilter, Location = new Point(pad, y), Size = new Size(595, 22), Font = new Font("Courier New", 9f) };
        Controls.Add(txtFilter);
        y += 32;

        Controls.Add(new Label { Text = "ESCLUDI1 – Tabelle da escludere:", Location = new Point(pad, y), AutoSize = true, Font = new Font("Segoe UI", 9f, FontStyle.Bold) });
        y += 20;
        txtExclude = new TextBox { Text = initialExclude, Location = new Point(pad, y), Size = new Size(595, 22), Font = new Font("Courier New", 9f) };
        Controls.Add(txtExclude);
        y += 40;

        var btnReset = new Button { Text = "🔄 Ripristina Standard", Location = new Point(pad, y), Size = new Size(200, 28) };
        btnReset.Click += (_, _) =>
        {
            txtFilter.Text  = "and (SUBSTRING(name,3,1) = '_')";
            txtExclude.Text = "and name NOT IN ('NUMBERS', 'FW_ASYNC_SCHEDULER', 'FW_PROCESSING_DETAILS')";
        };
        Controls.Add(btnReset);

        var btnOk     = new Button { Text = "Inizializza", DialogResult = DialogResult.OK,     Location = new Point(430, y), Size = new Size(90, 28) };
        var btnCancel = new Button { Text = "Annulla",     DialogResult = DialogResult.Cancel, Location = new Point(530, y), Size = new Size(90, 28) };
        Controls.Add(btnOk);
        Controls.Add(btnCancel);
        AcceptButton = btnOk;
        CancelButton = btnCancel;
    }
}
