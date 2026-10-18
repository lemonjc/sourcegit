using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.IO;
using System.Threading.Tasks;

using CommunityToolkit.Mvvm.ComponentModel;

namespace SourceGit.ViewModels
{
    public class CreateGroupRepository : ObservableObject
    {
        public string FullPath
        {
            get;
            init;
        }

        public string Name
        {
            get;
            init;
        }

        public bool IsSelected
        {
            get => _isSelected;
            set => SetProperty(ref _isSelected, value);
        }

        private bool _isSelected = false;
    }

    public class CreateGroup : Popup
    {
        [Required(ErrorMessage = "Group name is required!")]
        public string Name
        {
            get => _name;
            set => SetProperty(ref _name, value, true);
        }

        public List<CreateGroupRepository> Repositories
        {
            get;
        }

        public List<CreateGroupRepository> VisibleRepositories
        {
            get => _visibleRepositories;
            private set => SetProperty(ref _visibleRepositories, value);
        }

        public string Filter
        {
            get => _filter;
            set
            {
                if (SetProperty(ref _filter, value))
                    PostFilterChanged();
            }
        }

        public bool OpenAsCollection
        {
            get => _openAsCollection;
            set => SetProperty(ref _openAsCollection, value);
        }

        public CreateGroup(RepositoryNode parent)
        {
            _parent = parent;

            Repositories = [];
            CollectRepositories(Preferences.Instance.RepositoryNodes, Repositories);
            Repositories.Sort((l, r) => Models.NumericSort.Compare(l.Name, r.Name));

            VisibleRepositories = [.. Repositories];
        }

        public void AddRepositories(IEnumerable<string> paths)
        {
            var added = 0;
            foreach (var path in paths)
            {
                var normalized = NormalizeRepositoryPath(path);
                if (string.IsNullOrEmpty(normalized))
                {
                    Models.Notification.Send(null, App.Text("CreateGroup.InvalidRepository", path), true);
                    continue;
                }

                if (Repositories.Exists(x => x.FullPath.Equals(normalized, StringComparison.Ordinal)))
                    continue;

                var name = Path.GetFileName(normalized);
                var node = Preferences.Instance.FindNode(normalized);
                if (node != null && !string.IsNullOrEmpty(node.Name))
                    name = node.Name;

                var item = new CreateGroupRepository
                {
                    FullPath = normalized,
                    Name = name,
                    IsSelected = true,
                };

                Repositories.Add(item);
                added++;
            }

            if (added > 0)
            {
                Repositories.Sort((l, r) => Models.NumericSort.Compare(l.Name, r.Name));
                PostFilterChanged();
            }
        }

        public override Task<bool> Sure()
        {
            var group = new RepositoryNode()
            {
                Id = Guid.NewGuid().ToString(),
                Name = _name,
                IsRepository = false,
                IsExpanded = true,
            };

            Preferences.Instance.AddNode(group, _parent, false);

            var count = 0;
            foreach (var item in Repositories)
            {
                if (!item.IsSelected)
                    continue;

                var node = Preferences.Instance.FindNode(item.FullPath);
                if (node == null)
                    Preferences.Instance.FindOrAddNodeByRepositoryPath(item.FullPath, group, false, false);
                else
                    Preferences.Instance.MoveNode(node, group, false);

                count++;
            }

            Preferences.Instance.Save();
            Welcome.Instance.Refresh();

            if (_openAsCollection && count > 0)
                App.GetLauncher()?.OpenRepositoryGroupInTab(group, null);

            return Task.FromResult(true);
        }

        private void PostFilterChanged()
        {
            if (string.IsNullOrEmpty(_filter))
            {
                VisibleRepositories = [.. Repositories];
                return;
            }

            var visible = new List<CreateGroupRepository>();
            foreach (var item in Repositories)
            {
                if (item.Name.Contains(_filter, StringComparison.OrdinalIgnoreCase) ||
                    item.FullPath.Contains(_filter, StringComparison.OrdinalIgnoreCase))
                    visible.Add(item);
            }

            VisibleRepositories = visible;
        }

        private static void CollectRepositories(List<RepositoryNode> nodes, List<CreateGroupRepository> into)
        {
            foreach (var node in nodes)
            {
                if (node.IsRepository)
                {
                    if (Directory.Exists(node.Id))
                        into.Add(new CreateGroupRepository { FullPath = node.Id, Name = node.Name });
                }
                else
                {
                    CollectRepositories(node.SubNodes, into);
                }
            }
        }

        private static string NormalizeRepositoryPath(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return null;

            var normalized = path.Replace('\\', '/').TrimEnd('/');

            var root = new Commands.QueryRepositoryRootPath(normalized).GetResult();
            if (root.IsSuccess && !string.IsNullOrEmpty(root.StdOut))
                return root.StdOut.Trim().Replace('\\', '/').TrimEnd('/');

            if (new Commands.IsBareRepository(normalized).GetResult())
                return normalized;

            return null;
        }

        private readonly RepositoryNode _parent = null;
        private string _name = string.Empty;
        private string _filter = string.Empty;
        private bool _openAsCollection = true;
        private List<CreateGroupRepository> _visibleRepositories = [];
    }
}
