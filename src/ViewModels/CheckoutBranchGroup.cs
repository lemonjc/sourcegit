using System;
using System.Collections.Generic;
using System.Threading.Tasks;

using CommunityToolkit.Mvvm.ComponentModel;

namespace SourceGit.ViewModels
{
    public enum GroupCheckoutAction
    {
        Skip,
        CheckoutLocal,
        FastForward,
        CreateFromRemote,
    }

    public class CheckoutBranchGroupRepository : ObservableObject
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

        public GroupCheckoutAction Action
        {
            get;
            private set;
        } = GroupCheckoutAction.Skip;

        public string ActionDescription
        {
            get;
            private set;
        } = string.Empty;

        public Models.Branch LocalBranch
        {
            get;
            private set;
        }

        public Models.Branch RemoteBranch
        {
            get;
            private set;
        }

        public bool IsEnabled
        {
            get => _isEnabled;
            set => SetProperty(ref _isEnabled, value);
        }

        public bool CanToggle => Action != GroupCheckoutAction.Skip;

        public void Resolve(Models.Branch selected, bool allowOverwrite)
        {
            LocalBranch = null;
            RemoteBranch = null;

            if (selected == null)
            {
                Post(GroupCheckoutAction.Skip, string.Empty);
                return;
            }

            if (selected.IsLocal)
            {
                LocalBranch = Repo.Branches.Find(x => x.IsLocal && x.Name.Equals(selected.Name, StringComparison.Ordinal));
                if (LocalBranch == null)
                    Post(GroupCheckoutAction.Skip, App.Text("CheckoutBranchGroup.LocalMissing"));
                else if (LocalBranch.IsCurrent)
                    Post(GroupCheckoutAction.Skip, App.Text("CheckoutBranchGroup.AlreadyOn"));
                else
                    Post(GroupCheckoutAction.CheckoutLocal, $"{App.Text("CheckoutGroup.WillCheckout")} {LocalBranch.Name}");
            }
            else
            {
                RemoteBranch = Repo.Branches.Find(x => !x.IsLocal && x.FriendlyName.Equals(selected.FriendlyName, StringComparison.Ordinal));
                if (RemoteBranch == null)
                {
                    Post(GroupCheckoutAction.Skip, App.Text("CheckoutBranchGroup.RemoteMissing"));
                    return;
                }

                // Same decision tree as checking out a remote branch in a single repository:
                // prefer the local branch that tracks it (fast-forward when behind), otherwise
                // create a local branch from the remote branch.
                var tracking = Repo.Branches.Find(x => x.IsLocal &&
                    !string.IsNullOrEmpty(x.Upstream) &&
                    x.Upstream.Equals(RemoteBranch.FullName, StringComparison.Ordinal) &&
                    x.Ahead.Count == 0);

                if (tracking != null)
                {
                    LocalBranch = tracking;

                    if (tracking.Behind.Count > 0)
                        Post(GroupCheckoutAction.FastForward, App.Text("CheckoutBranchGroup.FastForward", RemoteBranch.FriendlyName));
                    else if (tracking.IsCurrent)
                        Post(GroupCheckoutAction.Skip, App.Text("CheckoutBranchGroup.AlreadyOn"));
                    else
                        Post(GroupCheckoutAction.CheckoutLocal, $"{App.Text("CheckoutGroup.WillCheckout")} {tracking.Name}");
                }
                else
                {
                    var sameName = Repo.Branches.Find(x => x.IsLocal && x.Name.Equals(RemoteBranch.Name, StringComparison.Ordinal));
                    if (sameName != null && !allowOverwrite)
                        Post(GroupCheckoutAction.Skip, App.Text("CheckoutBranchGroup.OverwriteNeeded"));
                    else
                        Post(GroupCheckoutAction.CreateFromRemote, App.Text("CheckoutBranchGroup.CreateFromRemote", RemoteBranch.Name));
                }
            }
        }

        private void Post(GroupCheckoutAction action, string desc)
        {
            Action = action;
            ActionDescription = desc;
            OnPropertyChanged(nameof(Action));
            OnPropertyChanged(nameof(ActionDescription));
            OnPropertyChanged(nameof(CanToggle));

            if (action == GroupCheckoutAction.Skip)
                IsEnabled = false;
            else
                IsEnabled = true;
        }

