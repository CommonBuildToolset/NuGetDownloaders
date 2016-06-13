using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;

namespace NuGetCredentialProviderBundleDownloader
{
    /// <summary>
    /// A NuGet downloader for the Visual Studio Team Services credential bundle.
    /// </summary>
    public sealed class Downloader
    {
        /// <summary>
        /// A default URL to use if one is not specified.  This URL is a VSTS account that will probably always exist and can technically be used by everyone.
        /// </summary>
        private static readonly Uri DefaultCredentialBundleUri = new Uri("https://microsoft.pkgs.visualstudio.com/_apis/public/nuget/client/CredentialProviderBundle.zip");

        private readonly Action<string> _logInfo;
        private readonly Action<string> _logError;

        private readonly CancellationToken _cancellationToken;

        public Downloader(Action<string> logInfo, Action<string> logError, CancellationToken cancellationToken)
        {
            _logInfo = logInfo;
            _logError = logError;
            _cancellationToken = cancellationToken;
        }

        public static bool Execute(string path, string arguments, Action<string> logInfo = null, Action<string> logError = null, CancellationToken cancellationToken = default(CancellationToken))
        {
            // Parse the arguments as a download URL
            //
            Uri downloadUri = DefaultCredentialBundleUri;

            if (!String.IsNullOrWhiteSpace(arguments))
            {
                Uri uri;

                if (!Uri.TryCreate(arguments, UriKind.Absolute, out uri) || !Path.GetFileName(uri.LocalPath).Equals("CredentialProviderBundle.zip", StringComparison.OrdinalIgnoreCase))
                {
                    throw new ArgumentException(String.Format("The specified NuGet downloader arguments '{0}' are invalid.  The value must be a valid URL that points to CredentialProviderBundle.zip.", arguments));
                }

                downloadUri = uri;
            }

            Downloader downloader = new Downloader(logInfo, logError, cancellationToken);

            FileInfo localFile = new FileInfo(Path.Combine(Path.GetTempPath(), Path.GetFileName(downloadUri.LocalPath)));

            if (downloader.DownloadFile(downloadUri, localFile))
            {
                // Only unzip .exe, .dll, and .config files to make it a little faster.  This could cause issues if dependencies are not unzipped...
                //
                downloader.Unzip(localFile.FullName, path, patterns: new[] { ".exe", ".dll", ".config" });

                return true;
            }

            return false;
        }

        private bool DownloadFile(Uri uri, FileInfo destination)
        {
            return Retry(() =>
            {
                using (WebClient webClient = new WebClient())
                {
                    try
                    {
                        LogMessage("Determining if credential bundle has already been downloaded");

                        if (destination.Exists)
                        {
                            // Open the request to get the size of the file
                            //
                            webClient.OpenReadTaskAsync(uri).Wait(_cancellationToken);

                            long size = Convert.ToInt64(webClient.ResponseHeaders["Content-Length"]);

                            if (destination.Length == size)
                            {
                                LogMessage("Credential bundle has already been downloaded");
                                return true;
                            }
                        }

                        // Ensure the directory is created
                        //
                        if (!String.IsNullOrWhiteSpace(destination.DirectoryName))
                        {
                            Directory.CreateDirectory(destination.DirectoryName);
                        }

                        LogMessage("Downloading credential bundle from '{0}'", uri);

                        webClient.DownloadFileTaskAsync(uri, destination.FullName).Wait(_cancellationToken);

                        return true;
                    }
                    catch (Exception e)
                    {
                        if (e is AggregateException)
                        {
                            e = ((AggregateException)e).Flatten().InnerExceptions.Last();
                        }

                        _logInfo($"{e.Message}");

                        webClient.CancelAsync();

                        if (destination.Exists)
                        {
                            try
                            {
                                // Attempt to clean up a partially downloaded file
                                //
                                Retry(() => File.Delete(destination.FullName), TimeSpan.FromMilliseconds(200));
                            }
                            catch (Exception)
                            {
                                // Ignored
                            }
                        }

                        if (e is OperationCanceledException)
                        {
                            return false;
                        }

                        throw;
                    }
                }
            }, TimeSpan.FromSeconds(3));
        }

        private void LogMessage(string format, params object[] args)
        {
            if (_logInfo == null)
            {
                Console.WriteLine(format, args);
            }
            else
            {
                _logInfo(String.Format(format, args));
            }
        }

        private void Retry(Action action, TimeSpan retryInterval, int retryCount = 3)
        {
            Retry<object>(() =>
            {
                action();
                return null;
            }, retryInterval, retryCount);
        }

        private T Retry<T>(Func<T> action, TimeSpan retryInterval, int retryCount = 3)
        {
            List<Exception> exceptions = new List<Exception>();

            for (int retry = 0; retry < retryCount; retry++)
            {
                try
                {
                    if (retry > 0)
                    {
                        Thread.Sleep(retryInterval);
                    }

                    return action();
                }
                catch (Exception e)
                {
                    exceptions.Add(e);
                }
            }

            throw new AggregateException(exceptions);
        }

        private void Unzip(string path, string destination, params string[] patterns)
        {
            LogMessage("Unzipping credential bundle");

            ICollection<string> unzippedFiles = new List<string>();

            try
            {
                using (FileStream fileStream = File.OpenRead(path))
                {
                    using (ZipArchive archive = new ZipArchive(fileStream, ZipArchiveMode.Read, leaveOpen: false))
                    {
                        foreach (var item in archive.Entries.Where(zipArchiveEntry => patterns.Any(pattern => pattern.Equals("*") || zipArchiveEntry.Name.EndsWith(pattern, StringComparison.OrdinalIgnoreCase))).Select(zipArchiveEntry => new
                        {
                            Source = zipArchiveEntry,
                            Destination = new FileInfo(Path.Combine(destination, zipArchiveEntry.FullName)),
                        }).TakeWhile(i => !_cancellationToken.IsCancellationRequested))
                        {
                            if (!item.Destination.Exists || item.Source.Length != item.Destination.Length)
                            {
                                if (!String.IsNullOrWhiteSpace(item.Destination.DirectoryName))
                                {
                                    Directory.CreateDirectory(item.Destination.DirectoryName);
                                }

                                unzippedFiles.Add(item.Destination.FullName);

                                using (Stream readStream = item.Source.Open())
                                {
                                    using (Stream writeStream = File.Open(item.Destination.FullName, FileMode.Create, FileAccess.Write, FileShare.None))
                                    {
                                        LogMessage("Unzipping file '{0}' -> '{1}'", item.Source.FullName, item.Destination.FullName);
                                        readStream.CopyToAsync(writeStream).Wait(_cancellationToken);
                                    }
                                }

                                File.SetLastWriteTime(item.Destination.FullName, item.Source.LastWriteTime.DateTime);
                            }
                            else
                            {
                                LogMessage("File '{0}' is already up-to-date", item.Destination.FullName);
                            }
                        }
                    }
                }
            }
            catch (Exception)
            {
                // Clean up any unzipped files
                //
                Parallel.ForEach(unzippedFiles.Where(i => File.Exists(i)), unzippedFile => { Retry(() => File.Delete(unzippedFile), TimeSpan.FromMilliseconds(200)); });

                throw;
            }
        }
    }
}
