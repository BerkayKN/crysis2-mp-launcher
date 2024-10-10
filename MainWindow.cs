using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Markup;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Wpf;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System.Security.Principal;
using System.Windows.Controls;


namespace C2COMMUNITY_Mod_Launcher
{
    public partial class MainWindow : Window, IComponentConnector
    {
        //Start of Defines
        private const string MD5_FILE_PATH = "/openspymod/openspymodfiles/md5sum.php";
        #if DEBUG
        private const string ZIP_FILE_PATH = "/openspymod/launcherdebug.zip";
        #else
        private const string ZIP_FILE_PATH = "/openspymod/openspymod.zip";
        #endif
        private const string WEBVIEW_DLL_PATH = "/openspymod/WebView2Loader.dll";
        private const string DEFAULT_SERVER_URL = "http://lb.crysis2.epicgamer.org";
        private const string GAME_MOD_FOLDER = "OpenSpy";
        private const string SERVER_MOD_PATH = "/openspymod/openspymodfiles/";
        private const string SERVER_LIST_SOURCE_URL = "http://beta.openspy.net/api/servers/capricorn";
        private const string GAME_STARTER_FILE_NAME = "Crysis 2 - OpenSpy.bat";
        //End of Defines

        private string _serverBaseUrl;
        private readonly string _bin32Folder;
        private readonly string _modSpyFolder;
        private string _md5Url;
        private string _zipUrl;
        private string _WebViewDLLUrl;
        private readonly string _webView2LoaderPath;
        private long _totalDownloadSize;
        private bool _isAdministrator;
        private long _downloadedSize;
        private string _jsonVersion;
        private readonly DispatcherTimer _serverTimer;
        //internal WebView2 webView;
        public ObservableCollection<Server> Servers { get; }

        private List<string> _failedDownloads = new List<string>();

