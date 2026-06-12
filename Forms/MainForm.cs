using PBScriptNew.Models;
using PBScriptNew.Services;
using System.Data;
using System.Text;
using System.Text.RegularExpressions;

namespace PBScriptNew.Forms;

public class MainForm : Form
{
    // ─── Services ────────────────────────────────────────────────────────────
    private readonly SqlService _sql;
    private readonly DatabaseConfig _config;
    private readonly DatabaseExplorerService _dbExplorer;

    // ─── State ───────────────────────────────────────────────────────────────
    private List<Dictionary<string, object?>> _queryResult = new();
    private List<string> _keyColumns = new();
    private HashSet<string> _identityColumns = new(StringComparer.OrdinalIgnoreCase);
    private List<TableInfo> _allTables = new();
    private TableInfo? _selectedTable = null;
    private string _auditFilter = string.Empty;
    private string _auditExclude = string.Empty;
    private bool _defaultConditionalUpdate = false;
    private string _lastAutoScript = string.Empty;

    // Guard: prevents re-entrant async calls
    private bool _busy = false;

    // ─── Controls ────────────────────────────────────────────────────────────
    private ToolStrip toolStrip = null!;
    private ComboBox cmbDatabases = null!;
    private TextBox txtTableSearch = null!;
    private ListBox lstTables = null!;
    private RichTextBox rtbSqlScript = null!;
    private DataGridView dgvResults = null!;
    private RichTextBox rtbGeneratedScript = null!;
    private ListBox lstMessages = null!;
    private TabControl tabResults = null!;
    private TabPage tabGrid = null!;
    private TabPage tabText = null!;
    private TabPage tabMessages = null!;
    private StatusStrip statusBar = null!;
    private ToolStripStatusLabel lblStatusServer = null!;
    private ToolStripStatusLabel lblStatusDb = null!;
    private ToolStripStatusLabel lblStatusUser = null!;
    private ToolStripStatusLabel lblStatusLoading = null!;
    private SplitContainer splitMain = null!;
    private SplitContainer splitRight = null!;
    private Panel pnlSearch = null!;
    private TextBox txtSearch = null!;
    private Label lblSearchCount = null!;
    private Panel pnlSearchGen = null!;
    private TextBox txtSearchGen = null!;
    private Label lblSearchCountGen = null!;

