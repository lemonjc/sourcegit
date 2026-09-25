using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

using CommunityToolkit.Mvvm.ComponentModel;

namespace SourceGit.ViewModels
{
    public class PushGroupRepository : ObservableObject
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

        public Models.Branch UpstreamRemoteBranch => FindUpstreamRemoteBranch();

        public bool HasUpstream => UpstreamRemoteBranch != null;

        public string TargetDescription
        {
            get
            {
                if (Current == null)
                    return string.Empty;

                if (UpstreamRemoteBranch != null)
                    return UpstreamRemoteBranch.FriendlyName;

                var remote = FindDefaultRemote();
                return remote != null ? $"({remote.Name}/{Current.Name})" : string.Empty;
            }
        }

        public bool IsEnabled
        {
            get => _isEnabled;
            set => SetProperty(ref _isEnabled, value);
        }

        public Models.Remote FindDefaultRemote()
        {
            if (Repo.Remotes.Count == 0)
                return null;

            if (!string.IsNullOrEmpty(Repo.Settings.DefaultRemote))
            {
                var def = Repo.Remotes.Find(x => x.Name.Equals(Repo.Settings.DefaultRemote, StringComparison.Ordinal));
                if (def != null)
                    return def;
            }

            return Repo.Remotes[0];
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

        private Models.Branch FindUpstreamRemoteBranch()
        {
            var upstream = Current?.Upstream;
            if (string.IsNullOrEmpty(upstream))
                return null;

            return Repo.Branches.Find(x => !x.IsLocal && x.FullName.Equals(upstream, StringComparison.Ordinal));
        }

        private bool _isEnabled = true;
    }

    public class PushGroup : Popup
    {
        public List<PushGroupRepository> Repositories
        {
            get;
        }

        public bool PushAllTags
        {
            get;
            set;
        }

        public PushGroup(RepositoryGroup group)
        {
            _group = group;
            CanTerminate = true;

            Repositories = [];
            foreach (var repo in group.Repositories)
            {
                Repositories.Add(new PushGroupRepository
                {
                    Repo = repo,
                    Name = group.GetChildName(repo),
                    IsEnabled = repo.CurrentBranch != null,
                });
            }
        }

        public override async Task<bool> Sure()
        {
            var targets = new List<PushGroupRepository>();
            foreach (var item in Repositories)
            {
                if (item.IsEnabled && item.Current != null && !string.IsNullOrEmpty(item.TargetDescription))
                    targets.Add(item);
            }

            if (targets.Count == 0)
                return true;

            using var lockWatcher = _group.LockWatcher();

            var log = _group.CreateLog($"Push Group '{_group.GroupName}'");
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

                    var local = item.Repo.Branches.Find(x => x.IsLocal && x.IsCurrent) ?? item.Current;
                    Models.Remote remote;
                    Models.Branch remoteBranch;
                    var track = false;

                    if (item.HasUpstream)
                    {
                        remote = item.UpstreamRemote;
                        remoteBranch = item.UpstreamRemoteBranch;
                    }
                    else
                    {
                        remote = item.FindDefaultRemote();
                        if (remote == null)
                            return;

                        remoteBranch = new Models.Branch
                        {
                            Name = local.Name,
                            Remote = remote.Name,
                        };
                        track = true;
                    }

                    await new Commands.Push(item.Repo.FullPath, local, remote, remoteBranch, PushAllTags, false, track, false, false)
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
