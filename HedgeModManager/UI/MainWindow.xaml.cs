﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using HedgeModManager.UI;
using System.Collections.ObjectModel;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using Application = System.Windows.Application;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using ListViewItem = System.Windows.Controls.ListViewItem;
using MenuItem = System.Windows.Controls.MenuItem;
using Timer = System.Threading.Timer;
using HMMResources = HedgeModManager.Properties.Resources;
using static HedgeModManager.Lang;
using HedgeModManager.Languages;
using HedgeModManager.UI.Models;
using HedgeModManager.Updates;
using Newtonsoft.Json;
using System.Windows.Data;
using GameBananaAPI;
using HedgeModManager.GitHub;
using HedgeModManager.Misc;
using HedgeModManager.Exceptions;

namespace HedgeModManager
{
    /// <summary>
    /// Interaction logic for MainWindow.xaml
    /// </summary>
    public partial class MainWindow : Window
    {
        public static bool IsCPKREDIRInstalled = false;
        public static ModsDB ModsDatabase;

        public static CodeFile CodesDatabase
        {
            get => ModsDatabase?.CodesDatabase;
            set => ModsDatabase.CodesDatabase = value;
        }

        public static List<FileSystemWatcher> ModsWatchers = new List<FileSystemWatcher>();
        public MainWindowViewModel ViewModel = new MainWindowViewModel();
        public bool CheckingForUpdates = false;
        public ModProfile SelectedModProfile = null;
        public CancellationTokenSource ContextCancelSource { get; set; }

        protected List<Task> Tasks { get; set; } = new List<Task>(4);
        protected Timer StatusTimer;

        private bool CodesOutdated = false;

        public ILogger StatusLog { get; }

        public MainWindow()
        {
            StatusLog = new StatusLogger(this);
            InitializeComponent();
        }

        public async Task RunTask(Task task)
        {
            lock (Tasks)
                Tasks.Add(task);

            await task;

            lock (Tasks)
                Tasks.Remove(task);
        }

        public Task WaitTasks()
        {
            lock (Tasks)
                return Task.WhenAll(Tasks);
        }

        public void Refresh()
        {
            RefreshMods();
            RefreshUI();
        }

        public void RefreshProfiles()
        {
            HedgeApp.ModProfiles.Clear();
            try
            {
                string profilePath = Path.Combine(HedgeApp.StartDirectory, "profiles.json");
                if (File.Exists(profilePath))
                    HedgeApp.ModProfiles = JsonConvert.DeserializeObject<List<ModProfile>>(File.ReadAllText(profilePath));

                HedgeApp.ModProfiles ??= new List<ModProfile>();

                // Remove profiles that don't exist
                HedgeApp.ModProfiles.RemoveAll(profile =>
                    !File.Exists(Path.Combine(HedgeApp.ModsDbPath, profile.ModDBPath)));

                // Create new profile set if needed
                if (HedgeApp.ModProfiles.Count == 0)
                    HedgeApp.ModProfiles.Add(new ModProfile("Default", "ModsDB.ini"));

                SelectedModProfile = HedgeApp.ModProfiles.FirstOrDefault(t => t.Name == HedgeApp.Config.ModProfile)
                    ?? HedgeApp.ModProfiles.First();
            }
            catch (Exception e)
            {
                new ExceptionWindow(e).ShowDialog();
                HedgeApp.ModProfiles ??= new List<ModProfile>();
                if (HedgeApp.ModProfiles.Count == 0)
                    HedgeApp.ModProfiles.Add(new ModProfile("Default", "ModsDB.ini"));
                SelectedModProfile = HedgeApp.ModProfiles.First();
            }
        }

        public void LoadDatabase()
        {
            ModsDatabase = new ModsDB(HedgeApp.ModsDbPath, SelectedModProfile.ModDBPath);
            if (!Directory.Exists(HedgeApp.ModsDbPath))
            {
                try
                {
                    ModsDatabase.SetupFirstTime();
                }catch
                {
                    Application.Current?.MainWindow?.Hide();
                    HedgeApp.CreateOKMessageBox(Localise("CommonUIError"),
                        string.Format(Localise("DialogUINoGameDirAccess"), HedgeApp.CurrentGameInstall.GameDirectory))
                        .ShowDialog();
                    Environment.Exit(0);
                }
            }
        }

        public void RefreshMods()
        {
            CodesList.Items.Clear();

            LoadDatabase();
            ModsDatabase.Mods.Sort((x, y) => x.Title.CompareTo(y.Title));

            CodesDatabase = CodeFile.FromFiles(CodeProvider.CodesTextPath, CodeProvider.ExtraCodesTextPath);
            ModsDatabase.Codes.ForEach((x) =>
            {
                var code = CodesDatabase.Codes.Find((y) => { return y.Name == x; });
                if (code != null)
                    code.Enabled = true;
            });

            CodesDatabase.Codes.Sort((x, y) => x.Name.CompareTo(y.Name));

            CodesDatabase.Codes.ForEach((x) =>
            {
                if (x.Enabled)
                    CodesList.Items.Insert(0, x);
                else
                    CodesList.Items.Add(x);
            });

            UpdateStatus(LocaliseFormat("StatusUILoadedMods", ModsDatabase.Mods.Count));
            CheckCodeCompatibility();
            var invalid = ModsDatabase.GetInvalidMods();
            if (invalid.Count > 0)
            {
                var messageBuilder = new StringBuilder();

                foreach (var mod in invalid)
                {
                    messageBuilder.AppendLine($"· {mod.Title}");
                }

                var box = new HedgeMessageBox(Localise("ModsUIInvalidIncludeDirs"), messageBuilder.ToString(), textAlignment: TextAlignment.Left);
                box.AddButton(Localise("CommonUIOK"), () =>
                {
                    foreach (var mod in invalid)
                    {
                        mod.FixIncludeDirectories();
                        mod.Save();
                    }

                    box.Close();
                });

                box.AddButton(Localise("CommonUICancel"), box.Close);
                box.ShowDialog();
            }
        }

        public void RefreshUI()
        {
            // Re-arrange the mods
            for (int i = 0; i < ModsDatabase.ActiveMods.Count; i++)
            {
                var mod = ModsDatabase.GetModFromActiveGUID(ModsDatabase.ActiveMods[i]);

                if (mod != null)
                {
                    ModsDatabase.Mods.Remove(mod);
                    ModsDatabase.Mods.Insert(i, mod);
                }
            }

            for (int i = 0; i < ModsDatabase.Mods.Count; i++)
            {
                var mod = ModsDatabase.Mods[i];
                if (mod.Favorite && !mod.Enabled)
                {
                    ModsDatabase.Mods.Remove(mod);
                    ModsDatabase.Mods.Insert(ModsDatabase.ActiveMods.Count, mod);
                }
            }

            // Sets the DataContext for all the Components
            ViewModel = new MainWindowViewModel
            {
                CPKREDIR = HedgeApp.Config,
                ModsDB = ModsDatabase,
                Games = HedgeApp.GameInstalls,
                Mods = new ObservableCollection<ModInfo>(ModsDatabase.Mods),
                Profiles = new ObservableCollection<ModProfile>(HedgeApp.ModProfiles),
                SelectedModProfile = SelectedModProfile,
                DevBuild = !string.IsNullOrEmpty(HedgeApp.RepoCommit)
            };
            if (ModsFind.Visibility == Visibility.Visible)
            {
                FilterMods(TextBox_ModsSearch.Text.ToLowerInvariant());
            }

            DataContext = ViewModel;

            Title = $"{HedgeApp.ProgramName} ({HedgeApp.VersionString}) - {HedgeApp.CurrentGame} ({SelectedModProfile?.Name})";

            if (HedgeApp.CurrentGame.ModLoader != null)
            {
                Button_OtherLoader.IsEnabled = true;
                Button_DownloadCodes.IsEnabled = !string.IsNullOrEmpty(HedgeApp.CurrentGame.CodesURL);
            }

            var exeDir = HedgeApp.StartDirectory;
            var modloader = HedgeApp.CurrentGame.ModLoader;
            bool hasOtherModLoader = modloader != null && File.Exists(Path.Combine(exeDir, modloader.ModLoaderFileName));
            IsCPKREDIRInstalled = HedgeApp.CurrentGame.SupportsCPKREDIR ? HedgeApp.IsCPKREDIRInstalled(Path.Combine(exeDir, HedgeApp.CurrentGame.ExecutableName)) : hasOtherModLoader;

            ComboBox_GameStatus.SelectedValue = HedgeApp.CurrentGameInstall;
            Button_OtherLoader.Content = Localise(hasOtherModLoader ? "SettingsUIUninstallLoader" : "SettingsUIInstallLoader");
        }

