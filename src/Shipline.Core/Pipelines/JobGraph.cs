namespace Shipline.Core.Pipelines;

/// <summary>
/// The jobs and their "needs" form a directed graph. A job can run only after every job it needs has finished,
/// so the graph must have no cycles, and the run order is a topological order of the graph.
/// </summary>
public static class JobGraph
{
    /// <summary>
    /// Returns the jobs so that every job comes after the jobs it needs. Among jobs that are ready at the same time,
    /// the one written first in the file comes first, so the order is stable and easy to predict.
    /// </summary>
    public static IReadOnlyList<JobDefinition> OrderByNeeds(IReadOnlyList<JobDefinition> jobs)
    {
        List<JobDefinition> ordered = new List<JobDefinition>();
        HashSet<string> placed = new HashSet<string>(StringComparer.Ordinal);
        while (ordered.Count < jobs.Count)
        {
            JobDefinition? next = FindFirstReadyJob(jobs, placed);
            if (next is null)
            {
                throw new InvalidOperationException("The jobs form a cycle. Validate the pipeline before ordering it.");
            }

            ordered.Add(next);
            placed.Add(next.Id);
        }

        return ordered;
    }

    /// <summary>Returns the given jobs plus every job they need, directly or indirectly, in run order.</summary>
    public static IReadOnlyList<JobDefinition> SelectWithNeeds(PipelineDefinition pipeline, IEnumerable<string> jobIds)
    {
        ArgumentNullException.ThrowIfNull(pipeline);
        HashSet<string> selected = new HashSet<string>(StringComparer.Ordinal);
        Stack<string> toVisit = new Stack<string>(jobIds);
        while (toVisit.Count > 0)
        {
            string id = toVisit.Pop();
            if (!selected.Add(id))
            {
                continue;
            }

            JobDefinition? job = pipeline.FindJob(id);
            if (job is null)
            {
                throw new ArgumentException("Unknown job '" + id + "'.", nameof(jobIds));
            }

            foreach (string need in job.Needs)
            {
                toVisit.Push(need);
            }
        }

        List<JobDefinition> jobs = pipeline.Jobs.Where(job => selected.Contains(job.Id)).ToList();
        return OrderByNeeds(jobs);
    }

    /// <summary>
    /// Returns one cycle as a path such as [a, b, a], or null when there is none.
    /// Needs that point to unknown jobs are ignored here; the parser reports them separately.
    /// </summary>
    public static IReadOnlyList<string>? FindCycle(IReadOnlyList<JobDefinition> jobs)
    {
        Dictionary<string, JobDefinition> jobsById = new Dictionary<string, JobDefinition>(StringComparer.Ordinal);
        foreach (JobDefinition job in jobs)
        {
            jobsById[job.Id] = job;
        }

        HashSet<string> finished = new HashSet<string>(StringComparer.Ordinal);
        foreach (JobDefinition job in jobs)
        {
            List<string> path = new List<string>();
            List<string>? cycle = FindCycleFrom(job, jobsById, path, finished);
            if (cycle is not null)
            {
                return cycle;
            }
        }

        return null;
    }

    private static JobDefinition? FindFirstReadyJob(IReadOnlyList<JobDefinition> jobs, HashSet<string> placed)
    {
        foreach (JobDefinition job in jobs)
        {
            if (placed.Contains(job.Id))
            {
                continue;
            }

            bool allNeedsPlaced = job.Needs.All(placed.Contains);
            if (allNeedsPlaced)
            {
                return job;
            }
        }

        return null;
    }

    // Depth-first search. "path" holds the jobs on the current branch; reaching one of them again means a cycle.
    private static List<string>? FindCycleFrom(
        JobDefinition job,
        Dictionary<string, JobDefinition> jobsById,
        List<string> path,
        HashSet<string> finished)
    {
        if (finished.Contains(job.Id))
        {
            return null;
        }

        int indexOnPath = path.IndexOf(job.Id);
        if (indexOnPath >= 0)
        {
            List<string> cycle = path.GetRange(indexOnPath, path.Count - indexOnPath);
            cycle.Add(job.Id);
            return cycle;
        }

        path.Add(job.Id);
        foreach (string need in job.Needs)
        {
            if (!jobsById.TryGetValue(need, out JobDefinition? neededJob))
            {
                continue;
            }

            List<string>? cycle = FindCycleFrom(neededJob, jobsById, path, finished);
            if (cycle is not null)
            {
                return cycle;
            }
        }

        path.RemoveAt(path.Count - 1);
        finished.Add(job.Id);
        return null;
    }
}
