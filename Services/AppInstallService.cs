using app_tự_động.Models;
using Microsoft.Win32;
using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Security.Principal;
using System.Threading.Tasks;

namespace app_tự_động.Services
{
    public class AppInstallService
    {
        private readonly Action<string> _log;
        private readonly Action<int> _setCurrentAppProgress;
        private readonly Action<string> _setStatus;
        private readonly Action<string> _setCurrentTask;
        private readonly Func<bool> _isSilentInstallEnabled;
        private readonly Func<bool> _isRetryDownloadEnabled;
        private readonly Func<bool> _isSkipIfExistsEnabled;

        public AppInstallService(
            Action<string> log,
            Action<int> setCurrentAppProgress,
            Action<string> setStatus,
            Action<string> setCurrentTask,
            Func<bool> isSilentInstallEnabled,
            Func<bool> isRetryDownloadEnabled,
            Func<bool> isSkipIfExistsEnabled)
        {
            _log = log;
            _setCurrentAppProgress = setCurrentAppProgress;
            _setStatus = setStatus;
            _setCurrentTask = setCurrentTask;
            _isSilentInstallEnabled = isSilentInstallEnabled;
            _isRetryDownloadEnabled = isRetryDownloadEnabled;
            _isSkipIfExistsEnabled = isSkipIfExistsEnabled;
        }

        public async Task<bool> HasInternet()
        {
            try
            {
                using (HttpClient client = new HttpClient())
                {
                    client.Timeout = TimeSpan.FromSeconds(8);
                    using (var response = await client.GetAsync("https://www.google.com"))
                    {
                        return response.IsSuccessStatusCode;
                    }
                }
            }
            catch
            {
                return false;
            }
        }

        public bool IsRunAsAdmin()
        {
            try
            {
                WindowsIdentity identity = WindowsIdentity.GetCurrent();
                WindowsPrincipal principal = new WindowsPrincipal(identity);
                return principal.IsInRole(WindowsBuiltInRole.Administrator);
            }
            catch
            {
                return false;
            }
        }

        public bool IsAppInstalled(AppItem app)
        {
            try
            {
                if (!string.IsNullOrWhiteSpace(app.ExePathHint1))
                {
                    if (File.Exists(app.ExePathHint1) || Directory.Exists(app.ExePathHint1))
                        return true;
                }

                if (!string.IsNullOrWhiteSpace(app.ExePathHint2))
                {
                    if (File.Exists(app.ExePathHint2) || Directory.Exists(app.ExePathHint2))
                        return true;
                }

                return IsInstalledFromRegistry(app.DetectKeyword);
            }
            catch
            {
                return false;
            }
        }