        public void FilterCodes(string text)
        {
            CodesList.Items.Clear();
            foreach (Code code in CodesDatabase.Codes)
                if (code.Name.ToLowerInvariant().Contains(text) ||
                    (code.Author != null && code.Author.ToLowerInvariant().Contains(text)))
                    if (code.Enabled)
                        CodesList.Items.Insert(0, code);
                    else
                        CodesList.Items.Add(code);
        }

        public void FilterMods(string text)
        {
            ViewModel.ModsSearch = new ObservableCollection<ModInfo>(ViewModel.Mods);
            for (int i = 0; i < ViewModel.ModsSearch.Count; ++i)
            {
                if (!(ViewModel.ModsSearch[i].Title.ToLowerInvariant().Contains(text) ||
                    ViewModel.ModsSearch[i].Author.ToLowerInvariant().Contains(text)))
                    ViewModel.ModsSearch.RemoveAt(i--);
            }
            string path = BindingOperations.GetBinding(ModsList, ListView.ItemsSourceProperty).Path.Path;
            string newPath = text.Length == 0 ? "Mods" : "ModsSearch";
            if (path != newPath)
            {
                Binding binding = new Binding();
                binding.Path = new PropertyPath(newPath);
                ModsList.SetBinding(ListView.ItemsSourceProperty, binding);
                ModsList.SetValue(GongSolutions.Wpf.DragDrop.DragDrop.IsDragSourceProperty, text.Length == 0 ? true : false);
                ModsList.SetValue(GongSolutions.Wpf.DragDrop.DragDrop.IsDropTargetProperty, text.Length == 0 ? true : false);
            }
        }

        private void UI_CodesTab_Click(object sender, RoutedEventArgs e)
        {
            if (CodesDatabase == null || CodesDatabase.Codes.Count == 0)
            {
                CodesStatusLbl.Visibility = Visibility.Visible;
                return;
            }

            // Display update alert.
            UpdateStatus(Localise(CodesOutdated ? "StatusUICodeUpdatesAvailable" : "StatusUINoCodeUpdatesFound"));
        }

        private async void OnFetchStatusVisibilityChanged(object sender, DependencyPropertyChangedEventArgs e)
        {
            if (!CodesTab.IsSelected)
                return;

            if (CodesStatusLbl.Visibility != Visibility.Visible)
                return;

            ContextCancelSource ??= new CancellationTokenSource();
            await RunTask(FetchCodesAsync(ContextCancelSource.Token));
        }

        public async Task FetchCodesAsync(CancellationToken token = default)
        {
            bool isFetching = false;
            Dispatcher.Invoke(() => isFetching = !Button_DownloadCodes.IsEnabled);
            if (isFetching)
                return;

            Dispatcher.Invoke(() => Button_DownloadCodes.IsEnabled = false);

            try
            {
                await Singleton.GetInstance<HttpClient>().DownloadFileAsync(HedgeApp.CurrentGame.CodesURL,
                    CodeProvider.CodesTextPath, null, token);

                Dispatcher.Invoke(Refresh);
            }
            catch (Exception)
            {
                Dispatcher.Invoke(() =>
                {
                    CodesStatusLbl.Visibility = Visibility.Hidden;
                    Button_DownloadCodes.IsEnabled = true;
                });
            }
            finally
            {
                Dispatcher.Invoke(() => Button_DownloadCodes.IsEnabled = true);
            }
        }

        public async Task CheckForCodeUpdates()
        {
            if (!File.Exists(CodeProvider.CodesTextPath))
                return;

            try
            {
                // Codes from disk.
                string localCodes = File.ReadAllText(CodeProvider.CodesTextPath);
                string repoCodes = await Singleton.GetInstance<HttpClient>().GetStringAsync(HedgeApp.CurrentGame.CodesURL);

                if (localCodes == repoCodes)
                {
                    CodesOutdated = false;

                    // Codes are the same, so use default text.
                    Button_DownloadCodes.Content = Localise("CodesUIDownload");
                }
                else
                {
                    CodesOutdated = true;

                    // Codes are different, report update possibility.
                    Button_DownloadCodes.Content = Localise("CodesUIUpdate");
                }
            }
            catch (HttpRequestException) { /* do nothing for http exceptions */ }
        }

        public async Task<bool> CheckForModUpdatesAsync(ModInfo mod, CancellationToken cancellationToken = default)
        {
            if (!mod.HasUpdates || Singleton.GetInstance<NetworkConfig>().IsServerBlocked(mod.UpdateServer))
                return false;

            CheckingForUpdates = true;
            UpdateStatus(string.Format(Localise("StatusUICheckingModUpdates"), mod.Title));
            mod.UpdateStatus = ModUpdateFetcher.Status.BeginCheck;
            var result = await ModUpdateFetcher.FetchUpdate(mod, Singleton.GetInstance<NetworkConfig>(), cancellationToken);

            CheckingForUpdates = false;
            bool doUpdate = result.UpdateInfo != null;

            mod.UpdateStatus = result.Status;
            switch (result.Status)
            {
                case ModUpdateFetcher.Status.Failed:
                    UpdateStatus(LocaliseFormat("StatusUIFailedToUpdate", mod.Title, result.FailException?.Message));
                    break;

                case ModUpdateFetcher.Status.UpToDate:
                    UpdateStatus(LocaliseFormat("DialogUIModNewest", mod.Title));
                    break;
            }

            if (!doUpdate)
                return false;

            var model = new ModUpdatesWindowViewModel(new[] { result.UpdateInfo });
            Dispatcher.Invoke(model.ShowDialog);
            return model.Mods.Count != 0;
        }

