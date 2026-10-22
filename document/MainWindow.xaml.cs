using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using document.Models;
using document.Services;

namespace document
{
    public partial class MainWindow : Window
    {
        private readonly UpdateService _updater = new();
        private readonly AppSettings _settings;

        /// <summary>存放位置（根資料夾）；左側「全部文件」就是它。</summary>
        private string _root;
        /// <summary>左側目前選的資料夾（完整路徑），等於 _root 時是「全部文件」。</summary>
        private string _selectedFolder;
        private readonly HashSet<string> _expanded = new(StringComparer.OrdinalIgnoreCase);
        private bool _rebuildingFolders;
        private bool _ready;
        private int _folderTotal, _fileTotal;

        // 內容沒變時不重設清單，捲動位置和選取才不會跳掉
        private string _folderSignature = "";
        private string _fileSignature = "";
        /// <summary>重新整理後要選取的項目（新增、改名、搬移後選到新的位置）。</summary>
        private List<string>? _pendingSelection;

        // 在檔案總管或別的程式裡改了檔案，等一下下再重新整理（複製大檔時會連續觸發很多次）
        private FileSystemWatcher? _watcher;
        private readonly DispatcherTimer _refreshTimer = new() { Interval = TimeSpan.FromMilliseconds(400) };

        private const string InternalDragFormat = "DocumentKeeper.InternalDrag";
        private Point? _dragStart;
        private object? _dragItem;
        private bool _busy;

        public MainWindow()
        {
            InitializeComponent();
            // 小螢幕（例如筆電）上不要讓視窗超出可用範圍
            Height = Math.Min(Height, SystemParameters.WorkArea.Height - 20);
            Width = Math.Min(Width, SystemParameters.WorkArea.Width - 20);

            _settings = DataStore.Load();
            // 從 APP 啟動器選了資料夾開啟時讀那個資料夾（不改設定）；否則用設定裡的存放位置
            _root = Program.StartupRoot ?? DataStore.RootPathOf(_settings);
            _selectedFolder = _root;
            _refreshTimer.Tick += (_, _) => { _refreshTimer.Stop(); RefreshAll(); };
            if (FromLauncher)
            {
                RootLabel.Text = "存放位置（從啟動器選擇）";
                ChangeRootButton.Visibility = Visibility.Collapsed;
            }
            SortBox.SelectedIndex = 0;

            UpdateStatus();
            ThemeService.ThemeChanged += OnThemeChanged; // 切換主題（或系統深淺色改變）時更新標題列與按鈕
            UpdateThemeButton();
        }

        // ---------- 主題：跟隨系統 / 淺色 / 深色 ----------

        private void Theme_Click(object sender, RoutedEventArgs e)
        {
            ThemeService.Cycle();
            StatusText.Text = $"主題：{ThemeName(ThemeService.Mode)}";
        }

        private static string ThemeName(AppTheme mode) => mode switch
        {
            AppTheme.Light => "淺色",
            AppTheme.Dark => "深色",
            _ => "跟隨系統",
        };

        private void OnThemeChanged()
        {
            UpdateThemeButton();
            WindowTheme.ApplyTitleBar(this);
        }

        private void UpdateThemeButton()
        {
            ThemeIcon.Text = ThemeService.Mode switch
            {
                AppTheme.Light => "\uE706", // 太陽
                AppTheme.Dark => "\uE708",  // 月亮
                _ => "\uE770",              // 電腦（跟隨系統）
            };
            ThemeButton.ToolTip = $"主題：{ThemeName(ThemeService.Mode)}（按一下切換）";
        }

