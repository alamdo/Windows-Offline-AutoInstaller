using app_tự_động.Models;
using app_tự_động.Services;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using Forms = System.Windows.Forms;

namespace app_tự_động
{
    public partial class MainWindow : Window
    {
        private const string CustomAppMarker = "__CUSTOM_APP__";

        private enum AppRunMode
        {
            Install,
            DownloadOnly,
            InstallFromFile
        }

        private readonly string _defaultDownloadFolder = Path.Combine(Path.GetTempPath(), "OneClickInstaller");
        private readonly string _catalogFilePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "apps.json");

        private string _downloadFolder;
        private string _logFilePath;

        private int _successCount = 0;
        private int _failedCount = 0;
        private int _skippedCount = 0;

        private bool _isRunning = false;
        private bool _cancelRequested = false;

        private readonly AppCatalogService _catalogService = new AppCatalogService();

        private AppInstallService _installService;
        private List<AppItem> _allApps = new List<AppItem>();
        private ObservableCollection<AppSelectionItem> _appSelections = new ObservableCollection<AppSelectionItem>();

        public MainWindow()
        {
            InitializeComponent();

            _installService = new AppInstallService(
                Log,
                SetCurrentAppProgress,
                SetStatus,
                SetCurrentTask,
                () => chkSilentInstall != null && chkSilentInstall.IsChecked == true,
                () => chkRetryDownload != null && chkRetryDownload.IsChecked == true,
                () => chkSkipIfExists != null && chkSkipIfExists.IsChecked == true
            );

            _downloadFolder = _defaultDownloadFolder;
            Directory.CreateDirectory(_downloadFolder);

            if (txtCustomFolder != null)
                txtCustomFolder.Text = _downloadFolder;

            BuildLogFilePath();
            RefreshFolderTexts();

            LoadAppCatalog();

            SetStatus("Sẵn sàng");
            SetProgress(0);
            SetCurrentAppProgress(0);
            ResetSummary();
            RefreshEnvironmentInfo();
            UpdateSelectedCount();
        }

        private void LoadAppCatalog()
        {
            try
            {
                _allApps = _catalogService.LoadFromJson(_catalogFilePath);

                _appSelections.Clear();
                foreach (var app in _allApps)
                {
                    _appSelections.Add(new AppSelectionItem
                    {
                        App = app,
                        IsSelected = true
                    });
                }

                if (itemsApps != null)
                    itemsApps.ItemsSource = _appSelections;

                Log("Đã nạp danh sách app từ: " + _catalogFilePath);
            }
            catch (Exception ex)
            {
                Log("Không nạp được apps.json: " + ex.Message);
                _allApps = new List<AppItem>();
                _appSelections.Clear();
            }
        }

        private void SaveAppCatalog()
        {
            try
            {
                _catalogService.SaveToJson(_catalogFilePath, _allApps);
                Log("Đã lưu danh sách app vào: " + _catalogFilePath);
            }
            catch (Exception ex)
            {
                Log("Không lưu được apps.json: " + ex.Message);
                throw;
            }
        }

        private string GetFolderInputText()
        {
            if (txtCustomFolder == null)
                return string.Empty;

            return txtCustomFolder.Text == null ? string.Empty : txtCustomFolder.Text.Trim();
        }

