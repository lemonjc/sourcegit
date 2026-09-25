using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace SourceGit.ViewModels
{
    /// <summary>
    ///     Working copy viewmodel for a <see cref="RepositoryGroup"/>. The change lists are
    ///     merged from all child repositories as <see cref="GroupChange"/> copies. Staging,
    ///     unstaging, committing and diffing are routed back to the child repository that
    ///     owns each file.
    /// </summary>
    public class GroupWorkingCopy : WorkingCopy
    {
        private readonly RepositoryGroup _group;

        public GroupWorkingCopy(RepositoryGroup repo)
            : base(repo)
        {
            _group = repo;
        }

        public override bool UseAmend
        {
            get => base.UseAmend;
            set
            {
                if (value)
                {
                    _group.SendNotification(App.Text("RepositoryGroup.AmendNotSupported"), true);
                    return;
                }

                base.UseAmend = value;
            }
        }

        public override async Task StageChangesAsync(List<Models.Change> changes, Models.Change next)
        {
            var grouped = GroupByOwner(changes);
            if (grouped.Count == 0)
                return;

            if (!await _group.ConfirmSameBranchAsync(
                    grouped.Keys.ToList(),
                    App.Text("RepositoryGroup.StageBranchCheck")))
            {
                return;
            }

            IsStaging = true;
            SelectedUnstaged = new(next != null ? new List<Models.Change> { next } : null);

            using (var lockWatcher = _group.LockWatcher())
            {
                foreach (var kv in grouped)
                {
                    var owner = kv.Key;
                    var realChanges = CollectRealChanges(kv.Value);
                    await owner.WorkingCopy.StageChangesAsync(realChanges, null);
                }
            }

            IsStaging = false;
        }

        public override async Task UnstageChangesAsync(List<Models.Change> changes, Models.Change next)
        {
            var grouped = GroupByOwner(changes);
            if (grouped.Count == 0)
                return;

            if (!await _group.ConfirmSameBranchAsync(
                    grouped.Keys.ToList(),
                    App.Text("RepositoryGroup.StageBranchCheck")))
            {
                return;
            }

            IsUnstaging = true;
            SelectedStaged = new(next != null ? new List<Models.Change> { next } : null);

            using (var lockWatcher = _group.LockWatcher())
            {
                foreach (var kv in grouped)
                {
                    var owner = kv.Key;
                    var realChanges = CollectRealChanges(kv.Value);
                    await owner.WorkingCopy.UnstageChangesAsync(realChanges, null);
                }
            }

            IsUnstaging = false;
        }

        public override async Task CommitAsync(bool autoStage, bool autoPush)
        {
            if (string.IsNullOrWhiteSpace(CommitMessage))
                return;

            if (!_group.CanCreatePopup())
            {
                _group.SendNotification("Repository has an unfinished job! Please wait!", true);
                return;
            }

            if (autoStage && HasUnsolvedConflicts)
            {
                _group.SendNotification("Repository has unsolved conflict(s). Auto-stage and commit is disabled!", true);
                return;
            }

            var stagedByOwner = GroupByOwner(Staged);
            if (autoStage)
            {
                foreach (var kv in GroupByOwner(Unstaged))
                {
                    if (!stagedByOwner.TryGetValue(kv.Key, out var list))
                    {
                        list = new List<Models.Change>();
                        stagedByOwner.Add(kv.Key, list);
                    }

                    list.AddRange(kv.Value);
                }
            }

            if (stagedByOwner.Count == 0)
            {
                _group.SendNotification(App.Text("RepositoryGroup.NothingToCommit"), true);
                return;
            }

            if (!await _group.ConfirmSameBranchAsync(
                    stagedByOwner.Keys.ToList(),
                    App.Text("RepositoryGroup.CommitBranchCheck")))
            {
                return;
            }

            IsCommitting = true;
            using (var lockWatcher = _group.LockWatcher())
            {
                if (autoStage)
                {
                    var unstagedByOwner = GroupByOwner(Unstaged);
                    foreach (var kv in unstagedByOwner)
                    {
                        if (kv.Value.Count > 0)
                            await kv.Key.WorkingCopy.StageChangesAsync(CollectRealChanges(kv.Value), null);
                    }
                }

                var allSucc = true;
                foreach (var kv in stagedByOwner)
                {
                    var owner = kv.Key;

                    if (owner.CurrentBranch is { IsDetachedHead: true })
                    {
                        _group.SendNotification(App.Text("RepositoryGroup.CommitSkippedDetachedHead", _group.GetChildName(owner)), true);
                        allSucc = false;
                        continue;
                    }

                    if (owner.WorkingCopy.Staged.Count == 0 && (!autoStage || owner.WorkingCopy.Unstaged.Count == 0))
                    {
                        _group.SendNotification(App.Text("RepositoryGroup.CommitSkippedNothingStaged", _group.GetChildName(owner)));
                        continue;
                    }

                    var log = owner.CreateLog("Commit");
                    var succ = await new Commands.Commit(
                            owner.FullPath,
                            CommitMessage,
                            EnableSignOff,
                            NoVerifyOnCommit,
                            false,
                            false)
                        .Use(log)
                        .RunAsync();
                    log.Complete();

                    if (!succ)
                        allSucc = false;

                    owner.MarkBranchesDirtyManually();
                }

                if (allSucc)
                {
                    CommitMessage = string.Empty;
                    if (autoPush)
                        await _group.PushAsync(true);
                }
            }

            IsCommitting = false;
        }

        public override void DiscardAllChanges()
        {
            var targets = new List<Repository>();
            foreach (var child in _group.Repositories)
            {
                if (child.LocalChangesCount > 0)
                    targets.Add(child);
            }

            if (targets.Count > 0 && _group.CanCreatePopup())
                _group.ShowPopup(new DiscardGroup(_group, null));
        }

        public override void Discard(List<Models.Change> changes, Models.Change next)
        {
            if (!_group.CanCreatePopup())
                return;

            var grouped = GroupByOwner(changes);
            if (grouped.Count == 1)
            {
                var owner = grouped.Keys.First();
                owner.WorkingCopy.Discard(CollectRealChanges(grouped[owner]), next is GroupChange gcn ? gcn.Source : null);
                return;
            }

            if (grouped.Count > 1)
                _group.ShowPopup(new DiscardGroup(_group, grouped));
        }

        public override async Task UseTheirsAsync(List<Models.Change> changes)
        {
            var grouped = GroupByOwner(changes);
            using (var lockWatcher = _group.LockWatcher())
            {
                foreach (var kv in grouped)
                    await kv.Key.WorkingCopy.UseTheirsAsync(CollectRealChanges(kv.Value));
            }
        }

        public override async Task UseMineAsync(List<Models.Change> changes)
        {
            var grouped = GroupByOwner(changes);
            using (var lockWatcher = _group.LockWatcher())
            {
                foreach (var kv in grouped)
                    await kv.Key.WorkingCopy.UseMineAsync(CollectRealChanges(kv.Value));
            }
        }

        public override async Task SaveChangesToPatchAsync(List<Models.Change> changes, bool isUnstaged, string saveTo)
        {
            var grouped = GroupByOwner(changes);
            var allSucc = true;

            if (grouped.Count == 1)
            {
                var owner = grouped.Keys.First();
                allSucc = await Commands.SaveChangesAsPatch.ProcessLocalChangesAsync(
                    owner.FullPath,
                    CollectRealChanges(grouped[owner]),
                    isUnstaged,
                    saveTo);
            }
            else
            {
                var extension = Path.GetExtension(saveTo);
                var prefix = saveTo.Substring(0, saveTo.Length - extension.Length);
                var index = 0;
                foreach (var kv in grouped)
                {
                    var target = grouped.Count > 1 ? $"{prefix}-{++index}-{_group.GetChildName(kv.Key)}{extension}" : saveTo;
                    var succ = await Commands.SaveChangesAsPatch.ProcessLocalChangesAsync(
                        kv.Key.FullPath,
                        CollectRealChanges(kv.Value),
                        isUnstaged,
                        target);
                    if (!succ)
                        allSucc = false;
                }
            }

            if (allSucc)
                _group.SendNotification(App.Text("SaveAsPatchSuccess"));
        }

        public override async Task<bool> UseExternalMergeToolAsync(Models.Change change)
        {
            if (change is GroupChange gc)
                return await new Commands.MergeTool(gc.Owner.FullPath, gc.RealPath).OpenAsync();

            return false;
        }

        public override void UseExternalDiffTool(Models.Change change, bool isUnstaged)
        {
            if (change is GroupChange gc)
                new Commands.DiffTool(gc.Owner.FullPath, new Models.DiffOption(gc.Source, isUnstaged)).Open();
        }

        protected override void SetDetail(Models.Change change, bool isUnstaged)
        {
            if (change == null)
            {
                DetailContext = null;
            }
            else if (change is GroupChange gc)
            {
                if (gc.Source.IsConflicted)
                    DetailContext = new Conflict(gc.Owner, this, gc.Source);
                else
                    DetailContext = new DiffContext(gc.Owner.FullPath, new Models.DiffOption(gc.Source, isUnstaged), DetailContext as DiffContext);
            }
        }

        protected override void UpdateInProgressState()
        {
            // Merge/rebase in progress is per child repository. Handle it in the single
            // repository view of that child instead of aggregating here.
        }

        private Dictionary<Repository, List<Models.Change>> GroupByOwner(List<Models.Change> changes)
        {
            var grouped = new Dictionary<Repository, List<Models.Change>>();
            if (changes == null)
                return grouped;

            foreach (var change in changes)
            {
                if (change is not GroupChange gc)
                    continue;

                if (!grouped.TryGetValue(gc.Owner, out var list))
                {
                    list = new List<Models.Change>();
                    grouped.Add(gc.Owner, list);
                }

                list.Add(change);
            }

            return grouped;
        }

        private List<Models.Change> CollectRealChanges(List<Models.Change> changes)
        {
            var real = new List<Models.Change>();
            var seen = new HashSet<Models.Change>();
            foreach (var change in changes)
            {
                if (change is GroupChange gc && seen.Add(gc.Source))
                    real.Add(gc.Source);
            }

            return real;
        }
    }
}
