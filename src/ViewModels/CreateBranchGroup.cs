using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Threading.Tasks;

using CommunityToolkit.Mvvm.ComponentModel;

namespace SourceGit.ViewModels
{
    public class CreateBranchGroupRepository : ObservableObject
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

        public List<string> LocalBranchNames
        {
            get;
            init;
        }

        public string SelectedBasedOn
        {
            get => _selectedBasedOn;
            set
            {
                if (SetProperty(ref _selectedBasedOn, value))
                    OnPropertyChanged(nameof(BasedOnSha));
            }
        }

        public string BasedOnSha
        {
            get
            {
                var branch = Repo.Branches.Find(x => x.IsLocal && x.Name.Equals(_selectedBasedOn, StringComparison.Ordinal));
                return branch?.Head ?? "HEAD";
            }
        }

        public bool AlreadyExists
        {
            get => !string.IsNullOrEmpty(_newBranchName) &&
                Repo.Branches.Find(x => x.IsLocal && x.Name.Equals(_newBranchName, StringComparison.Ordinal)) != null;
        }

        public string NewBranchName
        {
            get => _newBranchName;
            set
            {
                if (SetProperty(ref _newBranchName, value))
                    OnPropertyChanged(nameof(AlreadyExists));
            }
        }

        public bool IsEnabled
        {
            get => _isEnabled;
            set => SetProperty(ref _isEnabled, value);
        }

        private string _selectedBasedOn = string.Empty;
        private string _newBranchName = string.Empty;
        private bool _isEnabled = true;
    }

    public class CreateBranchGroup : Popup
    {
        [Required(ErrorMessage = "Branch name is required!")]
        [CustomValidation(typeof(CreateBranchGroup), nameof(ValidateBranchName))]
        public string Name
        {
            get => _name;
            set
            {
                if (SetProperty(ref _name, value, true))
                {
                    foreach (var item in Repositories)
                        item.NewBranchName = value;
                }
            }
        }

        public List<CreateBranchGroupRepository> Repositories
        {
            get;
        }

        public bool CheckoutAfterCreated
        {
            get;
            set;
        }

        public bool AllowOverwrite
        {
            get => _allowOverwrite;
            set
            {
                if (SetProperty(ref _allowOverwrite, value))
                    ValidateProperty(_name, nameof(Name));
            }
        }

        public CreateBranchGroup(RepositoryGroup group)
        {
            _group = group;

            Repositories = [];
            foreach (var repo in group.Repositories)
            {
                if (repo.IsBare)
                    continue;

                var names = new List<string>();
                foreach (var branch in repo.Branches)
                {
                    if (branch.IsLocal && !branch.IsDetachedHead)
                        names.Add(branch.Name);
                }

                var current = repo.CurrentBranch?.IsDetachedHead == false ? repo.CurrentBranch.Name : names.Count > 0 ? names[0] : string.Empty;

                Repositories.Add(new CreateBranchGroupRepository
                {
                    Repo = repo,
                    Name = group.GetChildName(repo),
                    LocalBranchNames = names,
                    SelectedBasedOn = current,
                });
            }
        }

        public static ValidationResult ValidateBranchName(string name, ValidationContext ctx)
        {
            if (ctx.ObjectInstance is CreateBranchGroup creator)
            {
                if (!Models.RefName.IsValidBranchName(name))
                    return new ValidationResult("Bad branch name format!");

                if (!creator._allowOverwrite)
                {
                    foreach (var item in creator.Repositories)
                    {
                        if (!item.IsEnabled)
                            continue;

                        if (item.Repo.Branches.Find(x => x.IsLocal && x.FriendlyName.Equals(name, StringComparison.Ordinal)) != null)
                            return new ValidationResult($"Branch '{name}' already exists in '{item.Name}'!");
                    }
                }

                return ValidationResult.Success;
            }

            return new ValidationResult("Missing runtime context to create branch!");
        }

        public override async Task<bool> Sure()
        {
            var targets = new List<CreateBranchGroupRepository>();
            foreach (var item in Repositories)
            {
                if (item.IsEnabled && !string.IsNullOrEmpty(item.SelectedBasedOn))
                    targets.Add(item);
            }

            if (targets.Count == 0)
                return true;

            using var lockWatcher = _group.LockWatcher();

            var log = _group.CreateLog($"Create Branch '{_name}' in {targets.Count} repositories");
            Use(log);

            var allSucc = true;
            foreach (var item in targets)
            {
                var basedOnSha = item.BasedOnSha;
                var succ = false;

                if (CheckoutAfterCreated && !item.Repo.IsBare)
                {
                    succ = await new Commands.Checkout(item.Repo.FullPath)
                        .Use(log)
                        .BranchAsync(_name, basedOnSha, false, _allowOverwrite);
                }
                else
                {
                    succ = await new Commands.Branch(item.Repo.FullPath, _name)
                        .Use(log)
                        .CreateAsync(basedOnSha, _allowOverwrite);
                }

                if (!succ)
                {
                    allSucc = false;
                    continue;
                }

                var basedOn = item.Repo.Branches.Find(x => x.IsLocal && x.Name.Equals(item.SelectedBasedOn, StringComparison.Ordinal));
                var created = new Models.Branch
                {
                    Name = _name,
                    FullName = $"refs/heads/{_name}",
                    CommitterDate = basedOn?.CommitterDate ?? 0,
                    Head = basedOnSha,
                    IsLocal = true,
                    IsCurrent = CheckoutAfterCreated,
                };

                if (CheckoutAfterCreated)
                    item.Repo.RefreshAfterCheckoutBranch(created);
                else
                    item.Repo.RefreshAfterCreateBranch(created, false);
            }

            log.Complete();
            return allSucc;
        }

        private readonly RepositoryGroup _group = null;
        private string _name = null;
        private bool _allowOverwrite = false;
    }
}