        private void btnBrowseFolder_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                using (var dialog = new Forms.FolderBrowserDialog())
                {
                    dialog.Description = "Chọn thư mục tải";
                    dialog.ShowNewFolderButton = true;

                    string currentFolder = GetFolderInputText();

                    if (!string.IsNullOrWhiteSpace(currentFolder) && Directory.Exists(currentFolder))
                    {
                        dialog.SelectedPath = currentFolder;
                    }
                    else if (Directory.Exists(_downloadFolder))
                    {
                        dialog.SelectedPath = _downloadFolder;
                    }

                    var result = dialog.ShowDialog();

                    if (result == Forms.DialogResult.OK && !string.IsNullOrWhiteSpace(dialog.SelectedPath))
                    {
                        txtCustomFolder.Text = dialog.SelectedPath;
                        SetStatus("Đã chọn thư mục tải");
                    }
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show("Không mở được hộp chọn thư mục: " + ex.Message, "Lỗi", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private List<AppSelectionItem> GetSelectedAppItems()
        {
            var result = new List<AppSelectionItem>();

            foreach (var item in _appSelections)
            {
                if (item.IsSelected && item.App != null)
                    result.Add(item);
            }

            return result;
        }

        private void ResetAppRuntimeStates()
        {
            foreach (var item in _appSelections)
            {
                item.ResetRuntimeState();
            }
        }

        private void BeginRun()
        {
            _isRunning = true;
            _cancelRequested = false;
            SetButtonsEnabled(false);
        }

        private void EndRun()
        {
            _isRunning = false;
            _cancelRequested = false;
            SetButtonsEnabled(true);
            RefreshEnvironmentInfo();
            SetCurrentAppProgress(0);
        }

        private void MarkPendingItemsAsCancelled(List<AppSelectionItem> selectedItems, int startIndex)
        {
            for (int i = startIndex; i < selectedItems.Count; i++)
            {
                var item = selectedItems[i];

                if (item.Status == AppProcessState.Ready)
                {
                    item.Status = AppProcessState.Cancelled;
                    item.Progress = 0;
                    item.Message = "Đã hủy";
                }
            }
        }

        private bool HandleCancelBeforeNextItem(List<AppSelectionItem> selectedItems, int currentIndex)
        {
            if (!_cancelRequested)
                return false;

            MarkPendingItemsAsCancelled(selectedItems, currentIndex);

            Log("Người dùng đã hủy. Dừng hàng đợi sau app hiện tại.");
            SetStatus("Đã hủy");
            SetCurrentTask("Đã hủy");
            return true;
        }

        private async void btnInstall_Click(object sender, RoutedEventArgs e)
        {
            await RunSelectedApps(AppRunMode.Install);
        }

        private async void btnDownloadOnly_Click(object sender, RoutedEventArgs e)
        {
            await RunSelectedApps(AppRunMode.DownloadOnly);
        }

        private async void btnInstallFromFile_Click(object sender, RoutedEventArgs e)
        {
            await RunSelectedApps(AppRunMode.InstallFromFile);
        }

        private async Task RunSelectedApps(AppRunMode mode)
        {
            var selectedItems = GetSelectedAppItems();
            if (selectedItems.Count == 0)
            {
                MessageBox.Show("Bạn chưa chọn phần mềm nào.", "Thông báo", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            if (RequiresInternet(mode))
            {
                if (!await _installService.HasInternet())
                {
                    MessageBox.Show("Không có internet hoặc không truy cập được nguồn tải.", "Lỗi", MessageBoxButton.OK, MessageBoxImage.Warning);
                    RefreshEnvironmentInfo();
                    return;
                }
            }

            if (!EnsureDownloadFolderReady())
                return;

            if (!ConfirmRun(mode))
                return;

            BeginRun();
            ResetSummary();
            ResetAppRuntimeStates();

            try
            {
                LogRunHeader(mode);

                int total = selectedItems.Count;
                int done = 0;

                SetProgress(0);
                SetCurrentAppProgress(0);

                for (int i = 0; i < selectedItems.Count; i++)
                {
                    if (HandleCancelBeforeNextItem(selectedItems, i))
                        break;

                    var item = selectedItems[i];

                    await ProcessSingleApp(item, mode);

                    done++;
                    SetProgress(done * 100 / total);
                    UpdateSummary();
                }

                if (!_cancelRequested)
                {
                    FinishRun(mode);
                }
            }
            catch (Exception ex)
            {
                Log("Lỗi tổng: " + ex.Message);
                SetStatus(GetErrorStatus(mode));
                SetCurrentTask(GetErrorTask(mode));
            }
            finally
            {
                EndRun();
            }
        }

        private async Task ProcessSingleApp(AppSelectionItem item, AppRunMode mode)
        {
            var app = item.App;

            Log("==================================================");
            SetStatus(GetProcessingStatus(mode, app.Name));
            SetCurrentTask(GetProcessingTask(mode, app.Name));
            SetCurrentAppProgress(0);

            item.Progress = 0;
            item.Message = "";
            item.Status = mode == AppRunMode.DownloadOnly
                ? AppProcessState.Downloading
                : AppProcessState.Checking;

            if (ShouldSkipInstalled(mode, app, item))
                return;

            if (mode == AppRunMode.DownloadOnly)
            {
                await ProcessDownloadOnly(item);
                return;
            }

            string installerPath = null;

            if (mode == AppRunMode.Install)
            {
                installerPath = await DownloadInstallerForInstall(item);
                if (string.IsNullOrWhiteSpace(installerPath))
                    return;
            }
            else if (mode == AppRunMode.InstallFromFile)
            {
                installerPath = GetExistingInstallerPath(item);
                if (string.IsNullOrWhiteSpace(installerPath))
                    return;
            }

            await InstallFromInstallerPath(item, installerPath);
        }

        private bool ShouldSkipInstalled(AppRunMode mode, AppItem app, AppSelectionItem item)
        {
            if (mode == AppRunMode.DownloadOnly)
                return false;

            if (chkSkipIfInstalled.IsChecked != true)
                return false;

            if (!_installService.IsAppInstalled(app))
                return false;

            _skippedCount++;
            item.Status = AppProcessState.Skipped;
            item.Progress = 100;
            item.Message = "Đã được cài sẵn";

            Log(app.Name + " đã được cài sẵn. Bỏ qua.");
            return true;
        }

        private async Task ProcessDownloadOnly(AppSelectionItem item)
        {
            var app = item.App;

            item.Status = AppProcessState.Downloading;
            item.Message = "Đang tải file cài đặt";

            var download = await _installService.DownloadFile(_downloadFolder, app.DownloadUrl, app.FileName, app.Name);

            if (download.Result == DownloadResult.Downloaded)
            {
                _successCount++;
                item.Status = AppProcessState.Success;
                item.Progress = 100;
                item.Message = "Tải thành công";
            }
            else if (download.Result == DownloadResult.SkippedExistingFile)
            {
                _skippedCount++;
                item.Status = AppProcessState.Skipped;
                item.Progress = 100;
                item.Message = "File đã tồn tại, bỏ qua tải";
            }
            else
            {
                _failedCount++;
                item.Status = AppProcessState.Failed;
                item.Message = "Tải thất bại";
            }
        }

        private async Task<string> DownloadInstallerForInstall(AppSelectionItem item)
        {
            var app = item.App;

            item.Status = AppProcessState.Downloading;
            item.Message = "Đang tải file cài đặt";

            var download = await _installService.DownloadFile(_downloadFolder, app.DownloadUrl, app.FileName, app.Name);

            if (download.Result == DownloadResult.Failed || string.IsNullOrWhiteSpace(download.FilePath))
            {
                _failedCount++;
                item.Status = AppProcessState.Failed;
                item.Message = "Tải thất bại";
                Log("Không tải được bộ cài " + app.Name);
                return null;
            }

            item.Progress = 100;

            if (download.Result == DownloadResult.SkippedExistingFile)
            {
                Log("Dùng file đã có sẵn để cài " + app.Name);
                item.Message = "Dùng file đã tải sẵn";
            }
            else
            {
                item.Message = "Tải xong, chuẩn bị cài";
            }

            return download.FilePath;
        }

        private string GetExistingInstallerPath(AppSelectionItem item)
        {
            var app = item.App;
            string filePath = Path.Combine(_downloadFolder, app.FileName);

            if (File.Exists(filePath))
            {
                item.Progress = 100;
                item.Message = "Đã tìm thấy file cài";
                return filePath;
            }

            _failedCount++;
            item.Status = AppProcessState.Failed;
            item.Message = "Không tìm thấy file đã tải";
            Log("Chưa có file tải sẵn cho " + app.Name + ": " + filePath);

            return null;
        }

        private async Task InstallFromInstallerPath(AppSelectionItem item, string filePath)
        {
            item.Status = AppProcessState.Installing;
            item.Progress = 100;
            item.Message = "Đang chạy file cài";

            bool ok = await _installService.RunInstaller(item.App, filePath);
            if (ok)
            {
                _successCount++;
                item.Status = AppProcessState.Success;
                item.Message = _cancelRequested
                    ? "Cài thành công (đã yêu cầu hủy hàng đợi)"
                    : "Cài thành công";
            }
            else
            {
                _failedCount++;
                item.Status = AppProcessState.Failed;
                item.Message = _cancelRequested
                    ? "Cài thất bại (sau khi đã yêu cầu hủy)"
                    : "Cài thất bại";
            }
        }

        private bool RequiresInternet(AppRunMode mode)
        {
            return mode == AppRunMode.Install || mode == AppRunMode.DownloadOnly;
        }

        private bool ConfirmRun(AppRunMode mode)
        {
            if (mode == AppRunMode.Install)
            {
                var confirm = MessageBox.Show(
                    "Bạn có chắc muốn tải và cài các phần mềm đã chọn không?",
                    "Xác nhận",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Question);

                return confirm == MessageBoxResult.Yes;
            }

            return true;
        }

        private void LogRunHeader(AppRunMode mode)
        {
            if (mode == AppRunMode.Install)
            {
                Log("Bắt đầu quá trình tải và cài đặt...");
                if (!_installService.IsRunAsAdmin())
                    Log("Cảnh báo: app chưa chạy bằng quyền Administrator. Một số installer có thể yêu cầu quyền admin.");
            }
            else if (mode == AppRunMode.DownloadOnly)
            {
                Log("Bắt đầu tải file cài...");
            }
            else
            {
                Log("Bắt đầu cài từ file đã tải...");
            }

            Log("Thư mục tải: " + _downloadFolder);
        }

        private string GetProcessingStatus(AppRunMode mode, string appName)
        {
            if (mode == AppRunMode.Install)
                return "Đang xử lý " + appName;

            if (mode == AppRunMode.DownloadOnly)
                return "Đang tải " + appName;

            return "Đang cài từ file " + appName;
        }

        private string GetProcessingTask(AppRunMode mode, string appName)
        {
            if (mode == AppRunMode.Install)
                return "Đang xử lý " + appName;

            if (mode == AppRunMode.DownloadOnly)
                return "Đang tải " + appName;

            return "Đang cài từ file " + appName;
        }

        private void FinishRun(AppRunMode mode)
        {
            Log("==================================================");

            if (mode == AppRunMode.Install)
            {
                Log("Hoàn tất toàn bộ quá trình.");
                SetStatus("Hoàn tất");
                SetCurrentTask("Hoàn tất");

                if (chkOpenFolderAfterInstall.IsChecked == true)
                    OpenFolder();
            }
            else if (mode == AppRunMode.DownloadOnly)
            {
                Log("Tải file hoàn tất.");
                SetStatus("Đã tải xong");
                SetCurrentTask("Đã tải xong");

                if (chkOpenFolderAfterDownload.IsChecked == true)
                    OpenFolder();
            }
            else
            {
                Log("Cài từ file hoàn tất.");
                SetStatus("Cài từ file xong");
                SetCurrentTask("Cài từ file xong");

                if (chkOpenFolderAfterInstall.IsChecked == true)
                    OpenFolder();
            }
        }

        private string GetErrorStatus(AppRunMode mode)
        {
            if (mode == AppRunMode.DownloadOnly)
                return "Lỗi tải file";

            if (mode == AppRunMode.InstallFromFile)
                return "Lỗi cài từ file";

            return "Có lỗi xảy ra";
        }

        private string GetErrorTask(AppRunMode mode)
        {
            if (mode == AppRunMode.DownloadOnly)
                return "Lỗi tải file";

            if (mode == AppRunMode.InstallFromFile)
                return "Lỗi cài từ file";

            return "Có lỗi";
        }

        private void btnCancel_Click(object sender, RoutedEventArgs e)
        {
            if (!_isRunning)
                return;

            _cancelRequested = true;

            SetStatus("Đang hủy...");
            SetCurrentTask("Đang hủy... sẽ dừng sau app hiện tại");
            Log("Đã nhận yêu cầu hủy. Tool sẽ dừng sau khi xử lý xong app hiện tại.");

            if (btnCancel != null)
                btnCancel.IsEnabled = false;
        }

        private void RefreshEnvironmentInfo()
        {
            txtInternet.Text = "Internet: đang kiểm tra...";
            txtAdmin.Text = "Admin: " + (_installService.IsRunAsAdmin() ? "Có" : "Không");

            Task.Run(async () =>
            {
                bool ok = await _installService.HasInternet();
                Dispatcher.Invoke(() =>
                {
                    txtInternet.Text = "Internet: " + (ok ? "Có" : "Không");
                });
            });
        }

        private void btnOpenFolder_Click(object sender, RoutedEventArgs e)
        {
            OpenFolder();
        }

        private void OpenFolder()
        {
            try
            {
                Directory.CreateDirectory(_downloadFolder);

                Process.Start(new ProcessStartInfo
                {
                    FileName = _downloadFolder,
                    UseShellExecute = true
                });

                SetStatus("Đã mở thư mục tải");
            }
            catch (Exception ex)
            {
                Log("Không mở được thư mục tải: " + ex.Message);
            }
        }

        private void btnOpenLogFile_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (!File.Exists(_logFilePath))
                {
                    MessageBox.Show("Chưa có file log.", "Thông báo", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }

                Process.Start(new ProcessStartInfo
                {
                    FileName = _logFilePath,
                    UseShellExecute = true
                });

                SetStatus("Đã mở file log");
            }
            catch (Exception ex)
            {
                Log("Không mở được file log: " + ex.Message);
            }
        }

        private void btnOpenChromeLink_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var chrome = _allApps.FirstOrDefault(x => x.Name == "Google Chrome");
                if (chrome == null || string.IsNullOrWhiteSpace(chrome.DownloadUrl))
                {
                    MessageBox.Show("Không tìm thấy link tải Chrome trong apps.json.", "Thông báo", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }

                OpenUrl(chrome.DownloadUrl);
            }
            catch (Exception ex)
            {
                Log("Không mở được link Chrome: " + ex.Message);
            }
        }

        private void btnSelectAll_Click(object sender, RoutedEventArgs e)
        {
            foreach (var item in _appSelections)
            {
                item.IsSelected = true;
            }

            if (itemsApps != null)
                itemsApps.Items.Refresh();

            UpdateSelectedCount();
            SetStatus("Đã chọn tất cả");
        }

        private void btnUnselectAll_Click(object sender, RoutedEventArgs e)
        {
            foreach (var item in _appSelections)
            {
                item.IsSelected = false;
            }

            if (itemsApps != null)
                itemsApps.Items.Refresh();

            UpdateSelectedCount();
            SetStatus("Đã bỏ chọn tất cả");
        }

        private void AppCheckChanged(object sender, RoutedEventArgs e)
        {
            UpdateSelectedCount();
        }

        private void UpdateSelectedCount()
        {
            int count = 0;

            foreach (var item in _appSelections)
            {
                if (item.IsSelected)
                    count++;
            }

            txtSelectedCount.Text = count.ToString();
        }

        private void ResetSummary()
        {
            _successCount = 0;
            _failedCount = 0;
            _skippedCount = 0;
            UpdateSummary();
        }

        private void UpdateSummary()
        {
            txtSummary.Text = "Kết quả: Thành công " + _successCount +
                              " | Thất bại " + _failedCount +
                              " | Bỏ qua " + _skippedCount;

            txtSuccessCount.Text = _successCount.ToString();
        }

        private void btnClear_Click(object sender, RoutedEventArgs e)
        {
            txtLog.Clear();
            SetStatus("Đã xóa log");
            SetProgress(0);
            SetCurrentAppProgress(0);
            ResetSummary();
            ResetAppRuntimeStates();
            SetCurrentTask("Đã xóa log");
        }

        private void btnUseDefaultFolder_Click(object sender, RoutedEventArgs e)
        {
            if (txtCustomFolder != null)
                txtCustomFolder.Text = _defaultDownloadFolder;

            SetStatus("Đã đưa về thư mục mặc định");
        }

        private void btnApplyFolder_Click(object sender, RoutedEventArgs e)
        {
            if (ApplyCustomFolder())
                MessageBox.Show("Đã áp dụng thư mục tải mới.", "Thông báo", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        private bool ApplyCustomFolder()
        {
            try
            {
                string folder = GetFolderInputText();

                if (string.IsNullOrWhiteSpace(folder))
                {
                    MessageBox.Show("Vui lòng nhập thư mục tải hợp lệ.", "Lỗi", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return false;
                }

                Directory.CreateDirectory(folder);
                _downloadFolder = folder;

                BuildLogFilePath();
                RefreshFolderTexts();
                SetStatus("Đã cập nhật thư mục tải");

                return true;
            }
            catch (Exception ex)
            {
                MessageBox.Show("Không áp dụng được thư mục tải: " + ex.Message, "Lỗi", MessageBoxButton.OK, MessageBoxImage.Error);
                return false;
            }
        }

        private bool EnsureDownloadFolderReady()
        {
            try
            {
                if (!ApplyCustomFolder())
                    return false;

                Directory.CreateDirectory(_downloadFolder);
                return true;
            }
            catch (Exception ex)
            {
                MessageBox.Show("Không tạo được thư mục tải: " + ex.Message, "Lỗi", MessageBoxButton.OK, MessageBoxImage.Error);
                return false;
            }
        }

        private void BuildLogFilePath()
        {
            _logFilePath = Path.Combine(_downloadFolder, "install_log_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".txt");
        }

        private void RefreshFolderTexts()
        {
            txtDownloadFolder.Text = "Thư mục tải: " + _downloadFolder;
            txtLogFile.Text = "Log file: " + _logFilePath;
        }

        private void OpenUrl(string url)
        {
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = url,
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                Log("Không mở được URL: " + ex.Message);
            }
        }

        private void SetButtonsEnabled(bool enabled)
        {
            btnInstall.IsEnabled = enabled;
            btnDownloadOnly.IsEnabled = enabled;
            btnInstallFromFile.IsEnabled = enabled;
            btnSelectAll.IsEnabled = enabled;
            btnUnselectAll.IsEnabled = enabled;
            btnClear.IsEnabled = enabled;
            btnOpenFolder.IsEnabled = enabled;
            btnOpenChromeLink.IsEnabled = enabled;
            btnOpenLogFile.IsEnabled = enabled;
            btnApplyFolder.IsEnabled = enabled;
            btnUseDefaultFolder.IsEnabled = enabled;
            btnBrowseFolder.IsEnabled = enabled;

            if (btnAddCustomApp != null)
                btnAddCustomApp.IsEnabled = enabled;

            if (btnClearCustomApp != null)
                btnClearCustomApp.IsEnabled = enabled;

            if (txtCustomAppName != null)
                txtCustomAppName.IsEnabled = enabled;

            if (txtCustomAppUrl != null)
                txtCustomAppUrl.IsEnabled = enabled;

            if (txtCustomAppFileName != null)
                txtCustomAppFileName.IsEnabled = enabled;

            if (txtCustomAppSilentArgs != null)
                txtCustomAppSilentArgs.IsEnabled = enabled;

            if (btnCancel != null)
                btnCancel.IsEnabled = !enabled;
        }

        private void SetStatus(string text)
        {
            txtStatus.Text = text;
        }

        private void SetCurrentTask(string text)
        {
            txtCurrentTask.Text = "Tác vụ hiện tại: " + text;
        }

        private void SetProgress(int value)
        {
            if (value < 0) value = 0;
            if (value > 100) value = 100;

            progressBar.Value = value;
            txtProgress.Text = value + "%";
        }

        private void SetCurrentAppProgress(int value)
        {
            if (value < 0) value = 0;
            if (value > 100) value = 100;

            progressCurrentApp.Value = value;
            txtCurrentAppProgress.Text = value + "%";
        }

        private void Log(string text)
        {
            string line = "[" + DateTime.Now.ToString("HH:mm:ss") + "] " + text + Environment.NewLine;

            txtLog.AppendText(line);
            txtLog.ScrollToEnd();

            if (chkExportLog != null && chkExportLog.IsChecked == true)
            {
                try
                {
                    Directory.CreateDirectory(_downloadFolder);
                    File.AppendAllText(_logFilePath, line);
                }
                catch
                {
                }
            }
        }

        private void btnAddCustomApp_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                AppItem app = BuildCustomAppFromInputs();
                if (app == null)
                    return;

                bool isUpdated = false;

                var existingApp = _allApps.FirstOrDefault(x =>
                    string.Equals(x.Name, app.Name, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(x.DownloadUrl, app.DownloadUrl, StringComparison.OrdinalIgnoreCase));

                if (existingApp != null)
                {
                    existingApp.Name = app.Name;
                    existingApp.DownloadUrl = app.DownloadUrl;
                    existingApp.FileName = app.FileName;
                    existingApp.SilentArgs = app.SilentArgs;
                    existingApp.InteractiveArgs = app.InteractiveArgs;
                    existingApp.DetectKeyword = app.DetectKeyword;
                    existingApp.ExePathHint1 = app.ExePathHint1;
                    existingApp.ExePathHint2 = app.ExePathHint2;

                    var existingSelection = _appSelections.FirstOrDefault(x => x.App == existingApp);
                    if (existingSelection != null)
                    {
                        existingSelection.IsSelected = true;
                        existingSelection.ResetRuntimeState();
                    }

                    isUpdated = true;
                }
                else
                {
                    _allApps.Add(app);
                    _appSelections.Add(new AppSelectionItem
                    {
                        App = app,
                        IsSelected = true
                    });
                }

                SaveAppCatalog();

                if (itemsApps != null)
                {
                    if (itemsApps.ItemsSource == null)
                        itemsApps.ItemsSource = _appSelections;

                    itemsApps.Items.Refresh();
                }

                UpdateSelectedCount();
                ClearCustomAppInputs();

                string actionText = isUpdated ? "Đã cập nhật app trong danh sách" : "Đã thêm app mới vào danh sách";
                Log(actionText + ": " + app.Name + " | File: " + app.FileName);
                SetStatus(actionText);

                MessageBox.Show(
                    actionText + "\n\nTên app: " + app.Name + "\nFile lưu: " + app.FileName,
                    "Thành công",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show("Không thể thêm app mới: " + ex.Message, "Lỗi", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void btnClearCustomApp_Click(object sender, RoutedEventArgs e)
        {
            ClearCustomAppInputs();
            SetStatus("Đã xóa form thêm app");
        }

        private AppItem BuildCustomAppFromInputs()
        {
            string name = txtCustomAppName == null ? string.Empty : txtCustomAppName.Text.Trim();
            string url = txtCustomAppUrl == null ? string.Empty : txtCustomAppUrl.Text.Trim();
            string fileNameInput = txtCustomAppFileName == null ? string.Empty : txtCustomAppFileName.Text.Trim();
            string silentArgs = txtCustomAppSilentArgs == null ? string.Empty : txtCustomAppSilentArgs.Text.Trim();

            if (string.IsNullOrWhiteSpace(name))
            {
                MessageBox.Show("Vui lòng nhập tên app.", "Thiếu thông tin", MessageBoxButton.OK, MessageBoxImage.Warning);
                return null;
            }

            if (string.IsNullOrWhiteSpace(url))
            {
                MessageBox.Show("Vui lòng nhập link tải.", "Thiếu thông tin", MessageBoxButton.OK, MessageBoxImage.Warning);
                return null;
            }

            Uri uri;
            if (!Uri.TryCreate(url, UriKind.Absolute, out uri) ||
                (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            {
                MessageBox.Show("Link tải không hợp lệ. Hãy dùng http:// hoặc https://", "Link không hợp lệ", MessageBoxButton.OK, MessageBoxImage.Warning);
                return null;
            }

            string fileName = string.IsNullOrWhiteSpace(fileNameInput)
                ? GenerateFileNameFromUrl(uri, name)
                : SanitizeFileName(fileNameInput);

            if (!string.IsNullOrWhiteSpace(fileName) && !Path.HasExtension(fileName))
                fileName += GetPreferredExtension(uri);

            if (string.IsNullOrWhiteSpace(fileName))
            {
                MessageBox.Show("Không tạo được tên file hợp lệ.", "Lỗi", MessageBoxButton.OK, MessageBoxImage.Warning);
                return null;
            }

            return new AppItem
            {
                Name = name,
                DownloadUrl = url,
                FileName = fileName,
                SilentArgs = silentArgs,
                InteractiveArgs = CustomAppMarker,
                DetectKeyword = name,
                ExePathHint1 = string.Empty,
                ExePathHint2 = string.Empty
            };
        }

        private string GenerateFileNameFromUrl(Uri uri, string appName)
        {
            string extension = GetPreferredExtension(uri);
            string fileNameFromUrl = Path.GetFileName(uri.LocalPath);
            fileNameFromUrl = SanitizeFileName(fileNameFromUrl);

            if (!string.IsNullOrWhiteSpace(fileNameFromUrl) && Path.HasExtension(fileNameFromUrl))
                return fileNameFromUrl;

            string safeName = ToSafeFileNamePrefix(appName);
            if (string.IsNullOrWhiteSpace(safeName))
                safeName = "custom_app";

            return safeName + "_installer" + extension;
        }

        private string GetPreferredExtension(Uri uri)
        {
            string extension = ".exe";
            string rawName = Path.GetFileName(uri.LocalPath);
            string urlExtension = Path.GetExtension(rawName);

            if (!string.IsNullOrWhiteSpace(urlExtension) && urlExtension.Length <= 10)
                extension = urlExtension;

            return extension;
        }

        private string SanitizeFileName(string fileName)
        {
            if (string.IsNullOrWhiteSpace(fileName))
                return string.Empty;

            var builder = new StringBuilder(fileName.Trim());

            foreach (char c in Path.GetInvalidFileNameChars())
            {
                builder.Replace(c, '_');
            }

            return builder.ToString().Trim();
        }

        private string ToSafeFileNamePrefix(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return string.Empty;

            var builder = new StringBuilder();

            foreach (char c in text.Trim())
            {
                if (char.IsLetterOrDigit(c))
                    builder.Append(char.ToLowerInvariant(c));
                else if (c == ' ' || c == '-' || c == '_')
                    builder.Append('_');
            }

            string result = builder.ToString().Trim('_');
            while (result.Contains("__"))
            {
                result = result.Replace("__", "_");
            }

            return result;
        }

        private void ClearCustomAppInputs()
        {
            if (txtCustomAppName != null)
                txtCustomAppName.Clear();

            if (txtCustomAppUrl != null)
                txtCustomAppUrl.Clear();

            if (txtCustomAppFileName != null)
                txtCustomAppFileName.Clear();

            if (txtCustomAppSilentArgs != null)
                txtCustomAppSilentArgs.Clear();
        }

        private void DeleteApp_Click(object sender, RoutedEventArgs e)
        {
            var button = sender as FrameworkElement;
            if (button == null)
                return;

            var item = button.DataContext as AppSelectionItem;
            if (item == null || item.App == null)
                return;

            if (!IsCustomApp(item.App))
                return;

            var result = MessageBox.Show(
                "Bạn có chắc chắn muốn xóa phần mềm \"" + item.App.Name + "\" không?",
                "Xác nhận xóa",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);

            if (result != MessageBoxResult.Yes)
                return;

            _allApps.Remove(item.App);
            _appSelections.Remove(item);

            SaveAppCatalog();

            if (itemsApps != null)
                itemsApps.Items.Refresh();

            UpdateSelectedCount();
            Log("Đã xóa app tự thêm: " + item.App.Name);
            SetStatus("Đã xóa " + item.App.Name);
        }

        private bool IsCustomApp(AppItem app)
        {
            if (app == null)
                return false;

            return string.Equals(app.InteractiveArgs, CustomAppMarker, StringComparison.Ordinal);
        }
    }

    public static class AppSelectionItemExtensions
    {
        public static Visibility GetCanDeleteVisibility(this AppSelectionItem item)
        {
            if (item == null || item.App == null)
                return Visibility.Collapsed;

            return string.Equals(item.App.InteractiveArgs, "__CUSTOM_APP__", StringComparison.Ordinal)
                ? Visibility.Visible
                : Visibility.Collapsed;
        }
    }
}