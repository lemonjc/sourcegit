using System;
using System.Collections.Generic;
using System.Threading.Tasks;

using CommunityToolkit.Mvvm.ComponentModel;

namespace SourceGit.ViewModels
{
    public class CheckoutGroupRepository : ObservableObject
    {
        public CheckoutGroupRepository(Repository repo, string name, string branchName)
        {
            Repo = repo;
            Name = name;

            Branch = repo.Branches.Find(x => x.IsLocal && x.Name.Equals(branchName, StringComparison.Ordinal));
            HasBranch = Branch != null;
        }

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

        public Models.Branch Branch
        {
            get;
        }

        public bool HasBranch
        {
            get;
        }

        public bool IsCurrent => Branch is { IsCurrent: true };

        public bool WillCheckout
        {
            get => _willCheckout;
            set => SetProperty(ref _willCheckout, value);
        }

        public bool CreateIfMissing
        {
            get => _createIfMissing;
            set => SetProperty(ref _createIfMissing, value);
        }

        private bool _willCheckout = true;
        private bool _createIfMissing = false;
    }

    public class CheckoutGroup : Popup
    {
        public string BranchName
        {
            get;
        }

        public List<CheckoutGroupRepository> Repositories
        {
            get;
        }

        public CheckoutGroup(RepositoryGroup group, string branchName)
        {
            _group = group;
            BranchName = branchName;

            Repositories = [];
            foreach (var repo in group.Repositories)
                Repositories.Add(new CheckoutGroupRepository(repo, group.GetChildName(repo), branchName));
        }

        public override async Task<bool> Sure()
        {
            using var lockWatcher = _group.LockWatcher();

            var log = _group.CreateLog($"Checkout Branch '{BranchName}'");
            Use(log);

            foreach (var item in Repositories)
            {
                if (item.IsCurrent)
                    continue;

                if (item.HasBranch)
                {
                    if (!item.WillCheckout)
                        continue;

                    var succ = await new Commands.Checkout(item.Repo.FullPath)
                        .Use(log)
                        .BranchAsync(BranchName, false);
                    if (succ)
                        item.Repo.RefreshAfterCheckoutBranch(item.Branch);
                }
                else if (item.CreateIfMissing)
                {
                    var basedOn = item.Repo.CurrentBranch?.Head ?? "HEAD";
                    var succ = await new Commands.Checkout(item.Repo.FullPath)
                        .Use(log)
                        .BranchAsync(BranchName, basedOn, false, false);
                    if (succ)
                    {
                        var created = new Models.Branch
                        {
                            Name = BranchName,
                            FullName = $"refs/heads/{BranchName}",
                            CommitterDate = item.Repo.CurrentBranch?.CommitterDate ?? 0,
                            Head = basedOn,
                            IsLocal = true,
                            IsCurrent = true,
                        };

                        item.Repo.RefreshAfterCreateBranch(created, true);
                    }
                }
            }

            log.Complete();
            return true;
        }

        private readonly RepositoryGroup _group = null;
    }
}