        public async Task CheckAllModsUpdatesAsync(CancellationToken cancellationToken = default)
        {
            var updateMods = ModsDatabase.Where(x => x.HasUpdates).ToList();
            int completedCount = 0;
            int failedCount = 0;

            CheckingForUpdates = true;
            var updates = await ModUpdateFetcher.FetchUpdates(updateMods, Singleton.GetInstance<NetworkConfig>(),
                (mod, status, exception) =>
                {
                    mod.UpdateStatus = status;
                    switch (status)
                    {
                        case ModUpdateFetcher.Status.BeginCheck:
                            UpdateStatus(LocaliseFormat("StatusUICheckingModUpdates", mod.Title));
                            break;

                        case ModUpdateFetcher.Status.Success:
                            completedCount++;
                            break;

                        case ModUpdateFetcher.Status.Failed:
                            UpdateStatus(LocaliseFormat("StatusUIFailedToUpdate", mod.Title, exception?.Message));
                            failedCount++;
                            break;
                    }
                }, cancellationToken);

            // Language Workaround
            string completedText = completedCount == 1 ? Localise("StatusUIUpdateCompletedSingular") : Localise("StatusUIUpdateCompletedPlural");
            string failedText = failedCount == 1 ? Localise("StatusUIUpdateFailedSingular") : Localise("StatusUIUpdateFailedPlural");
            string text = string.Format(Localise("StatusUIModUpdateCheckFinish"), completedCount, failedCount, completedText, failedText);
            UpdateStatus(text);
            CheckingForUpdates = false;

            if (updates.Count == 0)
                return;

            // Only update mods that weren't deleted
            var existingMods = updates.Where(u => Directory.Exists(u.Mod.RootDirectory)).ToList();
            if (existingMods.Count == 0)
                return;

            await WaitTasks();
            var model = new ModUpdatesWindowViewModel(existingMods);
            Dispatcher.Invoke(model.ShowDialog);

            if (model.Mods.Count == 0)
                return;

            Dispatcher.Invoke(RefreshMods);
            Dispatcher.Invoke(RefreshUI);
        }

        public async Task SaveModsDB()
        {
            HedgeApp.Config.ModsDbIni = Path.Combine(HedgeApp.ModsDbPath, SelectedModProfile.ModDBPath);
            try
            {
                HedgeApp.Config.Save(HedgeApp.ConfigPath);
                ModsDatabase.Mods.Clear();
                ModsDatabase.Codes.Clear();

                foreach (var mod in ViewModel.Mods)
                {
                    ModsDatabase.Mods.Add(mod);
                }

                foreach (var code in CodesDatabase.Codes)
                {
                    if (code.Enabled)
                    {
                        ModsDatabase.Codes.Add(code.Name);
                    }
                }

                await ModsDatabase.SaveDB();
            }
            catch (UnauthorizedAccessException)
            {
                HedgeApp.CreateOKMessageBox(Localise("CommonUIError"),
                    string.Format(Localise("DialogUINoGameDirAccess"), HedgeApp.CurrentGameInstall.GameDirectory))
                    .ShowDialog();
            }
        }

        public Task StartGame()
        {
            Process.Start(new ProcessStartInfo(Path.Combine(HedgeApp.StartDirectory, HedgeApp.CurrentGame.ExecutableName))
            {
                WorkingDirectory = HedgeApp.StartDirectory
            });

            if (!HedgeApp.Config.KeepOpen)
                Dispatcher.Invoke(() => Close());

            UpdateStatus(string.Format(Localise("StatusUIStartingGame"), HedgeApp.CurrentGame));
            return Task.CompletedTask;
        }

        private void SetupWatcher()
        {
            if (!Directory.Exists(HedgeApp.ModsDbPath))
                return;

            var watcher = new FileSystemWatcher(HedgeApp.ModsDbPath)
            {
                NotifyFilter = NotifyFilters.DirectoryName
            };

            watcher.Deleted += WatcherEvent;
            watcher.Created += WatcherEvent;
            watcher.EnableRaisingEvents = true;
            ModsWatchers.Add(watcher);

            foreach (var directory in Directory.GetDirectories(HedgeApp.ModsDbPath))
            {
                var watch = new FileSystemWatcher(directory);
                watch.Changed += WatcherModEvent;
                watch.Deleted += WatcherModEvent;
                watch.Created += WatcherModEvent;
                watch.Renamed += WatcherModEvent;
                watch.EnableRaisingEvents = true;
                ModsWatchers.Add(watch);
            }

            void WatcherModEvent(object sender, FileSystemEventArgs e)
            {
                if (e.Name == "mod.ini")
                {
                    Dispatcher.Invoke(() =>
                    {
                        try { Refresh(); } catch { }
                    });
                }
            }

            void WatcherEvent(object sender, FileSystemEventArgs e)
            {
                if (Directory.Exists(e.FullPath))
                {
                    var watch = new FileSystemWatcher(e.FullPath);
                    watch.Changed += WatcherModEvent;
                    watch.Deleted += WatcherModEvent;
                    watch.Created += WatcherModEvent;
                    watch.Renamed += WatcherModEvent;
                    watch.EnableRaisingEvents = true;
                    ModsWatchers.Add(watch);
                }
                Dispatcher.Invoke(() =>
                {
                    try { Refresh(); } catch { }
                });
            }
        }

        private void ResetWatchers()
        {
            foreach (var watcher in ModsWatchers)
            {
                watcher.Dispose();
            }
            ModsWatchers.Clear();
            SetupWatcher();
        }

        public async Task CheckForUpdatesAsync()
        {
            await CheckForManagerUpdatesAsync();

            if (HedgeApp.Config.CheckForModUpdates)
            {
                ContextCancelSource = new CancellationTokenSource();
                try
                {
                    await CheckAllModsUpdatesAsync(ContextCancelSource.Token);
                }
                catch (OperationCanceledException) { }
            }

            await CheckForCodeUpdates();
        }

        public async Task CheckForManagerUpdatesAsync()
        {
            if (!HedgeApp.Config.CheckForUpdates && !ViewModel.DevBuild)
                return;

            UpdateStatus(Localise("StatusUICheckingForUpdates"));
            try
            {
                if (ViewModel.DevBuild)
                {
                    var update = await HedgeApp.CheckForUpdatesDevAsync();

                    if (!update.Item1)
                    {
                        UpdateStatus(Localise("StatusUINoUpdatesFound"));
                        return;
                    }

                    string changelog = await HedgeApp.GetGitChangeLog(update.Item2.HeadSHA);
                    await Dispatcher.InvokeAsync(() => ShowUpdate(update.Item2, update.Item3, changelog));
                }
                else
                {
                    var update = await HedgeApp.CheckForUpdatesAsync();

                    if (!update.Item1)
                    {
                        UpdateStatus(Localise("StatusUINoUpdatesFound"));
                        return;
                    }

                    await Dispatcher.InvokeAsync(() => ShowUpdate(update.Item2));
                }
                UpdateStatus(string.Empty);
            }
            catch
            {
                UpdateStatus(Localise("StatusUIFailedToCheckUpdates"));
            }
        }

        public void ShowUpdate(ReleaseInfo release)
        {
            // http://wasteaguid.info/
            var path = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid()}.exe");

            var dialog = new HedgeMessageBox(release.Name, release.Body, HorizontalAlignment.Right, TextAlignment.Left, InputType.MarkDown);

            dialog.AddButton(Localise("CommonUIUpdate"), () =>
            {
                if (release.Assets.Count > 0)
                {
                    var asset = release.Assets[0];
                    dialog.Close();
                    var downloader = new DownloadWindow($"Downloading Hedge Mod Manager ({release.TagName})", asset.BrowserDownloadUrl.ToString(), path)
                    {
                        DownloadCompleted = () => HedgeApp.PerformUpdate(path, asset.ContentType)
                    };

                    downloader.Start();
                }
            });