        private bool _isEnabled = true;
    }

    public class CheckoutBranchGroup : Popup
    {
        public List<Models.Branch> Branches
        {
            get;
        }

        public Models.Branch SelectedBranch
        {
            get => _selectedBranch;
            set
            {
                if (SetProperty(ref _selectedBranch, value))
                    ResolveAll();
            }
        }

        public List<CheckoutBranchGroupRepository> Repositories
        {
            get;
        }

        public bool AllowOverwrite
        {
            get => _allowOverwrite;
            set
            {
                if (SetProperty(ref _allowOverwrite, value))
                    ResolveAll();
            }
        }

        public CheckoutBranchGroup(RepositoryGroup group)
        {
            _group = group;

            var locals = new Dictionary<string, Models.Branch>(StringComparer.Ordinal);
            var remotes = new Dictionary<string, Models.Branch>(StringComparer.Ordinal);
            var repos = new List<Repository>();

            foreach (var repo in group.Repositories)
            {
                if (repo.IsBare)
                    continue;

                repos.Add(repo);

                foreach (var branch in repo.Branches)
                {
                    if (branch.IsDetachedHead)
                        continue;

                    if (branch.IsLocal)
                    {
                        if (!locals.ContainsKey(branch.Name))
                            locals.Add(branch.Name, branch);
                    }
                    else if (!remotes.ContainsKey(branch.FriendlyName))
                    {
                        remotes.Add(branch.FriendlyName, branch);
                    }
                }
            }

            Branches = [];
            var localOptions = new List<Models.Branch>(locals.Values);
            localOptions.Sort((l, r) => Models.NumericSort.Compare(l.Name, r.Name));
            Branches.AddRange(localOptions);

            var remoteOptions = new List<Models.Branch>(remotes.Values);
            remoteOptions.Sort((l, r) => Models.NumericSort.Compare(l.FriendlyName, r.FriendlyName));
            Branches.AddRange(remoteOptions);

            Repositories = [];
            foreach (var repo in repos)
                Repositories.Add(new CheckoutBranchGroupRepository { Repo = repo, Name = group.GetChildName(repo) });

            var current = group.CurrentBranch;
            _selectedBranch = current != null
                ? Branches.Find(x => x.IsLocal && x.Name.Equals(current.Name, StringComparison.Ordinal))
                : null;
            _selectedBranch ??= Branches.Count > 0 ? Branches[0] : null;

            ResolveAll();
        }

        public override async Task<bool> Sure()
        {
            var selected = _selectedBranch;
            if (selected == null)
                return true;

            var targets = new List<CheckoutBranchGroupRepository>();
            foreach (var item in Repositories)
            {
                if (item.IsEnabled && item.Action != GroupCheckoutAction.Skip)
                    targets.Add(item);
            }

            if (targets.Count == 0)
                return true;

            using var lockWatcher = _group.LockWatcher();

            var log = _group.CreateLog($"Checkout Branch '{selected.Name}' in {targets.Count} repositories");
            Use(log);

            foreach (var item in targets)
            {
                switch (item.Action)
                {
                    case GroupCheckoutAction.CheckoutLocal:
                        {
                            var local = item.LocalBranch;
                            var succ = await new Commands.Checkout(item.Repo.FullPath)
                                .Use(log)
                                .BranchAsync(local.Name, false);
                            if (succ)
                                item.Repo.RefreshAfterCheckoutBranch(local);
                            break;
                        }
                    case GroupCheckoutAction.FastForward:
                        {
                            var local = item.LocalBranch;
                            var remote = item.RemoteBranch;
                            var succ = await new Commands.Checkout(item.Repo.FullPath)
                                .Use(log)
                                .BranchAsync(local.Name, remote.Head, false, true);
                            if (succ)
                            {
                                local.Behind.Clear();
                                local.Head = remote.Head;
                                local.CommitterDate = remote.CommitterDate;
                                item.Repo.RefreshAfterCheckoutBranch(local);
                            }
                            break;
                        }
                    case GroupCheckoutAction.CreateFromRemote:
                        {
                            var remote = item.RemoteBranch;
                            var name = remote.Name;
                            var succ = await new Commands.Checkout(item.Repo.FullPath)
                                .Use(log)
                                .BranchAsync(name, remote.Head, false, _allowOverwrite);
                            if (succ)
                            {
                                var created = new Models.Branch
                                {
                                    Name = name,
                                    FullName = $"refs/heads/{name}",
                                    CommitterDate = remote.CommitterDate,
                                    Head = remote.Head,
                                    IsLocal = true,
                                    IsCurrent = true,
                                    Upstream = remote.FullName,
                                };

                                await new Commands.Branch(item.Repo.FullPath, name)
                                    .Use(log)
                                    .SetUpstreamAsync(remote);

                                item.Repo.RefreshAfterCreateBranch(created, true);
                            }
                            break;
                        }
                    default:
                        break;
                }
            }

            log.Complete();
            return true;
        }

        private void ResolveAll()
        {
            foreach (var item in Repositories)
                item.Resolve(_selectedBranch, _allowOverwrite);
        }

        private readonly RepositoryGroup _group = null;
        private Models.Branch _selectedBranch = null;
        private bool _allowOverwrite = false;
    }
}
