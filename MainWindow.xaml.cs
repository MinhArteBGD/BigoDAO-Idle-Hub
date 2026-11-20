using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Threading;
using System.Linq;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace BigoDAOIdleHub;

public partial class MainWindow : Window
{
    private readonly ObservableCollection<GameSession> _sessions = new();
    private readonly List<string> _groups = new();
    private readonly string _dataRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BigoDAOIdleHub");
    private string ConfigPath => Path.Combine(_dataRoot, "sessions-v03.json");
    private string _viewMode = "grid";
    private bool _loading = true;
    private bool _rendering;
    private bool _changingPowerMode;
    private bool _updatingStats;
    private readonly DispatcherTimer _monitor = new() { Interval = TimeSpan.FromSeconds(8) };
    private readonly Process _process = Process.GetCurrentProcess();
    private readonly Dictionary<int, (TimeSpan Cpu, DateTime At)> _browserSamples = new();
    private readonly Dictionary<int, double> _browserCpu = new();
    private readonly Dictionary<int, long> _processRam = new();
    private readonly Dictionary<string, List<int>> _sessionPids = new();
    private readonly Dictionary<int, string> _pidOwner = new();
    private double _totalWebCpu;
    private long _totalWebRam;
    private TimeSpan _lastCpu;
    private DateTime _lastSample = DateTime.UtcNow;

    public MainWindow()
    {
        InitializeComponent();
        Directory.CreateDirectory(Path.Combine(_dataRoot, "Profiles"));
        LoadConfig();
        RefreshGroups("Todos os grupos");
        _loading = false;
        RefreshAccountList();
        _lastCpu = _process.TotalProcessorTime;
        _monitor.Tick += (_, _) => UpdateStats();
        _monitor.Start();
        Loaded += async (_, _) =>
        {
            foreach (var session in _sessions.ToList()) if (session.Mode != "standby") await InitializeBrowser(session);
            RenderSessions();
            UpdateStats();
        };
    }

