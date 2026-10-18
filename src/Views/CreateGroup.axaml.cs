using System;
using System.Collections.Generic;

using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;

namespace SourceGit.Views
{
    public partial class CreateGroup : UserControl
    {
        public CreateGroup()
        {
            InitializeComponent();
        }

        private async void OnAddFromDisk(object _1, RoutedEventArgs e)
        {
            if (DataContext is not ViewModels.CreateGroup vm)
                return;

            var topLevel = TopLevel.GetTopLevel(this);
            if (topLevel == null)
                return;

            var preference = ViewModels.Preferences.Instance;
            var workspace = preference.GetActiveWorkspace();
            var initDir = workspace.DefaultCloneDir;
            if (string.IsNullOrEmpty(initDir) || !System.IO.Directory.Exists(initDir))
                initDir = preference.GitDefaultCloneDir;

            var options = new FolderPickerOpenOptions() { AllowMultiple = true };
            if (System.IO.Directory.Exists(initDir))
            {
                var folder = await topLevel.StorageProvider.TryGetFolderFromPathAsync(initDir);
                options.SuggestedStartLocation = folder;
            }

            try
            {
                var selected = await topLevel.StorageProvider.OpenFolderPickerAsync(options);
                var paths = new List<string>();
                foreach (var folder in selected)
                {
                    var path = folder is { Path: { IsAbsoluteUri: true } uri } ? uri.LocalPath : folder?.Path.ToString();
                    if (!string.IsNullOrEmpty(path))
                        paths.Add(path);
                }

                if (paths.Count > 0)
                    vm.AddRepositories(paths);
            }
            catch (Exception exception)
            {
                Models.Notification.Send(null, $"Failed to add repositories: {exception.Message}", true);
            }

            e.Handled = true;
        }
    }
}
