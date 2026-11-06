using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace Campfire.Link.Runtime
{
    /// <summary>
    /// The networking component that multiplayer runs on. It is downloaded on first use into
    /// %LocalAppData%\Campfire\EasyTier and every file is checked against a known SHA-256 hash.
    /// </summary>
    internal static class EasyTierPackage
    {
        public const string Version = "2.6.4";

        private const string CoreFile = "easytier-core";
        private const string CliFile = "easytier-cli";
        private const int ProbeSize = 64 * 1024;
        public const string ArchiveSha256 = "61b659eaedba658fa66fe47d17e1426cdd77e5d02fa15fed447bb4357c09dfd6";
        private static readonly string[] Files = { CoreFile, CliFile };
        private static readonly Dictionary<string, string[]> Hashes = new Dictionary<string, string[]>
        {
            ["x86_64"] = new[]
            {
                "35b854e0c5853c02733870488c977200f6aa06d09c08d5dc7f9f9e150e7be8d5",
                "8da66644ec50dcf618fccf76780991f52dfbe2939797abc0efe0ef4d4c4d3383",
            },
        };

        private static readonly object Sync = new object();
        private static bool _verified;

        /// <summary>"x86_64", "arm64" or "i686", matching the native processor of this computer.</summary>
        public static string Architecture { get; } = "x86_64";
        public static bool IsSupportedSystem => OperatingSystem.IsLinux()
            && RuntimeInformation.ProcessArchitecture == System.Runtime.InteropServices.Architecture.X64;
        public static bool IsWindows7 => false;
        public static bool HasLoaderUpdate => true;

        public static string InstallDirectory { get; } =
            Path.Combine(AppSettings.DataDirectory, "EasyTier", Version + "-linux-" + Architecture);

        public static string CorePath => Path.Combine(InstallDirectory, CoreFile);

        public static string CliPath => Path.Combine(InstallDirectory, CliFile);

        /// <summary>Whether all files are present and intact. Hashes are checked once per run.</summary>
        public static bool IsInstalled()
        {
            lock (Sync)
            {
                if (_verified)
                    return true;
                _verified = Files.All(IsFileValid);
                return _verified;
            }
        }

        /// <summary>Deletes every downloaded version; it is downloaded again the next time it is needed.</summary>
        public static void Uninstall()
        {
            lock (Sync)
            {
                _verified = false;
                string root = Path.GetDirectoryName(InstallDirectory);
                if (Directory.Exists(root))
                    Directory.Delete(root, true);
            }
        }

        /// <summary>Downloads whatever is missing or damaged, trying the fastest source first.</summary>
        public static async Task InstallAsync(IProgress<DownloadProgress> progress, CancellationToken cancellation)
        {
            if (!IsSupportedSystem) throw new PlatformNotSupportedException("This port supports Linux x86_64 only.");
            if (await Task.Run(() => IsInstalled()).ConfigureAwait(false))
            {
                EnsureExecutable();
                return;
            }

            // A portable distribution may include the unchanged, pinned upstream archive.
            string bundled = Path.Combine(AppContext.BaseDirectory, "easytier-linux-x86_64-v2.6.4.zip");
            if (File.Exists(bundled))
            {
                cancellation.ThrowIfCancellationRequested();
                InstallArchive(bundled);
                return;
            }

            Directory.CreateDirectory(InstallDirectory);
            using (var client = CreateClient())
            {
                var sources = await RankSourcesAsync(client, cancellation).ConfigureAwait(false);
                if (sources.Count == 0)
                    throw new LinkException("Link.Error.DownloadUnreachable");

                Exception last = null;
                foreach (var source in sources)
                {
                    try
                    {
                        LinkLog.Info($"Downloading component from {source.Url} (range: {source.SupportsRange}).");
                        if (source.SupportsRange)
                            await DownloadEntriesAsync(client, source, progress, cancellation).ConfigureAwait(false);
                        else
                            await DownloadArchiveAsync(client, source, progress, cancellation).ConfigureAwait(false);

                        lock (Sync)
                            _verified = Files.All(IsFileValid);
                        if (_verified)
                        {
                            EnsureExecutable();
                            return;
                        }
                        throw new InvalidDataException("Downloaded files failed verification.");
                    }
                    catch (Exception ex) when (!cancellation.IsCancellationRequested)
                    {
                        last = ex;
                        LinkLog.Warn($"Source {source.Url} failed: {ex.Message}");
                    }
                }
                throw new LinkException(last, "Link.Error.DownloadFailed", last?.Message);
            }
        }

        // ======================== Sources ========================

        private static IEnumerable<string> GetSourceUrls()
        {
            string asset = $"easytier-linux-{Architecture}-v{Version}.zip";
            string github = $"https://github.com/EasyTier/EasyTier/releases/download/v{Version}/{asset}";
            yield return $"https://gitee.com/easytier/EasyTier/releases/download/v{Version}/{asset}";
            yield return github;
            yield return "https://ghfast.top/" + github;
            yield return "https://gh-proxy.com/" + github;
        }

        /// <summary>
        /// Requests the first 64 KB from every source at once and orders them by the estimated total download
        /// time. Sources that support ranges only need the three files, about 40% of the archive.
        /// </summary>
        private static async Task<List<Source>> RankSourcesAsync(HttpClient client, CancellationToken cancellation)
        {
            var probes = GetSourceUrls().Select(url => ProbeAsync(client, url, cancellation)).ToList();
            var results = await Task.WhenAll(probes).ConfigureAwait(false);
            cancellation.ThrowIfCancellationRequested();
            return results.Where(r => r != null).OrderBy(r => r.Score).ToList();
        }

        private static async Task<Source> ProbeAsync(HttpClient client, string url, CancellationToken cancellation)
        {
            var watch = Stopwatch.StartNew();
            using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation))
            {
                timeout.CancelAfter(TimeSpan.FromSeconds(10));
                try
                {
                    var request = new HttpRequestMessage(HttpMethod.Get, url);
                    request.Headers.Range = new RangeHeaderValue(0, ProbeSize - 1);
                    using (var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token)
                        .ConfigureAwait(false))
                    {
                        if (!response.IsSuccessStatusCode)
                            return null;

                        bool partial = response.StatusCode == HttpStatusCode.PartialContent;
                        long? length = partial ? response.Content.Headers.ContentRange?.Length : response.Content.Headers.ContentLength;
                        if (length == null || length < ProbeSize)
                            return null;

                        // Reads the probe window to measure throughput.
                        using (var stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false))
                        {
                            var counted = new ProgressStream(stream, response, null);
                            var buffer = new byte[16 * 1024];
                            int total = 0;
                            while (total < ProbeSize)
                            {
                                int read = await counted.ReadAsync(buffer, 0, buffer.Length, timeout.Token).ConfigureAwait(false);
                                if (read == 0)
                                    break;
                                total += read;
                            }
                            if (total < ProbeSize)
                                return null;
                        }

                        double seconds = Math.Max(watch.Elapsed.TotalSeconds, 0.05);
                        return new Source(url, partial, length.Value, seconds);
                    }
                }
                catch (Exception ex) when (!cancellation.IsCancellationRequested)
                {
                    LinkLog.Info($"Probe {url} failed: {ex.Message}");
                    return null;
                }
            }
        }

        // ======================== Download ========================

        private static async Task DownloadEntriesAsync(HttpClient client, Source source, IProgress<DownloadProgress> progress,
            CancellationToken cancellation)
        {
            var zip = await RemoteZip.OpenAsync(client, source.Url, source.Length, cancellation).ConfigureAwait(false);
            var wanted = Files
                .Where(file => !IsFileValid(file))
                .Select(file => new { File = file, Entry = zip.Entries.FirstOrDefault(e => MatchesEntry(e.Name, file)) })
                .ToList();
            if (wanted.Any(w => w.Entry == null))
                throw new InvalidDataException("The archive does not contain the expected files.");

            long total = wanted.Sum(w => w.Entry.CompressedSize);
            long received = 0;
            progress?.Report(new DownloadProgress(0, total));

            foreach (var item in wanted)
            {
                string temp = TempPath(item.File);
                using (var output = File.Create(temp))
                {
                    await zip.ExtractAsync(item.Entry, output, bytes =>
                    {
                        received += bytes;
                        progress?.Report(new DownloadProgress(received, total));
                    }, cancellation).ConfigureAwait(false);
                }
                CommitFile(item.File, temp);
            }
        }

        private static async Task DownloadArchiveAsync(HttpClient client, Source source, IProgress<DownloadProgress> progress,
            CancellationToken cancellation)
        {
            string archive = Path.Combine(InstallDirectory, "download.zip.tmp");
            try
            {
                using (var response = await client.GetAsync(source.Url, HttpCompletionOption.ResponseHeadersRead, cancellation)
                    .ConfigureAwait(false))
                {
                    response.EnsureSuccessStatusCode();
                    long total = response.Content.Headers.ContentLength ?? source.Length;
                    long received = 0;
                    progress?.Report(new DownloadProgress(0, total));

                    using (var network = await response.Content.ReadAsStreamAsync().ConfigureAwait(false))
                    using (var output = File.Create(archive))
                    {
                        var counted = new ProgressStream(network, response, bytes =>
                        {
                            received += bytes;
                            progress?.Report(new DownloadProgress(received, total));
                        });
                        await counted.CopyToAsync(output, 81920, cancellation).ConfigureAwait(false);
                    }
                }

                using (var zip = ZipFile.OpenRead(archive))
                {
                    foreach (string file in Files.Where(f => !IsFileValid(f)))
                    {
                        var entry = zip.Entries.FirstOrDefault(e => MatchesEntry(e.FullName, file))
                            ?? throw new InvalidDataException("The archive does not contain " + file + ".");
                        string temp = TempPath(file);
                        entry.ExtractToFile(temp, overwrite: true);
                        CommitFile(file, temp);
                    }
                }
            }
            finally
            {
                TryDelete(archive);
            }
        }

        /// <summary>Moves a verified temporary file into place; a file that fails the check is discarded.</summary>
        private static void CommitFile(string file, string temp)
        {
            string target = Path.Combine(InstallDirectory, file);
            if (!string.Equals(ComputeHash(temp), ExpectedHash(file), StringComparison.OrdinalIgnoreCase))
            {
                TryDelete(temp);
                throw new InvalidDataException(file + " failed verification.");
            }
            TryDelete(target);
            File.Move(temp, target);
            File.SetUnixFileMode(target, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        // ======================== Helpers ========================

        private static bool IsFileValid(string file)
        {
            string path = Path.Combine(InstallDirectory, file);
            try
            {
                return File.Exists(path)
                       && string.Equals(ComputeHash(path), ExpectedHash(file), StringComparison.OrdinalIgnoreCase);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                return false;
            }
        }

        private static string ExpectedHash(string file)
        {
            return Hashes[Architecture][Array.IndexOf(Files, file)];
        }

        private static string ComputeHash(string path)
        {
            using (var sha = SHA256.Create())
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                return Hex.Of(sha.ComputeHash(stream));
        }

        private static bool MatchesEntry(string entryName, string file)
        {
            string name = entryName.Replace('\\', '/');
            return name.EndsWith("/" + file, StringComparison.OrdinalIgnoreCase)
                   || string.Equals(name, file, StringComparison.OrdinalIgnoreCase);
        }

        private static string TempPath(string file)
        {
            return Path.Combine(InstallDirectory, file + ".tmp");
        }

        private static void TryDelete(string path)
        {
            try
            {
                if (File.Exists(path))
                    File.Delete(path);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
            }
        }

        private static HttpClient CreateClient()
        {
            // TLS 1.2, which Windows 7 does not offer by default, is switched on at startup.
            var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = true })
            {
                Timeout = Timeout.InfiniteTimeSpan,
            };
            string version = Assembly.GetExecutingAssembly().GetName().Version.ToString(3);
            client.DefaultRequestHeaders.UserAgent.ParseAdd("Campfire/" + version);
            return client;
        }

        /// <summary>
        /// Starts the component once to see that Windows can run it. On Windows 7 without the required updates it
        /// stops at once with a missing system function.
        /// </summary>
        public static async Task<bool> CanRunAsync()
        {
            try
            {
                var start = new ProcessStartInfo(CorePath, "--version")
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                };
                using (var process = Process.Start(start))
                {
                    var output = process.StandardOutput.ReadToEndAsync();
                    var errors = process.StandardError.ReadToEndAsync();
                    var exited = Task.Run(() => process.WaitForExit(15000));
                    if (!await exited.ConfigureAwait(false))
                    {
                        process.Kill();
                        return true; // Still running.
                    }
                    await Task.WhenAll(output, errors).ConfigureAwait(false);
                    if (process.ExitCode != 0)
                        LinkLog.Warn($"Component self-test exited with 0x{process.ExitCode:X8}: {errors.Result.Trim()}");
                    return process.ExitCode == 0;
                }
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception || ex is InvalidOperationException || ex is IOException)
            {
                LinkLog.Warn("Component self-test failed: " + ex.Message);
                return false;
            }
        }

        private static void EnsureExecutable()
        {
            foreach (string file in Files)
                File.SetUnixFileMode(Path.Combine(InstallDirectory, file), UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        /// <summary>Offline installation of the exact original upstream Linux release archive.</summary>
        public static void InstallArchive(string archive)
        {
            if (!IsSupportedSystem) throw new PlatformNotSupportedException("This port supports Linux x86_64 only.");
            if (!string.Equals(ComputeHash(archive), ArchiveSha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The archive is not the pinned upstream EasyTier 2.6.4 Linux x86_64 release.");
            Directory.CreateDirectory(InstallDirectory);
            using (var zip = ZipFile.OpenRead(archive))
                foreach (string file in Files)
                {
                    var entry = zip.Entries.Single(e => MatchesEntry(e.FullName, file));
                    string temporary = TempPath(file);
                    entry.ExtractToFile(temporary, true);
                    CommitFile(file, temporary);
                }
            lock (Sync) _verified = Files.All(IsFileValid);
            if (!_verified) throw new InvalidDataException("Component verification failed.");
            EnsureExecutable();
        }

        private sealed class Source
        {
            public Source(string url, bool supportsRange, long length, double probeSeconds)
            {
                Url = url;
                SupportsRange = supportsRange;
                Length = length;
                // Expected time = bytes to fetch / measured speed; ranged downloads fetch about 40% of the archive.
                Score = probeSeconds * (supportsRange ? 0.42 : 1.0) * length;
            }

            public string Url { get; }

            public bool SupportsRange { get; }

            public long Length { get; }

            public double Score { get; }
        }
    }

    internal readonly struct DownloadProgress
    {
        public DownloadProgress(long received, long total)
        {
            Received = received;
            Total = total;
        }

        public long Received { get; }

        public long Total { get; }

        public double Fraction => Total > 0 ? Math.Min(1.0, (double)Received / Total) : 0;
    }
}