        public MainWindow()
        {
            InitializeComponent();
            _bin32Folder = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "bin32");
            _modSpyFolder = AppDomain.CurrentDomain.BaseDirectory;
            _webView2LoaderPath = Path.Combine(_modSpyFolder, "WebView2Loader.dll");
            _isAdministrator = IsAdministrator();
            UpdateWindowTitle();
            Servers = new ObservableCollection<Server>();
            serverListView.ItemsSource = Servers;
            _ = UpdateServerList();
            _serverTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(15)
            };
            _serverTimer.Tick += async (s, e) => await UpdateServerList();
            _serverTimer.Start();
            CheckDirectoryStructure();
        }

        private async Task InitializeAsync()
        {
            try
            {
                using var client = new HttpClient();
                _serverBaseUrl = await client.GetStringAsync("https://raw.githubusercontent.com/BerkayKN/crysis2-mp-launcher/main/server/server.txt");
                _serverBaseUrl = string.IsNullOrWhiteSpace(_serverBaseUrl) ? DEFAULT_SERVER_URL : _serverBaseUrl.Trim().TrimEnd('/');
            }
            catch
            {
                _serverBaseUrl = DEFAULT_SERVER_URL;
            }
            _md5Url = $"{_serverBaseUrl}" + MD5_FILE_PATH;
            _zipUrl = $"{_serverBaseUrl}{ZIP_FILE_PATH}";
            _WebViewDLLUrl = $"{_serverBaseUrl}{WEBVIEW_DLL_PATH}";
        }

        private bool IsAdministrator()
        {
            using (WindowsIdentity identity = WindowsIdentity.GetCurrent())
            {
                WindowsPrincipal principal = new WindowsPrincipal(identity);
                return principal.IsInRole(WindowsBuiltInRole.Administrator);
            }
        }

        private void UpdateWindowTitle()
        {
            if (!_isAdministrator)
            {
                this.Title += " (Not Administrator)";
            }
        }


        private async void CheckDirectoryStructure()
        {
            launchGameButton.IsEnabled = false;
            ChangelogTab.IsEnabled = true; // Previously was false, new ui changes mostly fixed the freezing issue
            if (!Directory.Exists(_bin32Folder))
            {
                string message = File.Exists(Path.Combine(_modSpyFolder, "Crysis2.exe"))
                    ? "The launcher is inside Bin32 folder. Please place the launcher in the Crysis 2 root folder."
                    : "The launcher is outside Crysis 2 root folder. Please place the launcher in the Crysis 2 root folder.";
                MessageBox.Show(message, "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                Application.Current.Shutdown();
                return;
            }

            await UpdateStatusLabelAsync("Checking mod files...");
            await InitializeAsync();
            
            var progress = new Progress<string>(message => statusLabel.Content = message);
            await Task.Run(() => CheckAndUpdateModFiles(progress));
            
            launchGameButton.IsEnabled = true;
        }

        private async Task CheckAndUpdateModFiles(IProgress<string> progress)
        {
            try
            {
                _downloadedSize = 0;
                _failedDownloads.Clear();
                progress.Report("Downloading file list...");
                string md5Data = await DownloadStringAsync(_md5Url);
                progress.Report("Parsing file list data...");
                var fileHashes = ParseMd5Data(md5Data);
                if (fileHashes == null || fileHashes.Count == 0)
                {
                    progress.Report("Failed to load file list data. Aborting.");
                    return;
                }

                var jsonData = JsonConvert.DeserializeObject<dynamic>(md5Data);
                bool freshInstall = jsonData.freshinstallzip == "1";
                if (!Directory.Exists(Path.Combine(_modSpyFolder, "Mods", GAME_MOD_FOLDER)) && freshInstall)
                {
                    progress.Report("Extracting ZIP file...");
                    await DownloadAndExtractZipAsync(_zipUrl, _modSpyFolder, progress);
                    return;
                }

                long existingFilesSize = CalculateExistingFilesSize(fileHashes);
                _totalDownloadSize -= existingFilesSize;
                progress.Report($"Total download size after excluding existing files: {(_totalDownloadSize / 1048576.0):F2} MB");

                foreach (var fileHash in fileHashes)
                {
                    string url = fileHash.Key;
                    string hash = fileHash.Value;
                    string relativePath = url.Replace($"{_serverBaseUrl}{SERVER_MOD_PATH}", "").Replace('/', '\\');
                    string filePath = Path.Combine(_modSpyFolder, relativePath);
                    progress.Report($"Comparing MD5 hash for {Path.GetFileName(filePath)}...");

                    if (File.Exists(filePath) && ComputeMD5(filePath) == hash) continue;
                    bool downloadSuccess = await DownloadFileWithRetryAsync(url, filePath, 5, progress);
                    if (!downloadSuccess)
                    {
                        progress.Report("Failed to download the mod");
                        return; // Stop the process when download fails
                    }
                }

                progress.Report("Comparing installed files to file list...");
                DeleteFilesNotInMd5List(fileHashes, progress);

                if (!File.Exists(_webView2LoaderPath))
                {
                    using var client = new HttpClient();
                    try
                    {
                        byte[] dllBytes = await client.GetByteArrayAsync(_WebViewDLLUrl);
                        File.WriteAllBytes(_webView2LoaderPath, dllBytes);
                    }
                    catch { /* Ignore errors */ }
                }

                await Dispatcher.InvokeAsync(() =>
                {
                    UpdateProgressBar(0, 0, 0, 0, string.Empty);
                    HideDownloadLabels();
                    UpdateVersionLabel();
                    ChangelogTab.IsEnabled = true;
                });
                
                progress.Report("Ready to play");
            }
            catch (HttpRequestException ex)
            {
                await Dispatcher.InvokeAsync(() =>
                {
                    MessageBox.Show($"HTTP request error: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                });
            }
        }

        private long CalculateExistingFilesSize(Dictionary<string, string> fileHashes)
        {
            return fileHashes.Sum(fileHash =>
            {
                string relativePath = fileHash.Key.Replace($"{_serverBaseUrl}{SERVER_MOD_PATH}", "").Replace('/', '\\');
                string filePath = Path.Combine(_modSpyFolder, relativePath);
                return File.Exists(filePath) ? new FileInfo(filePath).Length : 0;
            });
        }

        private Task UpdateStatusLabelAsync(string message)
        {
            return Dispatcher.InvokeAsync(() => statusLabel.Content = message).Task;
        }

        private void HideDownloadLabels() => Dispatcher.Invoke(() =>
        {
            progressBar.Value = 0;
            netSpeedLabel.Content = string.Empty;
            progressLabel.Content = string.Empty;
        });

        private void DeleteFilesNotInMd5List(Dictionary<string, string> fileHashes, IProgress<string> progress)
        {
            string modSpyModFolder = Path.Combine(_modSpyFolder, "Mods", GAME_MOD_FOLDER);
            foreach (string filePath in Directory.EnumerateFiles(modSpyModFolder, "*", SearchOption.AllDirectories))
            {
                string relativePath = filePath.Replace(modSpyModFolder, "").TrimStart(Path.DirectorySeparatorChar).Replace('\\', '/');
                string url = Uri.UnescapeDataString($"{_serverBaseUrl}{SERVER_MOD_PATH}Mods/{GAME_MOD_FOLDER}/{Uri.EscapeDataString(relativePath)}");
                
                if (!fileHashes.ContainsKey(url))
                {
                    try
                    {
                        progress.Report($"Deleting file: {Path.GetFileName(filePath)}...");
                        File.Delete(filePath);
                    }
                    catch (Exception ex)
                    {
                        MessageBox.Show($"Error deleting file {filePath}: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                    }
                }
            }
        }

        private async Task<string> DownloadStringAsync(string url)
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromMinutes(2) };
            return await client.GetStringAsync(url);
        }

        private Dictionary<string, string> ParseMd5Data(string md5Data)
        {
            try
            {
                var jsonData = JObject.Parse(md5Data);
                _jsonVersion = jsonData["version"]?.ToString() ?? "Unknown version";
                _totalDownloadSize = jsonData["totalSize"]?.ToObject<long>() ?? 0;
                return jsonData["files"]?.ToObject<Dictionary<string, string>>() ?? new Dictionary<string, string>();
            }
            catch (JsonReaderException ex)
            {
                MessageBox.Show($"Error parsing MD5 data: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"An unexpected error occurred: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            return new Dictionary<string, string>();
        }

        private string ComputeMD5(string filePath)
        {
            try
            {
                using var md5 = MD5.Create();
                using var stream = File.OpenRead(filePath);
                return BitConverter.ToString(md5.ComputeHash(stream)).Replace("-", "").ToLowerInvariant();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error computing MD5 for {filePath}: {ex.Message}");
                return string.Empty;
            }
        }

        private async Task DownloadFileAsync(string url, string outputPath)
        {
            using var client = new HttpClient();
            try
            {
                using var response = await client.GetAsync(Uri.EscapeUriString(url), HttpCompletionOption.ResponseHeadersRead);
                response.EnsureSuccessStatusCode();
                long fileSize = response.Content.Headers.ContentLength ?? -1;

                Directory.CreateDirectory(Path.GetDirectoryName(outputPath));

                using var stream = await response.Content.ReadAsStreamAsync();
                using var fileStream = new FileStream(outputPath, FileMode.Create, FileAccess.Write, FileShare.None, 8192, true);
                var buffer = new byte[8192];
                long totalRead = 0;
                var stopwatch = Stopwatch.StartNew();

                while (true)
                {
                    int bytesRead = await stream.ReadAsync(buffer, 0, buffer.Length);
                    if (bytesRead == 0) break;

                    await fileStream.WriteAsync(buffer, 0, bytesRead);
                    totalRead += bytesRead;

                    if (fileSize != -1)
                    {
                        int progressPercentage = (int)(totalRead * 100 / fileSize);
                        double downloadSpeed = totalRead / stopwatch.Elapsed.TotalSeconds;
                        UpdateProgressBar(progressPercentage, totalRead, fileSize, downloadSpeed, Path.GetFileName(outputPath));
                    }
                }

                stopwatch.Stop();
                _downloadedSize += totalRead;
                UpdateDownloadLabel();
            }
            catch
            {
                throw; // Rethrow the exception so it can be caught by DownloadFileWithRetryAsync
            }
        }

        private async Task<bool> DownloadFileWithRetryAsync(string url, string outputPath, int maxRetries, IProgress<string> progress)
        {
            for (int attempt = 1; attempt <= maxRetries; attempt++)
            {
                try
                {
                    await DownloadFileAsync(url, outputPath);
                    return true;
                }
                catch (HttpRequestException ex)
                {
                    if (attempt == maxRetries)
                    {
                        _failedDownloads.Add(url);
                        progress.Report($"Failed to download: {Path.GetFileName(outputPath)}");
                        return false;
                    }
                    await Task.Delay(1000 * attempt); // Exponential backoff
                }
                catch (Exception)
                {
                    _failedDownloads.Add(url);
                    progress.Report($"Failed to download: {Path.GetFileName(outputPath)}");
                    return false;
                }
            }
            return false;
        }

        
     private async Task DownloadAndExtractZipAsync(string zipUrl, string destinationFolder, IProgress<string> progress)
        {
            string tempZipPath = Path.Combine(Path.GetTempPath(), $"openspy_temp_{Guid.NewGuid()}.zip");
            FileStream fileStream = null;
            ZipArchive archive = null;

            try
            {
                progress.Report("Downloading mod package...");
                using (var client = new HttpClient { Timeout = TimeSpan.FromMinutes(30) })
                using (var response = await client.GetAsync(zipUrl, HttpCompletionOption.ResponseHeadersRead))
                {
                    response.EnsureSuccessStatusCode();
                    long totalBytes = response.Content.Headers.ContentLength ?? -1;
                    long downloadedBytes = 0;
                    var buffer = new byte[81920];

                    using (var contentStream = await response.Content.ReadAsStreamAsync())
                    using (fileStream = new FileStream(tempZipPath, FileMode.Create, FileAccess.Write, FileShare.None, buffer.Length, true))
                    {
                        var startTime = DateTime.Now;
                        var lastUpdateTime = DateTime.Now;

                        while (true)
                        {
                            int bytesRead = await contentStream.ReadAsync(buffer, 0, buffer.Length);
                            if (bytesRead == 0) break;

                            await fileStream.WriteAsync(buffer, 0, bytesRead);
                            downloadedBytes += bytesRead;

                            var now = DateTime.Now;
                            if ((now - lastUpdateTime).TotalSeconds >= 1)
                            {
                                double downloadSpeed = downloadedBytes / 1048576.0 / (now - startTime).TotalSeconds;
                                _downloadedSize = downloadedBytes;
                                _totalDownloadSize = totalBytes;

                                await Dispatcher.InvokeAsync(() =>
                                {
                                    netSpeedLabel.Content = $"Speed: {downloadSpeed:F2} MB/s";
                                    progressLabel.Content = $"Downloaded: {_downloadedSize / 1048576.0:F2} MB / {_totalDownloadSize / 1048576.0:F2} MB";
                                    if (totalBytes > 0)
                                    {
                                        progressBar.Value = (double)downloadedBytes / totalBytes * 100;
                                    }
                                });

                                lastUpdateTime = now;
                            }
                        }
                    }
                }

                progress.Report("Extracting mod package...");
                await Task.Run(() =>
                {
                    using (archive = ZipFile.OpenRead(tempZipPath))
                    {
                        foreach (var entry in archive.Entries)
                        {
                            string fullPath = Path.GetFullPath(Path.Combine(destinationFolder, entry.FullName));

                            if (entry.FullName.EndsWith("/"))
                            {
                                Directory.CreateDirectory(fullPath);
                            }
                            else
                            {
                                Directory.CreateDirectory(Path.GetDirectoryName(fullPath));
                                for (int retries = 0; retries < 3; retries++)
                                {
                                    try
                                    {
                                        entry.ExtractToFile(fullPath, true);
                                        break;
                                    }
                                    catch (IOException) when (retries < 2)
                                    {
                                        Task.Delay(1000).Wait();
                                    }
                                }
                            }
                        }
                    }
                });

                await Dispatcher.InvokeAsync(() =>
                {
                    UpdateProgressBar(0, 0, 0, 0, string.Empty);
                    HideDownloadLabels();
                    UpdateVersionLabel();
                    ChangelogTab.IsEnabled = true;
                });
                progress.Report("Ready");
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error downloading and extracting zip file: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                if (archive != null)
                {
                    archive.Dispose();
                }

                if (fileStream != null)
                {
                    fileStream.Dispose();
                }

                await Task.Run(async () =>
                {
                    for (int i = 0; i < 5; i++)
                    {
                        try
                        {
                            if (File.Exists(tempZipPath))
                            {
                                File.Delete(tempZipPath);
                            }
                            break;
                        }
                        catch (Exception ex)
                        {
                            if (i == 4)
                            {
                                await Dispatcher.InvokeAsync(() =>
                                {
                                    MessageBox.Show($"Unable to delete temporary file: {tempZipPath}\nError: {ex.Message}", "Warning", MessageBoxButton.OK, MessageBoxImage.Warning);
                                });
                            }
                            await Task.Delay(1000);
                        }
                    }
                });
            }
        }

        private void UpdateProgressBar(int progressPercentage, long totalRead, long totalBytes, double downloadSpeed, string fileName)
        {
            Dispatcher.Invoke(() =>
            {
                progressBar.Value = progressPercentage;
                statusLabel.Content = $"Downloading: {fileName} - {totalRead / 1048576.0:F2} MB / {totalBytes / 1048576.0:F2} MB";
                netSpeedLabel.Content = $"Speed: {downloadSpeed / 1048576.0:F2} MB/s";
            });
        }

        private void UpdateDownloadLabel() => Dispatcher.Invoke(() =>
            progressLabel.Content = $"Downloaded: {_downloadedSize / 1048576.0:F2} MB / {_totalDownloadSize / 1048576.0:F2} MB");

        private void UpdateVersionLabel() => Dispatcher.Invoke(() => versionLabel.Content = _jsonVersion ?? "");

        private void LaunchGameButton_Click(object sender, RoutedEventArgs e)
        {
            string batPath = Path.Combine(_bin32Folder, GAME_STARTER_FILE_NAME);
            if (File.Exists(batPath))
            {
                try
                {
                    Process.Start(batPath);
                    Application.Current.Shutdown();
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"Error launching the game: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
            else
            {
                MessageBox.Show("Game executable not found!", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private async Task UpdateServerList()
        {
            try
            {
                using var client = new HttpClient();
                string json = await client.GetStringAsync(SERVER_LIST_SOURCE_URL);
                var serverList = JsonConvert.DeserializeObject<List<Server>>(json);
                Dispatcher.Invoke(() =>
                {
                    Servers.Clear();
                    foreach (var server in serverList)
                    {
                        Servers.Add(server);
                    }
                });
            }
            catch { /* Ignore errors */ }
        }
    }
}
