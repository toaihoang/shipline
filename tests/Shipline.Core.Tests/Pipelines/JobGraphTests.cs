using Shipline.Core.Pipelines;

namespace Shipline.Core.Tests.Pipelines;

public sealed class JobGraphTests
{
    [Fact]
    public void OrderByNeeds_PutsNeededJobsFirst_AndKeepsFileOrderOtherwise()
    {
        PipelineDefinition pipeline = Parse("""
            jobs:
              deploy:
                needs: [test, package]
                steps:
                  - run: x
              lint:
                steps:
                  - run: x
              test:
                needs: build
                steps:
                  - run: x
              package:
                needs: build
                steps:
                  - run: x
              build:
                steps:
                  - run: x
            """);

        IReadOnlyList<JobDefinition> ordered = JobGraph.OrderByNeeds(pipeline.Jobs);

        Assert.Equal(new[] { "lint", "build", "test", "package", "deploy" }, ordered.Select(job => job.Id));
    }

    [Fact]
    public void SelectWithNeeds_AddsEveryNeededJob()
    {
        PipelineDefinition pipeline = Parse("""
            jobs:
              build:
                steps:
                  - run: x
              lint:
                steps:
                  - run: x
              test:
                needs: build
                steps:
                  - run: x
              deploy:
                needs: test
                steps:
                  - run: x
            """);

        IReadOnlyList<JobDefinition> selected = JobGraph.SelectWithNeeds(pipeline, new[] { "deploy" });

        Assert.Equal(new[] { "build", "test", "deploy" }, selected.Select(job => job.Id));
    }

    [Fact]
    public void SelectWithNeeds_UnknownJob_Throws()
    {
        PipelineDefinition pipeline = Parse("jobs:\n  build:\n    steps:\n      - run: x\n");

        Assert.Throws<ArgumentException>(() => JobGraph.SelectWithNeeds(pipeline, new[] { "nope" }));
    }

    private static PipelineDefinition Parse(string yaml)
    {
        PipelineParseResult result = PipelineParser.Parse(yaml, "test");
        Assert.Empty(result.Diagnostics);
        Assert.NotNull(result.Pipeline);
        return result.Pipeline;
    }
}
