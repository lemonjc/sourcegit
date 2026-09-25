namespace SourceGit.ViewModels
{
    /// <summary>
    ///     A display copy of a <see cref="Models.Change"/> collected from one child repository
    ///     of a <see cref="RepositoryGroup"/>. The <c>Path</c> is prefixed with the repository
    ///     name so that the merged change list looks like a single big repository (top-level
    ///     folders are repository names), while git operations are routed back to the owning
    ///     child repository through <see cref="Owner"/> and <see cref="RealPath"/>.
    /// </summary>
    public class GroupChange : Models.Change
    {
        public Repository Owner
        {
            get;
        }

        public Models.Change Source
        {
            get;
        }

        public string RealPath
        {
            get;
        }

        public string RealOriginalPath
        {
            get;
        }

        public GroupChange(Repository owner, string repoName, Models.Change source)
        {
            Owner = owner;
            Source = source;
            RealPath = source.Path;
            RealOriginalPath = source.OriginalPath;

            Index = source.Index;
            WorkTree = source.WorkTree;
            ConflictReason = source.ConflictReason;
            DataForAmend = source.DataForAmend;

            Path = string.IsNullOrEmpty(repoName) ? source.Path : $"{repoName}/{source.Path}";
            OriginalPath = string.IsNullOrEmpty(source.OriginalPath) ? string.Empty : $"{repoName}/{source.OriginalPath}";
        }
    }
}
