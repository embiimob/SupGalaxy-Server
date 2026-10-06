using System.Collections.Concurrent;
using System.Globalization;

namespace SupGalaxyServer.Gui;

/// <summary>
/// Administration window: configure ports, start/stop, watch players (alphabetical + searchable), kick,
/// block/unblock user names, save now and discard the saved session.
/// Built in code (no designer file) to keep the project small and easy to edit.
/// </summary>
internal sealed class MainForm : Form
{
    private readonly string _dataDir;
    private readonly string _settingsPath;
    private readonly ConcurrentQueue<string> _pendingLog = new();
    private SupGalaxyServerHost? _host;
    private BlockList _offlineBlockList;
    private bool _closingAfterStop;
    private bool _blockListDirty = true;

    // Settings
    private readonly TextBox _serverName = new() { Width = 180 };
    private readonly TextBox _bindAddress = new() { Width = 120 };
    private readonly TextBox _publicAddress = new() { Width = 120, PlaceholderText = "optional" };
    private readonly NumericUpDown _mainPort = PortBox();
    private readonly NumericUpDown _playerStart = PortBox();
    private readonly NumericUpDown _playerEnd = PortBox();
    private readonly NumericUpDown _saveMinutes = new() { Minimum = 1, Maximum = 1440, Width = 60 };
    private readonly Button _startStop = new() { Text = "Start server", Width = 110 };
    private readonly Button _saveNow = new() { Text = "Save now", Width = 90, Enabled = false };
    private readonly Button _reset = new() { Text = "Reset saved session...", Width = 150 };
    private readonly Label _status = new() { AutoSize = true, Text = "Stopped." };

    // Players
    private readonly TextBox _search = new() { PlaceholderText = "Search players by name or world...", Dock = DockStyle.Top };
    private readonly ListView _players = new()
    {
        View = View.Details, FullRowSelect = true, MultiSelect = false, HideSelection = false, Dock = DockStyle.Fill,
    };
    private readonly TextBox _details = new() { Multiline = true, ReadOnly = true, Dock = DockStyle.Fill, ScrollBars = ScrollBars.Vertical, Font = new Font(FontFamily.GenericMonospace, 9) };
    private readonly Button _kick = new() { Text = "Kick", Width = 90 };
    private readonly Button _block = new() { Text = "Block", Width = 90 };

    // Blocked users
    private readonly ListBox _blocked = new() { Dock = DockStyle.Fill, Sorted = true };
    private readonly TextBox _blockName = new() { Width = 200, PlaceholderText = "User name" };
    private readonly Button _blockByName = new() { Text = "Block name", Width = 100 };
    private readonly Button _unblock = new() { Text = "Unblock selected", Width = 130 };

    // Worlds + log
    private readonly ListView _worlds = new() { View = View.Details, FullRowSelect = true, Dock = DockStyle.Fill };
    private readonly TextBox _log = new() { Multiline = true, ReadOnly = true, Dock = DockStyle.Fill, ScrollBars = ScrollBars.Both, WordWrap = false };

    private readonly System.Windows.Forms.Timer _refresh = new() { Interval = 1000 };

