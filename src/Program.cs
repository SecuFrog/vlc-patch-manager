using Microsoft.Win32;
using System;
using System.ComponentModel;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Windows.Forms;
using System.Web.Script.Serialization;
using System.Xml.Linq;

[assembly: AssemblyTitle("VLC Patch Manager")]
[assembly: AssemblyDescription("Applies reversible, version-specific patches to VLC media player.")]
[assembly: AssemblyProduct("VLC Patch Manager")]
[assembly: AssemblyVersion("0.2.0.0")]
[assembly: AssemblyFileVersion("0.2.0.0")]

namespace VlcPatchManager
{
    internal enum PeArchitecture
    {
        Unknown,
        X86,
        X64,
        Arm64
    }

    internal sealed class VlcInstallation
    {
        public string DirectoryPath;
        public string ExecutablePath;
        public Version Version;
        public PeArchitecture Architecture;

        public override string ToString()
        {
            return string.Format("VLC {0} ({1})  -  {2}", Version, Architecture, DirectoryPath);
        }
    }

    internal sealed class PatchDefinition
    {
        public string Id;
        public string Name;
        public string Description;
        public Version RequiredVersion;
        public PeArchitecture RequiredArchitecture;
        public string PatchVersion;
        public string DownloadUrl;
        public string PackageSha256;
        public string PayloadEntry;
        public string RelativeTarget;
        public string CacheMarker;

        public bool IsCompatible(VlcInstallation vlc)
        {
            return vlc != null && vlc.Version == RequiredVersion &&
                   vlc.Architecture == RequiredArchitecture;
        }

        public string CompatibilityText(VlcInstallation vlc)
        {
            if (vlc == null)
                return "Select a VLC installation";
            if (vlc.Architecture != RequiredArchitecture)
                return "Requires " + RequiredArchitecture;
            if (vlc.Version != RequiredVersion)
                return "Requires VLC " + RequiredVersion;
            return "Compatible";
        }
    }

    internal enum PatchInstallStatus
    {
        Available,
        Installed,
        Modified,
        Incompatible
    }

    internal sealed class PatchState
    {
        public string PatchId;
        public string RelativeTarget;
        public string InstalledHash;
        public bool HadOriginal;
        public string OriginalHash;
        public string Status;
    }

    internal sealed class CatalogDocument
    {
        public int schemaVersion { get; set; }
        public string updatedUtc { get; set; }
        public CatalogPatch[] patches { get; set; }
    }

    internal sealed class CatalogPatch
    {
        public string id { get; set; }
        public string name { get; set; }
        public string description { get; set; }
        public string patchVersion { get; set; }
        public string vlcVersion { get; set; }
        public string architecture { get; set; }
        public string downloadUrl { get; set; }
        public string packageSha256 { get; set; }
        public string payloadEntry { get; set; }
        public string relativeTarget { get; set; }
        public string cacheMarker { get; set; }
    }

    internal static class PatchCatalog
    {
        public const string CatalogUrl = "https://raw.githubusercontent.com/SecuFrog/vlc-patch-manager/main/catalog/patches.json";
        private static PatchDefinition[] all = new PatchDefinition[0];
        private static bool allowLocalPackages;

        public static PatchDefinition[] All
        {
            get { return all; }
        }

