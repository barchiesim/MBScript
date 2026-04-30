using PBScriptNew.Config;
using PBScriptNew.Models;
using PBScriptNew.Services;

namespace PBScriptNew.Forms;

public class LoginForm : Form
{
    private TextBox txtServer = null!;
    private TextBox txtDatabase = null!;
    private RadioButton rdoSql = null!;
    private RadioButton rdoWindows = null!;
    private TextBox txtUser = null!;
    private TextBox txtPassword = null!;
    private Label lblUser = null!;
    private Label lblPassword = null!;
    private Button btnConnect = null!;
    private Label lblError = null!;

    public LoginForm()
    {
        InitializeComponent();
    }

    private void InitializeComponent()
    {
        Text = "Login SQL Server - PBScript";
        Size = new Size(400, 360);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        Font = new Font("Segoe UI", 9f);

        var cfg = GlobalConfig.Instance.DbConfig;
        // Override with last used connection saved in settings if present
        try
        {
            var s = SettingsService.LoadAuditSettings();
            if (!string.IsNullOrEmpty(s.LastServer)) cfg.Server = s.LastServer;
            if (!string.IsNullOrEmpty(s.LastDatabase)) cfg.Database = s.LastDatabase;
            if (!string.IsNullOrEmpty(s.LastUser)) cfg.User = s.LastUser;
        }
        catch { }
        int margin = 16, lw = 80, cw = 270;

        var lblServer = new Label { Text = "Server:", Location = new Point(margin, 16), Size = new Size(lw, 20), TextAlign = ContentAlignment.MiddleRight };
        txtServer = new TextBox { Text = cfg.Server, Location = new Point(margin + lw + 4, 14), Size = new Size(cw, 22) };

        var lblDatabase = new Label { Text = "Database:", Location = new Point(margin, 46), Size = new Size(lw, 20), TextAlign = ContentAlignment.MiddleRight };
        txtDatabase = new TextBox { Text = cfg.Database, Location = new Point(margin + lw + 4, 44), Size = new Size(cw, 22) };

        // Gruppo autenticazione: aumentiamo l'altezza per evitare sovrapposizioni con i controlli sottostanti
        var grpAuth = new GroupBox { Text = "Autenticazione", Location = new Point(margin, 76), Size = new Size(355, 150) };
        rdoSql = new RadioButton { Text = "SQL Server Authentication", Location = new Point(12, 22), Size = new Size(240, 20), Checked = !cfg.IntegratedSecurity };
        rdoWindows = new RadioButton { Text = "Windows Authentication", Location = new Point(12, 48), Size = new Size(240, 20), Checked = cfg.IntegratedSecurity };

        lblUser = new Label { Text = "Utente:", Location = new Point(12, 76), Size = new Size(70, 20), TextAlign = ContentAlignment.MiddleRight };
        txtUser = new TextBox { Text = cfg.User, Location = new Point(86, 74), Size = new Size(120, 22) };
        lblPassword = new Label { Text = "Password:", Location = new Point(12, 100), Size = new Size(70, 20), TextAlign = ContentAlignment.MiddleRight };
        // Usa la visualizzazione password di sistema e lascia spazio sufficiente
        txtPassword = new TextBox { Text = cfg.Password, UseSystemPasswordChar = true, Location = new Point(86, 98), Size = new Size(160, 22) };

        grpAuth.Controls.AddRange(new Control[] { rdoSql, rdoWindows, lblUser, txtUser, lblPassword, txtPassword });
        rdoSql.CheckedChanged += (_, _) => UpdateAuthVisibility();
        rdoWindows.CheckedChanged += (_, _) => UpdateAuthVisibility();

        // Spostiamo il messaggio di errore e i pulsanti più in basso per evitare sovrapposizioni
        lblError = new Label { Text = "", ForeColor = Color.Red, Location = new Point(margin, 236), Size = new Size(355, 20), TextAlign = ContentAlignment.MiddleCenter };

        btnConnect = new Button { Text = "Connetti", Location = new Point(170, 268), Size = new Size(90, 28) };
        var btnCancel = new Button { Text = "Annulla", Location = new Point(270, 268), Size = new Size(90, 28), DialogResult = DialogResult.Cancel };

        // Premi Annulla chiude l'applicazione (funziona anche se il form è startup principale)
        btnCancel.Click += (_, _) => Close();

        btnConnect.Click += BtnConnect_Click;

        Controls.AddRange(new Control[] { lblServer, txtServer, lblDatabase, txtDatabase, grpAuth, lblError, btnConnect, btnCancel });
        AcceptButton = btnConnect;
        CancelButton = btnCancel;

        UpdateAuthVisibility();
    }

    private void UpdateAuthVisibility()
    {
        bool isSql = rdoSql.Checked;
        lblUser.Enabled = txtUser.Enabled = lblPassword.Enabled = txtPassword.Enabled = isSql;
    }

    private async void BtnConnect_Click(object? sender, EventArgs e)
    {
        lblError.Text = "";
        btnConnect.Enabled = false;
        btnConnect.Text = "Connessione...";

        var config = new DatabaseConfig
        {
            Server = txtServer.Text.Trim(),
            Database = txtDatabase.Text.Trim(),
            User = rdoSql.Checked ? txtUser.Text.Trim() : "",
            Password = rdoSql.Checked ? txtPassword.Text : "",
            IntegratedSecurity = rdoWindows.Checked
        };

        var sqlSvc = new SqlService();
        bool ok = await sqlSvc.ConnectAsync(config);

        if (ok)
        {
            var main = new MainForm(sqlSvc, config);
            main.Show();
            Hide();
            main.FormClosed += (_, _) => Close();
        }
        else
        {
            lblError.Text = "Connessione fallita. Verifica le credenziali.";
            btnConnect.Enabled = true;
            btnConnect.Text = "Connetti";
            sqlSvc.Dispose();
        }
    }
}
