using System.Collections.Generic;
using System.Threading.Tasks;

using CommunityToolkit.Mvvm.ComponentModel;

namespace SourceGit.ViewModels
{
    public class DiscardGroupRepository : ObservableObject
    {
        public Repository Repo
        {
            get;
            init;
        }

        public string Name
        {
            get;
            init;
        }

        public int ChangesCount
        {
            get;
            init;
        }

        public bool IsEnabled
        {
            get => _isEnabled;
            set => SetProperty(ref _isEnabled, value);
        }

        private bool _isEnabled = true;
    }

    public class DiscardGroup : Popup
    {
        public DiscardAllMode Mode
        {
            get;
        }

        public List<DiscardGroupRepository> Repositories
        {
            get;
        }

        public bool IsDiscardAll
        {
            get;
        }

        public DiscardGroup(RepositoryGroup group)
            : this(group, null)
        {
        }

        public DiscardGroup(RepositoryGroup group, Dictionary<Repository, List<Models.Change>> grouped)
        {
            _group = group;
            _grouped = grouped;
            Mode = new DiscardAllMode();
            IsDiscardAll = grouped == null;

            Repositories = [];
            if (grouped == null)
            {
                foreach (var repo in group.Repositories)
                {
                    if (repo.LocalChangesCount > 0)
                        Repositories.Add(new DiscardGroupRepository { Repo = repo, Name = group.GetChildName(repo), ChangesCount = repo.LocalChangesCount });
                }
            }
            else
            {
                foreach (var kv in grouped)
                    Repositories.Add(new DiscardGroupRepository { Repo = kv.Key, Name = group.GetChildName(kv.Key), ChangesCount = kv.Value.Count });
            }
        }

        public override async Task<bool> Sure()
        {
            using var lockWatcher = _group.LockWatcher();

            var log = _group.CreateLog("Discard Changes");
            Use(log);

            foreach (var item in Repositories)
            {
                if (!item.IsEnabled)
                    continue;

                if (_grouped == null)
                {
                    await Commands.Discard.AllAsync(item.Repo.FullPath, Mode.IncludeModified, Mode.IncludeUntracked, Mode.IncludeIgnored, log);
                    item.Repo.ClearCommitMessage();
                }
                else
                {
                    var real = new List<Models.Change>();
                    var seen = new HashSet<Models.Change>();
                    foreach (var change in _grouped[item.Repo])
                    {
                        if (change is GroupChange gc && seen.Add(gc.Source))
                            real.Add(gc.Source);
                    }

                    await Commands.Discard.ChangesAsync(item.Repo.FullPath, real, log);
                }

                item.Repo.MarkWorkingCopyDirtyManually();
            }

            log.Complete();
            return true;
        }

        private readonly RepositoryGroup _group = null;
        private readonly Dictionary<Repository, List<Models.Change>> _grouped = null;
    }
}