    public MainForm(string dataDir)
    {
        _dataDir = dataDir;
        Directory.CreateDirectory(dataDir);
        _settingsPath = Path.Combine(dataDir, "settings.json");
        _offlineBlockList = new BlockList(Path.Combine(dataDir, "blocked.json"));
        _offlineBlockList.Changed += () => _blockListDirty = true;

        Text = "SupGalaxy Server";
        Width = 1100;
        Height = 720;
        MinimumSize = new Size(900, 560);
        StartPosition = FormStartPosition.CenterScreen;

        BuildLayout();
        LoadSettingsIntoControls(ServerSettings.Load(_settingsPath));

        _startStop.Click += async (_, _) => await ToggleServerAsync();
        _saveNow.Click += (_, _) => SaveNow();
        _reset.Click += (_, _) => ResetSession();
        _search.TextChanged += (_, _) => RefreshPlayers();
        _players.SelectedIndexChanged += (_, _) => RefreshDetails();
        _kick.Click += (_, _) => KickSelected();
        _block.Click += (_, _) => BlockSelected();
        _blockByName.Click += (_, _) => BlockByName();
        _blockName.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Enter) BlockByName();
        };
        _unblock.Click += (_, _) => UnblockSelected();
        _refresh.Tick += (_, _) => RefreshAll();
        FormClosing += OnFormClosing;
        _refresh.Start();
        RefreshAll();
    }

    private static NumericUpDown PortBox() => new() { Minimum = 1, Maximum = 65535, Width = 75 };

    private void BuildLayout()
    {
        var settingsBox = new GroupBox { Text = "Server settings", Dock = DockStyle.Top, Height = 132, Padding = new Padding(8) };
        var flow = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = true, AutoScroll = true };
        var minutes = Caption("minutes");
        flow.Controls.AddRange(new Control[]
        {
            Caption("Server name"), _serverName,
            Caption("Bind address"), _bindAddress,
            Caption("Public IP"), _publicAddress,
            Caption("Main port"), _mainPort,
            Caption("Player ports"), _playerStart, Caption("to"), _playerEnd,
            Caption("Save every"), _saveMinutes, minutes,
            _startStop, _saveNow, _reset, _status,
        });
        flow.SetFlowBreak(_publicAddress, true);
        flow.SetFlowBreak(minutes, true);
        settingsBox.Controls.Add(flow);

        var tabs = new TabControl { Dock = DockStyle.Fill };

        // Players tab
        foreach (var (name, width) in new[] { ("Name", 170), ("World", 120), ("State", 85), ("Position", 150), ("Connected", 85), ("Port", 60), ("Address", 140), ("Msgs in/out", 110) })
            _players.Columns.Add(name, width);
        var playerButtons = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 36 };
        playerButtons.Controls.AddRange(new Control[] { _kick, _block });
        var listPanel = new Panel { Dock = DockStyle.Fill };
        listPanel.Controls.Add(_players);
        listPanel.Controls.Add(_search);
        listPanel.Controls.Add(playerButtons);
        var split = new SplitContainer { Dock = DockStyle.Fill, SplitterDistance = 700 };
        split.Panel1.Controls.Add(listPanel);
        var detailsBox = new GroupBox { Text = "Connection details", Dock = DockStyle.Fill };
        detailsBox.Controls.Add(_details);
        split.Panel2.Controls.Add(detailsBox);
        var playersTab = new TabPage("Players");
        playersTab.Controls.Add(split);

        // Blocked users tab
        var blockedButtons = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 36 };
        blockedButtons.Controls.AddRange(new Control[] { _blockName, _blockByName, _unblock });
        var blockedTab = new TabPage("Blocked users");
        blockedTab.Controls.Add(_blocked);
        blockedTab.Controls.Add(blockedButtons);

        // Worlds tab
        foreach (var (name, width) in new[] { ("World", 220), ("Chunks", 90), ("Blocks", 90), ("Stones", 90), ("Saved", 90) })
            _worlds.Columns.Add(name, width);
        var worldsTab = new TabPage("Saved worlds");
        worldsTab.Controls.Add(_worlds);

        var logTab = new TabPage("Log");
        logTab.Controls.Add(_log);

        tabs.TabPages.AddRange(new[] { playersTab, blockedTab, worldsTab, logTab });

        Controls.Add(tabs);
        Controls.Add(settingsBox);
    }

    private static Label Caption(string text) => new() { Text = text, AutoSize = true, Padding = new Padding(0, 6, 0, 0) };

    private void LoadSettingsIntoControls(ServerSettings s)
    {
        _serverName.Text = s.ServerName;
        _bindAddress.Text = s.BindAddress;
        _publicAddress.Text = s.PublicAddress ?? "";
        _mainPort.Value = Math.Clamp(s.SignalingPort, 1, 65535);
        _playerStart.Value = Math.Clamp(s.PlayerPortStart, 1, 65535);
        _playerEnd.Value = Math.Clamp(s.PlayerPortEnd, 1, 65535);
        _saveMinutes.Value = Math.Clamp(s.SaveIntervalMinutes, 1, 1440);
    }

    private ServerSettings ReadSettingsFromControls()
    {
        var s = ServerSettings.Load(_settingsPath);
        s.ServerName = _serverName.Text.Trim();
        s.BindAddress = _bindAddress.Text.Trim();
        s.PublicAddress = string.IsNullOrWhiteSpace(_publicAddress.Text) ? null : _publicAddress.Text.Trim();
        s.SignalingPort = (int)_mainPort.Value;
        s.PlayerPortStart = (int)_playerStart.Value;
        s.PlayerPortEnd = (int)_playerEnd.Value;
        s.SaveIntervalMinutes = (int)_saveMinutes.Value;
        return s;
    }

    private void SetSettingsEditable(bool editable)
    {
        foreach (var c in new Control[] { _serverName, _bindAddress, _publicAddress, _mainPort, _playerStart, _playerEnd, _saveMinutes })
            c.Enabled = editable;
    }

    private BlockList CurrentBlockList => _host?.BlockList ?? _offlineBlockList;

    private async Task ToggleServerAsync()
    {
        _startStop.Enabled = false;
        try
        {
            if (_host == null) await StartServerAsync();
            else await StopServerAsync();
        }
        finally
        {
            _startStop.Enabled = true;
            RefreshAll();
        }
    }

    private async Task StartServerAsync()
    {
        var settings = ReadSettingsFromControls();
        var errors = settings.Validate();
        if (errors.Count > 0)
        {
            MessageBox.Show(this, string.Join(Environment.NewLine, errors), "Invalid settings", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }
        settings.Save(_settingsPath);

        var host = new SupGalaxyServerHost(settings, _dataDir);
        host.Log += line => _pendingLog.Enqueue(line);
        host.BlockList.Changed += () => _blockListDirty = true;
        try
        {
            await host.StartAsync();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Could not start the server:{Environment.NewLine}{ex.Message}", "SupGalaxy Server", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }

        _host = host;
        _blockListDirty = true;
        _startStop.Text = "Stop server";
        _saveNow.Enabled = true;
        SetSettingsEditable(false);
    }

    private async Task StopServerAsync()
    {
        var host = _host;
        if (host == null) return;
        await host.StopAsync();
        _host = null;
        _offlineBlockList = new BlockList(Path.Combine(_dataDir, "blocked.json"));
        _offlineBlockList.Changed += () => _blockListDirty = true;
        _blockListDirty = true;
        _startStop.Text = "Start server";
        _saveNow.Enabled = false;
        SetSettingsEditable(true);
    }

    private void SaveNow()
    {
        if (_host == null) return;
        try
        {
            var written = _host.SaveNow();
            _pendingLog.Enqueue($"[{DateTime.Now:HH:mm:ss}] Manual save: {written} changed world(s) written.");
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Save failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void ResetSession()
    {
        var answer = MessageBox.Show(this,
            "WARNING: This permanently deletes the saved session - every block, stone and world edit stored by this server " +
            "will be lost and the server will start fresh. This cannot be undone." + Environment.NewLine + Environment.NewLine +
            "Blocked users and settings are kept. Connected players stay connected." + Environment.NewLine + Environment.NewLine +
            "Discard the saved session?",
            "Reset saved session", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2);
        if (answer != DialogResult.Yes) return;

        try
        {
            if (_host != null) _host.ResetSavedSession();
            else new WorldStateStore(Path.Combine(_dataDir, "session")).Reset();
            _pendingLog.Enqueue($"[{DateTime.Now:HH:mm:ss}] Saved session discarded.");
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Reset failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        RefreshAll();
    }

    private string? SelectedPlayer => _players.SelectedItems.Count == 1 ? _players.SelectedItems[0].Text : null;

    private void KickSelected()
    {
        if (_host == null || SelectedPlayer is not { } name) return;
        _host.Kick(name);
    }

    private void BlockSelected()
    {
        if (_host == null || SelectedPlayer is not { } name) return;
        if (MessageBox.Show(this, $"Block '{name}'? They will be disconnected and cannot reconnect until unblocked.", "Block user",
                MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes)
            _host.Block(name);
    }

    private void BlockByName()
    {
        var name = _blockName.Text.Trim();
        if (name.Length == 0) return;
        if (_host != null) _host.Block(name);
        else _offlineBlockList.Block(name);
        _blockName.Clear();
        _blockListDirty = true;
        RefreshBlocked();
    }

    private void UnblockSelected()
    {
        if (_blocked.SelectedItem is not string name) return;
        if (_host != null) _host.Unblock(name);
        else _offlineBlockList.Unblock(name);
        _blockListDirty = true;
        RefreshBlocked();
    }

    private void RefreshAll()
    {
        DrainLog();
        RefreshStatus();
        RefreshPlayers();
        RefreshBlocked();
        RefreshWorlds();
    }

    private void DrainLog()
    {
        var any = false;
        while (_pendingLog.TryDequeue(out var line))
        {
            _log.AppendText(line + Environment.NewLine);
            any = true;
        }
        if (any && _log.TextLength > 200_000) _log.Text = _log.Text[^100_000..];
    }

    private void RefreshStatus()
    {
        if (_host == null)
        {
            _status.Text = "Stopped.";
            return;
        }
        var saved = _host.Worlds.LastSaveUtc is { } t ? t.ToLocalTime().ToString("T", CultureInfo.CurrentCulture) : "not yet";
        _status.Text = $"Running on {string.Join(", ", _host.ListeningUrls)} | players {_host.Players.Count}/{_host.Ports.Capacity} | last save {saved}";
    }

    private void RefreshPlayers()
    {
        var selected = SelectedPlayer;
        var players = _host?.GetPlayers(_search.Text) ?? Array.Empty<PlayerInfo>();

        _players.BeginUpdate();
        try
        {
            // Update in place to keep the scroll position and selection stable.
            while (_players.Items.Count > players.Length) _players.Items.RemoveAt(_players.Items.Count - 1);
            for (int i = 0; i < players.Length; i++)
            {
                var p = players[i];
                var values = new[]
                {
                    p.Username, p.World ?? "", p.State.ToString(), $"{p.X:0.#}, {p.Y:0.#}, {p.Z:0.#}",
                    FormatDuration(DateTime.UtcNow - p.ConnectedAtUtc), p.Port.ToString(CultureInfo.InvariantCulture),
                    p.RemoteAddress ?? "", $"{p.MessagesIn}/{p.MessagesOut}",
                };
                if (i < _players.Items.Count)
                {
                    var item = _players.Items[i];
                    for (int c = 0; c < values.Length; c++)
                        if (item.SubItems[c].Text != values[c]) item.SubItems[c].Text = values[c];
                }
                else
                {
                    _players.Items.Add(new ListViewItem(values));
                }
                _players.Items[i].Selected = string.Equals(p.Username, selected, StringComparison.Ordinal);
            }
        }
        finally
        {
            _players.EndUpdate();
        }

        _kick.Enabled = _block.Enabled = _host != null && SelectedPlayer != null;
        RefreshDetails();
    }

    private void RefreshDetails()
    {
        var name = SelectedPlayer;
        var p = name == null ? null : _host?.GetPlayers().FirstOrDefault(x => x.Username == name);
        _kick.Enabled = _block.Enabled = _host != null && p != null;
        if (p == null)
        {
            _details.Text = _host == null ? "Server is stopped." : "Select a player to see connection details.";
            return;
        }
        _details.Text = string.Join(Environment.NewLine, new[]
        {
            $"User name:      {p.Username}",
            $"State:          {p.State}",
            $"ICE state:      {p.IceState ?? "-"}",
            $"World:          {p.World ?? "-"}",
            $"Position:       {p.X:0.##}, {p.Y:0.##}, {p.Z:0.##}",
            $"Remote address: {p.RemoteAddress ?? "-"}",
            $"Player port:    {p.Port}",
            $"Connected at:   {p.ConnectedAtUtc.ToLocalTime():G}",
            $"Connected for:  {FormatDuration(DateTime.UtcNow - p.ConnectedAtUtc)}",
            $"Last message:   {(DateTime.UtcNow - p.LastSeenUtc).TotalSeconds:0}s ago",
            $"Messages in:    {p.MessagesIn:N0} ({p.BytesIn / 1024.0:N1} KB)",
            $"Messages out:   {p.MessagesOut:N0} ({p.BytesOut / 1024.0:N1} KB)",
        });
    }

    private void RefreshBlocked()
    {
        if (!_blockListDirty) return;
        _blockListDirty = false;
        var selected = _blocked.SelectedItem as string;
        _blocked.BeginUpdate();
        _blocked.Items.Clear();
        foreach (var n in CurrentBlockList.Names) _blocked.Items.Add(n);
        if (selected != null && _blocked.Items.Contains(selected)) _blocked.SelectedItem = selected;
        _blocked.EndUpdate();
    }

    private void RefreshWorlds()
    {
        var worlds = _host?.Worlds.Summaries() ?? Array.Empty<WorldSummary>();
        _worlds.BeginUpdate();
        _worlds.Items.Clear();
        foreach (var w in worlds)
            _worlds.Items.Add(new ListViewItem(new[] { w.World, w.Chunks.ToString(), w.Blocks.ToString(), w.Stones.ToString(), w.Dirty ? "pending" : "yes" }));
        _worlds.EndUpdate();
    }

    private static string FormatDuration(TimeSpan t) =>
        t.TotalHours >= 1 ? $"{(int)t.TotalHours}h {t.Minutes:00}m" : $"{t.Minutes}m {t.Seconds:00}s";

    private async void OnFormClosing(object? sender, FormClosingEventArgs e)
    {
        if (_host == null || _closingAfterStop) return;
        // Stop gracefully (final save + notify players) before closing.
        e.Cancel = true;
        _closingAfterStop = true;
        try
        {
            await StopServerAsync();
        }
        finally
        {
            Close();
        }
    }
}
