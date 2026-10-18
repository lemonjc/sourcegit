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

        public List<Models.Branch> Branches
        {
            get;
            init;
        }

        public Models.Branch SelectedBranch
        {
            get => _selectedBranch;
            set
            {
                if (SetProperty(ref _selectedBranch, value))
                    Resolve();
            }
        }

        public bool AllowOverwrite
        {
            get => _allowOverwrite;
            set
            {
                if (_allowOverwrite != value)
                {
                    _allowOverwrite = value;
                    Resolve();
                }
            }
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

        private void Resolve()
        {
            LocalBranch = null;
            RemoteBranch = null;

            var selected = _selectedBranch;
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
                    if (sameName != null && !_allowOverwrite)
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

        private Models.Branch _selectedBranch = null;
        private bool _allowOverwrite = false;
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
                {
                    // The top selector applies to every repository; each repository can
                    // still be changed individually afterwards. Branches are matched by
                    // name because every repository has its own branch instances.
                    foreach (var item in Repositories)
                        item.SelectedBranch = value == null ? null : item.Branches.Find(x => IsSameBranch(x, value));
                }
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
                {
                    foreach (var item in Repositories)
                        item.AllowOverwrite = value;
                }
            }
        }

        public CheckoutBranchGroup(RepositoryGroup group)
        {
            _group = group;

            var repos = new List<Repository>();
            var optionsByRepo = new Dictionary<Repository, List<Models.Branch>>();
            HashSet<string> commonLocals = null;
            HashSet<string> commonRemotes = null;

            foreach (var repo in group.Repositories)
            {
                if (repo.IsBare)
                    continue;

                repos.Add(repo);

                var locals = new List<Models.Branch>();
                var remotes = new List<Models.Branch>();
                foreach (var branch in repo.Branches)
                {
                    if (branch.IsDetachedHead)
                        continue;

                    if (branch.IsLocal)
                        locals.Add(branch);
                    else
                        remotes.Add(branch);
                }

                locals.Sort((l, r) => Models.NumericSort.Compare(l.Name, r.Name));
                remotes.Sort((l, r) => Models.NumericSort.Compare(l.FriendlyName, r.FriendlyName));

                var options = new List<Models.Branch>(locals.Count + remotes.Count);
                options.AddRange(locals);
                options.AddRange(remotes);
                optionsByRepo.Add(repo, options);

                var localNames = new HashSet<string>(StringComparer.Ordinal);
                foreach (var b in locals)
                    localNames.Add(b.Name);

                var remoteNames = new HashSet<string>(StringComparer.Ordinal);
                foreach (var b in remotes)
                    remoteNames.Add(b.FriendlyName);

                if (commonLocals == null)
                    commonLocals = localNames;
                else
                    commonLocals.IntersectWith(localNames);

                if (commonRemotes == null)
                    commonRemotes = remoteNames;
                else
                    commonRemotes.IntersectWith(remoteNames);
            }

            // The "apply to all" selector only contains branches that exist in every
            // repository, so applying it never produces a missing branch row.
            Branches = [];
            if (repos.Count > 0 && commonLocals != null && commonRemotes != null)
            {
                foreach (var branch in optionsByRepo[repos[0]])
                {
                    if (branch.IsLocal && commonLocals.Contains(branch.Name))
                        Branches.Add(branch);
                }

                foreach (var branch in optionsByRepo[repos[0]])
                {
                    if (!branch.IsLocal && commonRemotes.Contains(branch.FriendlyName))
                        Branches.Add(branch);
                }
            }

            var current = group.CurrentBranch;
            var selected = current != null
                ? Branches.Find(x => x.IsLocal && x.Name.Equals(current.Name, StringComparison.Ordinal))
                : null;
            _selectedBranch = selected ?? (Branches.Count > 0 ? Branches[0] : null);

            Repositories = [];
            foreach (var repo in repos)
            {
                var options = optionsByRepo[repo];
                var initial = _selectedBranch != null ? options.Find(x => IsSameBranch(x, _selectedBranch)) : null;
                if (initial == null && repo.CurrentBranch != null)
                    initial = options.Find(x => x.IsLocal && x.Name.Equals(repo.CurrentBranch.Name, StringComparison.Ordinal));
                initial ??= options.Count > 0 ? options[0] : null;

                var item = new CheckoutBranchGroupRepository
                {
                    Repo = repo,
                    Name = group.GetChildName(repo),
                    Branches = options,
                    SelectedBranch = initial,
                };

                Repositories.Add(item);
            }
        }

        public override async Task<bool> Sure()
        {
            var targets = new List<CheckoutBranchGroupRepository>();
            foreach (var item in Repositories)
            {
                if (item.IsEnabled && item.Action != GroupCheckoutAction.Skip)
                    targets.Add(item);
            }

            if (targets.Count == 0)
                return true;

            using var lockWatcher = _group.LockWatcher();

            var log = _group.CreateLog($"Checkout Branch in {targets.Count} repositories");
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

        private static bool IsSameBranch(Models.Branch l, Models.Branch r)
        {
            if (l == null || r == null || l.IsLocal != r.IsLocal)
                return false;

            return l.IsLocal
                ? l.Name.Equals(r.Name, StringComparison.Ordinal)
                : l.FriendlyName.Equals(r.FriendlyName, StringComparison.Ordinal);
        }

        private readonly RepositoryGroup _group = null;
        private Models.Branch _selectedBranch = null;
        private bool _allowOverwrite = false;
    }
}
