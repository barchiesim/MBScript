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
    private List<TableInfo> _allTables = new();
    private TableInfo? _selectedTable = null;
    private string _auditFilter = string.Empty;
    private string _auditExclude = string.Empty;
    private bool _defaultConditionalUpdate = false;

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
        lstTables.DoubleClick += async (_, _) => await GuardAsync(LoadTableStructureAsync);
        // Aggiorna lo script anche al singolo click/selezione
        lstTables.SelectedIndexChanged += async (_, _) => await GuardAsync(LoadTableStructureAsync);

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
        rtbSqlScript = new RichTextBox
        {
            Dock = DockStyle.Fill,
            Font = new Font("Courier New", 8.5f),
            WordWrap = false,
            ScrollBars = RichTextBoxScrollBars.Both
        };
        rtbSqlScript.KeyDown += (_, e) =>
        {
            if (e.Control && e.KeyCode == Keys.Enter)
            {
                e.Handled = true;
                _ = GuardAsync(ExecuteQueryAsync);
            }
        };
        // Aggiungi prima il label (dock top) poi il controllo fill per evitare che il controllo riempia l'intera area
        pnlSql.Controls.Add(lblSql);
        pnlSql.Controls.Add(rtbSqlScript);
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

        rtbGeneratedScript = new RichTextBox
        {
            Dock = DockStyle.Fill,
            Font = new Font("Courier New", 8.5f),
            WordWrap = false,
            ScrollBars = RichTextBoxScrollBars.Both,
            AcceptsTab = true,
            DetectUrls = false
        };

        lstMessages = new ListBox
        {
            Dock = DockStyle.Fill,
            Font = new Font("Courier New", 7.5f),
            HorizontalScrollbar = true
        };

        tabGrid.Controls.Add(dgvResults);
        tabText.Controls.Add(rtbGeneratedScript);
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

    private async Task LoadTableStructureAsync()
    {
        if (lstTables.SelectedItem is not string item) return;
        var m = Regex.Match(item, @"\[([^\]]+)\]\.\[([^\]]+)\]");
        if (!m.Success) return;
        var schema = m.Groups[1].Value;
        var tbl = m.Groups[2].Value;
        var db = cmbDatabases.SelectedItem as string ?? _config.Database;

        _selectedTable = new TableInfo { TableSchema = schema, TableName = tbl, TableType = "BASE TABLE" };
        SetLoading(true);
        try
        {
            _keyColumns = await _dbExplorer.GetTableKeyColumnsAsync(db, schema, tbl);
            var scriptText = $"SELECT * FROM [{schema}].[{tbl}]";
            Action apply = () =>
            {
                // Replace the SQL editor content when selecting a table
                rtbSqlScript.Text = scriptText;
                rtbSqlScript.SelectionStart = 0;
                rtbSqlScript.ScrollToCaret();
                rtbSqlScript.BackColor = SystemColors.Window;
                rtbSqlScript.ForeColor = SystemColors.WindowText;
                rtbSqlScript.Visible = true;
                rtbSqlScript.BringToFront();
                rtbSqlScript.Refresh();
            };

            if (rtbSqlScript.InvokeRequired) rtbSqlScript.Invoke(apply);
            else apply();
        }
        finally { SetLoading(false); }
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
                tabResults.SelectedTab = tabMessages;
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
            if (_selectedTable is not null)
            {
                _keyColumns = await _dbExplorer.GetTableKeyColumnsAsync(db, _selectedTable.TableSchema, _selectedTable.TableName);
            }
            else
            {
                var mx = Regex.Match(rtbSqlScript.Text, @"FROM\s+(?:\[?(\w+)\]?\.)?\[?(\w+)\]?", RegexOptions.IgnoreCase);
                if (mx.Success)
                {
                    var sch = mx.Groups[1].Success ? mx.Groups[1].Value : "dbo";
                    var tbl = mx.Groups[2].Value;
                    _selectedTable = new TableInfo { TableSchema = sch, TableName = tbl, TableType = "BASE TABLE" };
                    _keyColumns = await _dbExplorer.GetTableKeyColumnsAsync(db, sch, tbl);
                }
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
                script = BuildInsertScript(_selectedTable, dlg.SelectedKeyColumns, dlg.SelectedColumns, selRows);
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

    private static string BuildInsertScript(TableInfo tbl, List<string> keyCols, List<string> valCols, List<Dictionary<string, object?>> rows)
    {
        var sb = new StringBuilder();
        var allCols = keyCols.Concat(valCols).ToList();
        var fn = FullName(tbl);
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
                // Fallback: no key columns provided, perform plain delete
                sb.AppendLine($"DELETE FROM {fn}");
                sb.AppendLine($"WHERE {WhereClause(keyCols, row)}");
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
            // Clear existing SQL and set the generated audit init script
            SetSqlScript(AuditScriptBuilder.BuildInitScript(db, auditDb, tables));
            AddMessage($"Fine - Creazione script: Inizializzazione/Reset Audit_UPD. Script generato: {tables.Count} tabelle + {tables.Count * 3} trigger.");
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
