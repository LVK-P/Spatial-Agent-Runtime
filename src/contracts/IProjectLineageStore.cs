namespace SpatialAgent.Runtime.Contracts
{
    /// <summary>
    /// Project-scoped derivation graph. Conversation reset must not erase it;
    /// deleting the project's .agent state remains the explicit forget boundary.
    /// </summary>
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
