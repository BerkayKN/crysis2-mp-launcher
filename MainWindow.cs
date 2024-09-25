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

namespace C2COMMUNITY_Mod_Launcher
{
    public partial class MainWindow : Window, IComponentConnector
    {
        private string _serverBaseUrl;
        private readonly string _bin32Folder;
        private readonly string _openSpyFolder;
        private string _md5Url;
        private string _zipUrl;
        private string _WebViewDLLUrl;
        private readonly string _webView2LoaderPath;
        private long _totalDownloadSize;
        private long _downloadedSize;
        private string _jsonVersion;
        private readonly DispatcherTimer _serverTimer;
        //internal WebView2 webView;
        public ObservableCollection<Server> Servers { get; }

        public MainWindow()
        {
            InitializeComponent();
            _bin32Folder = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "bin32");
            _openSpyFolder = AppDomain.CurrentDomain.BaseDirectory;
            _webView2LoaderPath = Path.Combine(_openSpyFolder, "WebView2Loader.dll");
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
                _serverBaseUrl = string.IsNullOrWhiteSpace(_serverBaseUrl) ? "http://lb.crysis2.epicgamer.org" : _serverBaseUrl.Trim().TrimEnd('/');
            }
            catch
            {
                _serverBaseUrl = "http://lb.crysis2.epicgamer.org";
            }
            _md5Url = $"{_serverBaseUrl}/openspymod/openspymodfiles/md5sum.php";
            _zipUrl = $"{_serverBaseUrl}/openspymod/openspymod.zip";
            _WebViewDLLUrl = $"{_serverBaseUrl}/openspymod/WebView2Loader.dll";
        }

        private async void CheckDirectoryStructure()
        {
            launchGameButton.IsEnabled = false;
            ChangelogTab.IsEnabled = false;
            if (!Directory.Exists(_bin32Folder))
            {
                string message = File.Exists(Path.Combine(_openSpyFolder, "Crysis2.exe"))
                    ? "The launcher is inside Bin32 folder. Please place the launcher in the Crysis 2 root folder."
                    : "The launcher is outside Crysis 2 root folder. Please place the launcher in the Crysis 2 root folder.";
                MessageBox.Show(message, "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                Application.Current.Shutdown();
                return;
            }
            
            UpdateStatusLabel("Checking mod files...");
            await InitializeAsync();
            await CheckAndUpdateModFiles();
            launchGameButton.IsEnabled = true;
        }

    
        //private void Window_SizeChanged(object sender, SizeChangedEventArgs e)
        //{
        //    if (e.NewSize.Width / e.NewSize.Height != e.PreviousSize.Width / e.PreviousSize.Height)
        //    {
        //        double ratio = MinWidth / MinHeight;
        //        if (e.NewSize.Width / e.NewSize.Height > ratio)
        //        {
        //            this.Width = e.NewSize.Height * ratio;
        //        }
        //        else
        //        {
        //            this.Height = e.NewSize.Width / ratio;
        //        }
        //    }
        //}

        private async Task CheckAndUpdateModFiles()
        {
            try
            {
                _downloadedSize = 0;
                string md5Data = await DownloadStringAsync(_md5Url);
                UpdateStatusLabel("Parsing file list data...");
                var fileHashes = ParseMd5Data(md5Data);
                if (fileHashes == null || fileHashes.Count == 0)
                {
                    UpdateStatusLabel("Failed to load file list data. Aborting.");
                    return;
                }

                var jsonData = JsonConvert.DeserializeObject<dynamic>(md5Data);
                bool freshInstall = jsonData.freshinstallzip == "1";
                if (!Directory.Exists(Path.Combine(_openSpyFolder, "Mods", "OpenSpy")) && freshInstall)
                {
                    UpdateStatusLabel("Extracting ZIP file...");
                    await DownloadAndExtractZipAsync(_zipUrl, _openSpyFolder);
                    return;
                }

                long existingFilesSize = CalculateExistingFilesSize(fileHashes);
                _totalDownloadSize -= existingFilesSize;
                UpdateStatusLabel($"Total download size after excluding existing files: {(_totalDownloadSize / 1048576.0):F2} MB");

                foreach (var fileHash in fileHashes)
                {
                    string url = fileHash.Key;
                    string hash = fileHash.Value;
                    string relativePath = url.Replace($"{_serverBaseUrl}/openspymod/openspymodfiles/", "").Replace('/', '\\');
                    string filePath = Path.Combine(_openSpyFolder, relativePath);
                    UpdateStatusLabel($"Comparing MD5 hash for {Path.GetFileName(filePath)}...");

                    if (File.Exists(filePath) && ComputeMD5(filePath) == hash) continue;
                    await DownloadFileAsync(url, filePath);
                }

                UpdateStatusLabel("Comparing installed files to file list...");
                DeleteFilesNotInMd5List(fileHashes);

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

                UpdateProgressBar(0, 0, 0, 0, string.Empty);
                HideDownloadLabels();
                ChangelogTab.IsEnabled = true;
                UpdateStatusLabel("Ready");
                UpdateVersionLabel();
            }
            catch (HttpRequestException ex)
            {
                MessageBox.Show($"HTTP request error: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private long CalculateExistingFilesSize(Dictionary<string, string> fileHashes)
        {
            return fileHashes.Sum(fileHash =>
            {
                string relativePath = fileHash.Key.Replace($"{_serverBaseUrl}/openspymod/openspymodfiles/", "").Replace('/', '\\');
                string filePath = Path.Combine(_openSpyFolder, relativePath);
                return File.Exists(filePath) ? new FileInfo(filePath).Length : 0;
            });
        }

        private void UpdateStatusLabel(string message) => Dispatcher.Invoke(() => statusLabel.Content = message);

        private void HideDownloadLabels() => Dispatcher.Invoke(() =>
        {
            progressBar.Value = 0;
            netSpeedLabel.Content = string.Empty;
            progressLabel.Content = string.Empty;
        });

        private void DeleteFilesNotInMd5List(Dictionary<string, string> fileHashes)
        {
            string openSpyModFolder = Path.Combine(_openSpyFolder, "Mods", "OpenSpy");
            foreach (string filePath in Directory.EnumerateFiles(openSpyModFolder, "*", SearchOption.AllDirectories))
            {
                string relativePath = filePath.Replace(openSpyModFolder, "").TrimStart(Path.DirectorySeparatorChar).Replace('\\', '/');
                string url = Uri.UnescapeDataString($"{_serverBaseUrl}/openspymod/openspymodfiles/Mods/OpenSpy/{Uri.EscapeDataString(relativePath)}");
                
                if (!fileHashes.ContainsKey(url))
                {
                    try
                    {
                        UpdateStatusLabel($"Deleting file: {Path.GetFileName(filePath)}...");
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
            catch (HttpRequestException ex)
            {
                MessageBox.Show($"HTTP error while downloading file: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"General error: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private async Task DownloadAndExtractZipAsync(string zipUrl, string destinationFolder)
        {
            string tempZipPath = Path.GetTempFileName();
            try
            {
                UpdateStatusLabel("Downloading mod package...");
                using var client = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
                using var response = await client.GetAsync(zipUrl, HttpCompletionOption.ResponseHeadersRead);
                response.EnsureSuccessStatusCode();

                long totalBytes = response.Content.Headers.ContentLength ?? -1;
                long downloadedBytes = 0;
                var buffer = new byte[8192];
                using var contentStream = await response.Content.ReadAsStreamAsync();
                using var fileStream = new FileStream(tempZipPath, FileMode.Create, FileAccess.Write, FileShare.None, buffer.Length, true);

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

                        Dispatcher.Invoke(() =>
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

                UpdateStatusLabel("Extracting mod package...");
                using (var archive = ZipFile.OpenRead(tempZipPath))
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
                            entry.ExtractToFile(fullPath, true);
                        }
                    }
                }

                UpdateProgressBar(0, 0, 0, 0, string.Empty);
                HideDownloadLabels();
                UpdateStatusLabel("Ready");
                UpdateVersionLabel();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error downloading and extracting zip file: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                try
                {
                    File.Delete(tempZipPath);
                }
                catch { /* Ignore errors */ }
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
            string batPath = Path.Combine(_bin32Folder, "Crysis 2 - OpenSpy.bat");
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
                string json = await client.GetStringAsync("http://beta.openspy.net/api/servers/capricorn");
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