    // Windows 11 private resident memory, excluding shared working-set pages.
    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryCountersEx2
    {
        public uint Size, PageFaultCount;
        public UIntPtr PeakWorkingSet, WorkingSet, QuotaPeakPagedPool, QuotaPagedPool,
            QuotaPeakNonPagedPool, QuotaNonPagedPool, Pagefile, PeakPagefile,
            PrivateUsage, PrivateWorkingSet, SharedCommit;
    }
    [DllImport("psapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetProcessMemoryInfo(IntPtr process, ref MemoryCountersEx2 counters, uint size);

    private static long ResidentPrivateBytes(Process process)
    {
        var size = (uint)Marshal.SizeOf<MemoryCountersEx2>();
        var counters = new MemoryCountersEx2 { Size = size };
        if (GetProcessMemoryInfo(process.Handle, ref counters, size))
            return (long)counters.PrivateWorkingSet.ToUInt64();
        throw new InvalidOperationException("Memória privada residente indisponível neste Windows.");
    }

    // Optional runtime feature: no suspension, navigation or forced working-set trim.
    private static void ApplyMemoryTarget(GameSession session, bool low)
    {
        try
        {
            var core = session.Browser.CoreWebView2;
            if (core == null || session.LastMemoryTarget == low) return;
            var property = core.GetType().GetProperty("MemoryUsageTargetLevel");
            if (property == null || !property.CanWrite) return;
            property.SetValue(core, Enum.Parse(property.PropertyType, low ? "Low" : "Normal"));
            session.LastMemoryTarget = low;
        }
        catch (Exception ex) { Debug.WriteLine("Memory target unavailable: " + ex.Message); }
    }

    private void UpdateStats()
    {
        if (_updatingStats || !IsLoaded) return;
        _updatingStats = true;
        try
        {
            _process.Refresh();
            var now = DateTime.UtcNow;
            var cpu = _process.TotalProcessorTime;
            var elapsed = (now - _lastSample).TotalMilliseconds;
            var percentage = elapsed > 0 ? Math.Max(0, (cpu - _lastCpu).TotalMilliseconds / elapsed / Environment.ProcessorCount * 100) : 0;
            _lastCpu = cpu;
            _lastSample = now;
            // Each account has its own WebView2 user-data environment. Collect all
            // browser/renderer/GPU/utility PIDs, deduplicating shared processes.
            var activePids = new HashSet<int>();
            _sessionPids.Clear();
            _pidOwner.Clear();
            foreach (var session in _sessions)
            {
                var pids = new List<int>();
                if (session.Mode != "standby" && session.Environment != null)
                {
                    try
                    {
                        foreach (var info in session.Environment.GetProcessInfos())
                        {
                            int pid = (int)info.ProcessId;
                            if (pid <= 0 || pids.Contains(pid)) continue;
                            pids.Add(pid);
                            activePids.Add(pid);
                            if (!_pidOwner.ContainsKey(pid)) _pidOwner[pid] = session.Id;
                        }
                    }
                    catch { /* Environment may be shutting down */ }
                }
                _sessionPids[session.Id] = pids;
            }
            foreach (var pid in activePids)
            {
                try
                {
                    using var proc = Process.GetProcessById(pid);
                    proc.Refresh();
                    try { _processRam[pid] = ResidentPrivateBytes(proc); }
                    catch { _processRam.Remove(pid); }
                    var total = proc.TotalProcessorTime;
                    if (_browserSamples.TryGetValue(pid, out var old))
                    {
                        var delta = (now - old.At).TotalMilliseconds;
                        _browserCpu[pid] = delta > 0 ? Math.Max(0, (total - old.Cpu).TotalMilliseconds / delta / Environment.ProcessorCount * 100) : 0;
                    }
                    _browserSamples[pid] = (total, now);
                }
                catch { _browserCpu.Remove(pid); _browserSamples.Remove(pid); _processRam.Remove(pid); }
            }
            foreach (var pid in _browserSamples.Keys.Where(p => !activePids.Contains(p)).ToList())
            { _browserSamples.Remove(pid); _browserCpu.Remove(pid); _processRam.Remove(pid); }
            _totalWebRam = activePids.Sum(pid => _processRam.GetValueOrDefault(pid));
            _totalWebCpu = activePids.Sum(pid => _browserCpu.GetValueOrDefault(pid));
            SessionStats.Text = $"{_sessions.Count} sessões  •  {_groups.Count} grupos";
            if (_viewMode == "list")
            {
                // Refresh values without rebuilding the rows or changing the selection.
                var current = VisibleSessions.ToList();
                for (int i = 0; i < Math.Min(current.Count, ListRows.Children.Count); i++)
                {
                    if (ListRows.Children[i] is Grid row)
                    {
                        var ramCell = row.Children.OfType<TextBlock>().FirstOrDefault(t => Grid.GetColumn(t) == 1);
                        if (ramCell != null) ramCell.Text = BrowserRamLabel(current[i]);
                        var cpuCell = row.Children.OfType<TextBlock>().FirstOrDefault(t => Grid.GetColumn(t) == 2);
                        if (cpuCell != null) cpuCell.Text = BrowserCpuLabel(current[i]);
                    }
                }
            }
            ResourceStats.Text = $"WebView2: {(activePids.Any(pid => !_processRam.ContainsKey(pid)) ? "parcial " : "")}{_totalWebRam / 1048576.0:F0} MB RAM priv.  •  CPU {_totalWebCpu:F1}%  |  App: {_process.WorkingSet64 / 1048576.0:F0} MB / {percentage:F1}%";
        }
        catch { ResourceStats.Text = "Medição indisponível"; }
        finally { _updatingStats = false; }
    }

    private void LoadConfig()
    {
        try
        {
            if (!File.Exists(ConfigPath)) return;
            var saved = JsonSerializer.Deserialize<SavedConfig>(File.ReadAllText(ConfigPath));
            if (saved is null) return;
            _groups.AddRange(saved.Groups.Where(g => !string.IsNullOrWhiteSpace(g)).Distinct());
            foreach (var entry in saved.Sessions)
            {
                if (!Guid.TryParse(entry.Id, out _) || !ValidUrl(entry.Url, out _)) continue;
                if (!string.IsNullOrWhiteSpace(entry.Group) && !_groups.Contains(entry.Group)) _groups.Add(entry.Group);
                _sessions.Add(new GameSession(entry.Id, entry.Group, entry.Name, entry.Url) { Mode = entry.Mode is "eco" or "standby" ? entry.Mode : "normal" });
            }
        }
        catch (Exception ex) { MessageBox.Show("Não foi possível carregar os perfis salvos: " + ex.Message); }
    }

    private void SaveConfig()
    {
        try
        {
            var config = new SavedConfig
            {
                Groups = _groups.ToList(),
                Sessions = _sessions.Select(s => new SavedSession { Id = s.Id, Group = s.Group, Name = s.Name, Url = PersistentUrl(s.Url), Mode = s.Mode }).ToList()
            };
            var temp = ConfigPath + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(config, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temp, ConfigPath, true);
        }
        catch (Exception ex) { MessageBox.Show("Não foi possível salvar as sessões: " + ex.Message); }
    }

    private static bool ValidUrl(string input, out Uri? uri)
    {
        return Uri.TryCreate(input, UriKind.Absolute, out uri) && uri.Scheme == Uri.UriSchemeHttps && !string.IsNullOrEmpty(uri.Host) && string.IsNullOrEmpty(uri.UserInfo);
    }

    // Do not persist query strings or fragments, which may contain login tokens.
    private static string PersistentUrl(string url)
    {
        if (!ValidUrl(url, out var uri)) return "";
        return uri!.GetLeftPart(UriPartial.Path);
    }

    private string CurrentGroup => GroupFilter.SelectedItem as string ?? "Todos os grupos";
    // The persisted collection is the actual visual order, even across groups.
    private IEnumerable<GameSession> VisibleSessions => _sessions
        .Where(s => CurrentGroup == "Todos os grupos" || s.Group == (CurrentGroup == "Sem grupo" ? "" : CurrentGroup));

    private void RefreshGroups(string? selected = null)
    {
        selected ??= CurrentGroup;
        GroupFilter.Items.Clear();
        GroupFilter.Items.Add("Todos os grupos");
        GroupFilter.Items.Add("Sem grupo");
        foreach (var group in _groups) GroupFilter.Items.Add(group);
        GroupFilter.SelectedItem = GroupFilter.Items.Contains(selected) ? selected : "Todos os grupos";
    }

    private void CreateGroup_Click(object sender, RoutedEventArgs e)
    {
        var group = GroupName.Text.Trim();
        if (!ValidGroupName(group)) return;
        _groups.Add(group);
        GroupName.Clear();
        UpdateGroups(group);
    }

    private bool ValidGroupName(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > 80 ||
            string.Equals(name, "Todos os grupos", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(name, "Sem grupo", StringComparison.OrdinalIgnoreCase) ||
            _groups.Contains(name, StringComparer.OrdinalIgnoreCase))
        { MessageBox.Show("Escolha um nome único para o grupo (até 80 caracteres)."); return false; }
        return true;
    }

    private void UpdateGroups(string filter, GameSession? selected = null)
    {
        _loading = true;
        try { RefreshGroups(filter); } finally { _loading = false; }
        RefreshAccountList(selected);
        SaveConfig();
        RenderSessions();
    }

    private void RenameGroup_Click(object sender, RoutedEventArgs e)
    {
        var old = CurrentGroup;
        if (!_groups.Contains(old)) { MessageBox.Show("Selecione um grupo para renomear."); return; }
        var name = GroupName.Text.Trim();
        if (!ValidGroupName(name)) return;
        _groups[_groups.IndexOf(old)] = name;
        foreach (var session in _sessions.Where(s => s.Group == old)) session.Group = name;
        GroupName.Clear();
        UpdateGroups(name, Sessions.SelectedItem as GameSession);
    }

    private void DeleteGroup_Click(object sender, RoutedEventArgs e)
    {
        var group = CurrentGroup;
        if (!_groups.Contains(group)) { MessageBox.Show("Selecione um grupo para excluir."); return; }
        if (MessageBox.Show($"Excluir o grupo {group}? As contas serão mantidas em Sem grupo.", "Excluir grupo", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes) return;
        foreach (var session in _sessions.Where(s => s.Group == group)) session.Group = "";
        _groups.Remove(group);
        UpdateGroups("Todos os grupos", Sessions.SelectedItem as GameSession);
    }

    private void MoveGroup_Click(object sender, RoutedEventArgs e)
    {
        if (Sessions.SelectedItem is not GameSession session) { MessageBox.Show("Selecione uma conta."); return; }
        var picker = new ComboBox { Margin = new Thickness(0,8,0,12), ItemsSource = new[] { "Sem grupo" }.Concat(_groups).ToList(), SelectedItem = string.IsNullOrEmpty(session.Group) ? "Sem grupo" : session.Group };
        picker.Style = (Style)FindResource(typeof(ComboBox));
        var dialog = new Window { Owner = this, Title = "Mover conta", Width = 340, SizeToContent = SizeToContent.Height, WindowStartupLocation = WindowStartupLocation.CenterOwner, ResizeMode = ResizeMode.NoResize, Background = Background, Foreground = Foreground };
        var panel = new StackPanel { Margin = new Thickness(18) };
        panel.Children.Add(new TextBlock { Text = session.Display, TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(picker);
        var ok = new Button { Content = "Mover conta", IsDefault = true };
        ok.Click += (_, _) => { dialog.DialogResult = true; };
        panel.Children.Add(ok);
        var cancel = new Button { Content = "Cancelar", IsCancel = true, Margin = new Thickness(0,6,0,0) };
        panel.Children.Add(cancel);
        dialog.Content = panel;
        if (dialog.ShowDialog() != true || picker.SelectedItem is not string destination) return;
        session.Group = destination == "Sem grupo" ? "" : destination;
        UpdateGroups(destination, session);
    }

    private async void Add_Click(object sender, RoutedEventArgs e)
    {
        var url = Address.Text.Trim();
        if (!ValidUrl(url, out var uri)) { MessageBox.Show("Informe uma URL HTTPS válida."); return; }
        var group = CurrentGroup;
        if (group == "Todos os grupos")
        {
            group = uri!.Host;
            if (!_groups.Contains(group)) _groups.Add(group);
        }
        if (group == "Sem grupo") group = "";
        var name = AccountName.Text.Trim();
        if (name.Length == 0) name = $"Conta {_sessions.Count(s => s.Group == group) + 1}";
        var session = new GameSession(Guid.NewGuid().ToString("N"), group, name, url);
        _sessions.Add(session);
        RefreshGroups(string.IsNullOrEmpty(group) ? "Sem grupo" : group);
        RefreshAccountList(session);
        SaveConfig();
        await InitializeBrowser(session);
        RenderSessions();
    }

    private async System.Threading.Tasks.Task InitializeBrowser(GameSession session)
    {
        try
        {
            var folder = Path.Combine(_dataRoot, "Profiles", session.Id);
            var environment = await CoreWebView2Environment.CreateAsync(userDataFolder: folder);
            session.Environment = environment;
            if (!_sessions.Contains(session)) return;
            await session.Browser.EnsureCoreWebView2Async(environment);
            session.LastMemoryTarget = null;
            session.Browser.CoreWebView2.IsMuted = session.Mode == "eco";
            ApplyMemoryTarget(session, session.Mode == "eco" || _viewMode == "list");
            var core = session.Browser.CoreWebView2;
            core.Settings.AreDevToolsEnabled = false;
            core.Settings.AreHostObjectsAllowed = false;
            core.Settings.IsWebMessageEnabled = false;
            core.PermissionRequested += (_, args) =>
            {
                args.State = CoreWebView2PermissionState.Deny;
                args.SavesInProfile = false;
            };
            core.DownloadStarting += (_, args) => { args.Cancel = true; args.Handled = true; ShowNotice("Download bloqueado por segurança."); };
            core.NewWindowRequested += (_, args) => { args.Handled = true; ShowNotice("Pop-up bloqueado por segurança."); };
            core.LaunchingExternalUriScheme += (_, args) => { args.Cancel = true; };
            session.Browser.CoreWebView2.NavigationStarting += (sender, args) =>
            {
                if (!Uri.TryCreate(args.Uri, UriKind.Absolute, out var target) ||
                    (!ValidUrl(args.Uri, out _) && args.Uri != "about:blank")) args.Cancel = true;
            };
            session.Browser.Source = new Uri(session.Url);
        }
        catch (Exception ex) { MessageBox.Show($"Falha ao abrir {session.Display}: {ex.Message}"); }
    }

    private DispatcherTimer? _noticeTimer;
    private void ShowNotice(string message)
    {
        SecurityNotice.Text = message;
        _noticeTimer ??= new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        _noticeTimer.Stop();
        _noticeTimer.Tick -= ClearNotice;
        _noticeTimer.Tick += ClearNotice;
        _noticeTimer.Start();
    }
    private void ClearNotice(object? sender, EventArgs e) { _noticeTimer?.Stop(); SecurityNotice.Text = ""; }

    private void GroupFilter_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        RefreshAccountList(Sessions.SelectedItem as GameSession);
        RenderSessions();
    }

    private void MoveUp_Click(object sender, RoutedEventArgs e) => MoveSelected(-1);
    private void MoveDown_Click(object sender, RoutedEventArgs e) => MoveSelected(1);

    private void MoveSelected(int direction)
    {
        if (Sessions.SelectedItem is not GameSession session) return;
        // Move against the visible list, including accounts from other groups
        // when "Todos os grupos" is selected.
        var visible = VisibleSessions.ToList();
        var index = visible.IndexOf(session);
        var next = index + direction;
        if (next < 0 || next >= visible.Count) return;
        var other = visible[next];
        var a = _sessions.IndexOf(session);
        var b = _sessions.IndexOf(other);
        _sessions[a] = other;
        _sessions[b] = session;
        SaveConfig();
        RefreshAccountList(session);
        RenderSessions();
    }

    private void RefreshAccountList(GameSession? selected = null)
    {
        var items = VisibleSessions.ToList();
        Sessions.ItemsSource = null;
        Sessions.ItemsSource = items;
        Sessions.SelectedItem = selected != null && items.Contains(selected) ? selected : items.FirstOrDefault();
        UpdateModeSelector();
    }

    private void Grid_Click(object sender, RoutedEventArgs e) { _viewMode = "grid"; RenderSessions(); }
    private void Single_Click(object sender, RoutedEventArgs e) { _viewMode = "single"; RenderSessions(); }
    private void List_Click(object sender, RoutedEventArgs e) { _viewMode = "list"; RenderSessions(); }
    private void Sessions_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        UpdateModeSelector();
        if (_viewMode == "single") RenderSessions();
    }

    private void UpdateModeSelector()
    {
        _changingPowerMode = true;
        var session = Sessions.SelectedItem as GameSession;
        NormalMode.IsEnabled = EcoMode.IsEnabled = StandbyMode.IsEnabled = session != null;
        var active = session?.Mode ?? "normal";
        foreach (var button in new[] { NormalMode, EcoMode, StandbyMode })
        {
            bool selected = (button.Tag as string) == active;
            button.Background = new SolidColorBrush(selected ? Color.FromRgb(24, 172, 99) : Color.FromRgb(32, 54, 43));
            button.BorderBrush = new SolidColorBrush(selected ? Color.FromRgb(65, 236, 139) : Color.FromRgb(53, 90, 67));
            button.FontWeight = selected ? FontWeights.Bold : FontWeights.Normal;
        }
        ModeHint.Text = session?.Mode switch
        {
            "eco" => "Som desligado e alvo de memória reduzido quando suportado. Página ativa.",
            "standby" => "Página descarregada. Só use se o jogo continuar farmando no servidor. Ao voltar, recarrega.",
            _ => "Página ativa, sem restrições adicionais."
        };
        _changingPowerMode = false;
    }

    private async void PowerMode_Click(object sender, RoutedEventArgs e)
    {
        if (_loading || _changingPowerMode || Sessions.SelectedItem is not GameSession session || sender is not Button button) return;
        var requested = button.Tag as string ?? "normal";
        if (requested == session.Mode) return;
        if (requested == "standby" && MessageBox.Show(
            "Standby vai fechar a página deste jogo, mas manter o perfil e login armazenados. O farm que depende da página pode parar. Continuar?",
            "Confirmar standby", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
        { UpdateModeSelector(); return; }
        try
        {
            if (requested == "standby")
            {
                Detach(session.Browser);
                session.Browser.Dispose();
                session.Browser = new WebView2();
            }
            else
            {
                var wasStandby = session.Mode == "standby";
                session.Mode = requested;
                if (wasStandby) await InitializeBrowser(session);
                else if (session.Browser.CoreWebView2 != null) session.Browser.CoreWebView2.IsMuted = requested == "eco";
            }
            session.Mode = requested;
            SaveConfig();
            UpdateModeSelector();
            RenderSessions();
        }
        catch (Exception ex) { MessageBox.Show("Erro ao mudar modo: " + ex.Message); UpdateModeSelector(); }
    }

    private async void ActivateAll_Click(object sender, RoutedEventArgs e)
    {
        foreach (var session in _sessions.ToList())
        {
            var wasStandby = session.Mode == "standby";
            session.Mode = "normal";
            if (wasStandby) await InitializeBrowser(session);
            else if (session.Browser.CoreWebView2 != null) session.Browser.CoreWebView2.IsMuted = false;
        }
        SaveConfig(); UpdateModeSelector(); RenderSessions();
    }

    private static void Detach(WebView2 browser)
    {
        if (browser.Parent is Panel panel) panel.Children.Remove(browser);
        else if (browser.Parent is ContentControl control) control.Content = null;
    }

    private void RenderSessions()
    {
        if (_loading || _rendering) return;
        _rendering = true;
        try
        {
            foreach (var session in _sessions) Detach(session.Browser);
            BrowserHost.Children.Clear();
            Tiles.Children.Clear();
            HiddenHost.Children.Clear();
            var visible = VisibleSessions.ToList();
            EmptyMessage.Visibility = visible.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            BrowserHost.Visibility = _viewMode == "single" ? Visibility.Visible : Visibility.Collapsed;
            GridScroll.Visibility = _viewMode == "grid" ? Visibility.Visible : Visibility.Collapsed;
            ListScroll.Visibility = _viewMode == "list" ? Visibility.Visible : Visibility.Collapsed;
            ListRows.Children.Clear();
            Tiles.Columns = visible.Count <= 1 ? 1 : visible.Count <= 4 ? 2 : visible.Count <= 9 ? 3 : 4;
            foreach (var session in _sessions.Where(s => !visible.Contains(s)).Concat(visible))
            {
                if (session.Mode == "standby")
                {
                    if (!visible.Contains(session)) continue;
                    if (_viewMode == "single" && Sessions.SelectedItem != session) continue;
                    var placeholder = new Border { Background = new SolidColorBrush(Color.FromRgb(28, 46, 35)), Margin = new Thickness(5), Padding = new Thickness(20), Child = new TextBlock { Text = session.Display + "\n\nSTANDBY — página descarregada\nSelecione a conta e escolha Normal para reabrir.", Foreground = Brushes.White, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center } };
                    if (_viewMode == "grid") Tiles.Children.Add(placeholder); else if (_viewMode == "single") BrowserHost.Children.Add(placeholder);
                    continue;
                }
                if (!visible.Contains(session) || _viewMode == "list" || (_viewMode == "single" && Sessions.SelectedItem != session))
                { HiddenHost.Children.Add(session.Browser); continue; }
                if (_viewMode == "single") { BrowserHost.Children.Add(session.Browser); continue; }
                var panel = new DockPanel { Margin = new Thickness(5), Background = new SolidColorBrush(Color.FromRgb(27,48,35)), Height = visible.Count <= 2 ? 480 : visible.Count <= 4 ? 360 : 290 };
                var header = new Button { Content = session.Display + "   ⛶", Foreground = Brushes.White, Background = new SolidColorBrush(Color.FromRgb(27,48,35)), BorderThickness = new Thickness(0), HorizontalContentAlignment = HorizontalAlignment.Left, Padding = new Thickness(8,5,8,5) };
                header.Click += (_, _) => { Sessions.SelectedItem = session; _viewMode = "single"; RenderSessions(); };
                DockPanel.SetDock(header, Dock.Top);
                panel.Children.Add(header);
                panel.Children.Add(session.Browser);
                Tiles.Children.Add(panel);
            }
            foreach (var session in _sessions)
            {
                if (session.Mode == "standby") continue;
                ApplyMemoryTarget(session, session.Mode == "eco" || session.Browser.Parent == HiddenHost);
            }
            if (_viewMode == "list")
            {
                foreach (var session in visible) ListRows.Children.Add(BuildListRow(session));
            }
        }
        finally { _rendering = false; }
    }

    private FrameworkElement BuildListRow(GameSession session)
    {
        var grid = new Grid { Background = new SolidColorBrush(Color.FromRgb(27, 48, 35)), Margin = new Thickness(2, 3, 2, 3), MinHeight = 48 };
        foreach (var width in new[] { new GridLength(2, GridUnitType.Star), new GridLength(110), new GridLength(110), new GridLength(155), new GridLength(100) })
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = width });
        var name = new TextBlock { Text = session.Display, Foreground = Brushes.White, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 4, 0), TextTrimming = TextTrimming.CharacterEllipsis };
        Grid.SetColumn(name, 0); grid.Children.Add(name);
        var ram = new TextBlock { Text = BrowserRamLabel(session), Foreground = Brushes.LightGreen, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(ram, 1); grid.Children.Add(ram);
        var cpu = new TextBlock { Text = BrowserCpuLabel(session), Foreground = Brushes.LightGray, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(cpu, 2); grid.Children.Add(cpu);
        var mode = new ComboBox { Margin = new Thickness(0, 7, 8, 7), SelectedValuePath = "Tag",  };
        foreach (var (label, tag) in new[] { ("Normal", "normal"), ("Econômico", "eco"), ("Standby", "standby") })
            mode.Items.Add(new ComboBoxItem { Content = label, Tag = tag });
        mode.SelectedValue = session.Mode;
        mode.SelectionChanged += (_, _) =>
        {
            if (_rendering || mode.SelectedValue is not string next || next == session.Mode) return;
            Sessions.SelectedItem = session;
            var button = next switch { "eco" => EcoMode, "standby" => StandbyMode, _ => NormalMode };
            PowerMode_Click(button, new RoutedEventArgs());
        };
        Grid.SetColumn(mode, 3); grid.Children.Add(mode);
        var open = new Button { Content = "Abrir", Margin = new Thickness(4, 7, 8, 7), Padding = new Thickness(5) };
        open.Click += (_, _) => { Sessions.SelectedItem = session; _viewMode = "single"; RenderSessions(); };
        Grid.SetColumn(open, 4); grid.Children.Add(open);
        return grid;
    }

    private IEnumerable<int> OwnedPids(GameSession session) =>
        _sessionPids.TryGetValue(session.Id, out var pids)
            ? pids.Where(pid => _pidOwner.GetValueOrDefault(pid) == session.Id)
            : Enumerable.Empty<int>();

    private string BrowserCpuLabel(GameSession session)
    {
        if (session.Mode == "standby") return "Suspensa";
        if (!_sessionPids.ContainsKey(session.Id)) return "—";
        return $"{OwnedPids(session).Sum(pid => _browserCpu.GetValueOrDefault(pid)):F1}%";
    }

    private void OpenTwitch_Click(object sender, RoutedEventArgs e) => OpenExternal("https://www.twitch.tv/minharte");
    private void OpenYouTube_Click(object sender, RoutedEventArgs e) => OpenExternal("https://www.youtube.com/@minharte");
    private void OpenX_Click(object sender, RoutedEventArgs e) => OpenExternal("https://x.com/MinhArteWeb3");
    private void OpenDiscord_Click(object sender, RoutedEventArgs e) => OpenExternal("https://discord.com/invite/bigodao");
    private static void OpenExternal(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch (Exception ex) { MessageBox.Show("Não foi possível abrir o link: " + ex.Message); }
    }

    private string BrowserRamLabel(GameSession session)
    {
        if (session.Mode == "standby") return "Suspensa";
        if (!_sessionPids.ContainsKey(session.Id) || OwnedPids(session).Any(pid => !_processRam.ContainsKey(pid))) return "—";
        return $"{OwnedPids(session).Sum(pid => _processRam.GetValueOrDefault(pid)) / 1048576.0:F0} MB";
    }

    private void Rename_Click(object sender, RoutedEventArgs e)
    {
        if (Sessions.SelectedItem is not GameSession session) return;
        var newName = AccountName.Text.Trim();
        if (newName.Length == 0) { MessageBox.Show("Digite o novo nome no campo ao lado do endereço."); return; }
        session.Name = newName;
        RefreshAccountList(session);
        SaveConfig();
        RenderSessions();
    }

    private async void Remove_Click(object sender, RoutedEventArgs e)
    {
        if (Sessions.SelectedItem is not GameSession session || sender is not Button button) return;
        var choice = MessageBox.Show($"Remover {session.Display}?\n\nSim: remover e excluir dados locais.\nNão: remover e manter dados locais.\nCancelar: manter a conta.", "Remover conta", MessageBoxButton.YesNoCancel, MessageBoxImage.Question, MessageBoxResult.Cancel);
        if (choice == MessageBoxResult.Cancel) return;
        bool erase = choice == MessageBoxResult.Yes;
        if (erase && MessageBox.Show("Excluir permanentemente cookies, cache e sessões deste perfil? Backups não serão apagados.", "Confirmar exclusão", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes) return;
        button.IsEnabled = false;
        try
        {
            if (erase && (!Guid.TryParseExact(session.Id, "N", out _) || _sessions.Any(s => s != session && string.Equals(s.Id, session.Id, StringComparison.OrdinalIgnoreCase))))
                throw new InvalidOperationException("Identificador de perfil inválido ou compartilhado.");
            Detach(session.Browser);
            _sessions.Remove(session);
            session.Browser.Dispose();
            SaveConfig();
            GroupFilter_SelectionChanged(sender, new SelectionChangedEventArgs(System.Windows.Controls.Primitives.Selector.SelectionChangedEvent, new System.Collections.IList[0], new System.Collections.IList[0]));
            if (!erase) return;
            var root = Path.Combine(_dataRoot, "Profiles");
            var folder = Path.Combine(root, session.Id);
            for (int attempt = 0; attempt < 20; attempt++)
            {
                try
                {
                    if (!Directory.Exists(folder)) break;
                    // Refuse junctions/symlinks rather than deleting through them.
                    CheckPlainDirectory(_dataRoot);
                    CheckPlainDirectory(root);
                    CheckProfileTree(folder);
                    Directory.Delete(folder, true);
                    break;
                }
                catch (IOException) when (attempt < 19) { await System.Threading.Tasks.Task.Delay(500); }
                catch (UnauthorizedAccessException) when (attempt < 19) { await System.Threading.Tasks.Task.Delay(500); }
            }
            if (Directory.Exists(folder)) throw new IOException("O perfil ainda existe.");
            MessageBox.Show("Dados locais do perfil excluídos.");
        }
        catch (Exception ex) { MessageBox.Show("A operação não foi concluída integralmente. Dados podem permanecer no computador. " + ex.Message, "Aviso"); }
        finally { button.IsEnabled = true; }
    }

    private static void CheckPlainDirectory(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidOperationException("Exclusão recusada: caminho redirecionado.");
    }

    private static void CheckProfileTree(string path)
    {
        CheckPlainDirectory(path);
        foreach (var entry in Directory.EnumerateFileSystemEntries(path))
        {
            var attributes = File.GetAttributes(entry);
            if ((attributes & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException("Exclusão recusada: caminho redirecionado.");
            if ((attributes & FileAttributes.Directory) != 0) CheckProfileTree(entry);
        }
    }

    private void Window_Closing(object? sender, CancelEventArgs e) { _monitor.Stop(); SaveConfig(); }

    private sealed class GameSession
    {
        public string Id { get; }
        public string Group { get; set; }
        public string Name { get; set; }
        public string Url { get; set; }
        public WebView2 Browser { get; set; } = new();
        public CoreWebView2Environment? Environment { get; set; }
        public string Mode { get; set; } = "normal";
        public bool? LastMemoryTarget { get; set; }
        public string Display => $"{(string.IsNullOrEmpty(Group) ? "Sem grupo" : Group)} / {Name}";
        public GameSession(string id, string group, string name, string url) { Id = id; Group = group; Name = name; Url = url; }
    }
    private sealed class SavedConfig
    {
        public List<string> Groups { get; set; } = new();
        public List<SavedSession> Sessions { get; set; } = new();
    }
    private sealed class SavedSession
    {
        public string Id { get; set; } = "";
        public string Group { get; set; } = "";
        public string Name { get; set; } = "";
        public string Url { get; set; } = "";
        public string Mode { get; set; } = "normal";
    }
}
