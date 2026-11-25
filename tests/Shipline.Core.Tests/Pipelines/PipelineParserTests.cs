using Shipline.Core.Pipelines;

namespace Shipline.Core.Tests.Pipelines;

public sealed class PipelineParserTests
{
    [Fact]
    public void Parse_FullPipeline_ReadsEveryField()
    {
        string yaml = """
            name: cicd-lab
            env:
              DOTNET_NOLOGO: 1
            defaults:
              shell: pwsh
            jobs:
              build:
                name: Build and test
                runs-on: [windows, dotnet]
                timeout: 30m
                env:
                  CONFIGURATION: Release
                steps:
                  - name: Test
                    run: dotnet test
                    shell: cmd
                    working-directory: src
                    timeout: 1h30m
                    continue-on-error: true
                    env:
                      VERBOSE: "true"
                  - run: |
                      dotnet publish
                      echo done
              deploy:
                needs: build
                runs-on: windows
                steps:
                  - run: ./deploy.ps1
            """;

        PipelineDefinition pipeline = ParseValid(yaml);

        Assert.Equal("cicd-lab", pipeline.Name);
        Assert.Equal("1", pipeline.Env["DOTNET_NOLOGO"]);
        Assert.Equal(ShellKind.Pwsh, pipeline.DefaultShell);
        Assert.Equal(new[] { "build", "deploy" }, pipeline.Jobs.Select(job => job.Id));

        JobDefinition build = pipeline.Jobs[0];
        Assert.Equal("Build and test", build.Name);
        Assert.Equal(new[] { "windows", "dotnet" }, build.RunsOn);
        Assert.Equal(TimeSpan.FromMinutes(30), build.Timeout);
        Assert.Equal("Release", build.Env["CONFIGURATION"]);
        Assert.Equal(new SourceLocation(7, 3), build.Location);

        StepDefinition test = build.Steps[0];
        Assert.Equal("Test", test.Name);
        Assert.Equal("dotnet test", test.Run);
        Assert.Equal(ShellKind.Cmd, test.Shell);
        Assert.Equal("src", test.WorkingDirectory);
        Assert.Equal(TimeSpan.FromMinutes(90), test.Timeout);
        Assert.True(test.ContinueOnError);
        Assert.Equal("true", test.Env["VERBOSE"]);

        StepDefinition publish = build.Steps[1];
        Assert.Equal("dotnet publish", publish.Name);
        Assert.Equal("dotnet publish\necho done\n", publish.Run);
        Assert.Null(publish.Shell);
        Assert.False(publish.ContinueOnError);

        JobDefinition deploy = pipeline.Jobs[1];
        Assert.Equal("deploy", deploy.Name);
        Assert.Equal(new[] { "build" }, deploy.Needs);
        Assert.Equal(new[] { "windows" }, deploy.RunsOn);
        Assert.Equal(JobDefinition.DefaultTimeout, deploy.Timeout);
    }

    [Fact]
    public void Parse_NoName_UsesDefaultName()
    {
        PipelineDefinition pipeline = ParseValid("jobs:\n  a:\n    steps:\n      - run: echo hi\n");

        Assert.Equal("default-name", pipeline.Name);
    }