        // ---------- Windows 11：標題列底色與視窗背景同色，看起來是一整片 ----------

        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);
            WindowTheme.ApplyTitleBar(this);
        }

        // ---------- 啟動 / 關閉 ----------

        private async void Window_Loaded(object sender, RoutedEventArgs e)
        {
            CleanupService.RunInBackground(); // 清掉更新後遺留的舊檔
            DesktopShortcutService.TidyUp(_updater.IsInstalled);     // 更新後：重複捷徑只留最新的、工作列釘選改指向新版
            DesktopShortcutService.EnsureOnce(_updater.IsInstalled); // 安裝版第一次開啟時補上桌面捷徑

            EnsureRoot();
            StartWatcher();
            _ready = true;
            RefreshAll();

            await CheckForUpdateAsync(manual: false);
        }

        private void Window_Closed(object? sender, EventArgs e) => _watcher?.Dispose();

        // 從別的程式切回來時順便重新整理（監看不到的網路磁碟也能更新）
        private void Window_Activated(object? sender, EventArgs e)
        {
            if (_ready) ScheduleRefresh();
        }

        // ---------- 更新 / 捷徑 ----------

        private void Shortcut_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (DesktopShortcutService.Create(_updater.IsInstalled) == ShortcutResult.SourceNotFound)
                {
                    MessageBox.Show(this, "找不到開始功能表裡的「文件整理」捷徑，請重新執行「安裝.cmd」。", "桌面捷徑",
                        MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
                StatusText.Text = "已在桌面建立捷徑";
                MessageBox.Show(this, "已在桌面建立捷徑。", "桌面捷徑", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or COMException)
            {
                MessageBox.Show(this, $"建立捷徑失敗：{ex.Message}", "桌面捷徑", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private async void CheckUpdate_Click(object sender, RoutedEventArgs e) => await CheckForUpdateAsync(manual: true);

        private async Task CheckForUpdateAsync(bool manual)
        {
            if (!_updater.IsRelease)
            {
                if (manual)
                    MessageBox.Show(this, "目前是開發版（不是從安裝檔或 APP 啟動器開啟的），無法線上更新。", "檢查更新");
                return;
            }

            try
            {
                var info = await _updater.CheckAsync();
                if (info == null)
                {
                    if (manual) MessageBox.Show(this, $"目前已是最新版（v{_updater.CurrentVersion}）。", "檢查更新");
                    return;
                }

                // 從 APP 啟動器開的：新版由啟動器下載，這裡只提醒（啟動時的自動檢查不打擾，啟動器自己會標示可更新）
                if (!_updater.IsInstalled)
                {
                    if (manual)
                        MessageBox.Show(this, $"發現新版本 {info.Version}（目前 {_updater.CurrentVersion}）。\n\n請到「APP 啟動器」按「更新到 v{info.Version}」，更新後從啟動器開啟新版。",
                            "有新版本", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }

                var answer = MessageBox.Show(this,
                    $"有新版本 v{info.Version}（目前 v{_updater.CurrentVersion}）。\n要現在下載並更新嗎？",
                    "發現新版本", MessageBoxButton.YesNo, MessageBoxImage.Question);
                if (answer != MessageBoxResult.Yes) return;

                StatusText.Text = "正在下載更新…";
                await _updater.DownloadAndLaunchAsync(info, p => Dispatcher.Invoke(() => StatusText.Text = $"正在下載更新… {p}%"));
                // 安裝程式已啟動，結束本程式讓它能覆蓋檔案；安裝完成後會自動重新開啟
                Application.Current.Shutdown();
            }
            catch (Exception ex)
            {
                UpdateStatus();
                // 啟動時的自動檢查失敗（例如沒網路）不打擾使用者
                if (manual) MessageBox.Show(this, $"檢查更新失敗：{ex.Message}", "檢查更新", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private void UpdateStatus()
        {
            // 視窗標題列顯示版本：安裝版或從啟動器開啟為「文件整理 v1.0.0」，直接從原始碼執行則標示開發版
            Title = (_updater.IsRelease ? $"文件整理 v{_updater.CurrentVersion}" : "文件整理（開發版）")
                + (FromLauncher ? $" － {RootName}" : "");
            StatusText.Text = $"版本 {_updater.CurrentVersion}";
        }

        // ---------- 存放位置 ----------

        /// <summary>這次是從 APP 啟動器選了資料夾開啟的：存放位置由啟動器決定，程式裡不能改。</summary>
        private static bool FromLauncher => Program.StartupRoot != null;

        private string RootName => Path.GetFileName(Path.TrimEndingDirectorySeparator(_root)) is { Length: > 0 } name ? name : _root;

        private static bool TryPrepareFolder(string path)
        {
            try
            {
                Directory.CreateDirectory(path);
                return true;
            }
            catch (Exception ex) when (IsFileError(ex)) { return false; }
        }

        /// <summary>確認存放位置存在；隨身碟或網路磁碟沒接上時，這次先用預設位置（不改設定，下次開啟仍先試原本的位置）。</summary>
        private void EnsureRoot()
        {
            if (TryPrepareFolder(_root)) return;
            var fallback = DataStore.DefaultRootPath;
            MessageBox.Show(this, FromLauncher
                ? $"找不到 APP 啟動器選的資料夾：\n{_root}\n\n可能是 NAS 沒開機或網路沒連上。這次先改用預設位置：\n{fallback}\n\n要讀取別的資料夾，請關閉文件整理，再從啟動器重新選擇。"
                : $"找不到存放位置：\n{_root}\n\n可能是隨身碟或網路磁碟沒有接上。這次先改用預設位置：\n{fallback}\n\n接上之後按「存放位置」就能改回來。",
                "存放位置", MessageBoxButton.OK, MessageBoxImage.Warning);
            _root = fallback;
            _selectedFolder = fallback;
            TryPrepareFolder(fallback);
        }

        private void StartWatcher()
        {
            _watcher?.Dispose();
            _watcher = null;
            try
            {
                var w = new FileSystemWatcher(_root)
                {
                    IncludeSubdirectories = true,
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite
                                 | NotifyFilters.Size | NotifyFilters.Attributes,
                };
                w.Created += (_, _) => ScheduleRefresh();
                w.Deleted += (_, _) => ScheduleRefresh();
                w.Changed += (_, _) => ScheduleRefresh();
                w.Renamed += (_, _) => ScheduleRefresh();
                w.Error += (_, _) => ScheduleRefresh();
                w.EnableRaisingEvents = true;
                _watcher = w;
            }
            catch (Exception ex) when (IsFileError(ex)) { /* 監看不了就靠切回視窗時重新整理 */ }
        }

        private void ScheduleRefresh() => Dispatcher.BeginInvoke(() =>
        {
            _refreshTimer.Stop();
            _refreshTimer.Start();
        });

        private void OpenRoot_Click(object sender, RoutedEventArgs e) => OpenInExplorer(_root);

        private async void ChangeRoot_Click(object sender, RoutedEventArgs e)
        {
            if (_busy) return;
            if (FromLauncher)
            {
                MessageBox.Show(this, $"這次是從 APP 啟動器選了資料夾開啟的：\n{_root}\n\n要換資料夾，請關閉文件整理，再從啟動器按「開啟」重新選擇。",
                    "存放位置", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            var dlg = new OpenFolderDialog
            {
                Title = "選擇資料夾和文件的存放位置",
                InitialDirectory = Directory.Exists(_root) ? Path.GetDirectoryName(_root) ?? _root : DataStore.DefaultRootPath,
            };
            if (dlg.ShowDialog(this) != true) return;
            var newRoot = Path.TrimEndingDirectorySeparator(dlg.FolderName);

            if (FolderService.SamePath(newRoot, _root))
            {
                StatusText.Text = "存放位置沒有變更";
                return;
            }
            string? problem =
                Path.GetPathRoot(newRoot) is { } drive && FolderService.SamePath(drive, newRoot) ? "請選擇一個資料夾，不要直接選整個磁碟。"
                : FolderService.IsSameOrInside(newRoot, _root) ? "新的位置不能在目前的存放位置裡面。"
                : FolderService.IsSameOrInside(_root, newRoot) ? "新的位置不能是目前存放位置的上層資料夾。"
                : null;
            if (problem != null)
            {
                MessageBox.Show(this, problem, "存放位置", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            bool hasContent = false;
            try { hasContent = Directory.Exists(_root) && Directory.EnumerateFileSystemEntries(_root).Any(); }
            catch (Exception ex) when (IsFileError(ex)) { }

            if (hasContent)
            {
                var answer = MessageBox.Show(this,
                    $"要把目前存放位置裡的資料夾和文件一起搬到新位置嗎？\n\n目前：{_root}\n新位置：{newRoot}\n\n" +
                    "是：全部搬過去\n否：不搬，直接改用新位置（原本的檔案留在原處）\n取消：不變更",
                    "變更存放位置", MessageBoxButton.YesNoCancel, MessageBoxImage.Question, MessageBoxResult.Cancel);
                if (answer == MessageBoxResult.Cancel) return;
                if (answer == MessageBoxResult.Yes && !await MoveRootContentsAsync(_root, newRoot)) return;
            }

            if (!TryPrepareFolder(newRoot))
            {
                MessageBox.Show(this, $"無法使用這個位置：\n{newRoot}", "存放位置", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }
            _settings.RootPath = newRoot;
            try { DataStore.Save(_settings); }
            catch (Exception ex) when (IsFileError(ex))
            {
                MessageBox.Show(this, $"儲存設定失敗，下次開啟會回到原本的位置：{ex.Message}", "存放位置", MessageBoxButton.OK, MessageBoxImage.Warning);
            }

            _root = newRoot;
            _selectedFolder = newRoot;
            _expanded.Clear();
            SearchBox.Clear();
            StartWatcher();
            RefreshAll();
            StatusText.Text = $"存放位置已改為 {newRoot}";
        }

        /// <summary>把舊位置的東西全部搬到新位置；失敗時告訴使用者哪些已經搬過去，並回傳 false（不切換位置）。</summary>
        private async Task<bool> MoveRootContentsAsync(string from, string to)
        {
            _busy = true;
            IsEnabled = false;
            StatusText.Text = "正在搬移到新位置…";
            _watcher?.Dispose();
            _watcher = null;
            try
            {
                int count = await Task.Run(() => FolderService.MoveContents(from, to));
                // 舊位置搬空了就順便刪掉，免得留下一個空資料夾
                try { if (!Directory.EnumerateFileSystemEntries(from).Any()) Directory.Delete(from); }
                catch (Exception ex) when (IsFileError(ex)) { }
                StatusText.Text = $"已搬移 {count} 個項目";
                return true;
            }
            catch (Exception ex) when (IsFileError(ex) || ex is InvalidOperationException)
            {
                MessageBox.Show(this,
                    $"搬移時發生錯誤：{ex.Message}\n\n已經搬過去的檔案在新位置，其餘的還在原本的位置。存放位置沒有變更。",
                    "變更存放位置", MessageBoxButton.OK, MessageBoxImage.Error);
                StartWatcher();
                RefreshAll();
                return false;
            }
            finally
            {
                IsEnabled = true;
                _busy = false;
            }
        }

        // ---------- 重新整理 ----------

        private void RefreshAll()
        {
            if (!_ready) return;
            if (!Directory.Exists(_root)) TryPrepareFolder(_root);
            _selectedFolder = NearestExisting(_selectedFolder);
            RebuildFolders();
            RefreshFiles();
            CountText.Text = $"{_folderTotal} 個資料夾 · {_fileTotal} 份文件";
        }

        /// <summary>資料夾被刪掉或搬走時，退到還存在的上層資料夾。</summary>
        private string NearestExisting(string path)
        {
            for (var d = path; d != null && FolderService.IsSameOrInside(d, _root); d = Path.GetDirectoryName(d))
                if (Directory.Exists(d)) return d;
            return _root;
        }

        private bool IsRootPath(string path) => FolderService.SamePath(path, _root);

        /// <summary>重建左側資料夾樹（只列出展開中的層級），並選到目前的資料夾。</summary>
        private void RebuildFolders()
        {
            // 選到的資料夾的上層都要展開，才看得到它
            for (var d = Path.GetDirectoryName(_selectedFolder); d != null && !IsRootPath(d) && FolderService.IsSameOrInside(d, _root); d = Path.GetDirectoryName(d))
                _expanded.Add(d);

            var nodes = new List<FolderNode>();
            _folderTotal = 0;
            _fileTotal = Walk(_root, 0, visible: true, nodes);
            RootPathText.Text = _root;
            RootPathText.ToolTip = FromLauncher ? $"{_root}\n（由 APP 啟動器選擇，要換資料夾請從啟動器重新開啟）" : _root;

            var signature = string.Join("|", nodes.Select(n => $"{n.Path}:{n.Count}:{n.HasChildren}:{n.IsExpanded}"));
            _rebuildingFolders = true;
            if (signature != _folderSignature)
            {
                _folderSignature = signature;
                FolderList.ItemsSource = nodes;
            }
            var list = (List<FolderNode>)FolderList.ItemsSource;
            FolderList.SelectedItem = list.FirstOrDefault(n => FolderService.SamePath(n.Path, _selectedFolder)) ?? list[0];
            _rebuildingFolders = false;
        }

        /// <summary>走過整個資料夾樹，回傳這個資料夾（含子資料夾）的文件數；visible 的才放進清單。</summary>
        private int Walk(string dir, int level, bool visible, List<FolderNode> nodes)
        {
            var subs = FolderService.SubFolders(dir);
            bool expanded = level == 0 || _expanded.Contains(dir);
            int insertAt = nodes.Count;
            int total = CountFiles(dir);
            foreach (var sub in subs)
            {
                _folderTotal++;
                total += Walk(sub.FullName, level + 1, visible && expanded, nodes);
            }
            if (visible)
                nodes.Insert(insertAt, new FolderNode
                {
                    Path = dir,
                    Name = level == 0 ? "全部文件" : Path.GetFileName(dir),
                    Level = level,
                    Count = total,
                    HasChildren = subs.Count > 0,
                    IsExpanded = expanded,
                });
            return total;
        }

        private static int CountFiles(string dir)
        {
            try { return new DirectoryInfo(dir).EnumerateFiles().Count(FolderService.IsVisible); }
            catch (Exception ex) when (IsFileError(ex)) { return 0; }
        }

        private static int CountFolders(string dir) =>
            FolderService.SubFolders(dir).Sum(d => 1 + CountFolders(d.FullName));

        // ---------- 左側資料夾樹 ----------

        private void FolderList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_rebuildingFolders || FolderList.SelectedItem is not FolderNode node) return;
            _selectedFolder = node.Path;
            RefreshFiles();
        }

        private void SelectFolder(string path)
        {
            _selectedFolder = path;
            RebuildFolders();
            RefreshFiles();
        }

        private void ToggleExpanded(string path)
        {
            if (!_expanded.Remove(path)) _expanded.Add(path);
            RebuildFolders();
        }

        private void Toggle_Click(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement { DataContext: FolderNode node }) ToggleExpanded(node.Path);
        }

        private void FolderList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (FindAncestor<ButtonBase>(e.OriginalSource as DependencyObject) != null) return; // 雙擊在箭頭或按鈕上
            if (FolderList.SelectedItem is FolderNode { IsFolder: true, HasChildren: true } node) ToggleExpanded(node.Path);
        }

        private void NewFolder_Click(object sender, RoutedEventArgs e) => CreateFolderIn(_selectedFolder);

        private void AddChild_Click(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement { DataContext: FolderNode node }) CreateFolderIn(node.Path);
        }

        private void RenameFolder_Click(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement { DataContext: FolderNode { IsFolder: true } node }) RenameItem(node.Path);
        }

        private void DeleteFolder_Click(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement { DataContext: FolderNode { IsFolder: true } node }) DeleteItems([node.Path]);
        }

        private string FolderLabel(string path) => IsRootPath(path) ? "全部文件（最上層）" : Path.GetFileName(path);

        private void CreateFolderIn(string parent)
        {
            var suggested = Path.GetFileName(FolderService.UniquePath(parent, "新資料夾", isFolder: true));
            var prompt = IsRootPath(parent) ? "資料夾名稱（放在最上層）" : $"資料夾名稱（放在「{Path.GetFileName(parent)}」裡）";
            var name = InputDialog.Ask(this, "新增資料夾", prompt, suggested,
                n => FolderService.Message(FolderService.Validate(n, parent)));
            if (name == null) return;
            try
            {
                var path = FolderService.CreateFolder(parent, name);
                if (!IsRootPath(parent)) _expanded.Add(parent);
                SelectFolder(path);
                RefreshAll();
                StatusText.Text = $"已新增資料夾「{name}」";
            }
            catch (Exception ex) when (IsFileError(ex)) { ShowError("新增資料夾失敗", ex); }
        }

        // 資料夾右鍵：新增子資料夾 / 重新命名 / 在檔案總管開啟 / 刪除；「全部文件」：新增資料夾 / 開啟 / 變更存放位置
        private void FolderList_ContextMenuOpening(object sender, ContextMenuEventArgs e)
        {
            var node = ItemFromSource<FolderNode>(e.OriginalSource as DependencyObject);
            var menu = FolderList.ContextMenu;
            menu.Items.Clear();
            if (node is not { IsFolder: true })
            {
                menu.Items.Add(MenuEntry("新增資料夾", () => CreateFolderIn(_root)));
                menu.Items.Add(MenuEntry("在檔案總管開啟", () => OpenInExplorer(_root)));
                menu.Items.Add(new Separator());
                menu.Items.Add(MenuEntry("變更存放位置…", () => ChangeRoot_Click(this, new RoutedEventArgs())));
                return;
            }
            menu.Items.Add(MenuEntry("新增子資料夾", () => CreateFolderIn(node.Path)));
            menu.Items.Add(MenuEntry("重新命名", () => RenameItem(node.Path)));
            menu.Items.Add(MenuEntry("在檔案總管開啟", () => OpenInExplorer(node.Path)));
            menu.Items.Add(new Separator());
            menu.Items.Add(MenuEntry("刪除", () => DeleteItems([node.Path]), (Brush)FindResource("DangerBrush")));
        }

        // ---------- 中間清單：搜尋 / 排序 / 選取 ----------

        private void SearchBox_TextChanged(object sender, TextChangedEventArgs e) => RefreshFiles();

        private void SortBox_SelectionChanged(object sender, SelectionChangedEventArgs e) => RefreshFiles();

        /// <summary>
        /// 一般資料夾：先列子資料夾，再列裡面的文件。「全部文件」或有搜尋文字時：列出底下所有子資料夾裡的文件，
        /// 並標示每份文件在哪個資料夾。
        /// </summary>
        private void RefreshFiles()
        {
            if (!_ready) return;
            var keyword = SearchBox.Text.Trim();
            bool searching = keyword.Length > 0;
            bool allFiles = IsRootPath(_selectedFolder);
            bool recursive = allFiles || searching;

            var folders = searching
                ? AllFolders(_selectedFolder).Where(d => d.Name.Contains(keyword, StringComparison.OrdinalIgnoreCase))
                : allFiles ? [] : FolderService.SubFolders(_selectedFolder);
            var files = FolderService.Files(_selectedFolder, recursive)
                .Where(f => !searching || f.Name.Contains(keyword, StringComparison.OrdinalIgnoreCase));

            var rows = Sorted(folders.Select(d => FolderRow(d, recursive)).Concat(files.Select(f => FileRowOf(f, recursive)))).ToList();

            var selected = _pendingSelection ?? SelectedRows().Select(r => r.Path).ToList();
            _pendingSelection = null;
            var signature = string.Join("|", rows.Select(r => $"{r.Path}:{r.Size}:{r.Modified.Ticks}:{r.FileCount}"));
            if (signature != _fileSignature)
            {
                _fileSignature = signature;
                FileList.ItemsSource = rows;
                FileList.SelectedItems.Clear();
                foreach (var row in rows.Where(r => selected.Any(p => FolderService.SamePath(p, r.Path))))
                    FileList.SelectedItems.Add(row);
                if (FileList.SelectedItem != null) FileList.ScrollIntoView(FileList.SelectedItem);
            }

            int fileCount = rows.Count(r => !r.IsFolder), folderCount = rows.Count - fileCount;
            LocationText.Text = allFiles ? "全部文件" : FolderService.RelativeDisplay(_root, _selectedFolder);
            ListCountText.Text = searching ? $"   找到 {rows.Count} 項"
                : folderCount > 0 ? $"   {folderCount} 個資料夾、{fileCount} 份文件" : $"   {fileCount} 份文件";
            EmptyText.Text = searching ? $"找不到檔名包含「{keyword}」的文件"
                : allFiles && _folderTotal == 0 ? "還沒有任何文件\n先在左邊新增資料夾，再把檔案拖進來"
                : allFiles ? "資料夾裡還沒有文件\n選一個資料夾，按「加入文件」或把檔案拖進來"
                : "這個資料夾是空的\n按「加入文件」或把檔案拖進來";
            EmptyIcon.Text = searching ? "\uE721" : "\uE898";
            UpdateDetails();
        }

        private static List<DirectoryInfo> AllFolders(string dir)
        {
            var result = new List<DirectoryInfo>();
            foreach (var sub in FolderService.SubFolders(dir))
            {
                result.Add(sub);
                result.AddRange(AllFolders(sub.FullName));
            }
            return result;
        }

        private FileRow FolderRow(DirectoryInfo d, bool showFolder) => new()
        {
            Path = d.FullName,
            Name = d.Name,
            IsFolder = true,
            Modified = d.LastWriteTime,
            Created = d.CreationTime,
            FileCount = FolderService.Files(d.FullName, recursive: true).Count,
            FolderText = showFolder ? FolderService.RelativeDisplay(_root, d.Parent!.FullName) : "",
        };

        private FileRow FileRowOf(FileInfo f, bool showFolder) => new()
        {
            Path = f.FullName,
            Name = f.Name,
            Size = f.Length,
            Modified = f.LastWriteTime,
            Created = f.CreationTime,
            FolderText = showFolder ? FolderService.RelativeDisplay(_root, f.DirectoryName!) : "",
        };

        /// <summary>資料夾一律排在前面（依名稱）；文件依下拉選單選的方式排序。</summary>
        private IEnumerable<FileRow> Sorted(IEnumerable<FileRow> rows)
        {
            var list = rows.ToList();
            var folders = list.Where(r => r.IsFolder).OrderBy(r => r.Name, NaturalComparer.Instance);
            var files = list.Where(r => !r.IsFolder);
            files = SortBox.SelectedIndex switch
            {
                1 => files.OrderByDescending(r => r.Modified),
                2 => files.OrderByDescending(r => r.Size),
                3 => files.OrderBy(r => r.TypeLabel, StringComparer.CurrentCulture).ThenBy(r => r.Name, NaturalComparer.Instance),
                _ => files.OrderBy(r => r.Name, NaturalComparer.Instance),
            };
            return folders.Concat(files);
        }

        private List<FileRow> SelectedRows() => FileList.SelectedItems.Cast<FileRow>().ToList();

        private void FileList_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateDetails();

        private void FileList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (ItemFromSource<FileRow>(e.OriginalSource as DependencyObject) is { } row) OpenRow(row);
        }

        private void FileList_KeyDown(object sender, KeyEventArgs e)
        {
            var rows = SelectedRows();
            if (rows.Count == 0) return;
            switch (e.Key)
            {
                case Key.Enter when rows.Count == 1: OpenRow(rows[0]); break;
                case Key.F2 when rows.Count == 1: RenameItem(rows[0].Path); break;
                case Key.Delete: DeleteItems(rows.Select(r => r.Path).ToList()); break;
                default: return;
            }
            e.Handled = true;
        }

        // 文件右鍵：開啟 / 在檔案總管顯示 / 重新命名 / 移到資料夾 / 刪除；空白處：加入文件 / 新增資料夾 / 開啟資料夾
        private void FileList_ContextMenuOpening(object sender, ContextMenuEventArgs e)
        {
            var menu = FileList.ContextMenu;
            menu.Items.Clear();
            if (ItemFromSource<FileRow>(e.OriginalSource as DependencyObject) is not { } row)
            {
                FileList.SelectedItems.Clear();
                menu.Items.Add(MenuEntry("加入文件…", () => AddFiles_Click(this, new RoutedEventArgs())));
                menu.Items.Add(MenuEntry("新增資料夾", () => CreateFolderIn(_selectedFolder)));
                menu.Items.Add(MenuEntry("在檔案總管開啟", () => OpenInExplorer(_selectedFolder)));
                return;
            }
            if (!FileList.SelectedItems.Contains(row))
            {
                FileList.SelectedItems.Clear();
                FileList.SelectedItems.Add(row);
            }
            var rows = SelectedRows();
            if (rows.Count == 1)
            {
                menu.Items.Add(MenuEntry(row.IsFolder ? "開啟資料夾" : "開啟", () => OpenRow(row)));
                menu.Items.Add(MenuEntry("在檔案總管顯示", () => Reveal(row.Path)));
                menu.Items.Add(MenuEntry("重新命名", () => RenameItem(row.Path)));
            }
            menu.Items.Add(MenuEntry("移到資料夾…", () => ShowMoveMenu(rows, null)));
            menu.Items.Add(new Separator());
            menu.Items.Add(MenuEntry(rows.Count == 1 ? "刪除" : $"刪除這 {rows.Count} 個項目",
                () => DeleteItems(rows.Select(r => r.Path).ToList()), (Brush)FindResource("DangerBrush")));
        }

        // ---------- 右側資訊 ----------

        private void UpdateDetails()
        {
            var rows = SelectedRows();
            NoSelectionPanel.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            DetailPanel.Visibility = rows.Count == 1 ? Visibility.Visible : Visibility.Collapsed;
            MultiPanel.Visibility = rows.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
            ActionPanel.Visibility = rows.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            OpenButton.Visibility = rows.Count == 1 ? Visibility.Visible : Visibility.Collapsed; // 主要按鈕沒有停用的樣子，多選時直接藏起來
            RevealButton.IsEnabled = RenameButton.IsEnabled = rows.Count == 1;

            if (rows.Count == 1)
            {
                var r = rows[0];
                DetailTile.Background = r.TileBrush;
                DetailGlyph.Text = r.Glyph;
                DetailName.Text = r.Name;
                DetailType.Text = r.TypeLabel;
                var parent = Path.GetDirectoryName(r.Path)!;
                DetailFolder.Text = IsRootPath(parent) ? "最上層（全部文件）" : FolderService.RelativeDisplay(_root, parent);
                DetailFolder.ToolTip = parent;
                DetailSizeLabel.Text = r.IsFolder ? "內容" : "大小";
                DetailSize.Text = r.IsFolder
                    ? $"{r.FileCount} 份文件、{CountFolders(r.Path)} 個子資料夾"
                    : $"{FileTypes.FormatSize(r.Size)}（{r.Size:N0} 位元組）";
                DetailModified.Text = r.ModifiedText;
                DetailCreated.Text = r.CreatedText;
                OpenButtonText.Text = r.IsFolder ? "開啟資料夾" : "開啟";
            }
            else if (rows.Count > 1)
            {
                var files = rows.Where(r => !r.IsFolder).ToList();
                int folders = rows.Count - files.Count;
                MultiText.Text = $"已選取 {rows.Count} 個項目";
                MultiSizeText.Text = (folders > 0 ? $"{folders} 個資料夾、" : "") +
                    $"{files.Count} 份文件，共 {FileTypes.FormatSize(files.Sum(f => f.Size))}";
            }
        }

        // ---------- 開啟 / 顯示 ----------

        private void Open_Click(object sender, RoutedEventArgs e)
        {
            if (SelectedRows() is [var row]) OpenRow(row);
        }

        private void OpenRow(FileRow row)
        {
            if (row.IsFolder)
            {
                SearchBox.Clear();
                SelectFolder(row.Path);
                return;
            }
            try
            {
                Process.Start(new ProcessStartInfo(row.Path) { UseShellExecute = true });
                StatusText.Text = $"已開啟「{row.Name}」";
            }
            catch (Exception ex) when (ex is Win32Exception or IOException)
            {
                MessageBox.Show(this, $"無法開啟「{row.Name}」：{ex.Message}\n\n可能是電腦上沒有可以開啟這種檔案的程式。",
                    "開啟", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private void Reveal_Click(object sender, RoutedEventArgs e)
        {
            if (SelectedRows() is [var row]) Reveal(row.Path);
        }

        private static void Reveal(string path) => StartExplorer($"/select,\"{path}\"");

        private void OpenCurrentFolder_Click(object sender, RoutedEventArgs e) => OpenInExplorer(_selectedFolder);

        private static void OpenInExplorer(string dir) => StartExplorer($"\"{dir}\"");

        private static void StartExplorer(string args)
        {
            try { Process.Start(new ProcessStartInfo("explorer.exe", args) { UseShellExecute = true }); }
            catch (Win32Exception) { }
        }

        // ---------- 重新命名 ----------

        private void Rename_Click(object sender, RoutedEventArgs e)
        {
            if (SelectedRows() is [var row]) RenameItem(row.Path);
        }

        private void RenameItem(string path)
        {
            bool isFolder = Directory.Exists(path);
            var parent = Path.GetDirectoryName(path)!;
            var oldName = Path.GetFileName(path);
            var name = InputDialog.Ask(this, isFolder ? "重新命名資料夾" : "重新命名文件", isFolder ? "資料夾名稱" : "檔案名稱", oldName,
                n => FolderService.Message(FolderService.Validate(n, parent, path)),
                selectLength: isFolder ? null : Path.GetFileNameWithoutExtension(oldName).Length);
            if (name == null || name == oldName) return;

            var oldExt = Path.GetExtension(oldName);
            if (!isFolder && !string.Equals(oldExt, Path.GetExtension(name), StringComparison.OrdinalIgnoreCase))
            {
                var ok = MessageBox.Show(this, $"副檔名從「{oldExt}」改成「{Path.GetExtension(name)}」，檔案可能會打不開。\n\n確定要改嗎？",
                    "重新命名", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
                if (ok != MessageBoxResult.Yes) return;
            }

            try
            {
                var newPath = FolderService.Rename(path, name);
                if (isFolder) RebaseState(path, newPath);
                _pendingSelection = [newPath];
                RefreshAll();
                StatusText.Text = $"已改名為「{name}」";
            }
            catch (Exception ex) when (IsFileError(ex)) { ShowError("重新命名失敗", ex, "檔案可能正在被其他程式使用，請先關閉再試。"); }
        }

        /// <summary>資料夾改名或搬走後，記住的選取與展開狀態改到新的位置。</summary>
        private void RebaseState(string oldPath, string newPath)
        {
            _selectedFolder = FolderService.Rebase(_selectedFolder, oldPath, newPath);
            var expanded = _expanded.Select(p => FolderService.Rebase(p, oldPath, newPath)).ToList();
            _expanded.Clear();
            _expanded.UnionWith(expanded);
        }

        // ---------- 刪除（丟到資源回收筒） ----------

        private void Delete_Click(object sender, RoutedEventArgs e) => DeleteItems(SelectedRows().Select(r => r.Path).ToList());

        private void DeleteItems(List<string> paths)
        {
            if (paths.Count == 0) return;
            string message;
            if (paths.Count == 1)
            {
                var p = paths[0];
                var name = Path.GetFileName(p);
                if (Directory.Exists(p))
                {
                    int files = FolderService.Files(p, recursive: true).Count, folders = CountFolders(p);
                    message = files + folders == 0
                        ? $"要刪除資料夾「{name}」嗎？"
                        : $"要刪除資料夾「{name}」嗎？\n\n裡面的 {files} 份文件和 {folders} 個子資料夾會一起刪除。";
                }
                else message = $"要刪除「{name}」嗎？";
            }
            else message = $"要刪除選取的 {paths.Count} 個項目嗎？";
            message += "\n\n刪除的東西會放到資源回收筒，需要時可以還原。";

            var ok = MessageBox.Show(this, message, "刪除", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
            if (ok != MessageBoxResult.Yes) return;

            var errors = new List<string>();
            int deleted = 0;
            foreach (var p in paths)
            {
                try
                {
                    RecycleBin.Send(p);
                    deleted++;
                }
                catch (Exception ex) when (IsFileError(ex)) { errors.Add($"{Path.GetFileName(p)}：{ex.Message}"); }
            }
            RefreshAll();
            StatusText.Text = $"已刪除 {deleted} 個項目（可從資源回收筒還原）";
            if (errors.Count > 0)
                MessageBox.Show(this, "下列項目沒有刪除：\n\n" + string.Join("\n", errors), "刪除", MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        // ---------- 搬移 ----------

        private void Move_Click(object sender, RoutedEventArgs e) => ShowMoveMenu(SelectedRows(), MoveButton);

        /// <summary>列出所有資料夾讓使用者選要搬去哪裡；placementTarget 為 null 時在滑鼠位置開啟。</summary>
        private void ShowMoveMenu(List<FileRow> rows, UIElement? placementTarget)
        {
            if (rows.Count == 0) return;
            var paths = rows.Select(r => r.Path).ToList();
            var parents = paths.Select(p => Path.GetDirectoryName(p)!).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

            var menu = new ContextMenu();
            menu.Items.Add(new MenuItem { Header = "移到資料夾", IsEnabled = false, FontSize = 12 });
            void Add(string dir, int level)
            {
                var label = new string(' ', Math.Max(0, level - 1) * 4) + (level == 0 ? "全部文件（最上層）" : Path.GetFileName(dir));
                var item = MenuEntry(label, () => MoveItems(paths, dir));
                item.IsChecked = parents.Count == 1 && FolderService.SamePath(parents[0], dir);
                // 不能搬進自己或自己底下的資料夾
                item.IsEnabled = !item.IsChecked && !rows.Any(r => r.IsFolder && FolderService.IsSameOrInside(dir, r.Path));
                menu.Items.Add(item);
                foreach (var sub in FolderService.SubFolders(dir)) Add(sub.FullName, level + 1);
            }
            Add(_root, 0);

            if (placementTarget != null)
            {
                menu.PlacementTarget = placementTarget;
                menu.Placement = PlacementMode.Top;
            }
            else menu.Placement = PlacementMode.MousePoint;
            menu.IsOpen = true;
        }

        private void MoveItems(List<string> paths, string destDir)
        {
            var errors = new List<string>();
            var moved = new List<string>();
            foreach (var p in paths)
            {
                try
                {
                    var newPath = FolderService.MoveInto(p, destDir);
                    if (FolderService.SamePath(newPath, p)) continue;
                    if (Directory.Exists(newPath)) RebaseState(p, newPath);
                    moved.Add(newPath);
                }
                catch (Exception ex) when (IsFileError(ex) || ex is InvalidOperationException)
                {
                    errors.Add($"{Path.GetFileName(p)}：{ex.Message}");
                }
            }
            _pendingSelection = moved;
            RefreshAll();
            if (moved.Count > 0) StatusText.Text = $"已把 {moved.Count} 個項目移到「{FolderLabel(destDir)}」";
            if (errors.Count > 0)
                MessageBox.Show(this, "下列項目沒有搬移（檔案可能正在被其他程式使用）：\n\n" + string.Join("\n", errors),
                    "搬移", MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        // ---------- 加入文件（複製進來） ----------

        private async void AddFiles_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new OpenFileDialog { Title = $"選擇要加入「{FolderLabel(_selectedFolder)}」的文件", Multiselect = true };
            if (dlg.ShowDialog(this) != true) return;
            await CopyItemsAsync(dlg.FileNames, _selectedFolder);
        }

        /// <summary>把電腦上其他地方的檔案或資料夾複製進來（原本的檔案不動），撞名時自動改名。</summary>
        private async Task CopyItemsAsync(IReadOnlyList<string> paths, string destDir)
        {
            if (_busy || paths.Count == 0) return;
            _busy = true;
            StatusText.Text = $"正在複製 {paths.Count} 個項目…";
            Mouse.OverrideCursor = Cursors.Wait;
            var errors = new List<string>();
            var copied = new List<string>();
            try
            {
                await Task.Run(() =>
                {
                    foreach (var p in paths)
                    {
                        try { copied.Add(FolderService.CopyInto(p, destDir)); }
                        catch (Exception ex) when (IsFileError(ex) || ex is InvalidOperationException)
                        {
                            errors.Add($"{Path.GetFileName(p)}：{ex.Message}");
                        }
                    }
                });
            }
            finally
            {
                Mouse.OverrideCursor = null;
                _busy = false;
            }
            _pendingSelection = copied.Where(p => FolderService.SamePath(Path.GetDirectoryName(p)!, _selectedFolder) || IsRootPath(_selectedFolder)).ToList();
            RefreshAll();
            StatusText.Text = $"已複製 {copied.Count} 個項目到「{FolderLabel(destDir)}」";
            if (errors.Count > 0)
                MessageBox.Show(this, "下列項目沒有複製：\n\n" + string.Join("\n", errors), "加入文件", MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        // ---------- 拖曳 ----------
        // 從檔案總管拖進來 = 複製；在程式裡把文件 / 資料夾拖到左邊的資料夾 = 搬移；拖到檔案總管或桌面 = 複製出去。

        private void List_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            var source = e.OriginalSource as DependencyObject;
            // 按在按鈕或捲軸上不是要拖曳
            if (FindAncestor<ButtonBase>(source) != null || FindAncestor<ScrollBar>(source) != null)
            {
                _dragStart = null;
                return;
            }
            _dragStart = e.GetPosition(this);
            _dragItem = ItemFromSource<object>(source);
        }

        private bool ShouldStartDrag(MouseEventArgs e)
        {
            if (_dragStart is not { } start || e.LeftButton != MouseButtonState.Pressed || _dragItem == null) return false;
            var delta = e.GetPosition(this) - start;
            return Math.Abs(delta.X) >= SystemParameters.MinimumHorizontalDragDistance
                || Math.Abs(delta.Y) >= SystemParameters.MinimumVerticalDragDistance;
        }

        private void FileList_PreviewMouseMove(object sender, MouseEventArgs e)
        {
            if (!ShouldStartDrag(e) || _dragItem is not FileRow) return;
            StartDrag(FileList, SelectedRows().Select(r => r.Path).ToList());
        }

        private void FolderList_PreviewMouseMove(object sender, MouseEventArgs e)
        {
            if (!ShouldStartDrag(e) || _dragItem is not FolderNode { IsFolder: true } node) return;
            StartDrag(FolderList, [node.Path]);
        }

        private void StartDrag(DependencyObject source, List<string> paths)
        {
            _dragStart = null;
            _dragItem = null;
            if (paths.Count == 0) return;
            var data = new DataObject();
            data.SetData(DataFormats.FileDrop, paths.ToArray());
            data.SetData(InternalDragFormat, true);
            // 拖到檔案總管時預設「複製」，不要把文件從這裡搬走（按住 Shift 才是搬移）
            data.SetData("Preferred DropEffect", new MemoryStream(BitConverter.GetBytes((int)DragDropEffects.Copy)));
            try { DragDrop.DoDragDrop(source, data, DragDropEffects.Copy | DragDropEffects.Move); }
            finally
            {
                ClearDropTargets();
                DropHint.Visibility = Visibility.Collapsed;
            }
            ScheduleRefresh();
        }

        private static bool IsInternal(DragEventArgs e) => e.Data.GetDataPresent(InternalDragFormat);

        private static string[]? DroppedPaths(DragEventArgs e) =>
            e.Data.GetDataPresent(DataFormats.FileDrop) ? e.Data.GetData(DataFormats.FileDrop) as string[] : null;

        // 中間清單只接受從外面拖進來的檔案（複製到目前的資料夾）；程式裡的文件請拖到左邊的資料夾
        private void FileList_DragOver(object sender, DragEventArgs e)
        {
            bool accept = !_busy && !IsInternal(e) && DroppedPaths(e) is { Length: > 0 };
            e.Effects = accept ? DragDropEffects.Copy : DragDropEffects.None;
            if (accept)
            {
                DropHintText.Text = $"放開就把檔案複製到「{FolderLabel(_selectedFolder)}」";
                DropHint.Visibility = Visibility.Visible;
            }
            e.Handled = true;
        }

        private void FileList_DragLeave(object sender, DragEventArgs e) => DropHint.Visibility = Visibility.Collapsed;

        private async void FileList_Drop(object sender, DragEventArgs e)
        {
            DropHint.Visibility = Visibility.Collapsed;
            if (IsInternal(e) || DroppedPaths(e) is not { Length: > 0 } paths) return;
            e.Handled = true;
            await CopyItemsAsync(paths, _selectedFolder);
        }

        private void FolderList_DragOver(object sender, DragEventArgs e)
        {
            var node = ItemFromSource<FolderNode>(e.OriginalSource as DependencyObject);
            var paths = DroppedPaths(e);
            var effect = DragDropEffects.None;
            if (!_busy && node != null && paths is { Length: > 0 })
            {
                if (!IsInternal(e)) effect = DragDropEffects.Copy;
                else if (CanMoveInto(paths, node.Path)) effect = DragDropEffects.Move;
            }
            e.Effects = effect;
            SetDropTarget(effect == DragDropEffects.None ? null : node);
            e.Handled = true;
        }

        /// <summary>至少有一項不在目標資料夾裡，而且不是把資料夾拖進它自己底下。</summary>
        private static bool CanMoveInto(string[] paths, string destDir) =>
            paths.Any(p => !FolderService.SamePath(Path.GetDirectoryName(p)!, destDir))
            && !paths.Any(p => Directory.Exists(p) && FolderService.IsSameOrInside(destDir, p));

        private void FolderList_DragLeave(object sender, DragEventArgs e) => SetDropTarget(null);

        private async void FolderList_Drop(object sender, DragEventArgs e)
        {
            SetDropTarget(null);
            var node = ItemFromSource<FolderNode>(e.OriginalSource as DependencyObject);
            if (node == null || DroppedPaths(e) is not { Length: > 0 } paths) return;
            e.Handled = true;
            if (!IsInternal(e)) await CopyItemsAsync(paths, node.Path);
            else if (CanMoveInto(paths, node.Path)) MoveItems(paths.ToList(), node.Path);
        }

        private void SetDropTarget(FolderNode? target)
        {
            if (FolderList.ItemsSource is not List<FolderNode> nodes) return;
            foreach (var n in nodes) n.IsDropTarget = n == target;
        }

        private void ClearDropTargets() => SetDropTarget(null);

        // ---------- 共用 ----------

        private static bool IsFileError(Exception ex) =>
            ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException
                or OperationCanceledException or System.Security.SecurityException;

        private void ShowError(string title, Exception ex, string? hint = null) =>
            MessageBox.Show(this, $"{title}：{ex.Message}" + (hint != null ? $"\n\n{hint}" : ""), title,
                MessageBoxButton.OK, MessageBoxImage.Error);

        private static T? FindAncestor<T>(DependencyObject? d) where T : DependencyObject
        {
            for (; d != null; d = d is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d))
                if (d is T t) return t;
            return null;
        }

        /// <summary>從滑鼠點到的元素往上找出清單項目的資料（點在空白處則為 null）。</summary>
        private static T? ItemFromSource<T>(DependencyObject? source) where T : class =>
            FindAncestor<ListBoxItem>(source)?.DataContext as T;

        private static MenuItem MenuEntry(string header, Action onClick, Brush? foreground = null)
        {
            var mi = new MenuItem { Header = header };
            if (foreground != null) mi.Foreground = foreground;
            mi.Click += (_, _) => onClick();
            return mi;
        }
    }
}