        private bool IsInstalledFromRegistry(string keyword)
        {
            if (string.IsNullOrWhiteSpace(keyword))
                return false;

            return CheckRegistryPath(Registry.LocalMachine, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall", keyword) ||
                   CheckRegistryPath(Registry.LocalMachine, @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall", keyword) ||
                   CheckRegistryPath(Registry.CurrentUser, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall", keyword);
        }

        private bool CheckRegistryPath(RegistryKey root, string subKeyPath, string keyword)
        {
            using (RegistryKey key = root.OpenSubKey(subKeyPath))
            {
                if (key == null)
                    return false;

                foreach (string subKeyName in key.GetSubKeyNames())
                {
                    using (RegistryKey subKey = key.OpenSubKey(subKeyName))
                    {
                        if (subKey == null)
                            continue;

                        string displayName = Convert.ToString(subKey.GetValue("DisplayName"));
                        if (!string.IsNullOrWhiteSpace(displayName) &&
                            displayName.IndexOf(keyword, StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            return true;
                        }
                    }
                }
            }

            return false;
        }

        public async Task<(DownloadResult Result, string FilePath)> DownloadFile(string downloadFolder, string url, string fileName, string appName)
        {
            string filePath = Path.Combine(downloadFolder, fileName);

            if (_isSkipIfExistsEnabled() && File.Exists(filePath))
            {
                _log("Bỏ qua tải " + appName + " vì file đã tồn tại: " + filePath);
                _setCurrentAppProgress(100);
                return (DownloadResult.SkippedExistingFile, filePath);
            }

            int maxRetries = _isRetryDownloadEnabled() ? 3 : 1;

            for (int attempt = 1; attempt <= maxRetries; attempt++)
            {
                try
                {
                    _log("Đang tải bộ cài " + appName + "...");
                    _log("URL: " + url);
                    _log("Lần thử: " + attempt + "/" + maxRetries);

                    using (HttpClient client = new HttpClient())
                    {
                        client.Timeout = TimeSpan.FromMinutes(30);

                        using (HttpResponseMessage response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead))
                        {
                            response.EnsureSuccessStatusCode();

                            long? totalBytes = response.Content.Headers.ContentLength;
                            long totalRead = 0;

                            using (Stream input = await response.Content.ReadAsStreamAsync())
                            using (FileStream output = new FileStream(filePath, FileMode.Create, FileAccess.Write, FileShare.None))
                            {
                                byte[] buffer = new byte[81920];
                                int read;

                                while ((read = await input.ReadAsync(buffer, 0, buffer.Length)) > 0)
                                {
                                    await output.WriteAsync(buffer, 0, read);
                                    totalRead += read;

                                    if (totalBytes.HasValue && totalBytes.Value > 0)
                                    {
                                        int percent = (int)((totalRead * 100L) / totalBytes.Value);
                                        _setCurrentAppProgress(percent);
                                        _setStatus("Đang tải " + appName + " (" + percent + "%)");
                                        _setCurrentTask("Đang tải " + appName);
                                    }
                                }
                            }
                        }
                    }

                    _log("Đã tải xong " + appName + ": " + filePath);
                    _setCurrentAppProgress(100);
                    return (DownloadResult.Downloaded, filePath);
                }
                catch (Exception ex)
                {
                    _log("Lỗi tải file " + appName + " (lần " + attempt + "): " + ex.Message);

                    try
                    {
                        if (File.Exists(filePath))
                            File.Delete(filePath);
                    }
                    catch
                    {
                    }

                    if (attempt == maxRetries)
                        return (DownloadResult.Failed, null);

                    await Task.Delay(1200);
                }
            }

            return (DownloadResult.Failed, null);
        }

        public async Task<bool> RunInstaller(AppItem app, string filePath)
        {
            try
            {
                if (!File.Exists(filePath))
                {
                    _log("Không tìm thấy file cài: " + filePath);
                    return false;
                }

                string args = _isSilentInstallEnabled() ? app.SilentArgs : app.InteractiveArgs;

                _log("Chạy installer cho " + app.Name);
                _log("File: " + filePath);

                if (string.IsNullOrWhiteSpace(args))
                    _log("Chế độ cài: interactive");
                else
                    _log("Args: " + args);

                _setStatus("Đang chạy installer " + app.Name);
                _setCurrentTask("Đang chạy installer " + app.Name);
                _setCurrentAppProgress(100);

                ProcessStartInfo psi = new ProcessStartInfo
                {
                    FileName = filePath,
                    Arguments = args,
                    UseShellExecute = true
                };

                if (!IsRunAsAdmin())
                    psi.Verb = "runas";

                using (Process process = Process.Start(psi))
                {
                    if (process == null)
                    {
                        _log("Không thể chạy installer của " + app.Name);
                        return false;
                    }

                    await Task.Run(() => process.WaitForExit());

                    _log("Installer đã kết thúc cho " + app.Name + " | ExitCode: " + process.ExitCode);

                    if (process.ExitCode == 0)
                    {
                        _log("Cài xong: " + app.Name);
                        return true;
                    }

                    _log("Cài có thể chưa thành công: " + app.Name);
                    return false;
                }
            }
            catch (Exception ex)
            {
                _log("Lỗi chạy installer " + app.Name + ": " + ex.Message);
                return false;
            }
        }
    }
}