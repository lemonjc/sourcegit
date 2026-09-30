using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;

using CommunityToolkit.Mvvm.ComponentModel;

namespace SourceGit.ViewModels
{
    /// <summary>
    ///     Sidebar entry of a child repository inside a <see cref="RepositoryGroup"/>.
    /// </summary>
    public class RepositoryGroupChild : ObservableObject
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

        public string CurrentBranch => Repo.CurrentBranch?.Name ?? string.Empty;

        public bool HasPending => Repo.CurrentBranch is { IsTrackStatusVisible: true };

        public bool HasChanges => Repo.LocalChangesCount > 0;

        public int ChangesCount => Repo.LocalChangesCount;

        public bool IsFocused
        {
            get => _isFocused;
            set => SetProperty(ref _isFocused, value);
        }

        public void RefreshInfo()
        {
            OnPropertyChanged(nameof(CurrentBranch));
            OnPropertyChanged(nameof(HasPending));
            OnPropertyChanged(nameof(HasChanges));
            OnPropertyChanged(nameof(ChangesCount));
        }

        private bool _isFocused = false;
    }

    /// <summary>
    ///     A virtual repository aggregating multiple managed repositories. It behaves like a
    ///     normal <see cref="Repository"/> in the UI (history, local changes, fetch/pull/push,
    ///     branch creation/checkout are all merged or batch executed), while all git operations
    ///     are actually routed to the child repositories.
    /// </summary>
    public class RepositoryGroup : Repository
    {
        public string GroupName
        {
            get;
        }

        public List<RepositoryGroupChild> Children
        {
            get;
        } = [];

        public IReadOnlyList<Repository> Repositories => _repositories;

        public bool IsHomeFocused
        {
            get => _isHomeFocused;
            private set => SetProperty(ref _isHomeFocused, value);
        }

        public RepositoryGroup(RepositoryNode node)
        {
            Node = node;
            GroupName = string.IsNullOrEmpty(node.Name) ? "Repository Group" : node.Name;
            FullPath = node.Id;
            GitDir = node.Id;
            IsRepositoryGroup = true;
            OwnerGroup = this;
            Remotes = [];
        }

        internal RepositoryNode Node
        {
            get;
        }

        public override void Open()
        {
            var repoNodes = new List<RepositoryNode>();
            CollectRepositoryNodes(Node, repoNodes);

            foreach (var node in repoNodes)
            {
                if (!Directory.Exists(node.Id))
                {
                    SendNotification($"Repository does NOT exist any more: {node.Name ?? node.Id}", true);
                    continue;
                }

                var isBare = new Commands.IsBareRepository(node.Id).GetResult();
                var gitDir = isBare ? node.Id : Launcher.GetRepositoryGitDir(node.Id);
                if (string.IsNullOrEmpty(gitDir))
                {
                    SendNotification($"Given path is not a valid git repository: {node.Id}", true);
                    continue;
                }

                var repo = new Repository(isBare, node.Id, gitDir)
                {
                    OwnerGroup = this,
                };

                _repositories.Add(repo);
                _repoByPath.Add(repo.FullPath, repo);
                Children.Add(new RepositoryGroupChild
                {
                    Repo = repo,
                    Name = string.IsNullOrEmpty(node.Name) ? Path.GetFileName(node.Id) : node.Name,
                });

                repo.DataChanged += OnChildDataChanged;
                repo.Open();
            }

            if (_repositories.Count == 0)
            {
                SendNotification("No valid repository found in this group!", true);
                return;
            }

            Histories = new GroupHistories(this);
            WorkingCopy = new GroupWorkingCopy(this);
            StashesPage = new StashesPage(this);
            _selectedViewIndex = Preferences.Instance.ShowLocalChangesByDefault ? 1 : 0;

            RebuildBranches();
            RebuildCommits();
            RebuildWorkingCopy();
        }

        public override void Close()
        {
            foreach (var repo in _repositories)
            {
                repo.DataChanged -= OnChildDataChanged;
                repo.Close();
            }

            _repositories.Clear();
            _repoByPath.Clear();
            Children.Clear();
        }

        public bool HasRepository(Repository repo)
        {
            return repo != null && _repoByPath.ContainsKey(repo.FullPath);
        }

        public Repository FindChildByPath(string path)
        {
            if (string.IsNullOrEmpty(path))
                return null;

            var normalized = path.Replace('\\', '/').TrimEnd('/');
            return _repoByPath.TryGetValue(normalized, out var repo) ? repo : null;
        }

        public Repository GetDefaultRepository()
        {
            return _repositories.Count > 0 ? _repositories[0] : null;
        }

        public List<Models.Branch> GetBranchOccurrences(string branchName)
        {
            return _branchOccurrences.TryGetValue(branchName, out var list) ? list : [];
        }

        public string GetChildName(Repository repo)
        {
            foreach (var child in Children)
            {
                if (ReferenceEquals(child.Repo, repo))
                    return child.Name;
            }

            return repo.FullPath;
        }

        public override IDisposable LockWatcher()
        {
            var locks = new List<IDisposable>();
            foreach (var repo in _repositories)
            {
                var lockWatcher = repo.LockWatcher();
                if (lockWatcher != null)
                    locks.Add(lockWatcher);
            }

            return new CompositeLock(locks);
        }

        public override void RefreshAll()
        {
            foreach (var repo in _repositories)
                repo.RefreshAll();
        }

        public override void RefreshBranches()
        {
            foreach (var repo in _repositories)
                repo.RefreshBranches();
        }

        public override void RefreshCommits()
        {
            foreach (var repo in _repositories)
                repo.RefreshCommits();
        }

        public override void RefreshTags()
        {
            // Tags are not aggregated in group view.
        }

        public override void RefreshSubmodules()
        {
            // Submodules are not aggregated in group view.
        }

        public override void RefreshWorktrees()
        {
            // Worktrees are not aggregated in group view.
        }

        public override void RefreshWorkingCopyChanges()
        {
            foreach (var repo in _repositories)
                repo.RefreshWorkingCopyChanges();
        }

        public override void RefreshStashes()
        {
            foreach (var repo in _repositories)
                repo.RefreshStashes();
        }

        public override void MarkWorkingCopyDirtyManually()
        {
            foreach (var repo in _repositories)
                repo.MarkWorkingCopyDirtyManually();
        }

        public override void MarkBranchesDirtyManually()
        {
            foreach (var repo in _repositories)
                repo.MarkBranchesDirtyManually();
        }

        public override bool IncludeUntracked
        {
            get => _includeUntracked;
            set
            {
                if (_includeUntracked == value)
                    return;

                _includeUntracked = value;
                OnPropertyChanged();
                foreach (var repo in _repositories)
                    repo.IncludeUntracked = value;
            }
        }

        public override void NavigateToCommit(string sha, bool isDelayMode = false)
        {
            SelectedViewIndex = 0;
            Histories?.NavigateTo(sha);
        }

        public override async Task FetchAsync(bool autoStart)
        {
            if (!CanCreatePopup())
                return;

            var hasRemote = false;
            foreach (var repo in _repositories)
            {
                if (repo.Remotes.Count > 0)
                {
                    hasRemote = true;
                    break;
                }
            }

            if (!hasRemote)
            {
                SendNotification("No remotes added to any repository in this group!!!", true);
                return;
            }

            var popup = new FetchGroup(this);
            if (autoStart)
                await ShowAndStartPopupAsync(popup);
            else
                ShowPopup(popup);
        }

        public override async Task PullAsync(bool autoStart)
        {
            if (!CanCreatePopup())
                return;

            var hasUpstream = false;
            foreach (var repo in _repositories)
            {
                if (repo.Remotes.Count > 0 && repo.CurrentBranch != null)
                {
                    hasUpstream = true;
                    break;
                }
            }

            if (!hasUpstream)
            {
                SendNotification("No repository in this group is ready to pull!!!", true);
                return;
            }

            var popup = new PullGroup(this);
            if (autoStart)
                await ShowAndStartPopupAsync(popup);
            else
                ShowPopup(popup);
        }

        public override async Task PushAsync(bool autoStart)
        {
            if (!CanCreatePopup())
                return;

            var hasUpstream = false;
            foreach (var repo in _repositories)
            {
                if (repo.Remotes.Count > 0 && repo.CurrentBranch != null)
                {
                    hasUpstream = true;
                    break;
                }
            }

            if (!hasUpstream)
            {
                SendNotification("No repository in this group is ready to push!!!", true);
                return;
            }

            var popup = new PushGroup(this);
            if (autoStart)
                await ShowAndStartPopupAsync(popup);
            else
                ShowPopup(popup);
        }

        public override void CreateNewBranch()
        {
            var hasCommit = false;
            foreach (var repo in _repositories)
            {
                if (repo.CurrentBranch != null)
                {
                    hasCommit = true;
                    break;
                }
            }

            if (!hasCommit)
            {
                SendNotification("Git cannot create a branch before your first commit.", true);
                return;
            }

            if (CanCreatePopup())
                ShowPopup(new CreateBranchGroup(this));
        }

        public override async Task CheckoutBranchAsync(Models.Branch branch)
        {
            await CheckoutBranchInAllRepositoriesAsync(branch?.Name);
        }

        public async Task CheckoutBranchInAllRepositoriesAsync(string branchName)
        {
            if (string.IsNullOrEmpty(branchName) || !CanCreatePopup())
                return;

            ShowPopup(new CheckoutGroup(this, branchName));
        }

        public override Task StashAllAsync(bool autoStart)
        {
            SendNotification(App.Text("RepositoryGroup.StashNotSupported"), true);
            return Task.CompletedTask;
        }

        /// <summary>
        ///     Check that all involved repositories are on branches with the same name before
        ///     performing a cross repository stage/commit. Ask the user to confirm when they
        ///     are not.
        /// </summary>
        public async Task<bool> ConfirmSameBranchAsync(List<Repository> involved, string action)
        {
            if (involved == null || involved.Count < 2)
                return true;

            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var repo in involved)
                names.Add(repo.CurrentBranch?.Name ?? "(detached HEAD)");

            if (names.Count == 1)
                return true;

            var details = new List<string>();
            foreach (var repo in involved)
                details.Add($"- {GetChildName(repo)}: {repo.CurrentBranch?.Name ?? "(detached HEAD)"}");

            var msg = App.Text("RepositoryGroup.ConfirmBranchMismatch", action, string.Join("\n", details));
            return await App.AskConfirmAsync(msg, Models.ConfirmButtonType.YesNo);
        }

        /// <summary>
        ///     Switch the owner tab to show one child repository in single repository mode.
        /// </summary>
        public void FocusChild(RepositoryGroupChild child)
        {
            var page = GetOwnerPage();
            if (page == null || child == null)
                return;

            // Keep the currently opened page (Dashboard / Local Changes) when switching.
            if (page.Data is Repository previous && !ReferenceEquals(previous, child.Repo))
                child.Repo.SelectedViewIndex = previous.SelectedViewIndex;

            page.Data = child.Repo;
            UpdateFocus(child.Repo);
        }

        /// <summary>
        ///     Switch the owner tab back to the merged group view.
        /// </summary>
        public void FocusGroupHome()
        {
            var page = GetOwnerPage();
            if (page == null)
                return;

            // Keep the currently opened page (Dashboard / Local Changes) when switching back.
            if (page.Data is Repository previous && !ReferenceEquals(previous, this))
                SelectedViewIndex = previous.SelectedViewIndex;

            page.Data = this;
            UpdateFocus(null);
        }

        private void UpdateFocus(Repository repo)
        {
            IsHomeFocused = repo == null;
            foreach (var child in Children)
                child.IsFocused = ReferenceEquals(child.Repo, repo);
        }

        private void OnChildDataChanged(Repository repo, RepositoryDataChange change)
        {
            if (Histories == null || WorkingCopy == null)
                return;

            switch (change)
            {
                case RepositoryDataChange.Branches:
                    RebuildBranches();
                    break;
                case RepositoryDataChange.Commits:
                    RebuildCommits();
                    break;
                case RepositoryDataChange.WorkingCopy:
                    RebuildWorkingCopy();
                    break;
                default:
                    break;
            }
        }

        private void RebuildBranches()
        {
            var byName = new Dictionary<string, List<Models.Branch>>(StringComparer.Ordinal);
            foreach (var repo in _repositories)
            {
                foreach (var branch in repo.Branches)
                {
                    if (!branch.IsLocal || branch.IsDetachedHead)
                        continue;

                    if (!byName.TryGetValue(branch.Name, out var list))
                    {
                        list = new List<Models.Branch>();
                        byName.Add(branch.Name, list);
                    }

                    list.Add(branch);
                }
            }

            var display = new List<Models.Branch>();
            foreach (var kv in byName)
            {
                ulong latest = 0;
                var allCurrent = true;
                foreach (var branch in kv.Value)
                {
                    if (branch.CommitterDate > latest)
                        latest = branch.CommitterDate;

                    if (!branch.IsCurrent)
                        allCurrent = false;
                }

                display.Add(new Models.Branch
                {
                    Name = kv.Key,
                    FullName = $"refs/heads/{kv.Key}",
                    CommitterDate = latest,
                    Head = kv.Value[0].Head,
                    IsLocal = true,
                    IsCurrent = allCurrent,
                });
            }

            _branchOccurrences = byName;
            Branches = display;
            LocalBranchesCount = display.Count;


            var builder = BuildBranchTree(display, []);
            LocalBranchTrees = builder.Locals;
            RemoteBranchTrees = [];

            var currentBranchName = _repositories.Count > 0 ? _repositories[0].CurrentBranch?.Name : null;
            var allOnSameBranch = currentBranchName != null;
            foreach (var repo in _repositories)
            {
                if (!string.Equals(repo.CurrentBranch?.Name, currentBranchName, StringComparison.Ordinal))
                {
                    allOnSameBranch = false;
                    break;
                }
            }

            Models.Branch current = null;
            if (allOnSameBranch)
                current = display.Find(x => x.IsLocal && x.Name.Equals(currentBranchName, StringComparison.Ordinal));

            CurrentBranch = current;

            var hasPendingPullOrPush = false;
            foreach (var repo in _repositories)
            {
                if (repo.CurrentBranch is { IsTrackStatusVisible: true })
                {
                    hasPendingPullOrPush = true;
                    break;
                }
            }

            GetOwnerPage()?.ChangeDirtyState(Models.DirtyState.HasPendingPullOrPush, !hasPendingPullOrPush);

            foreach (var child in Children)
                child.RefreshInfo();
        }

        private void RebuildCommits()
        {
            var merged = new List<Models.Commit>();
            var shaToRepo = new Dictionary<string, Repository>();
            var repoNames = new Dictionary<Repository, string>();

            foreach (var child in Children)
            {
                repoNames.Add(child.Repo, child.Name);

                foreach (var commit in child.Repo.Histories.Commits)
                {
                    merged.Add(commit);
                    if (!shaToRepo.ContainsKey(commit.SHA))
                        shaToRepo.Add(commit.SHA, child.Repo);
                }
            }

            merged.Sort((l, r) => r.CommitterTime.CompareTo(l.CommitterTime));
            (Histories as GroupHistories)?.SetMergedCommits(merged, shaToRepo, repoNames);
        }

        private void RebuildWorkingCopy()
        {
            var all = new List<Models.Change>();
            foreach (var child in Children)
            {
                var seen = new HashSet<Models.Change>();
                foreach (var change in child.Repo.WorkingCopy.Unstaged)
                    seen.Add(change);
                foreach (var change in child.Repo.WorkingCopy.Staged)
                    seen.Add(change);

                foreach (var source in seen)
                    all.Add(new GroupChange(child.Repo, child.Name, source));
            }

            all.Sort((l, r) => Models.NumericSort.Compare(l.Path, r.Path));
            WorkingCopy.SetData(all);
            LocalChangesCount = all.Count;
            GetOwnerPage()?.ChangeDirtyState(Models.DirtyState.HasLocalChanges, all.Count == 0);

            foreach (var child in Children)
                child.RefreshInfo();
        }

        private static void CollectRepositoryNodes(RepositoryNode node, List<RepositoryNode> into)
        {
            foreach (var sub in node.SubNodes)
            {
                if (sub.IsRepository)
                    into.Add(sub);
                else
                    CollectRepositoryNodes(sub, into);
            }
        }

        private class CompositeLock(List<IDisposable> locks) : IDisposable
        {
            public void Dispose()
            {
                foreach (var lockWatcher in _locks)
                    lockWatcher.Dispose();
            }

            private readonly List<IDisposable> _locks = locks;
        }

        private readonly List<Repository> _repositories = [];
        private readonly Dictionary<string, Repository> _repoByPath = new(StringComparer.OrdinalIgnoreCase);
        private Dictionary<string, List<Models.Branch>> _branchOccurrences = new(StringComparer.Ordinal);
        private bool _isHomeFocused = true;
        private bool _includeUntracked = true;
    }
}