    [Fact]
    public void Parse_LongFirstLine_ShortensStepName()
    {
        string longCommand = "echo " + new string('x', 100);

        PipelineDefinition pipeline = ParseValid("jobs:\n  a:\n    steps:\n      - run: " + longCommand + "\n");

        string name = pipeline.Jobs[0].Steps[0].Name;
        Assert.Equal(60, name.Length);
        Assert.EndsWith("...", name, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("", "The file is empty")]
    [InlineData("- a\n- b\n", "The pipeline must be a mapping")]
    [InlineData("name: x\n", "no \"jobs\" section")]
    [InlineData("jobs: {}\n", "\"jobs\" is empty")]
    [InlineData("jobs:\n  a:\n    steps: []\n", "empty \"steps\" list")]
    [InlineData("jobs:\n  a:\n    steps: echo hi\n", "\"steps\" must be a list")]
    [InlineData("jobs:\n  a: {}\n", "has no \"steps\"")]
    [InlineData("jobs:\n  a:\n    steps:\n      - name: x\n", "needs a \"run\" script")]
    [InlineData("jobs:\n  a:\n    steps:\n      - run: \"  \"\n", "\"run\" is empty")]
    [InlineData("jobs:\n  2fast:\n    steps:\n      - run: x\n", "Invalid job id \"2fast\"")]
    [InlineData("jobs:\n  a:\n    timeout: soon\n    steps:\n      - run: x\n", "Invalid \"timeout\" \"soon\"")]
    [InlineData("jobs:\n  a:\n    steps:\n      - run: x\n        continue-on-error: maybe\n", "must be true or false")]
    [InlineData("jobs:\n  a:\n    steps:\n      - run: x\n        shell: fish\n", "Unknown shell \"fish\"")]
    [InlineData("jobs:\n  a:\n    env:\n      SHIPLINE_JOB: x\n    steps:\n      - run: x\n", "\"SHIPLINE_JOB\" is set by Shipline")]
    [InlineData("jobs:\n  a:\n    env:\n      ci: x\n    steps:\n      - run: x\n", "\"ci\" is set by Shipline")]
    [InlineData("jobs:\n  a:\n    env:\n      LIST: [1, 2]\n    steps:\n      - run: x\n", "must be a single value")]
    [InlineData("on: push\njobs:\n  a:\n    steps:\n      - run: x\n", "\"on\" is not supported yet")]
    [InlineData("jobs:\n  a:\n    steps:\n      - run: x\n---\njobs: {}\n", "one YAML document")]
    [InlineData("max-parallel: 0\njobs:\n  a:\n    steps:\n      - run: x\n", "whole number of 1 or more")]
    [InlineData("fail-fast: sometimes\njobs:\n  a:\n    steps:\n      - run: x\n", "must be true or false")]
    [InlineData("jobs:\n  a:\n    when: !failure()\n    steps:\n      - run: x\n", "Put quotes around")]
    [InlineData("jobs:\n  a:\n    artifacts: out\n    steps:\n      - run: x\n", "\"artifacts\" is not supported yet")]
    public void Parse_InvalidPipeline_ReportsError(string yaml, string expectedMessagePart)
    {
        PipelineParseResult result = PipelineParser.Parse(yaml, "default-name");

        Assert.Null(result.Pipeline);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Message.Contains(expectedMessagePart, StringComparison.Ordinal));
    }

    [Fact]
    public void Parse_MisspelledKey_SuggestsTheRightKeyAtItsPosition()
    {
        string yaml = "jobs:\n  build:\n    stpes:\n      - run: x\n";

        PipelineParseResult result = PipelineParser.Parse(yaml, "default-name");

        PipelineDiagnostic unknownKey = Assert.Single(result.Diagnostics, diagnostic => diagnostic.Message.StartsWith("Unknown key", StringComparison.Ordinal));
        Assert.Contains("Did you mean \"steps\"?", unknownKey.Message, StringComparison.Ordinal);
        Assert.Equal(new SourceLocation(3, 5), unknownKey.Location);
    }

    [Fact]
    public void Parse_UnknownNeed_SuggestsJobEvenWhenThatJobHasErrors()
    {
        string yaml = "jobs:\n  build:\n    timeout: never\n    steps:\n      - run: x\n  deploy:\n    needs: biuld\n    steps:\n      - run: x\n";

        PipelineParseResult result = PipelineParser.Parse(yaml, "default-name");

        PipelineDiagnostic unknownNeed = Assert.Single(result.Diagnostics, diagnostic => diagnostic.Message.StartsWith("Unknown job", StringComparison.Ordinal));
        Assert.Contains("Did you mean \"build\"?", unknownNeed.Message, StringComparison.Ordinal);
        Assert.Equal(new SourceLocation(7, 12), unknownNeed.Location);
    }

