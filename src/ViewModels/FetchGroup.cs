using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

using CommunityToolkit.Mvvm.ComponentModel;

namespace SourceGit.ViewModels
{
    public class FetchGroupRepository : ObservableObject
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

        public bool HasRemotes => Repo.Remotes.Count > 0;

        public bool IsEnabled
        {
            get => _isEnabled;
            set => SetProperty(ref _isEnabled, value);
        }

        private bool _isEnabled = true;
    }

    public class FetchGroup : Popup
    {
        public List<FetchGroupRepository> Repositories
        {
            get;
        }

        public bool HasMultipleRemotes
        {
            get;
            private set;
        }

        public bool NoTags
        {
            get;
            set;
        }

        public bool Force
        {
            get;
            set;
        }

        public FetchGroup(RepositoryGroup group)
        {
            _group = group;
            CanTerminate = true;

            Repositories = [];
            foreach (var repo in group.Repositories)
                Repositories.Add(new FetchGroupRepository { Repo = repo, Name = group.GetChildName(repo) });
        }

        public override async Task<bool> Sure()
        {
            var targets = new List<FetchGroupRepository>();
            foreach (var item in Repositories)
            {
                if (item.IsEnabled && item.HasRemotes)
                    targets.Add(item);
            }

            if (targets.Count == 0)
                return true;

            using var lockWatcher = _group.LockWatcher();

            var log = _group.CreateLog($"Fetch Group '{_group.GroupName}'");
            Use(log);

            _cancellation = new CancellationTokenSource();
            var token = _cancellation.Token;

            var tasks = new List<Task>();
            foreach (var item in targets)
            {
                tasks.Add(Task.Run(async () =>
                {
                    foreach (var remote in item.Repo.Remotes)
                    {
                        if (token.IsCancellationRequested)
                            return;

                        await new Commands.Fetch(item.Repo.FullPath, remote, NoTags, Force)
                            .WithCancellation(token)
                            .Use(log)
                            .ExecAsync();
                    }

                    item.Repo.MarkFetched();
                }, token));
            }

            await Task.WhenAll(tasks);

            log.Complete();
            _cancellation = null;
            return true;
        }

        public override void Terminate()
        {
            _ = _cancellation?.CancelAsync();
        }

        private readonly RepositoryGroup _group = null;
        private CancellationTokenSource _cancellation = null;
    }
}