        public static bool Refresh(out string message)
        {
            try
            {
                string json = DownloadText(CatalogUrl);
                PatchDefinition[] downloaded = Parse(json);
                all = downloaded;
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(CachePath));
                    File.WriteAllText(CachePath, json, new UTF8Encoding(false));
                }
                catch { }
                message = "Patch catalog updated from GitHub.";
                return true;
            }
            catch (Exception remoteError)
            {
                try
                {
                    if (!File.Exists(CachePath))
                        throw new FileNotFoundException("No cached catalog is available.");
                    all = Parse(File.ReadAllText(CachePath, Encoding.UTF8));
                    message = "GitHub is unavailable; using the cached patch catalog. " + remoteError.Message;
                    return false;
                }
                catch
                {
                    all = new PatchDefinition[0];
                    throw new InvalidOperationException("Could not load the GitHub patch catalog. " + remoteError.Message);
                }
            }
        }

        public static void LoadFromFile(string path)
        {
            allowLocalPackages = true;
            all = Parse(File.ReadAllText(path, Encoding.UTF8));
        }

        public static PatchDefinition Find(string id)
        {
            return All.FirstOrDefault(p => string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase));
        }

        private static string CachePath
        {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VlcPatchManager", "catalog.json"); }
        }

        private static string DownloadText(string url)
        {
            ServicePointManager.SecurityProtocol = (SecurityProtocolType)3072;
            using (WebClient client = NewWebClient())
                return client.DownloadString(url);
        }

        public static WebClient NewWebClient()
        {
            WebClient client = new WebClient();
            client.Headers[HttpRequestHeader.UserAgent] = "VlcPatchManager/0.2";
            return client;
        }

        private static PatchDefinition[] Parse(string json)
        {
            CatalogDocument document = new JavaScriptSerializer().Deserialize<CatalogDocument>(json);
            if (document == null || document.schemaVersion != 1 || document.patches == null)
                throw new InvalidDataException("Unsupported or invalid patch catalog.");

            List<PatchDefinition> definitions = new List<PatchDefinition>();
            HashSet<string> ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (CatalogPatch item in document.patches)
            {
                Version version;
                PeArchitecture architecture;
                if (item == null || !IsSafeId(item.id) || !ids.Add(item.id) ||
                    string.IsNullOrWhiteSpace(item.name) || string.IsNullOrWhiteSpace(item.description) ||
                    string.IsNullOrWhiteSpace(item.patchVersion) || !IsSafeCacheMarker(item.cacheMarker) ||
                    !Version.TryParse(item.vlcVersion, out version) ||
                    !Enum.TryParse<PeArchitecture>(item.architecture, true, out architecture) ||
                    !IsSha256(item.packageSha256) || !IsSafePackageEntry(item.payloadEntry) ||
                    !IsSafePluginTarget(item.relativeTarget) || !IsTrustedDownloadUrl(item.downloadUrl))
                    throw new InvalidDataException("The patch catalog contains an invalid entry.");

                definitions.Add(new PatchDefinition
                {
                    Id = item.id,
                    Name = item.name,
                    Description = item.description,
                    PatchVersion = item.patchVersion,
                    RequiredVersion = version,
                    RequiredArchitecture = architecture,
                    DownloadUrl = item.downloadUrl,
                    PackageSha256 = item.packageSha256.ToUpperInvariant(),
                    PayloadEntry = item.payloadEntry.Replace('\\', '/'),
                    RelativeTarget = item.relativeTarget.Replace('/', Path.DirectorySeparatorChar),
                    CacheMarker = item.cacheMarker
                });
            }
            return definitions.ToArray();
        }

        private static bool IsSha256(string value)
        {
            return !string.IsNullOrEmpty(value) && value.Length == 64 && value.All(Uri.IsHexDigit);
        }

        private static bool IsSafeId(string value)
        {
            return !string.IsNullOrWhiteSpace(value) && value.Length <= 100 &&
                   value.All(character => char.IsLetterOrDigit(character) || character == '-' || character == '_' || character == '.');
        }

        private static bool IsSafeCacheMarker(string value)
        {
            return !string.IsNullOrWhiteSpace(value) &&
                   string.Equals(Path.GetFileName(value), value, StringComparison.Ordinal);
        }

        private static bool IsSafePackageEntry(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return false;
            string normalized = value.Replace('\\', '/');
            return !normalized.StartsWith("/") && !normalized.Split('/').Contains("..");
        }

        private static bool IsSafePluginTarget(string value)
        {
            if (string.IsNullOrWhiteSpace(value) || Path.IsPathRooted(value)) return false;
            string normalized = value.Replace('/', '\\');
            return normalized.StartsWith("plugins\\", StringComparison.OrdinalIgnoreCase) &&
                   !normalized.Split('\\').Contains("..");
        }

        private static bool IsTrustedDownloadUrl(string value)
        {
            Uri uri;
            if (!Uri.TryCreate(value, UriKind.Absolute, out uri))
                return false;
            if (allowLocalPackages && uri.IsFile)
                return true;
            if (uri.Scheme != Uri.UriSchemeHttps)
                return false;
            if (string.Equals(uri.Host, "raw.githubusercontent.com", StringComparison.OrdinalIgnoreCase))
                return uri.AbsolutePath.StartsWith("/SecuFrog/vlc-patch-manager/", StringComparison.OrdinalIgnoreCase);
            if (string.Equals(uri.Host, "github.com", StringComparison.OrdinalIgnoreCase))
                return uri.AbsolutePath.StartsWith("/SecuFrog/vlc-patch-manager/", StringComparison.OrdinalIgnoreCase);
            return false;
        }
    }

    internal static class VlcDetector
    {
        public static List<VlcInstallation> FindAll()
        {
            HashSet<string> paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            AddRegistryPaths(paths);
            AddStandardPaths(paths);

            List<VlcInstallation> installations = new List<VlcInstallation>();
            foreach (string path in paths)
            {
                VlcInstallation installation = Inspect(path);
                if (installation != null)
                    installations.Add(installation);
            }
            return installations.OrderByDescending(v => v.Version).ThenBy(v => v.DirectoryPath).ToList();
        }

        public static VlcInstallation Inspect(string requestedPath)
        {
            if (string.IsNullOrWhiteSpace(requestedPath))
                return null;
            try
            {
                string path = requestedPath.Trim().Trim('"');
                if (File.Exists(path))
                    path = Path.GetDirectoryName(path);
                path = Path.GetFullPath(path);
                string exe = Path.Combine(path, "vlc.exe");
                if (!File.Exists(exe))
                    return null;

                FileVersionInfo info = FileVersionInfo.GetVersionInfo(exe);
                Version version = new Version(
                    Math.Max(0, info.FileMajorPart), Math.Max(0, info.FileMinorPart),
                    Math.Max(0, info.FileBuildPart), Math.Max(0, info.FilePrivatePart));
                return new VlcInstallation
                {
                    DirectoryPath = path,
                    ExecutablePath = exe,
                    Version = version,
                    Architecture = ReadArchitecture(exe)
                };
            }
            catch
            {
                return null;
            }
        }

        private static void AddStandardPaths(HashSet<string> paths)
        {
            Add(paths, Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "VideoLAN", "VLC"));
            Add(paths, Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "VideoLAN", "VLC"));
            Add(paths, Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "VideoLAN", "VLC"));

            string pathEnvironment = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
            foreach (string item in pathEnvironment.Split(Path.PathSeparator))
                Add(paths, item);
        }

        private static void AddRegistryPaths(HashSet<string> paths)
        {
            RegistryHive[] hives = { RegistryHive.LocalMachine, RegistryHive.CurrentUser };
            RegistryView[] views = { RegistryView.Registry64, RegistryView.Registry32 };
            foreach (RegistryHive hive in hives)
            foreach (RegistryView view in views)
            {
                try
                {
                    using (RegistryKey root = RegistryKey.OpenBaseKey(hive, view))
                    {
                        using (RegistryKey vlc = root.OpenSubKey(@"Software\VideoLAN\VLC"))
                        {
                            if (vlc != null)
                            {
                                Add(paths, Convert.ToString(vlc.GetValue("InstallDir")));
                                Add(paths, Convert.ToString(vlc.GetValue("InstallPath")));
                            }
                        }
                        AddUninstallPaths(root, paths);
                    }
                }
                catch { }
            }
        }

        private static void AddUninstallPaths(RegistryKey root, HashSet<string> paths)
        {
            using (RegistryKey uninstall = root.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Uninstall"))
            {
                if (uninstall == null)
                    return;
                foreach (string name in uninstall.GetSubKeyNames())
                {
                    try
                    {
                        using (RegistryKey product = uninstall.OpenSubKey(name))
                        {
                            string displayName = Convert.ToString(product.GetValue("DisplayName"));
                            if (displayName.IndexOf("VLC media player", StringComparison.OrdinalIgnoreCase) < 0)
                                continue;
                            Add(paths, Convert.ToString(product.GetValue("InstallLocation")));
                            string icon = Convert.ToString(product.GetValue("DisplayIcon"));
                            if (!string.IsNullOrWhiteSpace(icon))
                                Add(paths, Path.GetDirectoryName(icon.Trim('"').Split(',')[0]));
                        }
                    }
                    catch { }
                }
            }
        }

        private static void Add(HashSet<string> paths, string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return;
            try
            {
                string full = Path.GetFullPath(path.Trim().Trim('"'));
                if (File.Exists(Path.Combine(full, "vlc.exe")))
                    paths.Add(full);
            }
            catch { }
        }

        private static PeArchitecture ReadArchitecture(string executable)
        {
            using (FileStream stream = File.OpenRead(executable))
            using (BinaryReader reader = new BinaryReader(stream))
            {
                if (stream.Length < 64)
                    return PeArchitecture.Unknown;
                stream.Position = 0x3c;
                int peOffset = reader.ReadInt32();
                if (peOffset < 0 || peOffset + 6 > stream.Length)
                    return PeArchitecture.Unknown;
                stream.Position = peOffset;
                if (reader.ReadUInt32() != 0x00004550)
                    return PeArchitecture.Unknown;
                ushort machine = reader.ReadUInt16();
                if (machine == 0x014c) return PeArchitecture.X86;
                if (machine == 0x8664) return PeArchitecture.X64;
                if (machine == 0xaa64) return PeArchitecture.Arm64;
                return PeArchitecture.Unknown;
            }
        }
    }

    internal sealed class PatchEngine
    {
        public PatchInstallStatus GetStatus(VlcInstallation vlc, PatchDefinition patch)
        {
            if (!patch.IsCompatible(vlc))
                return PatchInstallStatus.Incompatible;
            string stateFile = GetStateFile(vlc, patch);
            if (!File.Exists(stateFile))
                return PatchInstallStatus.Available;
            try
            {
                PatchState state = LoadState(stateFile);
                ValidateState(state, patch);
                string target = Path.Combine(vlc.DirectoryPath, patch.RelativeTarget);
                if (!File.Exists(target) || !string.Equals(Hash(target), state.InstalledHash, StringComparison.OrdinalIgnoreCase))
                    return PatchInstallStatus.Modified;
                return PatchInstallStatus.Installed;
            }
            catch
            {
                return PatchInstallStatus.Modified;
            }
        }

        public void Install(VlcInstallation vlc, PatchDefinition patch)
        {
            if (!patch.IsCompatible(vlc))
                throw new InvalidOperationException(patch.CompatibilityText(vlc));
            EnsureVlcClosed(vlc);

            string stateDirectory = GetStateDirectory(vlc, patch);
            string stateFile = GetStateFile(vlc, patch);
            if (File.Exists(stateFile))
                throw new InvalidOperationException("This patch is already installed.");

            string target = Path.Combine(vlc.DirectoryPath, patch.RelativeTarget);
            string backup = Path.Combine(stateDirectory, "backup", Path.GetFileName(target));
            string temporaryPackage = Path.Combine(Path.GetTempPath(), "vlc-patch-package-" + Guid.NewGuid().ToString("N") + ".zip");
            string temporaryPayload = Path.Combine(Path.GetTempPath(), "vlc-patch-" + Guid.NewGuid().ToString("N") + ".dll");
            bool hadOriginal = File.Exists(target);
            string originalHash = hadOriginal ? Hash(target) : string.Empty;

            Directory.CreateDirectory(stateDirectory);
            Directory.CreateDirectory(Path.GetDirectoryName(target));
            if (hadOriginal)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(backup));
                File.Copy(target, backup, true);
            }

            try
            {
                DownloadPackage(patch, temporaryPackage);
                ExtractPayloadAndDocumentation(patch, temporaryPackage, temporaryPayload, stateDirectory);
                string installedHash = Hash(temporaryPayload);
                SaveState(stateFile, new PatchState
                {
                    PatchId = patch.Id,
                    RelativeTarget = patch.RelativeTarget,
                    InstalledHash = installedHash,
                    HadOriginal = hadOriginal,
                    OriginalHash = originalHash,
                    Status = "Installing"
                });

                File.Copy(temporaryPayload, target, true);
                RebuildPluginCache(vlc);
                VerifyPluginCache(vlc, patch.CacheMarker);

                SaveState(stateFile, new PatchState
                {
                    PatchId = patch.Id,
                    RelativeTarget = patch.RelativeTarget,
                    InstalledHash = installedHash,
                    HadOriginal = hadOriginal,
                    OriginalHash = originalHash,
                    Status = "Installed"
                });
            }
            catch
            {
                TryDelete(target);
                if (hadOriginal && File.Exists(backup))
                    File.Copy(backup, target, true);
                TryRebuildPluginCache(vlc);
                TryDeleteDirectory(stateDirectory);
                throw;
            }
            finally
            {
                TryDelete(temporaryPackage);
                TryDelete(temporaryPayload);
            }
        }

        public void Remove(VlcInstallation vlc, PatchDefinition patch, bool force)
        {
            EnsureVlcClosed(vlc);
            string stateDirectory = GetStateDirectory(vlc, patch);
            string stateFile = GetStateFile(vlc, patch);
            if (!File.Exists(stateFile))
                throw new InvalidOperationException("This patch is not installed by VLC Patch Manager.");

            PatchState state = LoadState(stateFile);
            ValidateState(state, patch);
            string target = Path.Combine(vlc.DirectoryPath, patch.RelativeTarget);
            string backup = Path.Combine(stateDirectory, "backup", Path.GetFileName(target));
            if (File.Exists(target) && !force &&
                !string.Equals(Hash(target), state.InstalledHash, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("The installed patch file was modified. Use forced removal to remove it anyway.");
            if (state.HadOriginal && !File.Exists(backup))
                throw new InvalidOperationException("The original VLC file backup is missing.");

            string removalBackup = Path.Combine(Path.GetTempPath(), "vlc-patch-remove-" + Guid.NewGuid().ToString("N") + ".dll");
            bool targetExisted = File.Exists(target);
            if (targetExisted)
                File.Copy(target, removalBackup, true);
            try
            {
                TryDelete(target);
                if (state.HadOriginal)
                {
                    File.Copy(backup, target, true);
                    if (!string.IsNullOrEmpty(state.OriginalHash) &&
                        !string.Equals(Hash(target), state.OriginalHash, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidOperationException("The restored VLC file did not match its original hash.");
                }

                RebuildPluginCache(vlc);
                TryDeleteDirectory(stateDirectory);
            }
            catch
            {
                TryDelete(target);
                if (targetExisted && File.Exists(removalBackup))
                    File.Copy(removalBackup, target, true);
                TryRebuildPluginCache(vlc);
                throw;
            }
            finally
            {
                TryDelete(removalBackup);
            }
        }

        public bool CanWrite(VlcInstallation vlc)
        {
            string probe = Path.Combine(vlc.DirectoryPath, ".vlc-patch-manager-" + Guid.NewGuid().ToString("N"));
            try
            {
                File.WriteAllText(probe, "probe", Encoding.ASCII);
                File.Delete(probe);
                return true;
            }
            catch
            {
                TryDelete(probe);
                return false;
            }
        }

        private static void DownloadPackage(PatchDefinition patch, string packagePath)
        {
            ServicePointManager.SecurityProtocol = (SecurityProtocolType)3072;
            using (WebClient client = PatchCatalog.NewWebClient())
                client.DownloadFile(patch.DownloadUrl, packagePath);
            string actualHash = Hash(packagePath);
            if (!string.Equals(actualHash, patch.PackageSha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The downloaded patch package failed SHA-256 verification.");
        }

        private static void ExtractPayloadAndDocumentation(PatchDefinition patch, string packagePath, string payloadPath, string stateDirectory)
        {
            using (ZipArchive archive = ZipFile.OpenRead(packagePath))
            {
                ZipArchiveEntry payload = archive.Entries.FirstOrDefault(entry =>
                    string.Equals(entry.FullName.Replace('\\', '/'), patch.PayloadEntry,
                                  StringComparison.OrdinalIgnoreCase));
                if (payload == null)
                    throw new InvalidOperationException("Patch payload is missing from its package.");
                using (Stream source = payload.Open())
                using (FileStream destination = File.Create(payloadPath))
                    source.CopyTo(destination);

                string packageDirectory = Path.Combine(stateDirectory, "package");
                foreach (ZipArchiveEntry entry in archive.Entries)
                {
                    string normalized = entry.FullName.Replace('\\', '/');
                    bool keep = normalized.Equals("README.txt", StringComparison.OrdinalIgnoreCase) ||
                                normalized.Equals("BUILD.md", StringComparison.OrdinalIgnoreCase) ||
                                normalized.Equals("THIRD-PARTY-NOTICES.txt", StringComparison.OrdinalIgnoreCase) ||
                                normalized.StartsWith("licenses/", StringComparison.OrdinalIgnoreCase) ||
                                normalized.StartsWith("source/", StringComparison.OrdinalIgnoreCase) ||
                                normalized.Equals("Uninstall-VlcTorrent.ps1", StringComparison.OrdinalIgnoreCase);
                    if (!keep || normalized.EndsWith("/"))
                        continue;
                    string output = Path.GetFullPath(Path.Combine(packageDirectory, normalized.Replace('/', Path.DirectorySeparatorChar)));
                    if (!output.StartsWith(Path.GetFullPath(packageDirectory) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidOperationException("Unsafe path in patch package.");
                    Directory.CreateDirectory(Path.GetDirectoryName(output));
                    using (Stream source = entry.Open())
                    using (FileStream destination = File.Create(output))
                        source.CopyTo(destination);
                }
            }
        }

        private static void RebuildPluginCache(VlcInstallation vlc)
        {
            string generator = Path.Combine(vlc.DirectoryPath, "vlc-cache-gen.exe");
            if (!File.Exists(generator))
                throw new FileNotFoundException("vlc-cache-gen.exe was not found.", generator);
            ProcessStartInfo start = new ProcessStartInfo(generator, Quote(Path.Combine(vlc.DirectoryPath, "plugins")));
            start.UseShellExecute = false;
            start.CreateNoWindow = true;
            start.RedirectStandardError = true;
            start.RedirectStandardOutput = true;
            using (Process process = Process.Start(start))
            {
                string output = process.StandardOutput.ReadToEnd();
                string error = process.StandardError.ReadToEnd();
                if (!process.WaitForExit(60000))
                {
                    process.Kill();
                    throw new TimeoutException("VLC plugin cache rebuild timed out.");
                }
                if (process.ExitCode != 0)
                    throw new InvalidOperationException("VLC plugin cache rebuild failed. " + output + " " + error);
            }
        }

        private static void VerifyPluginCache(VlcInstallation vlc, string marker)
        {
            string cache = Path.Combine(vlc.DirectoryPath, "plugins", "plugins.dat");
            if (!File.Exists(cache))
                throw new InvalidOperationException("VLC did not create plugins.dat.");
            string text = Encoding.ASCII.GetString(File.ReadAllBytes(cache));
            if (text.IndexOf(marker, StringComparison.OrdinalIgnoreCase) < 0)
                throw new InvalidOperationException("VLC skipped the patch while rebuilding its plugin cache.");
        }

        private static void EnsureVlcClosed(VlcInstallation vlc)
        {
            foreach (Process process in Process.GetProcessesByName("vlc"))
            {
                try
                {
                    if (string.Equals(process.MainModule.FileName, vlc.ExecutablePath, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidOperationException("Close VLC before changing patches.");
                }
                catch (InvalidOperationException) { throw; }
                catch { }
                finally { process.Dispose(); }
            }
        }

        private static string GetStateDirectory(VlcInstallation vlc, PatchDefinition patch)
        {
            return Path.Combine(vlc.DirectoryPath, "vlc-patch-manager", "patches", patch.Id);
        }

        private static string GetStateFile(VlcInstallation vlc, PatchDefinition patch)
        {
            return Path.Combine(GetStateDirectory(vlc, patch), "state.xml");
        }

        private static void SaveState(string path, PatchState state)
        {
            XDocument document = new XDocument(new XElement("PatchState",
                new XElement("PatchId", state.PatchId),
                new XElement("RelativeTarget", state.RelativeTarget),
                new XElement("InstalledHash", state.InstalledHash),
                new XElement("HadOriginal", state.HadOriginal),
                new XElement("OriginalHash", state.OriginalHash ?? string.Empty),
                new XElement("Status", state.Status)));
            document.Save(path);
        }

        private static PatchState LoadState(string path)
        {
            XDocument document = XDocument.Load(path);
            XElement root = document.Root;
            return new PatchState
            {
                PatchId = Value(root, "PatchId"),
                RelativeTarget = Value(root, "RelativeTarget"),
                InstalledHash = Value(root, "InstalledHash"),
                HadOriginal = bool.Parse(Value(root, "HadOriginal")),
                OriginalHash = Value(root, "OriginalHash"),
                Status = Value(root, "Status")
            };
        }

        private static void ValidateState(PatchState state, PatchDefinition patch)
        {
            if (!string.Equals(state.PatchId, patch.Id, StringComparison.Ordinal) ||
                !string.Equals(state.RelativeTarget, patch.RelativeTarget, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Patch state does not match the selected patch definition.");
        }

        private static string Value(XElement root, string name)
        {
            XElement element = root.Element(name);
            if (element == null)
                throw new InvalidDataException("Invalid patch state: " + name + " is missing.");
            return element.Value;
        }

        private static string Hash(string path)
        {
            using (SHA256 algorithm = SHA256.Create())
            using (FileStream stream = File.OpenRead(path))
                return BitConverter.ToString(algorithm.ComputeHash(stream)).Replace("-", string.Empty);
        }

        private static string Quote(string value)
        {
            return "\"" + value.Replace("\"", "\\\"") + "\"";
        }

        private static void TryRebuildPluginCache(VlcInstallation vlc)
        {
            try { RebuildPluginCache(vlc); } catch { }
        }

        private static void TryDelete(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); } catch { }
        }

        private static void TryDeleteDirectory(string path)
        {
            try { if (Directory.Exists(path)) Directory.Delete(path, true); } catch { }
        }
    }

    internal sealed class MainForm : Form
    {
        private readonly ComboBox vlcSelector = new ComboBox();
        private readonly DataGridView patchGrid = new DataGridView();
        private readonly Button browseButton = new Button();
        private readonly Button refreshButton = new Button();
        private readonly Button installButton = new Button();
        private readonly Button removeButton = new Button();
        private readonly Label summaryLabel = new Label();
        private readonly Label messageLabel = new Label();
        private readonly PatchEngine engine = new PatchEngine();
        private List<VlcInstallation> installations = new List<VlcInstallation>();
        private bool catalogLoading;

        public MainForm()
        {
            Text = "VLC Patch Manager";
            StartPosition = FormStartPosition.CenterScreen;
            MinimumSize = new Size(760, 440);
            Size = new Size(880, 520);
            Font = new Font("Segoe UI", 9F);
            BackColor = Color.FromArgb(247, 248, 250);
            AutoScaleMode = AutoScaleMode.Dpi;
            BuildInterface();
            Shown += delegate
            {
                RefreshInstallations(null);
                BeginCatalogRefresh();
            };
        }

        private void BuildInterface()
        {
            Panel header = new Panel { Dock = DockStyle.Top, Height = 82, Padding = new Padding(20, 14, 20, 8), BackColor = Color.White };
            Label title = new Label { Text = "VLC Patch Manager", Font = new Font("Segoe UI Semibold", 16F), AutoSize = true, Location = new Point(20, 12) };
            summaryLabel.AutoSize = true;
            summaryLabel.ForeColor = Color.FromArgb(90, 96, 106);
            summaryLabel.Location = new Point(22, 48);
            header.Controls.Add(title);
            header.Controls.Add(summaryLabel);

            Panel selectorPanel = new Panel { Dock = DockStyle.Top, Height = 72, Padding = new Padding(20, 18, 20, 10) };
            Label vlcLabel = new Label { Text = "VLC installation", AutoSize = true, Location = new Point(20, 4) };
            vlcSelector.DropDownStyle = ComboBoxStyle.DropDownList;
            vlcSelector.Anchor = AnchorStyles.Left | AnchorStyles.Top;
            vlcSelector.Location = new Point(20, 26);
            vlcSelector.Width = 650;
            vlcSelector.SelectedIndexChanged += delegate { PopulatePatches(); };
            browseButton.Text = "Browse...";
            browseButton.Anchor = AnchorStyles.Top;
            browseButton.Size = new Size(86, 28);
            browseButton.Location = new Point(ClientSize.Width - 202, 25);
            browseButton.Click += BrowseClicked;
            refreshButton.Text = "Refresh";
            refreshButton.Anchor = AnchorStyles.Top;
            refreshButton.Size = new Size(82, 28);
            refreshButton.Location = new Point(ClientSize.Width - 108, 25);
            refreshButton.Click += delegate
            {
                RefreshInstallations(SelectedVlc == null ? null : SelectedVlc.DirectoryPath);
                BeginCatalogRefresh();
            };
            selectorPanel.Controls.Add(vlcLabel);
            selectorPanel.Controls.Add(vlcSelector);
            selectorPanel.Controls.Add(browseButton);
            selectorPanel.Controls.Add(refreshButton);
            selectorPanel.Resize += delegate
            {
                refreshButton.Left = selectorPanel.ClientSize.Width - 20 - refreshButton.Width;
                browseButton.Left = refreshButton.Left - 10 - browseButton.Width;
                vlcSelector.Width = Math.Max(260, browseButton.Left - 30);
            };

            patchGrid.Dock = DockStyle.Fill;
            patchGrid.BackgroundColor = Color.White;
            patchGrid.BorderStyle = BorderStyle.FixedSingle;
            patchGrid.AllowUserToAddRows = false;
            patchGrid.AllowUserToDeleteRows = false;
            patchGrid.AllowUserToResizeRows = false;
            patchGrid.RowHeadersVisible = false;
            patchGrid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
            patchGrid.MultiSelect = false;
            patchGrid.AutoSizeRowsMode = DataGridViewAutoSizeRowsMode.AllCells;
            patchGrid.DefaultCellStyle.WrapMode = DataGridViewTriState.True;
            patchGrid.DefaultCellStyle.Padding = new Padding(5);
            patchGrid.ColumnHeadersHeight = 34;
            patchGrid.Columns.Add(new DataGridViewCheckBoxColumn { Name = "Selected", HeaderText = "", Width = 42 });
            patchGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Patch", HeaderText = "Patch", Width = 190, ReadOnly = true });
            patchGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Status", HeaderText = "Status", Width = 120, ReadOnly = true });
            patchGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Details", HeaderText = "Details", AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill, ReadOnly = true });
            patchGrid.CurrentCellDirtyStateChanged += delegate { if (patchGrid.IsCurrentCellDirty) patchGrid.CommitEdit(DataGridViewDataErrorContexts.Commit); };
            patchGrid.CellValueChanged += delegate { UpdateButtons(); };

            Panel gridHost = new Panel { Dock = DockStyle.Fill, Padding = new Padding(20, 4, 20, 12) };
            gridHost.Controls.Add(patchGrid);

            Panel footer = new Panel { Dock = DockStyle.Bottom, Height = 72, Padding = new Padding(20, 14, 20, 12), BackColor = Color.White };
            messageLabel.AutoEllipsis = true;
            messageLabel.ForeColor = Color.FromArgb(90, 96, 106);
            messageLabel.Location = new Point(20, 24);
            messageLabel.Anchor = AnchorStyles.Left | AnchorStyles.Top;
            messageLabel.Width = 500;
            installButton.Text = "Install selected";
            installButton.Anchor = AnchorStyles.Top;
            installButton.Size = new Size(126, 34);
            installButton.Location = new Point(ClientSize.Width - 286, 18);
            installButton.BackColor = Color.FromArgb(30, 107, 214);
            installButton.ForeColor = Color.White;
            installButton.FlatStyle = FlatStyle.Flat;
            installButton.FlatAppearance.BorderSize = 0;
            installButton.Click += delegate { ApplySelected(false); };
            removeButton.Text = "Remove selected";
            removeButton.Anchor = AnchorStyles.Top;
            removeButton.Size = new Size(126, 34);
            removeButton.Location = new Point(ClientSize.Width - 148, 18);
            removeButton.Click += delegate { ApplySelected(true); };
            footer.Controls.Add(messageLabel);
            footer.Controls.Add(installButton);
            footer.Controls.Add(removeButton);
            footer.Resize += delegate
            {
                removeButton.Left = footer.ClientSize.Width - 20 - removeButton.Width;
                installButton.Left = removeButton.Left - 12 - installButton.Width;
                messageLabel.Width = Math.Max(180, installButton.Left - 40);
            };

            Controls.Add(gridHost);
            Controls.Add(selectorPanel);
            Controls.Add(header);
            Controls.Add(footer);
        }

        private VlcInstallation SelectedVlc
        {
            get { return vlcSelector.SelectedItem as VlcInstallation; }
        }

        private void RefreshInstallations(string preservePath)
        {
            installations = VlcDetector.FindAll();
            vlcSelector.Items.Clear();
            foreach (VlcInstallation installation in installations)
                vlcSelector.Items.Add(installation);
            if (installations.Count == 0)
            {
                summaryLabel.Text = "No VLC installation detected. Use Browse to select a portable copy.";
                PopulatePatches();
                return;
            }
            int selected = 0;
            if (!string.IsNullOrEmpty(preservePath))
            {
                int match = installations.FindIndex(v => string.Equals(v.DirectoryPath, preservePath, StringComparison.OrdinalIgnoreCase));
                if (match >= 0) selected = match;
            }
            vlcSelector.SelectedIndex = selected;
            summaryLabel.Text = installations.Count == 1 ? "1 VLC installation detected" : installations.Count + " VLC installations detected";
        }

        private void BrowseClicked(object sender, EventArgs e)
        {
            using (FolderBrowserDialog dialog = new FolderBrowserDialog())
            {
                dialog.Description = "Select the folder containing vlc.exe";
                dialog.ShowNewFolderButton = false;
                if (dialog.ShowDialog(this) != DialogResult.OK)
                    return;
                VlcInstallation installation = VlcDetector.Inspect(dialog.SelectedPath);
                if (installation == null)
                {
                    MessageBox.Show(this, "vlc.exe was not found in that folder.", Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }
                if (!installations.Any(v => string.Equals(v.DirectoryPath, installation.DirectoryPath, StringComparison.OrdinalIgnoreCase)))
                {
                    installations.Add(installation);
                    vlcSelector.Items.Add(installation);
                }
                vlcSelector.SelectedItem = installations.First(v => string.Equals(v.DirectoryPath, installation.DirectoryPath, StringComparison.OrdinalIgnoreCase));
                summaryLabel.Text = "Portable or custom VLC selected";
            }
        }

        private void PopulatePatches()
        {
            patchGrid.Rows.Clear();
            VlcInstallation vlc = SelectedVlc;
            foreach (PatchDefinition patch in PatchCatalog.All)
            {
                PatchInstallStatus status = engine.GetStatus(vlc, patch);
                string statusText = status == PatchInstallStatus.Incompatible ? patch.CompatibilityText(vlc) : status.ToString();
                bool selected = status != PatchInstallStatus.Incompatible;
                string details = string.IsNullOrEmpty(patch.PatchVersion)
                    ? patch.Description
                    : "Patch " + patch.PatchVersion + ". " + patch.Description;
                int index = patchGrid.Rows.Add(selected, patch.Name, statusText, details);
                DataGridViewRow row = patchGrid.Rows[index];
                row.Tag = patch;
                if (status == PatchInstallStatus.Incompatible)
                {
                    row.Cells[0].ReadOnly = true;
                    row.DefaultCellStyle.ForeColor = Color.Gray;
                }
            }
            if (catalogLoading)
                messageLabel.Text = "Checking GitHub for available patches...";
            else if (PatchCatalog.All.Length == 0)
                messageLabel.Text = "No patch catalog is available. Check your internet connection and refresh.";
            else
                messageLabel.Text = vlc == null ? "Select VLC to see compatible patches." : "Compatible patches are selected automatically.";
            UpdateButtons();
        }

        private void BeginCatalogRefresh()
        {
            if (catalogLoading)
                return;
            catalogLoading = true;
            refreshButton.Enabled = false;
            PopulatePatches();

            BackgroundWorker worker = new BackgroundWorker();
            worker.DoWork += delegate(object sender, DoWorkEventArgs e)
            {
                string status;
                bool current = PatchCatalog.Refresh(out status);
                e.Result = new object[] { current, status };
            };
            worker.RunWorkerCompleted += delegate(object sender, RunWorkerCompletedEventArgs e)
            {
                catalogLoading = false;
                refreshButton.Enabled = true;
                if (e.Error != null)
                {
                    PopulatePatches();
                    messageLabel.Text = e.Error.Message;
                }
                else
                {
                    object[] result = (object[])e.Result;
                    PopulatePatches();
                    if (!(bool)result[0])
                        messageLabel.Text = (string)result[1];
                }
                worker.Dispose();
            };
            worker.RunWorkerAsync();
        }

        private List<PatchDefinition> CheckedPatches()
        {
            List<PatchDefinition> patches = new List<PatchDefinition>();
            foreach (DataGridViewRow row in patchGrid.Rows)
            {
                bool selected = Convert.ToBoolean(row.Cells[0].Value ?? false);
                if (selected)
                    patches.Add((PatchDefinition)row.Tag);
            }
            return patches;
        }

        private void UpdateButtons()
        {
            VlcInstallation vlc = SelectedVlc;
            List<PatchDefinition> selected = CheckedPatches();
            installButton.Enabled = vlc != null && selected.Any(p => engine.GetStatus(vlc, p) == PatchInstallStatus.Available);
            removeButton.Enabled = vlc != null && selected.Any(p =>
                engine.GetStatus(vlc, p) == PatchInstallStatus.Installed || engine.GetStatus(vlc, p) == PatchInstallStatus.Modified);
        }

        private void ApplySelected(bool remove)
        {
            VlcInstallation vlc = SelectedVlc;
            if (vlc == null)
                return;
            List<PatchDefinition> selected = CheckedPatches();
            PatchDefinition patch = selected.FirstOrDefault(p => remove
                ? engine.GetStatus(vlc, p) == PatchInstallStatus.Installed || engine.GetStatus(vlc, p) == PatchInstallStatus.Modified
                : engine.GetStatus(vlc, p) == PatchInstallStatus.Available);
            if (patch == null)
                return;

            bool force = false;
            if (remove && engine.GetStatus(vlc, patch) == PatchInstallStatus.Modified)
            {
                DialogResult answer = MessageBox.Show(this,
                    "The patch file was modified after installation. Remove it anyway?",
                    Text, MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
                if (answer != DialogResult.Yes)
                    return;
                force = true;
            }

            Enabled = false;
            Cursor = Cursors.WaitCursor;
            try
            {
                if (!engine.CanWrite(vlc))
                {
                    int code = RunElevated(remove ? "remove" : "install", patch, vlc, force);
                    if (code != 0)
                        throw new InvalidOperationException("The administrator operation did not complete.");
                }
                else if (remove)
                    engine.Remove(vlc, patch, force);
                else
                    engine.Install(vlc, patch);

                MessageBox.Show(this, patch.Name + (remove ? " was removed." : " was installed."), Text,
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally
            {
                Cursor = Cursors.Default;
                Enabled = true;
                PopulatePatches();
            }
        }

        private static int RunElevated(string operation, PatchDefinition patch, VlcInstallation vlc, bool force)
        {
            string arguments = string.Format("--{0} {1} --vlc \"{2}\"{3}", operation, patch.Id,
                vlc.DirectoryPath.Replace("\"", "\\\""), force ? " --force" : string.Empty);
            ProcessStartInfo start = new ProcessStartInfo(Application.ExecutablePath, arguments);
            start.UseShellExecute = true;
            start.Verb = "runas";
            start.WindowStyle = ProcessWindowStyle.Hidden;
            using (Process process = Process.Start(start))
            {
                process.WaitForExit();
                return process.ExitCode;
            }
        }
    }

    internal static class Program
    {
        [STAThread]
        private static int Main(string[] args)
        {
            if (args.Length > 0)
                return RunCommand(args);
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new MainForm());
            return 0;
        }

        private static int RunCommand(string[] args)
        {
            try
            {
                if (args.Length == 1 && args[0] == "--diagnostics")
                {
                    foreach (VlcInstallation detectedVlc in VlcDetector.FindAll())
                        Console.WriteLine(detectedVlc);
                    Console.WriteLine("Catalog URL: " + PatchCatalog.CatalogUrl);
                    return 0;
                }

                bool install = args.Contains("--install");
                bool remove = args.Contains("--remove");
                string patchId = ValueAfter(args, install ? "--install" : "--remove");
                string vlcPath = ValueAfter(args, "--vlc");
                string catalogFile = ValueAfter(args, "--catalog-file");
                bool force = args.Contains("--force");
                if ((!install && !remove) || string.IsNullOrEmpty(patchId) || string.IsNullOrEmpty(vlcPath))
                    return 2;

                if (!string.IsNullOrEmpty(catalogFile))
                    PatchCatalog.LoadFromFile(catalogFile);
                else
                {
                    string catalogMessage;
                    PatchCatalog.Refresh(out catalogMessage);
                }

                PatchDefinition patch = PatchCatalog.Find(patchId);
                VlcInstallation vlc = VlcDetector.Inspect(vlcPath);
                if (patch == null || vlc == null)
                    return 3;
                PatchEngine engine = new PatchEngine();
                if (install) engine.Install(vlc, patch);
                else engine.Remove(vlc, patch, force);
                return 0;
            }
            catch
            {
                return 1;
            }
        }

        private static string ValueAfter(string[] args, string key)
        {
            int index = Array.IndexOf(args, key);
            return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
        }
    }
}