    [Fact]
    public void Parse_JobNeedsItself_ReportsError()
    {
        string yaml = "jobs:\n  a:\n    needs: a\n    steps:\n      - run: x\n";

        PipelineParseResult result = PipelineParser.Parse(yaml, "default-name");

        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Message.Contains("needs itself", StringComparison.Ordinal));
    }

    [Fact]
    public void Parse_NeedsCycle_ReportsThePath()
    {
        string yaml = """
            jobs:
              a:
                needs: c
                steps:
                  - run: x
              b:
                needs: a
                steps:
                  - run: x
              c:
                needs: b
                steps:
                  - run: x
            """;

        PipelineParseResult result = PipelineParser.Parse(yaml, "default-name");

        PipelineDiagnostic cycle = Assert.Single(result.Diagnostics);
        Assert.Contains("a -> c -> b -> a", cycle.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_SeveralErrors_ReportsAllSortedByPosition()
    {
        string yaml = "jobs:\n  a:\n    timeout: x\n    steps:\n      - run: y\n        shell: z\n  b:\n    stpes: []\n";

        PipelineParseResult result = PipelineParser.Parse(yaml, "default-name");

        // Line 7 is job "b", which has no "steps" because of the typo on line 8.
        Assert.Equal(new[] { 3, 6, 7, 8 }, result.Diagnostics.Select(diagnostic => diagnostic.Location.Line));
    }

    [Fact]
    public void Parse_YamlSyntaxError_ReportsPosition()
    {
        PipelineParseResult result = PipelineParser.Parse("jobs:\n  a:\n    steps: [unclosed\n", "default-name");

        PipelineDiagnostic error = Assert.Single(result.Diagnostics);
        Assert.True(error.Location.Line >= 3);
    }

    [Fact]
    public void Parse_DuplicateKey_ReportsError()
    {
        PipelineParseResult result = PipelineParser.Parse("jobs:\n  a:\n    steps:\n      - run: x\n  a:\n    steps:\n      - run: y\n", "default-name");

        Assert.Null(result.Pipeline);
        Assert.NotEmpty(result.Diagnostics);
    }

    [Fact]
    public void Parse_WhenAndRunOptions_AreRead()
    {
        string yaml = """
            fail-fast: true
            max-parallel: 2
            jobs:
              deploy:
                when: branch == 'main'
                steps:
                  - run: echo deploy
                    when: "!failure()"
            """;

        PipelineDefinition pipeline = ParseValid(yaml);

        Assert.True(pipeline.FailFast);
        Assert.Equal(2, pipeline.MaxParallel);
        JobDefinition deploy = pipeline.Jobs[0];
        Assert.NotNull(deploy.When);
        Assert.Equal("branch == 'main'", deploy.When.Text);
        Assert.NotNull(deploy.Steps[0].When);
        Assert.True(deploy.Steps[0].When!.UsesStatusFunction);
    }

    [Fact]
    public void Parse_NoRunOptions_UsesDefaults()
    {
        PipelineDefinition pipeline = ParseValid("jobs:\n  a:\n    steps:\n      - run: x\n");

        Assert.False(pipeline.FailFast);
        Assert.Null(pipeline.MaxParallel);
        Assert.Null(pipeline.Jobs[0].When);
    }

    [Theory]
    [InlineData("    when: branhc == 'x'", 11, "Did you mean \"branch\"?")]
    [InlineData("    when: \"branch = 'x'\"", 19, "Use ==")]
    [InlineData("    when: branch == 'x' && env.", 32, "A variable name must follow")]
    public void Parse_InvalidWhen_PointsAtTheCharacterInsideTheCondition(string whenLine, int expectedColumn, string expectedMessagePart)
    {
        string yaml = "jobs:\n  a:\n" + whenLine + "\n    steps:\n      - run: x\n";

        PipelineParseResult result = PipelineParser.Parse(yaml, "default-name");

        PipelineDiagnostic error = Assert.Single(result.Diagnostics);
        Assert.Equal(new SourceLocation(3, expectedColumn), error.Location);
        Assert.StartsWith("Invalid \"when\": ", error.Message, StringComparison.Ordinal);
        Assert.Contains(expectedMessagePart, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Format_WritesFileLineColumn()
    {
        PipelineDiagnostic diagnostic = new PipelineDiagnostic(new SourceLocation(4, 7), "Broken.");

        Assert.Equal(".shipline.yml:4:7: error: Broken.", diagnostic.Format(".shipline.yml"));
    }

    private static PipelineDefinition ParseValid(string yaml)
    {
        PipelineParseResult result = PipelineParser.Parse(yaml, "default-name");
        Assert.Empty(result.Diagnostics);
        Assert.NotNull(result.Pipeline);
        return result.Pipeline;
    }
}