            dialog.ShowDialog();
        }

        public void ShowUpdate(WorkflowRunInfo workflow, ArtifactInfo artifact, string changelog)
        {
            // http://wasteaguid.info/
            var path = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid()}.zip");

            var dialog = new HedgeMessageBox($"{artifact.Name} ({workflow.HeadSHA.Substring(0, 7)})", changelog, HorizontalAlignment.Right, TextAlignment.Left, InputType.MarkDown);

            dialog.AddButton(Localise("CommonUIUpdate"), () =>
            {
                dialog.Close();
                var downloader = new DownloadWindow($"Downloading {artifact.Name} ({workflow.HeadSHA.Substring(0, 7)})",
                    string.Format(HMMResources.URL_HMM_DEV, workflow.CheckSuiteID, artifact.ID), path)
                {
                    DownloadCompleted = () => HedgeApp.PerformUpdate(path, "application/x-zip-compressed")
                };

                downloader.Start();
            });

            dialog.ShowDialog();
        }

        protected async Task CheckForLoaderUpdateAsync()
        {
            if (!HedgeApp.Config.CheckLoaderUpdates)
                return;

            if (HedgeApp.CurrentGame.ModLoader == null)
                return;

            await Task.Yield();

            UpdateStatus(string.Format(Localise("StatusUICheckingForLoaderUpdate"), HedgeApp.CurrentGame.ModLoader.ModLoaderName));
            try
            {
                using (var stream = await Singleton.GetInstance<HttpClient>().GetStreamAsync(HMMResources.URL_LOADERS_INI))
                {
                    var loaderInfo = HedgeApp.GetCodeLoaderInfo(HedgeApp.CurrentGame);
                    // Check if there is a loader version, if not return
                    if (loaderInfo.LoaderVersion == null)
                        return;

                    var ini = new IniFile(stream);
                    var name = HedgeApp.GetCodeLoaderName(HedgeApp.CurrentGame);
                    var mlID = HedgeApp.CurrentGame.ModLoader.ModLoaderID ?? HedgeApp.CurrentGame.ToString();
                    var info = ini[mlID];
                    var newVersion = new Version(info["LoaderVersion"]);

                    if (HedgeApp.ExpandVersion(newVersion) == loaderInfo.LoaderVersion)
                    {
                        UpdateStatus(string.Format(Localise("StatusUILoaderUpToDate"), HedgeApp.CurrentGame.ModLoader.ModLoaderName));
                        return;
                    }

                    Dispatcher.Invoke(() =>
                    {
                        var dialog = new HedgeMessageBox($"{HedgeApp.CurrentGame.ModLoader.ModLoaderName} ({info["LoaderVersion"]})", info["LoaderChangelog"].Replace("\\n", "\n"), textAlignment: TextAlignment.Left);

                        dialog.AddButton(Localise("CommonUIUpdate"), () =>
                        {
                            dialog.Close();
                            if (HedgeApp.InstallOtherLoader(false))
                                UpdateStatus($"Updated {HedgeApp.CurrentGame.ModLoader.ModLoaderName} to {info["LoaderVersion"]}");
                            else
                                UpdateStatus($"Failed to update {HedgeApp.CurrentGame.ModLoader.ModLoaderName} to {info["LoaderVersion"]}");
                        });

                        dialog.AddButton(Localise("CommonUIIgnore"), () =>
                        {
                            dialog.Close();
                        });

                        dialog.ShowDialog();
                    });
                }
            }
            catch
            {
                UpdateStatus(string.Format(Localise("StatusUIFailedLoaderUpdateCheck"), HedgeApp.CurrentGame.ModLoader.ModLoaderName));
            }
        }

        protected void CheckCodeCompatibility()
        {
            var info = HedgeApp.GetCodeLoaderInfo(HedgeApp.CurrentGame);
            if (CodesDatabase.Codes.Count == 0)
                return;

            if (CodesDatabase.FileVersion >= info.MinCodeVersion && CodesDatabase.FileVersion <= info.MaxCodeVersion)
                return;

            var dialog = new HedgeMessageBox(Localise("CommonUIWarning"), Localise("CodesUIVersionIncompatible"));
            dialog.AddButton(Localise("CommonUIUpdate"), () =>
            {
                HedgeApp.InstallOtherLoader(false);
                UI_Download_Codes(null, null);
                dialog.Close();
            });
            dialog.AddButton(Localise("CommonUICancel"), dialog.Close);
            dialog.ShowDialog();
        }

        public void EnableSaveRedirIfUsed()
        {
            if (HedgeApp.Config.EnableFallbackSaveRedirection)
            {
                HedgeApp.Config.EnableSaveFileRedirection = true;
                return;
            }

            HedgeApp.Config.EnableSaveFileRedirection = false;
            foreach (var mod in ModsDatabase.Mods)
            {
                if (mod.SupportsSave && mod.Enabled)
                {
                    HedgeApp.Config.EnableSaveFileRedirection = true;
                    break;
                }
            }
        }

        public void ShowMissingOtherLoaderWarning()
        {
            if (HedgeApp.CurrentGame.ModLoader == null)
                return;
            bool loaderInstalled = File.Exists(Path.Combine(HedgeApp.StartDirectory, HedgeApp.CurrentGame.ModLoader.ModLoaderFileName));
            if (loaderInstalled)
                return;

            Dispatcher.Invoke(() =>
            {
                var dialog = new HedgeMessageBox(Localise("MainUIMissingLoaderHeader"), string.Format(Localise("MainUIMissingLoaderDesc"), HedgeApp.CurrentGame));

                dialog.AddButton(Localise("CommonUIYes"), () =>
                {
                    dialog.Close();
                    if (HedgeApp.InstallOtherLoader(false))
                        UpdateStatus(string.Format(Localise("StatusUIInstalledLoader"), HedgeApp.CurrentGame.ModLoader.ModLoaderName));
                });

                dialog.AddButton(Localise("CommonUINo"), () =>
                {
                    dialog.Close();
                });

                dialog.ShowDialog();
            });
        }

        public async Task SaveConfig(bool startGame = false)
        {
            string profilePath = Path.Combine(HedgeApp.StartDirectory, "profiles.json");
            File.WriteAllText(profilePath, JsonConvert.SerializeObject(HedgeApp.ModProfiles));
            ShowMissingOtherLoaderWarning();
            EnableSaveRedirIfUsed();
            try
            {
                await SaveModsDB();
                Refresh();
                UpdateStatus(Localise("StatusUIModsDBSaved"));
                if (startGame)
                    await StartGame();
            }
            catch (Exception ex)
            {
                Dispatcher.Invoke(() => new ExceptionWindow(ex).ShowDialog());
            }
        }

        public bool CheckModDepends()
        {
            bool abort = false;
            var report = ModsDatabase.ResolveDepends();

            if (report.HasErrors)
            {
                var box = new HedgeMessageBox(Localise("MainUIMissingDependsHeader"), report.BuildMarkdown(), textAlignment: TextAlignment.Left, type: InputType.MarkDown);
                Button resolveButton = null;
                Button ignoreButton = null;
                Button cancelButton = null;
                if (CheckReportForGBDepends(report))
                {
                    resolveButton = box.AddButton(Localise("MainUIMissingDependsResolve"), async () =>
                    {
                        resolveButton.IsEnabled = ignoreButton.IsEnabled = cancelButton.IsEnabled = false;
                        var updateInfo = new List<IModUpdateInfo>();
                        var processedIDs = new List<int>();
                        foreach (var error in report.Errors)
                        {
                            foreach (var depend in error.UnresolvedDepends)
                            {
                                if (!depend.HasLink)
                                    continue;

                                int id = GBAPI.GetGameBananaModID(depend.Link);
                                if (processedIDs.Contains(id))
                                    continue;

                                try
                                {
                                    var gbItem = await GBAPI.PopulateItemDataAsync(new GBAPIItemDataBasic("Mod", id));
                                    var file = gbItem.Files.FirstOrDefault(t =>
                                        t.Value.FileMetadata.Files.Any(s => s.Contains("mod.ini"))).Value;
                                    if (file != null)
                                        updateInfo.Add(
                                            new ModUpdateGameBanana(depend.Title, gbItem, file, ModsDatabase));
                                    processedIDs.Add(id);
                                }
                                catch
                                {
                                    // ignored
                                }
                            }
                        }

                        // Stop watching
                        foreach (var watcher in ModsWatchers)
                            watcher.Dispose();

                        ModsWatchers.Clear();

                        var model = new ModUpdatesWindowViewModel(updateInfo, true);
                        box.Close();
                        model.ShowDialog();
                        await SaveConfig();
                        // Reset watchers
                        Refresh();
                        SetupWatcher();

                        abort = CheckModDepends();
                    });
                }
                ignoreButton = box.AddButton(Localise("CommonUIIgnore"), () => box.Close());
                cancelButton = box.AddButton(Localise("CommonUICancel"), () =>
                {
                    box.Close();
                    abort = true;
                });
                box.ShowDialog();
            }

            return !abort;
        }

        public bool CheckReportForGBDepends(DependencyReport report)
        {
            foreach (var error in report.Errors)
            {
                foreach (var depend in error.UnresolvedDepends)
                {
                    if (depend.HasLink && GBAPI.GetGameBananaModID(depend.Link) != -1)
                        return true;
                }
            }
            return false;
        }

        public bool CheckDepends()
        {
            return !DependsHandler.AskToInstallRuntime(HedgeApp.CurrentGame.AppID,
                HedgeApp.CurrentGame.Is64Bit ? DependTypes.VS2019x64 : DependTypes.VS2019x86);
        }

        public bool CheckDepend(string id, string filePath, string dependName, string downloadURL, string fileName)
        {
            bool abort = false;
            if (HedgeApp.CurrentGame.AppID == id && !File.Exists(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), filePath)))
            {
                var dialog = new HedgeMessageBox(Localise("MainUIRuntimeMissingTitle"), string.Format(Localise("MainUIRuntimeMissingMsg"), HedgeApp.CurrentGame, dependName));

                dialog.AddButton(Localise("CommonUIYes"), () =>
                {
                    DownloadWindow window = new DownloadWindow($"Downloading {dependName}...", downloadURL, fileName);
                    window.Start();
                    if (File.Exists(fileName))
                    {
                        // For VC++
                        Process.Start(fileName, "/passive /norestart").WaitForExit(30000);
                        File.Delete(fileName);
                    }
                    dialog.Close();
                });

                dialog.AddButton(Localise("CommonUINo"), () =>
                {
                    abort = true;
                    dialog.Close();
                });

                dialog.ShowDialog();
            }
            return abort;
        }

        private async void Window_Loaded(object sender, RoutedEventArgs e)
        {
            StatusTimer = new Timer((state) => UpdateStatus(string.Empty));

            // Update CPKREDIR if needed
            if (HedgeApp.CurrentGame.SupportsCPKREDIR)
                HedgeApp.UpdateCPKREDIR();

            RefreshProfiles();
            Refresh();
            await CheckForUpdatesAsync();

            if (HedgeApp.AprilFools)
            {
                var random = new Random();
                if (random.Next(10) == 0)
                {
                    CleaningGrid.Visibility = Visibility.Visible;
                    DispatcherTimer timer = new DispatcherTimer();
                    timer.Interval = TimeSpan.FromMilliseconds(175);
                    int skipped = 0;
                    SaveButton.IsEnabled = SavePlayButton.IsEnabled = false;
                    timer.Tick += (sender, e) =>
                    {
                        if (random.Next(5) == 0)
                            ++skipped;
                        if (ViewModel.Mods.Count <= skipped)
                        {
                            RefreshUI();
                            RefreshButton.IsEnabled = SaveButton.IsEnabled = SavePlayButton.IsEnabled = true;
                            CleaningGrid.Visibility = Visibility.Collapsed;
                            timer.Stop();
                            return;
                        }
                        ViewModel.Mods.RemoveAt(skipped);
                    };
                    timer.Start();
                }
            }
        }

        private void UI_RemoveMod_Click(object sender, RoutedEventArgs e)
        {
            var mod = ModsList.SelectedValue as ModInfo;
            if (mod == null)
                return;

            var box = new HedgeMessageBox(Localise("CommonUIWarning"), string.Format(Localise("DialogUIDeleteMod"), mod.Title));

            box.AddButton(Localise("CommonUIDelete"), () =>
            {
                foreach (var watcher in ModsWatchers)
                {
                    watcher.Dispose();
                }
                ModsWatchers.Clear();
                ModsDatabase.DeleteMod(ViewModel.SelectedMod);
                UpdateStatus(string.Format(Localise("StatusUIDeletedMod"), ViewModel.SelectedMod.Title));
                Refresh();
                SetupWatcher();
                box.Close();
            });

            box.AddButton(Localise("CommonUICancel"), () =>
            {
                box.Close();
            });

            box.ShowDialog();
        }

        private async void UI_CheckUpdates_AllMods(object sender, RoutedEventArgs e)
        {
            if (CheckingForUpdates)
                return;

            ContextCancelSource = new CancellationTokenSource();
            try
            {
                await CheckAllModsUpdatesAsync(ContextCancelSource.Token);
            }
            catch (OperationCanceledException) { }
        }

        private void UI_Refresh_Click(object sender, RoutedEventArgs e)
        {
            Refresh();
        }

        private async void UI_Save_Click(object sender, RoutedEventArgs e)
        {
            if (CheckModDepends())
                await SaveConfig();
        }

        private async void UI_SaveAndPlay_Click(object sender, RoutedEventArgs e)
        {
            await SaveConfig(CheckDepends() && CheckModDepends());
        }

        private void UI_Play_Click(object sender, RoutedEventArgs e)
        {
            if (CheckDepends())
                StartGame();
        }

        private void UI_About_Click(object sender, RoutedEventArgs e)
        {
            new AboutWindow().ShowDialog();
        }

        private void UI_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            var item = (ListViewItem)sender;
            if (item == null || item.Content == null)
                return;

            var modInfo = (ModInfo)item.Content;
            if (modInfo.HasSchema)
            {
                var window = new ModConfigWindow(modInfo) { Owner = this };
                window.ShowDialog();
            }
            else
            {
                var dialog = new AboutModWindow(modInfo) { Owner = this };
                dialog.ShowDialog();
            }
        }

        private void UI_OtherLoader_Click(object sender, RoutedEventArgs e)
        {
            HedgeApp.InstallOtherLoader(true);
            RefreshUI();
        }

        private async void UI_Update_Mod(object sender, RoutedEventArgs e)
        {
            ContextCancelSource = new CancellationTokenSource();
            try
            {
                //new ModUpdateGeneratorModel(ViewModel.SelectedMod).ShowDialog();

                if (await CheckForModUpdatesAsync(ViewModel.SelectedMod, ContextCancelSource.Token)
                    .ConfigureAwait(false))
                {
                    Dispatcher.Invoke(RefreshMods);
                    Dispatcher.Invoke(RefreshUI);
                }
            }
            catch (OperationCanceledException)
            {

            }
        }

        private void UI_Edit_Mod(object sender, RoutedEventArgs e)
        {
            var mod = ViewModel.SelectedMod;
            var newMod = mod.Clone();
            var window = new EditModWindow(newMod) { Owner = this };
            if (window.ShowDialog().Value)
            {
                newMod.Save();
                newMod.UpdateStatus = ModUpdateFetcher.Status.NoUpdates;
                ViewModel.SelectedMod = ViewModel.Mods[ViewModel.Mods.IndexOf(mod)] = newMod;
            }
        }

        private void UI_Description_Click(object sender, RoutedEventArgs e)
        {
            if (ViewModel.SelectedMod == null)
                return;

            var dialog = new AboutModWindow(ViewModel.SelectedMod) { Owner = this };
            dialog.ShowDialog();
        }

        private void UI_Favorite_Click(object sender, RoutedEventArgs e)
        {
            ViewModel.SelectedMod.Favorite = !ViewModel.SelectedMod.Favorite;
            RefreshUI();
        }

        private void UI_Open_Folder(object sender, RoutedEventArgs e)
        {
            Process.Start(ViewModel.SelectedMod.RootDirectory);
        }

        private void UI_Install_Mod(object sender, RoutedEventArgs e)
        {
            var choice = new OptionsWindow(Localise("MainUIInstallFormHeader"), Localise("MainUIInstallFormOptionDir"), Localise("MainUIInstallFormOptionArc"), Localise("MainUIInstallFormOptionNew")).Ask();
            
            if (choice == 0)
            {
                var dialog = new FolderBrowserDialog();
                if (dialog.ShowDialog())
                {
                    ModsDatabase.InstallMod(dialog.SelectedFolder);
                }
            }
            else if (choice == 1)
            {
                var dialog = new FileOpenDialog();
                var filters = new COMDLG_FILTERSPEC[3];
                filters[0] = new COMDLG_FILTERSPEC() { pszName = "Zip archive", pszSpec = "*.zip" };
                filters[1] = new COMDLG_FILTERSPEC() { pszName = "7z archive", pszSpec = "*.7z" };
                filters[2] = new COMDLG_FILTERSPEC() { pszName = "Rar archive", pszSpec = "*.rar" };
                dialog.SetFileTypes((uint)filters.Length, filters);
                if (dialog.Show(new WindowInteropHelper(this).Handle) == 0)
                {
                    dialog.GetResult(out var item);
                    item.GetDisplayName(SIGDN.SIGDN_FILESYSPATH, out var path);
                    try
                    {
                        ModsDatabase.InstallMod(path);
                    }
                    catch (ModInstallException)
                    {
                        var box = new HedgeMessageBox(Localise("CommonUIError"), Localise("DialogUINoDecompressor"));
                        box.AddButton(Localise("CommonUIClose"), () => box.Close());
                        box.ShowDialog();
                    }
                }
            }
            else if (choice == 2)
            {
                var mod = new ModInfo
                {
                    Title = GenerateModTitle(),
                    Date = DateTime.Today.ToString("yyyy-MM-dd"),
                    Version = "1.0",
                    Author = Environment.UserName
                };

                mod.IncludeDirs.Add(".");
                var editor = new EditModWindow(mod) { Owner = this };
                if (editor.ShowDialog().Value)
                {
                    var modDir = "disk";
                    if (HedgeApp.CurrentGame == Games.PuyoPuyoTetris2)
                        modDir = "raw";
                    else if (HedgeApp.CurrentGame == Games.SonicColorsUltimate)
                        modDir = "";
                    ModsDatabase.CreateMod(mod, modDir, true);
                }
            }

            if (choice != -1)
            {
                RefreshMods();
                RefreshUI();
            }
        }

        protected string GenerateModTitle()
        {
            for (int i = 1; i < int.MaxValue; i++)
            {
                var title = $"{HedgeApp.CurrentGame} Mod {i}";
                title = string.Concat(title.Split(Path.GetInvalidFileNameChars()));
                if (!Directory.Exists(Path.Combine(ModsDatabase.RootDirectory, title)))
                    return title;
            }
            return string.Empty;
        }

        public void UpdateStatus(string str)
        {
            Dispatcher.Invoke(() => StatusLbl.Text = str);
            StatusTimer.Change(4000, Timeout.Infinite);
        }

        private async void Game_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (ComboBox_GameStatus.SelectedItem != null && ComboBox_GameStatus.SelectedItem != HedgeApp.CurrentGameInstall)
            {
                ContextCancelSource?.Cancel();
                ContextCancelSource = new CancellationTokenSource();

                CheckingForUpdates = false;

                HedgeApp.SelectGameInstall((GameInstall)ComboBox_GameStatus.SelectedItem);

                if (HedgeApp.CurrentGame.SupportsCPKREDIR)
                {
                    // Remove old patch
                    string exePath = Path.Combine(HedgeApp.StartDirectory, HedgeApp.CurrentGame.ExecutableName);
                    if (HedgeApp.IsCPKREDIRInstalled(exePath))
                        HedgeApp.InstallCPKREDIR(exePath, false);

                    // Update CPKREDIR if needed
                    HedgeApp.UpdateCPKREDIR();
                }

                ResetWatchers();
                RefreshProfiles();
                Refresh();
                UpdateStatus(string.Format(Localise("StatusUIGameChange"), HedgeApp.CurrentGame));

                // Schedule checking for code updates if available.
                if (Button_DownloadCodes.IsEnabled)
                    await CheckForCodeUpdates();

                await CheckForUpdatesAsync();
            }

            await RunTask(CheckForLoaderUpdateAsync());
        }

        private void UI_Download_Codes(object sender, RoutedEventArgs e)
        {
            UpdateStatus(string.Format(Localise("StatusUIDownloadingCodes"), HedgeApp.CurrentGame));
            try
            {
                var downloader = new DownloadWindow($"Downloading codes for {HedgeApp.CurrentGame}", HedgeApp.CurrentGame.CodesURL, CodeProvider.CodesTextPath)
                {
                    DownloadCompleted = () =>
                    {
                        Refresh();
                        UpdateStatus(Localise("StatusUIDownloadFinished"));

                        // Update button visual.
                        {
                            CodesOutdated = false;

                            // Reset button text if there was an update that was just downloaded.
                            Button_DownloadCodes.Content = Localise("CodesUIDownload");
                        }
                    }
                };
                downloader.Start();
            }
            catch
            {
                UpdateStatus(Localise("StatusUIDownloadFailed"));
            }
        }

        private void UI_OpenMods_Click(object sender, RoutedEventArgs e)
        {
            Process.Start(HedgeApp.ModsDbPath);
        }

        private void UI_OpenGameDir_Click(object sender, RoutedEventArgs e)
        {
            Process.Start(HedgeApp.CurrentGameInstall.GameDirectory);
        }

        private void UI_ChangeDatabasePath_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new FolderBrowserDialog
            {
                Title = Localise("MainUISelectModsDBTitle")
            };

            if (dialog.ShowDialog())
            {
                HedgeApp.ModsDbPath = dialog.SelectedFolder;
                ViewModel.CPKREDIR.ModsDbIni = Path.Combine(HedgeApp.ModsDbPath, SelectedModProfile.ModDBPath);
                if (ViewModel.CPKREDIR.ModsDbIni.StartsWith(HedgeApp.StartDirectory))
                    ViewModel.CPKREDIR.ModsDbIni = ViewModel.CPKREDIR.ModsDbIni.Substring(HedgeApp.StartDirectory.Length + 1);
                ViewModel.CPKREDIR.Save(Path.Combine(HedgeApp.StartDirectory, "cpkredir.ini"));
                Refresh();
                UpdateStatus(Localise("StatusUIModsDBLocationChanged"));
            }
        }

        private void ComboBox_Languages_Changed(object sender, SelectionChangedEventArgs e)
        {
            HedgeApp.ChangeLanguage();
            RefreshUI();
        }

        private void ComboBox_Languages_Loaded(object sender, RoutedEventArgs e)
        {
            ComboBox_Languages.GetBindingExpression(ComboBox.ItemsSourceProperty).UpdateTarget();
            ComboBox_Languages.GetBindingExpression(ComboBox.SelectedItemProperty).UpdateTarget();
        }

        private void ComboBox_Themes_Changed(object sender, SelectionChangedEventArgs e)
        {
            HedgeApp.UpdateTheme();
            RefreshUI();
        }

        private void ComboBox_Themes_Loaded(object sender, RoutedEventArgs e)
        {
            ComboBox_Themes.GetBindingExpression(ComboBox.ItemsSourceProperty).UpdateTarget();
            ComboBox_Themes.GetBindingExpression(ComboBox.SelectedItemProperty).UpdateTarget();
        }

        private void UI_ConfigureMod_Click(object sender, RoutedEventArgs e)
        {
            if (!ViewModel.SelectedMod.HasSchema)
                return;

            var window = new ModConfigWindow(ViewModel.SelectedMod) { Owner = this };
            window.ShowDialog();
        }

        private void UI_ContextMenu_Opening(object sender, ContextMenuEventArgs e)
        {
            var mod = ViewModel.SelectedMod;
            if (mod == null || sender is not ListViewItem listItem)
            {
                e.Handled = true;
                return;
            }

            var itemConfigure = HedgeApp.FindChild<MenuItem>(listItem.ContextMenu, "ContextMenuItemConfigure");
            var itemCheckUpdate = HedgeApp.FindChild<MenuItem>(listItem.ContextMenu, "ContextMenuItemCheckUpdate");
            var itemCheckUpdateAll = HedgeApp.FindChild<MenuItem>(listItem.ContextMenu, "ContextMenuItemCheckUpdateAll");

            if (itemConfigure != null)
                itemConfigure.IsEnabled = mod.HasSchema;

            if (itemCheckUpdateAll != null)
                itemCheckUpdateAll.IsEnabled = !CheckingForUpdates;

            if (itemCheckUpdate != null)
            {
                if (CheckingForUpdates)
                {
                    itemCheckUpdate.IsEnabled = false;
                    return;
                }

                itemCheckUpdate.IsEnabled = mod.HasUpdates;
                if (mod.HasUpdates)
                    itemCheckUpdate.IsEnabled = !Singleton.GetInstance<NetworkConfig>()
                        .IsServerBlocked(ViewModel.SelectedMod.UpdateServer);
            }
        }

        private void ModsList_OnPreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (ViewModel.SelectedMod == null)
                return;

            var mod = ViewModel.SelectedMod;
            var ctrlKey = Keyboard.IsKeyDown(Key.LeftCtrl) || Keyboard.IsKeyDown(Key.RightCtrl);
            var altKey = Keyboard.IsKeyDown(Key.LeftAlt) || Keyboard.IsKeyDown(Key.RightAlt);

            if (Keyboard.IsKeyDown(Key.Space))
                mod.Enabled = !mod.Enabled;

            if (ctrlKey)
            {
                if (Keyboard.IsKeyDown(Key.C))
                    UI_ConfigureMod_Click(null, null);
                else if (Keyboard.IsKeyDown(Key.D))
                    UI_Description_Click(null, null);
                else if (Keyboard.IsKeyDown(Key.E))
                    UI_Edit_Mod(null, null);
                else if (Keyboard.IsKeyDown(Key.O))
                    UI_Open_Folder(null, null);
                else if (Keyboard.IsKeyDown(Key.U))
                    UI_Update_Mod(null, null);

                e.Handled = true;
            }
            else if (altKey)
            {
                if (Keyboard.IsKeyDown(Key.F))
                    UI_Favorite_Click(null, null);

                e.Handled = true;
            }

            if (Keyboard.IsKeyDown(Key.Delete))
            {
                UI_RemoveMod_Click(null, null);
                e.Handled = true;
            }
        }

        private void CodesList_OnPreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (ViewModel.SelectedCode == null)
                return;

            var code = ViewModel.SelectedCode;

            if (Keyboard.IsKeyDown(Key.Space))
            {
                code.Enabled = !code.Enabled;
            }
        }

        private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            var shiftKey = Keyboard.IsKeyDown(Key.LeftShift) || Keyboard.IsKeyDown(Key.RightShift);
            var ctrlkey = Keyboard.IsKeyDown(Key.LeftCtrl) || Keyboard.IsKeyDown(Key.RightCtrl);

            if (shiftKey)
            {
                if (Keyboard.IsKeyDown(Key.F1))
                {
                    try
                    {
                        var time = DateTime.Now;
                        var path =
                            $"HMM_Snapshot_{time.Date:00}{time.Month:00}{time.Year:0000}{time.Hour:00}{time.Minute:00}{time.Second:00}.txt";

                        File.WriteAllText(path, Convert.ToBase64String(SnapshotBuilder.Build()));
                        Process.Start($"explorer.exe", $"/select,\"{Path.GetFullPath(path)}\"");
                        HedgeApp.CreateOKMessageBox("Hedge Mod Manager", $"Please attach the file\n{path}\nto the issue.").ShowDialog();
                    }
                    catch { }
                }
                if (Keyboard.IsKeyDown(Key.F2))
                {
                    try
                    {
                        HedgeApp.FindMissingLanguageEntries(HedgeApp.CurrentCulture.FileName);
                    }
                    catch { }

                }
                if (Keyboard.IsKeyDown(Key.F3))
                {
                    ViewModel.HiddenMode = !ViewModel.HiddenMode;
                }
                if (Keyboard.IsKeyDown(Key.F4))
                {
                    try
                    {
                        HedgeApp.DumpLanguage(HedgeApp.CurrentCulture.FileName);
                    }
                    catch { }
                }

            }else
            {
                if (Keyboard.IsKeyDown(Key.F5))
                {
                    Refresh();
                }
            }
            if (ctrlkey)
            {
                if (Keyboard.IsKeyDown(Key.F))
                {
                    if (MainTabControl.SelectedItem == ModsTab)
                    {
                        if (ModsFind.Visibility == Visibility.Visible)
                        {
                            ModsFind.Visibility = Visibility.Collapsed;
                            FilterMods("");
                        }
                        else
                        {
                            ModsFind.Visibility = Visibility.Visible;
                            FilterMods(TextBox_ModsSearch.Text.ToLowerInvariant());
                            TextBox_ModsSearch.Focus();
                        }
                    }
                    else if (MainTabControl.SelectedItem == CodesTab)
                    {
                        if (CodesFind.Visibility == Visibility.Visible)
                        {
                            CodesFind.Visibility = Visibility.Collapsed;
                            FilterCodes("");
                        }
                        else
                        {
                            CodesFind.Visibility = Visibility.Visible;
                            FilterCodes(TextBox_CodesSearch.Text.ToLowerInvariant());
                            TextBox_CodesSearch.Focus();
                        }
                    }
                }
            }
            if (Keyboard.IsKeyDown(Key.Escape))
            {
                if (MainTabControl.SelectedItem == ModsTab)
                {
                    ModsFind.Visibility = Visibility.Collapsed;
                    FilterMods("");
                }
                else if (MainTabControl.SelectedItem == CodesTab)
                {
                    CodesFind.Visibility = Visibility.Collapsed;
                    FilterCodes("");
                }
            }
        }

        private async void ComboBox_ModProfile_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            // Ignore event when combobox is initalising 
            if (ComboBox_ModProfile.SelectedItem == null || ComboBox_ModProfile.SelectedItem == SelectedModProfile)
                return;

            // Save profile
            try
            {
                await SaveModsDB();
            }
            catch (Exception ex)
            {
                new ExceptionWindow(ex).ShowDialog();
            }
            SelectedModProfile.Enabled = false;
            SelectedModProfile = ComboBox_ModProfile.SelectedItem as ModProfile ?? HedgeApp.ModProfiles.First();
            SelectedModProfile.Enabled = true;
            HedgeApp.Config.ModProfile = SelectedModProfile.Name;
            string profilePath = Path.Combine(HedgeApp.StartDirectory, "profiles.json");
            HedgeApp.Config.Save(HedgeApp.ConfigPath);
            File.WriteAllText(profilePath, JsonConvert.SerializeObject(HedgeApp.ModProfiles));
            Refresh();
        }

        private void UI_ManageProfile_Click(object sender, RoutedEventArgs e)
        {
            var manager = new ProfileManagerWindow() { Owner = this };
            manager.DataContext = DataContext;
            manager.ShowDialog();
            // Update profiles
            HedgeApp.ModProfiles.Clear();
            HedgeApp.ModProfiles.AddRange(ViewModel.Profiles);
            // Save profiles
            string profilePath = Path.Combine(HedgeApp.StartDirectory, "profiles.json");
            File.WriteAllText(profilePath, JsonConvert.SerializeObject(HedgeApp.ModProfiles));
            Refresh();
        }

        private void MainWindow_OnUnloaded(object sender, RoutedEventArgs e)
        {
            ContextCancelSource?.Cancel();
            CheckingForUpdates = false;
        }

        private void TextBox_CodeSearch_LostFocus(object sender, RoutedEventArgs e)
        {
            // Close if focus is lost with no text
            if (TextBox_CodesSearch.Text.Length == 0)
                CodesFind.Visibility = Visibility.Collapsed;
        }

        private void TextBox_CodeSearch_TextChanged(object sender, TextChangedEventArgs e)
        {
            FilterCodes(TextBox_CodesSearch.Text.ToLowerInvariant());
        }

        private void TextBox_ModsSearch_TextChanged(object sender, TextChangedEventArgs e)
        {
            FilterMods(TextBox_ModsSearch.Text.ToLowerInvariant());
        }

        private void TextBox_ModsSearch_LostFocus(object sender, RoutedEventArgs e)
        {
            // Close if focus is lost with no text
            if (TextBox_ModsSearch.Text.Length == 0)
                ModsFind.Visibility = Visibility.Collapsed;
        }

        private void ComboBox_Channel_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            string currentChannel = !string.IsNullOrEmpty(HedgeApp.RepoCommit) ? "Development" : "Release";
            
            if (ComboBox_Channel.SelectedItem == null)
                return;

            string message = ViewModel.DevBuild ? Localise("SettingsUIChangeChannelRel") : Localise("SettingsUIChangeChannelDev");
            if (currentChannel != ComboBox_Channel.SelectedItem as string)
            {
                var box = new HedgeMessageBox(Localise("SettingsUIChangingChannelTitle"), message);
                Button button = null;
                button = box.AddButton(Localise("CommonUIYes"), async () =>
                {
                    button.IsEnabled = false;
                    if (ViewModel.DevBuild)
                    {
                        var update = await HedgeApp.CheckForUpdatesAsync();
                        box.Close();
                        var path = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid()}.exe");
                        var release = update.Item2;
                        if (release.Assets.Count > 0)
                        {
                            var asset = release.Assets[0];
                            var downloader = new DownloadWindow($"Downloading Hedge Mod Manager ({release.TagName})", asset.BrowserDownloadUrl.ToString(), path)
                            {
                                DownloadCompleted = () => HedgeApp.PerformUpdate(path, asset.ContentType)
                            };
                            downloader.Start();
                        }
                    }
                    else
                    {
                        var update = await HedgeApp.CheckForUpdatesDevAsync();
                        box.Close();
                        var path = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid()}.zip");
                        var artifact = update.Item3;
                        var workflow = update.Item2;
                        var downloader = new DownloadWindow($"Downloading {artifact.Name} ({workflow.HeadSHA.Substring(0, 7)})",
                            string.Format(HMMResources.URL_HMM_DEV, workflow.CheckSuiteID, artifact.ID), path)
                        {
                            DownloadCompleted = () => HedgeApp.PerformUpdate(path, "application/x-zip-compressed")
                        };
                        downloader.Start();
                    }
                });
                box.AddButton(Localise("CommonUINo"), () =>
                {
                    ComboBox_Channel.SelectedItem = currentChannel;
                    box.Close();
                });
                box.ShowDialog();
            }
        }

        private async void UI_CheckUpdates(object sender, RoutedEventArgs e)
        {
            await CheckForManagerUpdatesAsync();
        }

        private void UI_ModFeatureConfig_PreviewMouseDown(object sender, MouseButtonEventArgs e)
        {
            var modInfo = (sender as Border).Tag as ModInfo;
            if (modInfo == null)
                return;

            ViewModel.SelectedMod = modInfo;
            if (modInfo.HasSchema)
            {
                var window = new ModConfigWindow(modInfo) { Owner = this };
                window.ShowDialog();
            }
        }

        private void UI_ModFeatureUpdate_PreviewMouseDown(object sender, MouseButtonEventArgs e)
        {
            var modInfo = (sender as Border).Tag as ModInfo;
            if (modInfo == null)
                return;

            ViewModel.SelectedMod = modInfo;
            UI_Update_Mod(sender, null);
        }

        private void MainTabControl_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (RefreshButton != null)
            {
                RefreshButton.IsEnabled = MainTabControl.SelectedItem != SettingsTab;
            }
            if (Button_ConfigureMod != null)
            {
                // Check selected item to make sure we're only enabling it if there's anything selected and has config schema
                if (MainTabControl.SelectedItem == ModsTab && ModsList.SelectedItem is ModInfo modInfo)
                    Button_ConfigureMod.IsEnabled = modInfo.HasSchema;
                else
                    Button_ConfigureMod.IsEnabled = false;
            }
        }

        class StatusLogger : ILogger
        {
            private MainWindow Window { get; }
            public StatusLogger(MainWindow window) => Window = window;
            public void Write(string str) => Window.UpdateStatus(str);
            public void WriteLine(string str) => Window.UpdateStatus(str);
        }
    }
}
