using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

using CommunityToolkit.Mvvm.ComponentModel;

namespace SourceGit.ViewModels
{
    public class PullGroupRepository : ObservableObject
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

        public Models.Branch Current => Repo.CurrentBranch;

        public Models.Remote UpstreamRemote => FindUpstreamRemote();

        public string UpstreamBranch => Current?.Upstream ?? string.Empty;

        public bool CanPull => UpstreamRemote != null && !string.IsNullOrEmpty(UpstreamBranch);

        public bool HasLocalChanges => Repo.LocalChangesCount > 0;

        public bool IsEnabled
        {
            get => _isEnabled;
            set => SetProperty(ref _isEnabled, value);
        }

        private Models.Remote FindUpstreamRemote()
        {
            var upstream = Current?.Upstream;
            if (string.IsNullOrEmpty(upstream))
                return null;

            var end = upstream.IndexOf('/', 13);
            if (end <= 13)
                return null;

            var remoteName = upstream.Substring(13, end - 13);
            return Repo.Remotes.Find(x => x.Name.Equals(remoteName, StringComparison.Ordinal));
        }

        private bool _isEnabled = true;
    }

    public class PullGroup : Popup
    {
        public List<PullGroupRepository> Repositories
        {
            get;
        }

        public bool UseRebase
        {
            get;
            set;
        }

        public PullGroup(RepositoryGroup group)
        {
            _group = group;
            CanTerminate = true;

            Repositories = [];
            foreach (var repo in group.Repositories)
            {
                Repositories.Add(new PullGroupRepository
                {
                    Repo = repo,
                    Name = group.GetChildName(repo),
                    IsEnabled = repo.LocalChangesCount == 0,
                });
            }
        }

        public override async Task<bool> Sure()
        {
            var targets = new List<PullGroupRepository>();
            foreach (var item in Repositories)
            {
                if (item.IsEnabled && item.CanPull && !item.HasLocalChanges)
                    targets.Add(item);
            }

            if (targets.Count == 0)
                return true;

            using var lockWatcher = _group.LockWatcher();

            var log = _group.CreateLog($"Pull Group '{_group.GroupName}'");
            Use(log);

            _cancellation = new CancellationTokenSource();
            var token = _cancellation.Token;

            var tasks = new List<Task>();
            foreach (var item in targets)
            {
                tasks.Add(Task.Run(async () =>
                {
                    if (token.IsCancellationRequested)
                        return;

                    var remoteBranch = item.Repo.Branches.Find(x => x.FullName.Equals(item.UpstreamBranch, StringComparison.Ordinal));
                    if (remoteBranch == null)
                        return;

                    await new Commands.Pull(item.Repo.FullPath, item.UpstreamRemote, remoteBranch, UseRebase)
                        .WithCancellation(token)
                        .Use(log)
                        .ExecAsync();
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
