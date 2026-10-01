using MBScript.Config;
using MBScript.Models;
using MBScript.Services;

namespace MBScript.Forms;

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
        // Tutte le misure derivano dal testo reale (pixel al DPI corrente): con Windows
        // al 250% la scalatura automatica non ingrandiva le larghezze fisse. None evita
        // che, dove invece funziona, misure già corrette vengano ingrandite due volte.
        AutoScaleMode = AutoScaleMode.None;

        Text = "Login SQL Server - MBScript";
        FormBorderStyle = FormBorderStyle.Sizable;
        MaximizeBox = true;
        MinimizeBox = true;
        StartPosition = FormStartPosition.CenterScreen;
        Font = new Font("Segoe UI", 9f);
        Padding = new Padding(16);
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        // Larghezza minima: titolo + icona + i tre pulsanti della barra, altrimenti il
        // titolo resta troncato ("Lo...").
        int titleWidth = TextRenderer.MeasureText(Text, SystemFonts.CaptionFont).Width
                         + SystemInformation.SmallIconSize.Width
                         + SystemInformation.CaptionButtonSize.Width * 3
                         + SystemInformation.FrameBorderSize.Width * 2 + 24;
        MinimumSize = new Size(titleWidth, 0);
        int FieldWidth(string sample) => TextRenderer.MeasureText(sample, Font).Width + 12;

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

        TableLayoutPanel root = new()
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink
        };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));

        static Label BuildFieldLabel(string text) => new()
        {
            Text = text,
            AutoSize = true,
            TextAlign = ContentAlignment.MiddleRight,
            Anchor = AnchorStyles.Right,
            Margin = new Padding(3, 9, 6, 3)
        };

        Label lblServer = BuildFieldLabel("Server:");
        txtServer = new TextBox { Text = cfg.Server, Width = Math.Max(FieldWidth("SV-VM-00000.teamsystem.com\\SQL"), FieldWidth(cfg.Server ?? "")), Anchor = AnchorStyles.Left | AnchorStyles.Right, Margin = new Padding(3) };
        root.Controls.Add(lblServer, 0, 0);
        root.Controls.Add(txtServer, 1, 0);

        Label lblDatabase = BuildFieldLabel("Database:");
        txtDatabase = new TextBox { Text = cfg.Database, Anchor = AnchorStyles.Left | AnchorStyles.Right, Margin = new Padding(3) };
        root.Controls.Add(lblDatabase, 0, 1);
        root.Controls.Add(txtDatabase, 1, 1);

        GroupBox grpAuth = new()
        {
            Text = "Autenticazione",
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Dock = DockStyle.Fill,
            Margin = new Padding(3, 8, 3, 3),
            Padding = new Padding(10, 6, 10, 10)
        };

        TableLayoutPanel authLayout = new()
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink
        };
        authLayout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        authLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));

        rdoSql = new RadioButton { Text = "SQL Server Authentication", AutoSize = true, Checked = !cfg.IntegratedSecurity, Margin = new Padding(3) };
        rdoWindows = new RadioButton { Text = "Windows Authentication", AutoSize = true, Checked = cfg.IntegratedSecurity, Margin = new Padding(3) };
        authLayout.Controls.Add(rdoSql, 0, 0);
        authLayout.SetColumnSpan(rdoSql, 2);
        authLayout.Controls.Add(rdoWindows, 0, 1);
        authLayout.SetColumnSpan(rdoWindows, 2);

        lblUser = BuildFieldLabel("Utente:");
        txtUser = new TextBox { Text = cfg.User, Width = FieldWidth("XXXXXXXXXXXXXXXX"), Anchor = AnchorStyles.Left, Margin = new Padding(3) };
        authLayout.Controls.Add(lblUser, 0, 2);
        authLayout.Controls.Add(txtUser, 1, 2);

        lblPassword = BuildFieldLabel("Password:");
        // Usa la visualizzazione password di sistema
        txtPassword = new TextBox { Text = cfg.Password, UseSystemPasswordChar = true, Width = FieldWidth("XXXXXXXXXXXXXXXX"), Anchor = AnchorStyles.Left, Margin = new Padding(3) };
        authLayout.Controls.Add(lblPassword, 0, 3);
        authLayout.Controls.Add(txtPassword, 1, 3);

        grpAuth.Controls.Add(authLayout);
        rdoSql.CheckedChanged += (_, _) => UpdateAuthVisibility();
        rdoWindows.CheckedChanged += (_, _) => UpdateAuthVisibility();

        root.Controls.Add(grpAuth, 0, 2);
        root.SetColumnSpan(grpAuth, 2);

        lblError = new Label
        {
            Text = "",
            ForeColor = Color.Red,
            AutoSize = true,
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleCenter,
            Margin = new Padding(3, 10, 3, 3)
        };
        root.Controls.Add(lblError, 0, 3);
        root.SetColumnSpan(lblError, 2);

        FlowLayoutPanel buttonsPanel = new()
        {
            FlowDirection = FlowDirection.RightToLeft,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Dock = DockStyle.Right,
            Margin = new Padding(0, 6, 0, 0)
        };
        var btnCancel = new Button { Text = "Annulla", AutoSize = true, Padding = new Padding(6, 2, 6, 2), Margin = new Padding(6, 0, 0, 0), DialogResult = DialogResult.Cancel };
        btnConnect = new Button { Text = "Connetti", AutoSize = true, Padding = new Padding(6, 2, 6, 2), Margin = new Padding(0) };
        buttonsPanel.Controls.Add(btnCancel);
        buttonsPanel.Controls.Add(btnConnect);
        root.Controls.Add(buttonsPanel, 0, 4);
        root.SetColumnSpan(buttonsPanel, 2);

        // Premi Annulla chiude l'applicazione (funziona anche se il form è startup principale)
        btnCancel.Click += (_, _) => Close();
        btnConnect.Click += BtnConnect_Click;

        Controls.Add(root);
        AcceptButton = btnConnect;
        CancelButton = btnCancel;

        UpdateAuthVisibility();

        // La scalatura DPI automatica di WinForms (AutoScaleMode.Dpi) viene applicata
        // solo alla creazione dell'handle (primo Show), non qui nel costruttore: se
        // disattivassimo AutoSize adesso, la finestra resterebbe bloccata alla
        // dimensione calcolata PRIMA dello scaling, mentre i controlli interni
        // (es. Width=260 di txtServer) verrebbero comunque ingranditi dopo — risultato
        // contenuto troncato. Rimandiamo il blocco a dopo il Load, quando lo scaling
        // è già avvenuto.
        Load += LockSizeAfterAutoScale;
    }

    private void LockSizeAfterAutoScale(object? sender, EventArgs e)
    {
        // AutoSize serve solo per calcolare la dimensione corretta in base al
        // contenuto reale (a qualunque DPI): se restasse attivo impedirebbe
        // all'utente di ridimensionare la finestra, perché ogni resize manuale
        // verrebbe subito annullato dal layout automatico che la riporta alla
        // dimensione calcolata. Lo disattiviamo ora che lo scaling è completo,
        // lasciando il bordo Sizable libero di funzionare.
        PerformLayout();
        Size contentSize = Size;
        AutoSize = false;
        Size = contentSize;
        MinimumSize = new Size(Math.Max(MinimumSize.Width, contentSize.Width), contentSize.Height);
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
            try
            {
                AuditSettings saved = SettingsService.LoadAuditSettings();
                saved.LastServer = config.Server;
                saved.LastDatabase = config.Database;
                saved.LastUser = config.IntegratedSecurity ? "" : config.User;
                SettingsService.SaveAuditSettings(saved);
            }
            catch { }

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
