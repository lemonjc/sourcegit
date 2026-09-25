using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace SourceGit.ViewModels
{
    /// <summary>
    ///     History viewmodel for a <see cref="RepositoryGroup"/>. The commit list is merged from
    ///     all child repositories (sorted by time). All commit related operations are routed back
    ///     to the child repository that owns the selected commit.
    /// </summary>
    public class GroupHistories : Histories
    {
        private readonly Dictionary<string, Repository> _shaToRepo = new();

        public GroupHistories(Repository repo)
            : base(repo)
        {
        }

        public void SetMergedCommits(List<Models.Commit> commits, Dictionary<string, Repository> shaToRepo, Dictionary<Repository, string> repoNames)
        {
            _shaToRepo.Clear();
            foreach (var kv in shaToRepo)
                _shaToRepo.Add(kv.Key, kv.Value);

            // Clone each commit and prepend a repository decorator, so that the merged
            // history clearly shows which child repository owns the commit. The child
            // repositories keep sharing their original (untouched) commit instances.
            var cloned = new List<Models.Commit>(commits.Count);
            foreach (var commit in commits)
            {
                var repo = shaToRepo.TryGetValue(commit.SHA, out var owner) ? owner : null;
                var repoName = repo != null && repoNames.TryGetValue(repo, out var name) ? name : null;
                cloned.Add(CloneWithRepository(commit, repoName));
            }

            IsLoading = false;
            Commits = cloned;
        }

        private static Models.Commit CloneWithRepository(Models.Commit source, string repoName)
        {
            var cloned = new Models.Commit
            {
                SHA = source.SHA,
                Author = source.Author,
                AuthorTime = source.AuthorTime,
                Committer = source.Committer,
                CommitterTime = source.CommitterTime,
                Subject = source.Subject,
                Parents = source.Parents,
                IsMerged = source.IsMerged,
                Color = source.Color,
                LeftMargin = source.LeftMargin,
                IsHighlightedInGraph = source.IsHighlightedInGraph,
            };

            var decorators = new List<Models.Decorator>(source.Decorators.Count + 1);
            if (!string.IsNullOrEmpty(repoName))
                decorators.Add(new Models.Decorator { Type = Models.DecoratorType.Repository, Name = repoName });

            decorators.AddRange(source.Decorators);
            cloned.Decorators = decorators;

            return cloned;
        }

        public Repository FindOwner(Models.Commit commit)
        {
            if (commit != null && _shaToRepo.TryGetValue(commit.SHA, out var repo))
                return repo;

            return null;
        }

        public override void NavigateTo(string commitSHA)
        {
            var commit = Commits.Find(x => x.SHA.StartsWith(commitSHA, StringComparison.Ordinal));
            if (commit != null)
                SelectedCommits = [commit];
        }

        public override async Task<Models.Commit> GetCommitAsync(string sha)
        {
            var commit = Commits.Find(x => x.SHA.StartsWith(sha, StringComparison.Ordinal));
            if (commit != null)
                return commit;

            var queried = new HashSet<Repository>();
            foreach (var repo in _shaToRepo.Values)
            {
                if (!queried.Add(repo))
                    continue;

                var result = await new Commands.QuerySingleCommit(repo.FullPath, sha)
                    .GetResultAsync()
                    .ConfigureAwait(false);
                if (result != null)
                    return result;
            }

            return null;
        }

        public override void CheckoutCommitDetached(Models.Commit c)
        {
            var owner = FindOwner(c);
            if (owner == null || c.IsCurrentHead || !owner.CanCreatePopup())
                return;

            owner.ShowPopup(new CheckoutDetached(owner, c));
        }

        public override async Task<bool> CheckoutBranchByDecoratorAsync(Models.Decorator decorator)
        {
            if (decorator == null)
                return false;

            if (decorator.Type == Models.DecoratorType.Repository)
                return true;

            if (decorator.Type == Models.DecoratorType.CurrentBranchHead ||
                decorator.Type == Models.DecoratorType.CurrentCommitHead)
                return true;

            if (decorator.Type == Models.DecoratorType.LocalBranchHead)
            {
                var group = _repo as RepositoryGroup;
                var branch = _repo.Branches.Find(x => x.IsLocal && x.Name == decorator.Name);
                if (branch == null || group == null)
                    return false;

                await group.CheckoutBranchInAllRepositoriesAsync(branch.Name);
                return true;
            }

            _repo.SendNotification(App.Text("RepositoryGroup.RemoteBranchInGroupUnsupported"), true);
            return false;
        }

        public override async Task CheckoutBranchByCommitAsync(Models.Commit commit)
        {
            if (commit.IsCurrentHead)
                return;

            var owner = FindOwner(commit);
            if (owner == null)
                return;

            Models.Branch firstRemoteBranch = null;
            foreach (var d in commit.Decorators)
            {
                if (d.Type == Models.DecoratorType.LocalBranchHead)
                {
                    var b = owner.Branches.Find(x => x.Name == d.Name);
                    if (b == null)
                        continue;

                    await owner.CheckoutBranchAsync(b);
                    return;
                }

                if (d.Type == Models.DecoratorType.RemoteBranchHead)
                {
                    var rb = owner.Branches.Find(x => x.FriendlyName == d.Name);
                    if (rb == null)
                        continue;

                    var lb = owner.Branches.Find(x => x.IsLocal && x.Upstream == rb.FullName);
                    if (lb != null && lb.Behind.Count > 0 && lb.Ahead.Count == 0)
                    {
                        if (owner.CanCreatePopup())
                            owner.ShowPopup(new CheckoutAndFastForward(owner, lb, rb));
                        return;
                    }

                    firstRemoteBranch ??= rb;
                }
            }

            if (owner.CanCreatePopup())
            {
                if (firstRemoteBranch != null)
                    owner.ShowPopup(new CreateBranch(owner, firstRemoteBranch));
                else if (!owner.IsBare)
                    owner.ShowPopup(new CheckoutDetached(owner, commit));
            }
        }

        public override async Task CherryPickAsync(Models.Commit commit)
        {
            var owner = FindOwner(commit);
            if (owner == null || !owner.CanCreatePopup())
                return;

            if (commit.Parents.Count <= 1)
            {
                owner.ShowPopup(new CherryPick(owner, [commit]));
                return;
            }

            var parents = new List<Models.Commit>();
            foreach (var sha in commit.Parents)
            {
                var parent = Commits.Find(x => x.SHA.Equals(sha, StringComparison.Ordinal));
                if (parent == null)
                    parent = await new Commands.QuerySingleCommit(owner.FullPath, sha).GetResultAsync();

                if (parent != null)
                    parents.Add(parent);
            }

            owner.ShowPopup(new CherryPick(owner, commit, parents));
        }

        public override async Task<string> GetCommitFullMessageAsync(Models.Commit commit)
        {
            var owner = FindOwner(commit) ?? throw new InvalidOperationException("Cannot find the repository owning the commit");
            return await new Commands.QueryCommitFullMessage(owner.FullPath, commit.SHA)
                .GetResultAsync()
                .ConfigureAwait(false);
        }

        public override async Task<Models.Commit> CompareWithHeadAsync(Models.Commit commit)
        {
            var owner = FindOwner(commit);
            if (owner == null)
                return null;

            Models.Commit head = null;
            foreach (var c in Commits)
            {
                if (c.IsCurrentHead && _shaToRepo.TryGetValue(c.SHA, out var repo) && ReferenceEquals(repo, owner))
                {
                    head = c;
                    break;
                }
            }

            if (head == null)
            {
                head = await new Commands.QuerySingleCommit(owner.FullPath, "HEAD").GetResultAsync();
                if (head != null)
                    DetailContext = new RevisionCompare(owner, commit, head);

                return null;
            }

            return head;
        }

        public override void CompareWithWorktree(Models.Commit commit)
        {
            var owner = FindOwner(commit);
            if (owner != null)
                DetailContext = new RevisionCompare(owner, commit, null);
        }

        protected override void PostSelectedCommitsChanged()
        {
            if (_ignoreSelectionChange)
                return;

            var selected = SelectedCommits;
            if (selected.Count == 0)
            {
                DetailContext = Models.Null.Instance;
            }
            else if (selected.Count == 1)
            {
                var c = selected[0];
                if (DetailContext is CommitDetail detail)
                {
                    detail.Commit = c;
                }
                else
                {
                    var owner = FindOwner(c) ?? (_repo as RepositoryGroup)?.GetDefaultRepository();
                    if (owner != null)
                        DetailContext = new CommitDetail(owner, _commitDetailSharedData) { Commit = c };
                }
            }
            else if (selected.Count == 2)
            {
                var left = FindOwner(selected[0]);
                var right = FindOwner(selected[1]);
                if (left != null && ReferenceEquals(left, right))
                {
                    if (DetailContext is RevisionCompare compare)
                        compare.SetTargets(selected[1], selected[0]);
                    else
                        DetailContext = new RevisionCompare(left, selected[1], selected[0]);
                }
                else
                {
                    DetailContext = new Models.Count(selected.Count);
                }
            }
            else
            {
                DetailContext = new Models.Count(selected.Count);
            }

            if (_repo.UIStates.GraphHighlighting >= Models.CommitGraphHighlighting.SelectedCommitsOnly)
                GenerateGraph(Commits);
        }
    }
}
