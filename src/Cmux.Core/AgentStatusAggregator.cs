namespace Cmux.Core;

public static class AgentStatusAggregator
{
    public static AgentStatus Aggregate(IEnumerable<AgentStatus> statuses)
    {
        var values = statuses.ToHashSet();
        if (values.Contains(AgentStatus.Error)) return AgentStatus.Error;
        if (values.Contains(AgentStatus.Waiting)) return AgentStatus.Waiting;
        if (values.Contains(AgentStatus.Working)) return AgentStatus.Working;
        if (values.Contains(AgentStatus.Completed)) return AgentStatus.Completed;
        return AgentStatus.Idle;
    }
}