    // ─── Syntax highlight state ───────────────────────────────────────────────
    private bool _isHighlighting = false;
    private System.Windows.Forms.Timer _syntaxTimer = null!;

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hWnd, int msg, bool wParam, int lParam);
    private const int WM_SETREDRAW = 0x000B;

    private static readonly string _sqlKeywordPattern =
        @"\b(SELECT|FROM|WHERE|INSERT|UPDATE|DELETE|CREATE|DROP|ALTER|TABLE|INTO|VALUES|SET" +
        @"|JOIN|ON|AND|OR|NOT|IN|LIKE|ORDER|BY|GROUP|HAVING|AS|DISTINCT|TOP|INNER|LEFT|RIGHT" +
        @"|OUTER|FULL|CROSS|UNION|ALL|EXISTS|NULL|IS|BETWEEN|WITH|BEGIN|END|GO|USE|IF|ELSE" +
        @"|DECLARE|EXEC|PROCEDURE|TRIGGER|ENABLE|DISABLE|PRIMARY|KEY|FOREIGN|REFERENCES" +
        @"|CONSTRAINT|INDEX|VIEW|IDENTITY|DEFAULT|COUNT|SUM|MAX|MIN|AVG|CAST|CONVERT" +
        @"|ISNULL|COALESCE|CASE|WHEN|THEN|RETURN|NOLOCK|ROLLBACK|COMMIT|TRANSACTION" +
        @"|CHAR|VARCHAR|NVARCHAR|INT|BIGINT|SMALLINT|DATETIME|DATE|BIT|DECIMAL|FLOAT" +
        @"|MONEY|UNIQUEIDENTIFIER|VARBINARY|TEXT|NTEXT)\b";

    // ─── Constructor ─────────────────────────────────────────────────────────
    public MainForm(SqlService sqlService, DatabaseConfig config)
    {
        _sql = sqlService;
        _config = config;
        _dbExplorer = new DatabaseExplorerService(sqlService);
        // Carica le impostazioni audit salvate (incluso stato UI)
        var auditSettings = SettingsService.LoadAuditSettings();
        _auditFilter = auditSettings.AuditFilter;
        _auditExclude = auditSettings.AuditExclude;
        // Restore saved table search and default conditional update flag
        txtTableSearch = new TextBox(); // temporary until InitializeComponent sets the real one
        txtTableSearch.Text = auditSettings.TableSearch;
        // Store default flag to use when showing ColumnSelectorDialog
        _defaultConditionalUpdate = auditSettings.DefaultConditionalUpdate;
        // Restore last server/db if provided in config
        if (!string.IsNullOrEmpty(auditSettings.LastServer)) _config.Server = auditSettings.LastServer;
        if (!string.IsNullOrEmpty(auditSettings.LastDatabase)) _config.Database = auditSettings.LastDatabase;
        if (!string.IsNullOrEmpty(auditSettings.LastUser)) _config.User = auditSettings.LastUser;

        InitializeComponent();

        // Both splitter setup and initial data load happen after the form is fully visible
        Shown += OnFormShown;
    }

    private async void OnFormShown(object? sender, EventArgs e)
    {
        // Start data load immediately
        await GuardAsync(LoadInitialDataAsync);

        // Force layout to complete before setting splitters
        Application.DoEvents();

        // Imposta MinSize e SplitterDistance ora che il form ha dimensioni reali
        try
        {
            // Per lo splitter orizzontale
            splitMain.Panel1MinSize = 150;
            splitMain.Panel2MinSize = 400;

            int targetDistance = 250;
            int maxAllowed = splitMain.ClientSize.Width - splitMain.Panel2MinSize - splitMain.SplitterWidth;

            if (maxAllowed > splitMain.Panel1MinSize && targetDistance <= maxAllowed)
                splitMain.SplitterDistance = targetDistance;
            else if (maxAllowed > splitMain.Panel1MinSize)
                splitMain.SplitterDistance = splitMain.Panel1MinSize;

            splitMain.IsSplitterFixed = false;  // Sblocca lo splitter per permettere all'utente di spostarlo
        }
        catch { /* ignore splitter errors */ }

        try
        {
            // Per lo splitter verticale
            splitRight.Panel1MinSize = 60;
            splitRight.Panel2MinSize = 120;

            int targetDistance = 180;
            int maxAllowed = splitRight.ClientSize.Height - splitRight.Panel2MinSize - splitRight.SplitterWidth;

            if (maxAllowed > splitRight.Panel1MinSize && targetDistance <= maxAllowed)
                splitRight.SplitterDistance = targetDistance;
            else if (maxAllowed > splitRight.Panel1MinSize)
                splitRight.SplitterDistance = splitRight.Panel1MinSize;

            splitRight.IsSplitterFixed = false;  // Sblocca lo splitter per permettere all'utente di spostarlo
        }
        catch { /* ignore splitter errors */ }

        // Restore UI persisted settings
        try
        {
            var s = Services.SettingsService.LoadAuditSettings();
            if (!string.IsNullOrEmpty(s.TableSearch))
                txtTableSearch.Text = s.TableSearch;
            _defaultConditionalUpdate = s.DefaultConditionalUpdate;
        }
        catch { }
    }

    // ─── UI Build ────────────────────────────────────────────────────────────

    private void InitializeComponent()
    {
        Text = $"PBScript – SQL Explorer – {_config.Server} – {_config.Database}";
        Size = new Size(1200, 800);
        MinimumSize = new Size(800, 600);
        StartPosition = FormStartPosition.CenterScreen;
        Font = new Font("Segoe UI", 9f);

        // Build main layout first so Docking of ToolStrip/StatusStrip applied afterwards
        BuildMainLayout();
        BuildToolStrip();
        BuildStatusBar();
    }

    private void BuildToolStrip()
    {
        toolStrip = new ToolStrip { Dock = DockStyle.Top, GripStyle = ToolStripGripStyle.Hidden };

        var btnEsegui = new ToolStripButton("▶ Esegui") { DisplayStyle = ToolStripItemDisplayStyle.Text };
        btnEsegui.Click += async (_, _) => await GuardAsync(ExecuteQueryAsync);
        toolStrip.Items.Add(btnEsegui);
        toolStrip.Items.Add(new ToolStripSeparator());

        var btnScript = new ToolStripDropDownButton("📝 Script") { DisplayStyle = ToolStripItemDisplayStyle.Text };
        var miIns = new ToolStripMenuItem("Crea script INSERT"); miIns.Click += async (_, _) => await GuardAsync(() => GenerateScriptAsync("INSERT"));
        var miUpd = new ToolStripMenuItem("Crea script UPDATE"); miUpd.Click += async (_, _) => await GuardAsync(() => GenerateScriptAsync("UPDATE"));
        var miDel = new ToolStripMenuItem("Crea script DELETE"); miDel.Click += async (_, _) => await GuardAsync(() => GenerateScriptAsync("DELETE"));
        btnScript.DropDownItems.AddRange(new ToolStripItem[] { miIns, miUpd, miDel });
        toolStrip.Items.Add(btnScript);
        toolStrip.Items.Add(new ToolStripSeparator());

        var btnAudit = new ToolStripDropDownButton("📋 Audit") { DisplayStyle = ToolStripItemDisplayStyle.Text };
        var miAInit = new ToolStripMenuItem("Inizializza/Resetta db Audit_UPD (SETUP/INSTALLAZIONE)"); miAInit.Click += async (_, _) => await GuardAsync(AuditInitializeAsync);
        var miARem = new ToolStripMenuItem("Elimina sistema di Audit (DISINSTALLAZIONE)"); miARem.Click += (_, _) => AuditRemove();
        var miAOn = new ToolStripMenuItem("Attiva/Riattiva trigger Audit (INIZIO ATTIVITÀ)"); miAOn.Click += (_, _) => AuditActivate();
        var miAOff = new ToolStripMenuItem("Disattiva trigger Audit (PAUSA ATTIVITÀ)"); miAOff.Click += (_, _) => AuditDeactivate();
        var miAGen = new ToolStripMenuItem("Genera Script Audit da eSYS/eSYS_UPD (RILASCIO)"); miAGen.Click += async (_, _) => await GuardAsync(AuditGenerateScriptAsync);
        btnAudit.DropDownItems.AddRange(new ToolStripItem[] { miAInit, miARem, miAOn, miAOff, miAGen });
        toolStrip.Items.Add(btnAudit);
        toolStrip.Items.Add(new ToolStripSeparator());

        var btnLogout = new ToolStripButton("⬅ Logout") { DisplayStyle = ToolStripItemDisplayStyle.Text };
        btnLogout.Click += (_, _) => { new LoginForm().Show(); Close(); };
        toolStrip.Items.Add(btnLogout);

        Controls.Add(toolStrip);
    }

    private void BuildStatusBar()
    {
        statusBar = new StatusStrip { Dock = DockStyle.Bottom };
        lblStatusServer = new ToolStripStatusLabel($"Server: {_config.Server}");
        lblStatusDb = new ToolStripStatusLabel($"Database: {_config.Database}");
        lblStatusUser = new ToolStripStatusLabel($"User: {(_config.IntegratedSecurity ? "Windows Auth" : _config.User)}");
        lblStatusLoading = new ToolStripStatusLabel("") { Spring = true, TextAlign = ContentAlignment.MiddleRight };
        statusBar.Items.AddRange(new ToolStripItem[] { lblStatusServer, new ToolStripSeparator(), lblStatusDb, new ToolStripSeparator(), lblStatusUser, lblStatusLoading });
        Controls.Add(statusBar);
    }

    private void BuildMainLayout()
    {
        SuspendLayout();

        // ── Left panel ───────────────────────────────────────────────────────
        var pnlLeft = new Panel { Dock = DockStyle.Fill };
        var lblDb = new Label { Text = "Database:", Dock = DockStyle.Top, Height = 20, Font = new Font("Segoe UI", 7.5f, FontStyle.Bold) };
        cmbDatabases = new ComboBox { Dock = DockStyle.Top, DropDownStyle = ComboBoxStyle.DropDownList, Font = new Font("Segoe UI", 8f) };
        // NOTE: SelectedIndexChanged is wired AFTER population to avoid spurious calls
        var lblSearch = new Label { Text = "Cerca tabella:", Dock = DockStyle.Top, Height = 20, Font = new Font("Segoe UI", 7.5f, FontStyle.Bold) };
        txtTableSearch = new TextBox { Dock = DockStyle.Top, PlaceholderText = "Cerca tabella...", Font = new Font("Segoe UI", 8f) };
        txtTableSearch.TextChanged += (_, _) => FilterTableList();
        var lblTables = new Label { Text = "Tabelle:", Dock = DockStyle.Top, Height = 20, Font = new Font("Segoe UI", 7.5f, FontStyle.Bold) };
        lstTables = new ListBox { Dock = DockStyle.Fill, Font = new Font("Courier New", 7.5f) };
        lstTables.DoubleClick += (_, _) => LoadTableStructure();
        lstTables.SelectedIndexChanged += (_, _) => LoadTableStructure();

        pnlLeft.Controls.Add(lstTables);
        pnlLeft.Controls.Add(lblTables);
        pnlLeft.Controls.Add(txtTableSearch);
        pnlLeft.Controls.Add(lblSearch);
        pnlLeft.Controls.Add(cmbDatabases);
        pnlLeft.Controls.Add(lblDb);

        // ── Outer split: left / right ─────────────────────────────────────
        splitMain = new SplitContainer
        {
            Dock = DockStyle.Fill,
            Orientation = Orientation.Vertical,
            FixedPanel = FixedPanel.Panel1,
            SplitterWidth = 4,
            IsSplitterFixed = true  // Blocca temporaneamente per evitare validazione prematura
        };
        // MinSize impostato in OnFormShown per evitare validazione prematura
        splitMain.Panel1.Controls.Add(pnlLeft);

        // ── Inner split: SQL editor (top) / tabs (bottom) ─────────────────
        splitRight = new SplitContainer
        {
            Dock = DockStyle.Fill,
            Orientation = Orientation.Horizontal,
            SplitterWidth = 4,
            IsSplitterFixed = true  // Blocca temporaneamente
        };
        // MinSize impostato in OnFormShown per evitare validazione prematura

        var pnlSql = new Panel { Dock = DockStyle.Fill };
        var lblSql = new Label { Text = "Script SQL:", Dock = DockStyle.Top, Height = 20, Font = new Font("Segoe UI", 7.5f, FontStyle.Bold) };

        // ── Barra di ricerca (Ctrl+F) ──────────────────────────────────────
        pnlSearch = new Panel { Dock = DockStyle.Top, Height = 26, Visible = false, BackColor = SystemColors.Info, Padding = new Padding(2) };
        txtSearch = new TextBox { Dock = DockStyle.Fill, Font = new Font("Segoe UI", 8.5f) };
        lblSearchCount = new Label { Dock = DockStyle.Right, Width = 70, TextAlign = ContentAlignment.MiddleLeft, Font = new Font("Segoe UI", 7.5f) };
        Button btnFindNext = new Button { Dock = DockStyle.Right, Width = 26, Text = "▼", FlatStyle = FlatStyle.Flat, Font = new Font("Segoe UI", 7f) };
        Button btnFindPrev = new Button { Dock = DockStyle.Right, Width = 26, Text = "▲", FlatStyle = FlatStyle.Flat, Font = new Font("Segoe UI", 7f) };
        Button btnCloseSearch = new Button { Dock = DockStyle.Right, Width = 26, Text = "✕", FlatStyle = FlatStyle.Flat, Font = new Font("Segoe UI", 7f) };
        btnFindNext.Click += (_, _) => FindInRtb(rtbSqlScript, txtSearch, lblSearchCount, forward: true);
        btnFindPrev.Click += (_, _) => FindInRtb(rtbSqlScript, txtSearch, lblSearchCount, forward: false);
        btnCloseSearch.Click += (_, _) => CloseSearchRtb(pnlSearch, lblSearchCount, rtbSqlScript);
        txtSearch.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Enter) { e.Handled = true; FindInRtb(rtbSqlScript, txtSearch, lblSearchCount, forward: true); }
            else if (e.KeyCode == Keys.Escape) { e.Handled = true; CloseSearchRtb(pnlSearch, lblSearchCount, rtbSqlScript); }
            else if (e.Shift && e.KeyCode == Keys.F3) { e.Handled = true; FindInRtb(rtbSqlScript, txtSearch, lblSearchCount, forward: false); }
        };
        txtSearch.TextChanged += (_, _) => FindInRtb(rtbSqlScript, txtSearch, lblSearchCount, forward: true, resetPos: true);
        // ordine di aggiunta: destra per prima (z-order inverso per Dock.Right)
        pnlSearch.Controls.Add(txtSearch);
        pnlSearch.Controls.Add(lblSearchCount);
        pnlSearch.Controls.Add(btnCloseSearch);
        pnlSearch.Controls.Add(btnFindNext);
        pnlSearch.Controls.Add(btnFindPrev);

        // ── Editor SQL ─────────────────────────────────────────────────────
        rtbSqlScript = new RichTextBox
        {
            Dock = DockStyle.Fill,
            Font = new Font("Courier New", 8.5f),
            WordWrap = false,
            ScrollBars = RichTextBoxScrollBars.Both
        };

        // Syntax highlight con debounce 350ms
        _syntaxTimer = new System.Windows.Forms.Timer { Interval = 350 };
        _syntaxTimer.Tick += (_, _) => { _syntaxTimer.Stop(); ApplySyntaxHighlight(rtbSqlScript); };
        rtbSqlScript.TextChanged += (_, _) => { if (!_isHighlighting) { _syntaxTimer.Stop(); _syntaxTimer.Start(); } };

        rtbSqlScript.KeyDown += (_, e) =>
        {
            if (e.Control && e.KeyCode == Keys.Enter) { e.Handled = true; _ = GuardAsync(ExecuteQueryAsync); }
            else if (e.Control && e.KeyCode == Keys.F) { e.Handled = true; ShowSearchRtb(pnlSearch, txtSearch); }
            else if (e.KeyCode == Keys.F3) { e.Handled = true; FindInRtb(rtbSqlScript, txtSearch, lblSearchCount, forward: !e.Shift); }
            else if (e.KeyCode == Keys.Escape && pnlSearch.Visible) { e.Handled = true; CloseSearchRtb(pnlSearch, lblSearchCount, rtbSqlScript); }
        };

        // Ordine aggiunta: lblSql (Top) → pnlSearch (Top, nascosto) → rtbSqlScript (Fill)
        pnlSql.Controls.Add(lblSql);
        pnlSql.Controls.Add(pnlSearch);
        pnlSql.Controls.Add(rtbSqlScript);
        rtbSqlScript.BringToFront(); // garantisce interattività fin dall'avvio
        splitRight.Panel1.Controls.Add(pnlSql);

        // ── Tab control ───────────────────────────────────────────────────
        tabResults = new TabControl { Dock = DockStyle.Fill };
        tabGrid = new TabPage("Griglia");
        tabText = new TabPage("Testo");
        tabMessages = new TabPage("Messaggi");

        dgvResults = new DataGridView
        {
            Dock = DockStyle.Fill,
            ReadOnly = true,
            AllowUserToAddRows = false,
            SelectionMode = DataGridViewSelectionMode.FullRowSelect,
            MultiSelect = true,
            AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.None,
            Font = new Font("Segoe UI", 7.5f),
            RowHeadersWidth = 30,
            ScrollBars = ScrollBars.Both,
            AllowUserToResizeColumns = true,
            ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.AutoSize,
            AutoGenerateColumns = true
        };

        // ── Tab Testo: pannello con barra ricerca + editor generato ──────────
        Panel pnlGen = new Panel { Dock = DockStyle.Fill };

        pnlSearchGen = new Panel { Dock = DockStyle.Top, Height = 26, Visible = false, BackColor = SystemColors.Info, Padding = new Padding(2) };
        txtSearchGen = new TextBox { Dock = DockStyle.Fill, Font = new Font("Segoe UI", 8.5f) };
        lblSearchCountGen = new Label { Dock = DockStyle.Right, Width = 70, TextAlign = ContentAlignment.MiddleLeft, Font = new Font("Segoe UI", 7.5f) };
        Button btnGenFindNext = new Button { Dock = DockStyle.Right, Width = 26, Text = "▼", FlatStyle = FlatStyle.Flat, Font = new Font("Segoe UI", 7f) };
        Button btnGenFindPrev = new Button { Dock = DockStyle.Right, Width = 26, Text = "▲", FlatStyle = FlatStyle.Flat, Font = new Font("Segoe UI", 7f) };
        Button btnGenClose = new Button { Dock = DockStyle.Right, Width = 26, Text = "✕", FlatStyle = FlatStyle.Flat, Font = new Font("Segoe UI", 7f) };
        btnGenFindNext.Click += (_, _) => FindInRtb(rtbGeneratedScript, txtSearchGen, lblSearchCountGen, forward: true);
        btnGenFindPrev.Click += (_, _) => FindInRtb(rtbGeneratedScript, txtSearchGen, lblSearchCountGen, forward: false);
        btnGenClose.Click += (_, _) => CloseSearchRtb(pnlSearchGen, lblSearchCountGen, rtbGeneratedScript);
        txtSearchGen.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Enter) { e.Handled = true; FindInRtb(rtbGeneratedScript, txtSearchGen, lblSearchCountGen, forward: true); }
            else if (e.KeyCode == Keys.Escape) { e.Handled = true; CloseSearchRtb(pnlSearchGen, lblSearchCountGen, rtbGeneratedScript); }
            else if (e.Shift && e.KeyCode == Keys.F3) { e.Handled = true; FindInRtb(rtbGeneratedScript, txtSearchGen, lblSearchCountGen, forward: false); }
        };
        txtSearchGen.TextChanged += (_, _) => FindInRtb(rtbGeneratedScript, txtSearchGen, lblSearchCountGen, forward: true, resetPos: true);
        pnlSearchGen.Controls.Add(txtSearchGen);
        pnlSearchGen.Controls.Add(lblSearchCountGen);
        pnlSearchGen.Controls.Add(btnGenClose);
        pnlSearchGen.Controls.Add(btnGenFindNext);
        pnlSearchGen.Controls.Add(btnGenFindPrev);

        rtbGeneratedScript = new RichTextBox
        {
            Dock = DockStyle.Fill,
            Font = new Font("Courier New", 8.5f),
            WordWrap = false,
            ScrollBars = RichTextBoxScrollBars.Both,
            AcceptsTab = true,
            DetectUrls = false
        };

        rtbGeneratedScript.KeyDown += (_, e) =>
        {
            if (e.Control && e.KeyCode == Keys.F) { e.Handled = true; ShowSearchRtb(pnlSearchGen, txtSearchGen); }
            else if (e.KeyCode == Keys.F3) { e.Handled = true; FindInRtb(rtbGeneratedScript, txtSearchGen, lblSearchCountGen, forward: !e.Shift); }
            else if (e.KeyCode == Keys.Escape && pnlSearchGen.Visible) { e.Handled = true; CloseSearchRtb(pnlSearchGen, lblSearchCountGen, rtbGeneratedScript); }
        };

        pnlGen.Controls.Add(pnlSearchGen);
        pnlGen.Controls.Add(rtbGeneratedScript);
        rtbGeneratedScript.BringToFront();

        lstMessages = new ListBox
        {
            Dock = DockStyle.Fill,
            Font = new Font("Courier New", 7.5f),
            HorizontalScrollbar = true
        };

        tabGrid.Controls.Add(dgvResults);
        tabText.Controls.Add(pnlGen);
        tabMessages.Controls.Add(lstMessages);
        tabResults.TabPages.AddRange(new[] { tabGrid, tabText, tabMessages });
        splitRight.Panel2.Controls.Add(tabResults);

        splitMain.Panel2.Controls.Add(splitRight);
        Controls.Add(splitMain);

        // Rimuove manipolazioni manuali della z-order: lascia che il sistema di Dock gestisca il layout

        ResumeLayout(false);
    }

    // ─── Guard helper ────────────────────────────────────────────────────────
    /// <summary>Runs async operation; silently skips if already busy.</summary>
    private async Task GuardAsync(Func<Task> action)
    {
        if (_busy) return;
        _busy = true;
        try { await action(); }
        catch (Exception ex) { AddMessage($"❌ Errore: {ex.Message}"); tabResults.SelectedTab = tabMessages; }
        finally { _busy = false; }
    }

    // ─── Data loading ────────────────────────────────────────────────────────

    private async Task LoadInitialDataAsync()
    {
        SetLoading(true);
        try
        {
            var info = await _sql.GetServerInfoAsync();
            if (info is not null)
                Text = $"PBScript – SQL Explorer – {info.ServerName} – {_config.Database}";

            var dbResult = await _dbExplorer.GetDatabasesAsync();
            if (dbResult.Success && dbResult.Data is { Count: > 0 })
            {
                // Populate WITHOUT triggering SelectedIndexChanged
                cmbDatabases.Items.Clear();
                foreach (var db in dbResult.Data)
                    cmbDatabases.Items.Add(db.Name);

                // Set initial selection BEFORE wiring the event to prevent spurious OnDatabaseChanged
                // Prefer LastDatabase from settings (already applied to _config if present)
                int idx = cmbDatabases.Items.IndexOf(_config.Database);
                cmbDatabases.SelectedIndex = idx >= 0 ? idx : 0;

                // Wire the event only now – from here on it fires only on real user interaction
                cmbDatabases.SelectedIndexChanged += OnDatabaseChanged;

                // Load tables for the initially selected db explicitly
                await LoadTablesAsync(cmbDatabases.SelectedItem as string ?? _config.Database);
            }
            else
            {
                cmbDatabases.SelectedIndexChanged += OnDatabaseChanged;
            }
        }
        finally { SetLoading(false); }
    }

    private async void OnDatabaseChanged(object? sender, EventArgs e)
    {
        if (cmbDatabases.SelectedItem is string dbName)
            await GuardAsync(() => LoadTablesAsync(dbName));
    }

    private async Task LoadTablesAsync(string dbName)
    {
        lblStatusDb.Text = $"Database: {dbName}";
        SetLoading(true);
        try
        {
            _allTables = new();
            var result = await _dbExplorer.GetTablesAsync(dbName);
            if (result.Success && result.Data is not null)
                _allTables = result.Data;
            FilterTableList();
        }
        finally { SetLoading(false); }
    }

    private void FilterTableList()
    {
        var filter = txtTableSearch.Text.ToLowerInvariant();
        lstTables.BeginUpdate();
        lstTables.Items.Clear();
        foreach (var t in _allTables)
            if (t.TableName.ToLowerInvariant().Contains(filter))
                lstTables.Items.Add($"[{t.TableSchema}].[{t.TableName}]");
        lstTables.EndUpdate();
    }

    /// <summary>
    /// Aggiorna l'editor SQL con la SELECT della tabella selezionata nel ListBox.
    /// Carica chiavi e colonne identity per la generazione degli script.
    /// </summary>
    /// <summary>
    /// Aggiorna l'editor SQL con la SELECT della tabella selezionata.
    /// Sincrono: nessun I/O. Chiavi e identity vengono caricate in GenerateScriptAsync quando servono.
    /// </summary>
    private void LoadTableStructure()
    {
        if (lstTables.SelectedItem is not string item) return;
        Regex rx = new Regex(@"\[([^\]]+)\]\.\[([^\]]+)\]");
        Match m = rx.Match(item);
        if (!m.Success) return;
        string schema = m.Groups[1].Value;
        string tbl = m.Groups[2].Value;

        _selectedTable = new TableInfo { TableSchema = schema, TableName = tbl, TableType = "BASE TABLE" };
        _keyColumns = new List<string>();
        _identityColumns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        string scriptText = $"SELECT * FROM [{schema}].[{tbl}]";
        _lastAutoScript = scriptText;

        rtbSqlScript.Text = scriptText;
        rtbSqlScript.SelectionStart = 0;
        rtbSqlScript.ScrollToCaret();
        rtbSqlScript.BringToFront();
        rtbSqlScript.Refresh();
    }

    // ─── Query execution ─────────────────────────────────────────────────────

    private async Task ExecuteQueryAsync()
    {
        var script = rtbSqlScript.Text.Trim();
        if (string.IsNullOrEmpty(script))
        {
            AddMessage("Errore: nessuno script da eseguire");
            return;
        }

        SetLoading(true);
        var goCount = Regex.Matches(script, @"\bGO\b", RegexOptions.IgnoreCase).Count;
        var lineCount = script.Split('\n').Length;
        AddMessage($"📋 Inizio esecuzione ({lineCount} righe, {goCount} batch GO)");

        // Detect special operations (triggers) and add more descriptive messages for execution
        string? specialOp = null;
        try
        {
            var s = script.ToUpperInvariant();
            if (Regex.IsMatch(s, @"\bDISABLE\s+TRIGGER\b")) specialOp = "Disattivazione trigger";
            else if (Regex.IsMatch(s, @"\bENABLE\s+TRIGGER\b")) specialOp = "Attivazione trigger";
            else if (Regex.IsMatch(s, @"\bCREATE\s+TRIGGER\b")) specialOp = "Creazione trigger";
            else if (Regex.IsMatch(s, @"\bDROP\s+TRIGGER\b")) specialOp = "Eliminazione trigger";
            else if (Regex.IsMatch(s, @"\bALTER\s+TRIGGER\b")) specialOp = "Modifica trigger";
        }
        catch { specialOp = null; }

        if (!string.IsNullOrEmpty(specialOp))
            AddMessage($"▶ Inizio esecuzione operazione: {specialOp}");

        try
        {
            var result = await _sql.ExecuteQueryAsync(script);
            if (result.Success && result.Data is { Count: > 0 })
            {
                _queryResult = result.Data;
                PopulateGrid(_queryResult);
                AddMessage($"✅ {result.Data.Count} righe restituite");
                if (!string.IsNullOrEmpty(specialOp)) AddMessage($"▶ Fine esecuzione operazione: {specialOp}");
                tabResults.SelectedTab = tabGrid;
            }
            else if (result.Success)
            {
                _queryResult = new();
                dgvResults.DataSource = null;
                AddMessage("✅ Script eseguito con successo (nessun risultato)");
                if (!string.IsNullOrEmpty(specialOp)) AddMessage($"▶ Fine esecuzione operazione: {specialOp}");
                tabResults.SelectedTab = tabGrid;
            }
            else
            {
                AddMessage($"❌ Errore: {result.Error}");
                tabResults.SelectedTab = tabMessages;
            }
        }
        finally { SetLoading(false); }
    }

    private void PopulateGrid(List<Dictionary<string, object?>> rows)
    {
        if (rows.Count == 0) { dgvResults.DataSource = null; return; }

        dgvResults.SuspendLayout();

        var dt = new DataTable();
        foreach (var key in rows[0].Keys)
            dt.Columns.Add(key, typeof(string));
        foreach (var row in rows)
        {
            var dr = dt.NewRow();
            foreach (var kv in row)
                dr[kv.Key] = kv.Value is null ? (object)DBNull.Value : Convert.ToString(kv.Value) ?? (object)DBNull.Value;
            dt.Rows.Add(dr);
        }
        dgvResults.DataSource = dt;

        // Imposta larghezza colonne
        int totalWidth = 0;
        foreach (DataGridViewColumn col in dgvResults.Columns)
        {
            col.Width = Math.Min(200, Math.Max(60, col.HeaderText.Length * 9));
            totalWidth += col.Width;
        }

        // Forza il refresh delle scrollbars
        dgvResults.ResumeLayout();
        dgvResults.PerformLayout();

        // Se la larghezza totale supera la larghezza visibile, forza il refresh
        if (totalWidth > dgvResults.ClientSize.Width)
        {
            dgvResults.Invalidate();
            Application.DoEvents();
        }
    }

    // ─── Script generation ───────────────────────────────────────────────────

    private async Task GenerateScriptAsync(string scriptType)
    {
        if (_queryResult.Count == 0)
        {
            MessageBox.Show("Esegui prima una SELECT per ottenere dati.", "Attenzione", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        var selectedIndices = GetSelectedRowIndices();
        if (selectedIndices.Count == 0)
        {
            MessageBox.Show("Seleziona almeno una riga nella griglia.", "Attenzione", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        var db = cmbDatabases.SelectedItem as string ?? _config.Database;

        SetLoading(true);
        try
        {
            // L'SQL nel box ha sempre la precedenza sulla tabella selezionata nella treeview.
            // Se il FROM dell'SQL corrente indica una tabella diversa da _selectedTable, aggiorna _selectedTable.
            Match sqlMatch = Regex.Match(rtbSqlScript.Text, @"FROM\s+(?:\[?(\w+)\]?\.)?\[?(\w+)\]?", RegexOptions.IgnoreCase);
            if (sqlMatch.Success)
            {
                string sch = sqlMatch.Groups[1].Success ? sqlMatch.Groups[1].Value : "dbo";
                string tbl = sqlMatch.Groups[2].Value;
                bool differs = _selectedTable is null
                    || !string.Equals(_selectedTable.TableName, tbl, StringComparison.OrdinalIgnoreCase)
                    || !string.Equals(_selectedTable.TableSchema, sch, StringComparison.OrdinalIgnoreCase);
                if (differs)
                    _selectedTable = new TableInfo { TableSchema = sch, TableName = tbl, TableType = "BASE TABLE" };
            }

            if (_selectedTable is not null)
            {
                _keyColumns = await _dbExplorer.GetTableKeyColumnsAsync(db, _selectedTable.TableSchema, _selectedTable.TableName);
                _identityColumns = new HashSet<string>(await _dbExplorer.GetIdentityColumnsAsync(db, _selectedTable.TableSchema, _selectedTable.TableName), StringComparer.OrdinalIgnoreCase);
            }
        }
        finally { SetLoading(false); }

        if (_selectedTable is null)
        {
            MessageBox.Show("Impossibile identificare la tabella. Selezionane una dalla lista.", "Attenzione", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        var allCols = _queryResult[0].Keys.ToList();

        // Escludi automaticamente i campi di tipo VersionTs/rowversion/timestamp
        var versionNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "VersionTs", "versionts", "rowversion", "timestamp" };
        var versionCols = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var col in allCols)
        {
            // Se il nome suggerisce una colonna version oppure il valore è un byte[] (rowversion)
            var val = _queryResult[0].GetValueOrDefault(col);
            if (versionNames.Contains(col) || val is byte[])
                versionCols.Add(col);
        }

        var filteredCols = allCols.Except(versionCols, StringComparer.OrdinalIgnoreCase).ToList();
        var filteredKeyCols = _keyColumns.Except(versionCols, StringComparer.OrdinalIgnoreCase).ToList();


        var selRows = selectedIndices.Select(i => _queryResult[i]).ToList();

        string script;
        if (scriptType == "DELETE")
        {
            // For DELETE do not show the column selector: use detected key columns. If none, fallback to all columns.
            var keyColsForDelete = filteredKeyCols.Count > 0 ? filteredKeyCols : filteredCols;
            if (keyColsForDelete.Count == 0)
            {
                MessageBox.Show("Impossibile determinare colonne chiave per DELETE.", "Attenzione", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            script = BuildDeleteScript(_selectedTable, keyColsForDelete, selRows);
        }
        else
        {
            using var dlg = new ColumnSelectorDialog(filteredCols, filteredKeyCols, $"{_selectedTable.TableSchema}.{_selectedTable.TableName}", scriptType, _defaultConditionalUpdate);
            if (dlg.ShowDialog(this) != DialogResult.OK) return;

            // Remember user's choice for next time
            _defaultConditionalUpdate = dlg.ConditionalUpdate;

            if (scriptType == "INSERT")
                script = BuildInsertScript(_selectedTable, dlg.SelectedKeyColumns, dlg.SelectedColumns, selRows, _identityColumns);
            else // UPDATE
                script = dlg.ConditionalUpdate
                    ? BuildConditionalUpdateScript(_selectedTable, dlg.SelectedKeyColumns, dlg.SelectedColumns, selRows)
                    : BuildUpdateScript(_selectedTable, dlg.SelectedKeyColumns, dlg.SelectedColumns, selRows);
        }

        AppendToGeneratedScript(script);
        //  EnsureHorizontalScrollBar(rtbGeneratedScript);
        tabResults.SelectedTab = tabText;
        AddMessage($"Creazione script {scriptType} terminata ({selRows.Count} comandi)");
    }

    // Appends text to the generated script pane (thread-safe)
    private void AppendToGeneratedScript(string text)
    {
        if (rtbGeneratedScript is null) return;
        Action apply = () =>
        {
            if (!string.IsNullOrEmpty(rtbGeneratedScript.Text))
                rtbGeneratedScript.AppendText(Environment.NewLine + text);
            else
                rtbGeneratedScript.AppendText(text);
            rtbGeneratedScript.SelectionStart = rtbGeneratedScript.TextLength;
            rtbGeneratedScript.ScrollToCaret();
            rtbGeneratedScript.Refresh();
        };

        if (rtbGeneratedScript.InvokeRequired) rtbGeneratedScript.Invoke(apply);
        else apply();
    }

    private List<int> GetSelectedRowIndices()
    {
        var indices = new List<int>();
        foreach (DataGridViewRow row in dgvResults.SelectedRows)
            indices.Add(row.Index);
        indices.Sort();
        return indices;
    }

    // ─── Script builders ─────────────────────────────────────────────────────

    private static string FmtVal(object? val)
    {
        if (val is null || val == DBNull.Value) return "NULL";
        if (val is bool b) return b ? "1" : "0";
        if (val is DateTime dt) return $" {{ts '{dt:yyyy-MM-dd HH:mm:ss.fff}'}}";
        if (val is Guid g) return $"'{g}'";
        if (val is byte[] bytes) return "0x" + Convert.ToHexString(bytes);
        if (val is string s)
        {
            if (s.Length >= 10 && DateTime.TryParse(s, out var dtp))
                return $" {{ts '{dtp:yyyy-MM-dd HH:mm:ss.fff}'}}";
            return $"'{s.Replace("'", "''")}'";
        }
        return Convert.ToString(val, System.Globalization.CultureInfo.InvariantCulture) ?? "NULL";
    }

    private static string FullName(TableInfo t) =>
        t.TableSchema.ToLower() == "dbo"
            ? $"[{t.TableName}]"
            : $"[{t.TableSchema}].[{t.TableName}]";

    private static string WhereClause(List<string> keys, Dictionary<string, object?> row) =>
        string.Join(" AND ", keys.Select(c =>
            row.GetValueOrDefault(c) is null or DBNull
                ? $"{c} IS NULL"
                : $"{c} = {FmtVal(row.GetValueOrDefault(c))}"));

    private static string BuildInsertScript(TableInfo tbl, List<string> keyCols, List<string> valCols, List<Dictionary<string, object?>> rows, HashSet<string>? identityCols = null)
    {
        var sb = new StringBuilder();
        var allCols = keyCols.Concat(valCols).ToList();
        var fn = FullName(tbl);
        var hasIdentity = identityCols is not null && allCols.Any(c => identityCols.Contains(c));
        if (hasIdentity)
        {
            sb.AppendLine($"SET IDENTITY_INSERT {fn} ON");
            sb.AppendLine();
        }
        foreach (var row in rows)
        {
            if (keyCols.Count > 0)
            {
                sb.AppendLine($"IF NOT EXISTS( SELECT 1 FROM {fn} WHERE {WhereClause(keyCols, row)} )");
                sb.AppendLine("BEGIN");
            }
            var tab = keyCols.Count > 0 ? "\t" : "";
            var cols = string.Join(", ", allCols);
            var vals = string.Join(", ", allCols.Select(c => FmtVal(row.GetValueOrDefault(c))));
            sb.AppendLine($"{tab}INSERT INTO {fn} ( {cols} )");
            sb.AppendLine($"{tab}SELECT {vals}");
            if (keyCols.Count > 0) sb.AppendLine("END");
            sb.AppendLine();
        }
        if (hasIdentity)
        {
            sb.AppendLine($"SET IDENTITY_INSERT {fn} OFF");
            sb.AppendLine();
        }
        return sb.ToString();
    }

    private static string BuildUpdateScript(TableInfo tbl, List<string> keyCols, List<string> setCols, List<Dictionary<string, object?>> rows)
    {
        var sb = new StringBuilder();
        var fn = FullName(tbl);
        foreach (var row in rows)
        {
            var set = string.Join(",\n\t", setCols.Select(c => $"{c} = {FmtVal(row.GetValueOrDefault(c))}"));
            sb.AppendLine($"UPDATE {fn}");
            sb.AppendLine($"SET\n\t{set}");
            sb.AppendLine($"WHERE {WhereClause(keyCols, row)}");
            sb.AppendLine();
        }
        return sb.ToString();
    }

    // Builds conditional update similar to Audit: IF NOT EXISTS(check on keys+modified cols) BEGIN UPDATE ... END
    private static string BuildConditionalUpdateScript(TableInfo tbl, List<string> keyCols, List<string> setCols, List<Dictionary<string, object?>> rows)
    {
        var sb = new StringBuilder();
        var fn = FullName(tbl);
        foreach (var row in rows)
        {
            // determine modified cols by comparing original values? here we don't have old values, so use setCols as modified set
            var modifiedCols = setCols;
            if (modifiedCols.Count == 0) continue;

            // build check conditions: keys + modified cols
            var checkConditions = new List<string>();
            foreach (var key in keyCols)
            {
                var val = row.GetValueOrDefault(key);
                if (val is null or DBNull) checkConditions.Add($"{key} IS NULL");
                else checkConditions.Add($"{key} = {FmtVal(val)}");
            }
            foreach (var col in modifiedCols)
            {
                var val = row.GetValueOrDefault(col);
                if (val is null or DBNull) checkConditions.Add($"{col} IS NULL");
                else checkConditions.Add($"{col} = {FmtVal(val)}");
            }

            var set = string.Join(", ", modifiedCols.Select(c => $"{c} = {FmtVal(row.GetValueOrDefault(c))}"));

            sb.AppendLine($"IF NOT EXISTS(SELECT 1 FROM {fn} WHERE {string.Join(" AND ", checkConditions)})");
            sb.AppendLine("BEGIN");
            sb.AppendLine($"  UPDATE {fn}");
            sb.AppendLine($"  SET {set}");
            sb.AppendLine($"  WHERE {WhereClause(keyCols, row)}");
            sb.AppendLine("END");
            sb.AppendLine();
        }
        return sb.ToString();
    }

    private static string BuildDeleteScript(TableInfo tbl, List<string> keyCols, List<Dictionary<string, object?>> rows)
    {
        var sb = new StringBuilder();
        var fn = FullName(tbl);
        foreach (var row in rows)
        {
            if (keyCols != null && keyCols.Count > 0)
            {
                sb.AppendLine($"IF EXISTS(SELECT 1 FROM {fn} WHERE {WhereClause(keyCols, row)})");
                sb.AppendLine("BEGIN");
                sb.AppendLine($"  DELETE FROM {fn} WHERE {WhereClause(keyCols, row)}");
                sb.AppendLine("END");
            }
            else
            {
                // Fallback: nessuna colonna chiave disponibile, impossibile generare un DELETE sicuro
                sb.AppendLine($"-- ATTENZIONE: nessuna colonna chiave per DELETE su {fn}");
            }
            sb.AppendLine();
        }
        return sb.ToString();
    }

    // ─── Audit operations ────────────────────────────────────────────────────

    private async Task AuditInitializeAsync()
    {
        var db = cmbDatabases.SelectedItem as string;
        if (string.IsNullOrEmpty(db)) { MessageBox.Show("Seleziona prima un database."); return; }

        using var dlg = new AuditFilterDialog(db, _auditFilter, _auditExclude);
        if (dlg.ShowDialog(this) != DialogResult.OK) return;

        _auditFilter = dlg.AuditFilter;
        _auditExclude = dlg.AuditExclude;

        // Salva le impostazioni modificate
        SettingsService.SaveAuditSettings(new AuditSettings
        {
            AuditFilter = _auditFilter,
            AuditExclude = _auditExclude
        });

        var auditDb = $"{db}_UPD";
        AddMessage("Inizializza/Resetta db Audit_UPD (SETUP/INSTALLAZIONE)");
        SetLoading(true);
        try
        {
            var res = await _sql.ExecuteQueryAsync($"SELECT name FROM {db}..sysobjects WHERE type = 'U' {_auditFilter} {_auditExclude}");
            var tables = res.Success && res.Data is not null
                ? res.Data.Select(r => r.GetValueOrDefault("name")?.ToString() ?? "").Where(n => !string.IsNullOrEmpty(n)).ToList()
                : new List<string>();

            AddMessage($"Tabelle selezionate con filtri: {tables.Count}");

            var tableColumns = new Dictionary<string, List<ColumnInfo>>();
            foreach (var tbl in tables)
            {
                var colRes = await _dbExplorer.GetTableColumnsAsync(db, "dbo", tbl);
                tableColumns[tbl] = colRes.Success && colRes.Data is not null ? colRes.Data : new List<ColumnInfo>();
            }

            var script = AuditScriptBuilder.BuildInitScript(db, auditDb, tableColumns);
            SetSqlScript(script);
            AddMessage($"Script generato: {tables.Count} tabelle + {tables.Count * 3} trigger.");

            var answer = MessageBox.Show(
                $"Script generato per {tables.Count} tabelle.\n\nEseguire direttamente sul database '{db}'?",
                "Esegui script audit",
                MessageBoxButtons.YesNo, MessageBoxIcon.Question);

            if (answer == DialogResult.Yes)
            {
                AddMessage("Esecuzione script in corso...");
                var execResult = await _sql.ExecuteScriptAsync(script);
                if (execResult.Success)
                {
                    AddMessage($"✅ Script eseguito con successo.");
                    SetSqlScript(string.Empty);
                    rtbGeneratedScript?.Clear();
                }
                else
                    AddMessage($"❌ Errore: {execResult.Error}");
            }
            else
            {
                AddMessage("Script pronto nel pannello SQL — eseguilo manualmente quando vuoi.");
            }
        }
        finally { SetLoading(false); }
    }

    private void AuditRemove()
    {
        var db = cmbDatabases.SelectedItem as string;
        if (string.IsNullOrEmpty(db)) { MessageBox.Show("Seleziona prima un database."); return; }
        SetSqlScript(AuditScriptBuilder.BuildRemoveScript(db));
        AddMessage("Fine - Creazione script: Eliminazione sistema Audit (DISINSTALLAZIONE)");
    }

    private void AuditActivate()
    {
        var db = cmbDatabases.SelectedItem as string;
        if (string.IsNullOrEmpty(db)) { MessageBox.Show("Seleziona prima un database."); return; }
        SetSqlScript(AuditScriptBuilder.BuildActivateScript(db));
        AddMessage("Fine - Creazione script: Attivazione trigger Audit (INIZIO ATTIVITÀ)");
    }

    private void AuditDeactivate()
    {
        var db = cmbDatabases.SelectedItem as string;
        if (string.IsNullOrEmpty(db)) { MessageBox.Show("Seleziona prima un database."); return; }
        SetSqlScript(AuditScriptBuilder.BuildDeactivateScript(db));
        AddMessage("Fine - Creazione script: Disattivazione trigger Audit (PAUSA ATTIVITÀ)");
    }

    private async Task AuditGenerateScriptAsync()
    {
        var db = cmbDatabases.SelectedItem as string;
        if (string.IsNullOrEmpty(db)) { MessageBox.Show("Seleziona prima un database."); return; }

        var auditDb = db.EndsWith("_UPD") ? db : $"{db}_UPD";
        var originDb = auditDb.Replace("_UPD", "");
        AddMessage("Genera Script Audit da eSYS/eSYS_UPD (RILASCIO)");
        SetLoading(true);
        try
        {
            var chk = await _sql.ExecuteQueryAsync($"SELECT COUNT(*) AS cnt FROM sys.databases WHERE name = '{auditDb}'");
            if (chk.Success && Convert.ToInt32(chk.Data?[0].GetValueOrDefault("cnt") ?? 0) == 0)
            {
                AddMessage($"❌ Database audit '{auditDb}' non trovato.");
                tabResults.SelectedTab = tabMessages;
                return;
            }

            var tablesRes = await _sql.ExecuteQueryAsync($"SELECT name FROM {auditDb}.sys.tables ORDER BY name");
            var tableNames = tablesRes.Success && tablesRes.Data is not null
                ? tablesRes.Data.Select(r => r.GetValueOrDefault("name")?.ToString() ?? "").Where(n => !string.IsNullOrEmpty(n)).ToList()
                : new List<string>();
            AddMessage($"Tabelle trovate: {tableNames.Count}");

            var sb = new StringBuilder();
            int totalCmds = 0;
            var auditMeta = new HashSet<string>(
                new[] { "dba_tipo_comando", "dba_tipo_dato", "dba_macchina", "dba_utente", "dba_data", "dba_applicazione", "dba_guid", "dba_progupd" },
                StringComparer.OrdinalIgnoreCase);

            sb.AppendLine($"-- Script Audit  db:{originDb}  audit:{auditDb}  {DateTime.Now}");
            sb.AppendLine($"USE [{originDb}];");
            sb.AppendLine("GO");
            sb.AppendLine();

            foreach (var tbl in tableNames)
            {
                AddMessage($"Analisi {tbl}…");
                var rows = await _sql.ExecuteQueryAsync($"SELECT * FROM {auditDb}..{tbl} ORDER BY dba_data");
                if (!rows.Success || rows.Data is null || rows.Data.Count == 0) continue;

                var keysRes = await _dbExplorer.GetTableKeyColumnsAsync(originDb, "dbo", tbl);
                var dataCols = rows.Data[0].Keys.Where(k => !auditMeta.Contains(k)).ToList();
                var effKeys = keysRes.Count > 0 ? keysRes : dataCols;

                sb.AppendLine($"-- == {tbl} ({rows.Data.Count} righe) ==");

                var byGuid = new Dictionary<string, (Dictionary<string, object?>? Old, Dictionary<string, object?>? New)>(StringComparer.OrdinalIgnoreCase);
                foreach (var row in rows.Data)
                {
                    var guid = row.GetValueOrDefault("dba_guid")?.ToString() ?? Guid.NewGuid().ToString();
                    var tipo = (row.GetValueOrDefault("dba_tipo_dato")?.ToString() ?? "").Trim().ToUpper();
                    if (!byGuid.ContainsKey(guid)) byGuid[guid] = (null, null);
                    var e = byGuid[guid];
                    byGuid[guid] = tipo == "OLD" ? (row, e.New) : (e.Old, row);
                }

                foreach (var (_, (oldRow, newRow)) in byGuid)
                {
                    var cmd = ((newRow ?? oldRow)?.GetValueOrDefault("dba_tipo_comando")?.ToString() ?? "").Trim().ToUpper();
                    var rec = newRow ?? oldRow;
                    if (rec is null) continue;

                    if (cmd == "I")
                    {
                        var cols = string.Join(", ", dataCols);
                        var vals = string.Join(", ", dataCols.Select(c => FmtVal(rec.GetValueOrDefault(c))));
                        sb.AppendLine($"IF NOT EXISTS(SELECT 1 FROM {tbl} WHERE {WhereClause(effKeys, rec)})");
                        sb.AppendLine("BEGIN");
                        sb.AppendLine($"  INSERT INTO {tbl} ({cols}) VALUES ({vals})");
                        sb.AppendLine("END");
                    }
                    else if (cmd == "U")
                    {
                        if (oldRow is null || newRow is null) continue;

                        // Trova solo i campi che sono stati modificati
                        var modifiedCols = new List<string>();
                        foreach (var col in dataCols.Except(effKeys, StringComparer.OrdinalIgnoreCase))
                        {
                            var oldVal = oldRow.GetValueOrDefault(col);
                            var newVal = newRow.GetValueOrDefault(col);

                            // Confronta i valori
                            bool isDifferent = false;
                            if (oldVal is null && newVal is not null) isDifferent = true;
                            else if (oldVal is not null && newVal is null) isDifferent = true;
                            else if (oldVal is not null && newVal is not null)
                            {
                                isDifferent = !oldVal.Equals(newVal);
                            }

                            if (isDifferent)
                                modifiedCols.Add(col);
                        }

                        if (modifiedCols.Count == 0) continue;

                        // Costruisci la condizione IF NOT EXISTS con i campi modificati + chiavi
                        var checkConditions = new List<string>();
                        foreach (var key in effKeys)
                        {
                            var val = newRow.GetValueOrDefault(key);
                            if (val is null or DBNull)
                                checkConditions.Add($"{key} IS NULL");
                            else
                                checkConditions.Add($"{key} = {FmtVal(val)}");
                        }
                        foreach (var col in modifiedCols)
                        {
                            var val = newRow.GetValueOrDefault(col);
                            if (val is null or DBNull)
                                checkConditions.Add($"{col} IS NULL");
                            else
                                checkConditions.Add($"{col} = {FmtVal(val)}");
                        }

                        var setStr = string.Join(", ", modifiedCols.Select(c => $"{c} = {FmtVal(newRow.GetValueOrDefault(c))}"));

                        sb.AppendLine($"IF NOT EXISTS(SELECT 1 FROM {tbl} WHERE {string.Join(" AND ", checkConditions)})");
                        sb.AppendLine("BEGIN");
                        sb.AppendLine($"  UPDATE {tbl}");
                        sb.AppendLine($"  SET {setStr}");
                        sb.AppendLine($"  WHERE {WhereClause(effKeys, newRow)}");
                        sb.AppendLine("END");
                    }
                    else if (cmd == "D")
                    {
                        var dr = oldRow ?? rec;
                        sb.AppendLine($"IF EXISTS(SELECT 1 FROM {tbl} WHERE {WhereClause(effKeys, dr)})");
                        sb.AppendLine("BEGIN");
                        sb.AppendLine($"  DELETE FROM {tbl} WHERE {WhereClause(effKeys, dr)}");
                        sb.AppendLine("END");
                    }

                    sb.AppendLine(); sb.AppendLine("GO"); sb.AppendLine();
                    totalCmds++;
                }
            }

            sb.AppendLine($"-- Comandi totali: {totalCmds}");
            SetSqlScript(sb.ToString());
            rtbGeneratedScript.Text = sb.ToString();
            tabResults.SelectedTab = tabText;
            AddMessage($"✅ Script generato: {totalCmds} comandi da {tableNames.Count} tabelle");
        }
        finally { SetLoading(false); }
    }

    // ─── Helpers ─────────────────────────────────────────────────────────────

    private void SetLoading(bool loading)
    {
        if (InvokeRequired)
        {
            Invoke(() => SetLoading(loading));
            return;
        }

        lblStatusLoading.Text = loading ? "⏳ Caricamento…" : "";
        UseWaitCursor = loading;
    }

    // Appends text to the SQL editor in a thread-safe way
    private void AppendToSqlScript(string text)
    {
        if (rtbSqlScript is null) return;
        Action apply = () =>
        {
            if (!string.IsNullOrEmpty(rtbSqlScript.Text))
                rtbSqlScript.AppendText(Environment.NewLine + text);
            else
                rtbSqlScript.AppendText(text);
            rtbSqlScript.SelectionStart = rtbSqlScript.TextLength;
            rtbSqlScript.ScrollToCaret();
            rtbSqlScript.Refresh();
        };

        if (rtbSqlScript.InvokeRequired) rtbSqlScript.Invoke(apply);
        else apply();
    }

    // Replaces the SQL editor content in a thread-safe way
    private void SetSqlScript(string text)
    {
        if (rtbSqlScript is null) return;
        Action apply = () =>
        {
            _lastAutoScript = text;
            rtbSqlScript.Text = text;
            rtbSqlScript.SelectionStart = 0;
            rtbSqlScript.ScrollToCaret();
            rtbSqlScript.Refresh();
        };

        if (rtbSqlScript.InvokeRequired) rtbSqlScript.Invoke(apply);
        else apply();
    }

    private void AddMessage(string msg)
    {
        lstMessages.Items.Add($"[{DateTime.Now:HH:mm:ss}] {msg}");
        lstMessages.TopIndex = lstMessages.Items.Count - 1;
    }

    // ─── Syntax highlight ─────────────────────────────────────────────────────

    private void ApplySyntaxHighlight(RichTextBox rtb)
    {
        if (_isHighlighting || rtb == null) return;
        _isHighlighting = true;

        int selStart = rtb.SelectionStart;
        int selLen = rtb.SelectionLength;
        string text = rtb.Text;

        SendMessage(rtb.Handle, WM_SETREDRAW, false, 0);
        try
        {
            rtb.SelectAll();
            rtb.SelectionColor = SystemColors.WindowText;

            foreach (Match m in Regex.Matches(text, @"--[^\r\n]*"))
            {
                rtb.Select(m.Index, m.Length);
                rtb.SelectionColor = Color.DarkGreen;
            }

            foreach (Match m in Regex.Matches(text, @"'(?:[^']|'')*'"))
            {
                rtb.Select(m.Index, m.Length);
                rtb.SelectionColor = Color.DarkRed;
            }

            foreach (Match m in Regex.Matches(text, _sqlKeywordPattern, RegexOptions.IgnoreCase))
            {
                rtb.Select(m.Index, m.Length);
                if (rtb.SelectionColor != Color.DarkGreen && rtb.SelectionColor != Color.DarkRed)
                    rtb.SelectionColor = Color.Blue;
            }
        }
        finally
        {
            rtb.SelectionStart = selStart;
            rtb.SelectionLength = selLen;
            rtb.SelectionColor = SystemColors.WindowText;
            SendMessage(rtb.Handle, WM_SETREDRAW, true, 0);
            rtb.Invalidate();
            _isHighlighting = false;
        }
    }

    // ─── Ricerca generica su RichTextBox ─────────────────────────────────────

    private static void ShowSearchRtb(Panel pnl, TextBox txt)
    {
        pnl.Visible = true;
        txt.Focus();
        txt.SelectAll();
    }

    private static void CloseSearchRtb(Panel pnl, Label lblCount, RichTextBox rtb)
    {
        pnl.Visible = false;
        lblCount.Text = string.Empty;
        rtb.Focus();
    }

    private static void FindInRtb(RichTextBox rtb, TextBox txtSearch, Label lblCount, bool forward, bool resetPos = false)
    {
        string needle = txtSearch.Text;
        if (string.IsNullOrEmpty(needle))
        {
            lblCount.Text = string.Empty;
            return;
        }

        string haystack = rtb.Text;
        MatchCollection all = Regex.Matches(haystack, Regex.Escape(needle), RegexOptions.IgnoreCase);
        if (all.Count == 0)
        {
            lblCount.Text = "Non trovato";
            lblCount.ForeColor = Color.Red;
            return;
        }

        lblCount.ForeColor = SystemColors.WindowText;

        int startFrom = resetPos ? 0 : (forward
            ? rtb.SelectionStart + rtb.SelectionLength
            : rtb.SelectionStart - 1);

        int idx;
        if (forward)
        {
            idx = haystack.IndexOf(needle, Math.Max(0, startFrom), StringComparison.OrdinalIgnoreCase);
            if (idx < 0) idx = haystack.IndexOf(needle, 0, StringComparison.OrdinalIgnoreCase);
        }
        else
        {
            int backFrom = Math.Max(0, startFrom);
            idx = backFrom > 0 ? haystack.LastIndexOf(needle, backFrom, StringComparison.OrdinalIgnoreCase) : -1;
            if (idx < 0) idx = haystack.LastIndexOf(needle, StringComparison.OrdinalIgnoreCase);
        }

        if (idx >= 0)
        {
            rtb.Select(idx, needle.Length);
            rtb.ScrollToCaret();
        }

        int current = 0;
        for (int i = 0; i < all.Count; i++)
        {
            if (all[i].Index == idx) { current = i + 1; break; }
        }

        lblCount.Text = $"{current}/{all.Count}";
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        // Persist UI settings (table search + default conditional update)
        try
        {
            var s = Services.SettingsService.LoadAuditSettings();
            s.TableSearch = txtTableSearch?.Text ?? string.Empty;
            s.DefaultConditionalUpdate = _defaultConditionalUpdate;
            // Save last connected server/database
            s.LastServer = _config.Server ?? string.Empty;
            s.LastDatabase = _config.Database ?? string.Empty;
            s.LastUser = _config.User ?? string.Empty;
            Services.SettingsService.SaveAuditSettings(s);
        }
        catch { }

        _sql.Dispose();
        base.OnFormClosed(e);
    }
}
