namespace SpatialAgent.Runtime.Contracts
{
    /// <summary>
    /// Project-scoped derivation graph. Conversation reset must not erase it;
    /// deleting the project's .agent state remains the explicit forget boundary.
    /// </summary>
    /// <remarks>中文：项目范围推导图。对话重置不能抹去它;删除项目的.agent状态仍然是显式的遗忘边界。</remarks>
    public interface IProjectLineageStore
    {
        IReadOnlyList<DataLineageRecord> LoadLineage();
        void UpsertLineage(DataLineageRecord lineage);
        void UpsertLineage(IEnumerable<DataLineageRecord> lineage)
        {
            foreach (var item in lineage)
                UpsertLineage(item);
        }
    }
}
