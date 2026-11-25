# Shipline

A simple CI/CD tool: one executable, YAML pipelines, and every pipeline runs on your own machine with one command.

Shipline is built from scratch to fix the parts of Jenkins that hurt most, and to show how a tool like Jenkins works
inside. It is built phase by phase; see [Roadmap](#roadmap).

```text
> shipline run --branch develop
Pipeline phase2-demo, 5 job(s), branch develop, run local-20261125-103436-391689e4
[build      ] == build
[build      ] building
[build      ] == build: succeeded in 799ms
[unit-tests ] == unit-tests
[lint       ] == lint
[unit-tests ] unit tests passed
[lint       ] lint passed
[unit-tests ] == unit-tests: succeeded in 2.3s
[lint       ] == lint: succeeded in 2.3s
[deploy-prod] == deploy-prod: skipped. The condition "branch == 'main'" is false.
[deploy-qa  ] == deploy-qa
[deploy-qa  ] deploying to QA
[deploy-qa  ] == deploy-qa: succeeded in 241ms

Summary
  build        succeeded   799ms
  unit-tests   succeeded   2.3s
  lint         succeeded   2.3s
  deploy-prod  skipped
  deploy-qa    succeeded   241ms

Run succeeded in 3.3s.
```

## Why

| Jenkins | Shipline |
|---|---|
| A JVM, plugins, Configuration as Code and init scripts before the first build | One executable, no configuration |
| About 1,800 plugins, with version conflicts and security advisories | No plugins. Git, webhooks, secrets, artifacts and approvals are built in. Anything else is a script |
| Groovy pipelines (CPS, sandbox, hard-to-read errors) | Declarative YAML close to GitHub Actions, with a strict schema |
| A typo in a key is ignored or fails late | `shipline lint` lists every error with its line, column and a "did you mean" hint |
| Pipelines run only on the server | `shipline run` runs the same pipeline on any machine |
| Leftover processes need the ProcessTreeKiller and its `BUILD_ID` tricks | Every step runs in a Windows Job Object: when a step ends, all its processes end |
| No default timeout | Every job has a timeout, 60 minutes unless set |

## Quick start

Requirements: the .NET 10 SDK.

```powershell
dotnet build Shipline.slnx
cd path\to\your\repo                  # a folder with a .shipline.yml
dotnet D:\WORK\shipline\src\Shipline.Cli\bin\Debug\net10.0\shipline.dll run
```

Shipline builds itself with its own pipeline:

```powershell
dotnet run --project src/Shipline.Cli -- run
```

One self-contained file (no .NET install needed on the target machine):

```powershell
dotnet publish src/Shipline.Cli -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -o publish
publish\shipline.exe --help
```

## Commands

| Command | What it does |
|---|---|
| `shipline run` | Runs `.shipline.yml` from the current folder. Exit code 0 = succeeded, 1 = failed, 2 = invalid input, 130 = cancelled |
| `shipline run -f ci/build.yml` | Runs another pipeline file. Steps run in the folder of that file |
| `shipline run --job deploy` | Runs one job and every job it needs. Repeatable |
| `shipline run -e CONFIGURATION=Debug` | Sets a variable; it wins over the file. Repeatable |
| `shipline run --branch main` | Sets the branch that `when` conditions see. Default: the Git branch of the pipeline folder |
| `shipline lint` | Checks the file without running anything |
| `shipline --version` | Prints the version |

Ctrl+C cancels the run and kills the processes of the running step.

## Pipeline file

```yaml
name: cicd-lab                 # optional; default is the folder name
env:                           # for every step
  DOTNET_NOLOGO: "1"
defaults:
  shell: powershell            # powershell | pwsh | cmd | bash | sh
fail-fast: false               # true: the first failed job stops the others
max-parallel: 4                # most jobs at the same time (default: one per processor)

jobs:
  build:                       # job id: letters, digits, - and _
    name: Build and test       # optional display name
    runs-on: [windows]         # agent labels; used from phase 6, ignored by "shipline run"
    timeout: 30m               # 45s, 30m, 1h30m ... (default 60m)
    env:
      CONFIGURATION: Release
    steps:
      - name: Test             # optional; default is the first line of "run"
        run: dotnet test -c $env:CONFIGURATION
      - name: Publish
        run: |
          dotnet publish src/Web -c Release -o out/web
          Write-Output "published"
        working-directory: .   # relative to the folder of the pipeline file
        timeout: 10m
        continue-on-error: false
        env:
          VERBOSE: "true"

  deploy:
    needs: build               # a job id or a list; runs only after "build" succeeds
    when: branch == 'main'     # optional condition, see below
    steps:
      - run: ./deploy/Deploy-Iis.ps1 -Site CicdLab_QA
      - name: Tell the team
        when: failure()        # runs only when an earlier step failed
        run: ./notify.ps1
```

Rules:

- **Order.** A job starts as soon as every job in its `needs` has finished, so independent jobs run in parallel (up to
  `max-parallel`). Steps in a job run one after another. A failed step skips the rest of its job and every job that
  needs it; other jobs still run, unless `fail-fast: true`.
- **Variables.** Narrower scopes win: pipeline `env` < job `env` < step `env` < `--env` on the command line. Shipline
  also sets `CI=true`, `SHIPLINE=true`, `SHIPLINE_RUN_ID`, `SHIPLINE_PIPELINE`, `SHIPLINE_JOB`, `SHIPLINE_STEP` and
  `SHIPLINE_WORKSPACE`; these names cannot be overridden.
- **Default shell.** `powershell` (Windows PowerShell 5.1) on Windows, `bash` elsewhere. On Windows, `bash` means Git
  Bash, never the WSL `bash.exe` in System32.
- **Failure.** A step fails when its exit code is not 0. In `powershell`/`pwsh`, errors stop the script, and the exit
  code of the last program counts; PowerShell 7.4+ also stops at the first failing program. `bash` runs with
  `-e -o pipefail`.
- **Input.** Standard input is closed, so a command that asks a question gets "end of input" instead of hanging.
- **Not yet supported** (reported by `lint`): `on`, `artifacts`, `environment`, `approval`, `image`.

### Conditions (`when`)

A job or a step can have a `when` condition. `lint` checks it, including its types, and points at the exact character
of an error.

| Part | Meaning |
|---|---|
| `branch`, `event` | The Git branch (or `--branch`), and what started the run (`manual` for `shipline run`) |
| `env.NAME` | An environment variable as the job or step would see it; empty text when it is not set |
| `'text'`, `"text"`, `true`, `false` | Values. Write a quote twice to include it: `'it''s'` |
| `==`, `!=`, `!`, `&&`, `\|\|`, `( )` | Compare and combine. `&&` binds tighter than `\|\|` |
| `startsWith(a, b)`, `endsWith(a, b)`, `contains(a, b)` | Text tests; case-sensitive, like Git branch names |
| `success()` | Job: every needed job succeeded. Step: no earlier step failed |
| `failure()` | Job: a needed job, directly or further up the chain, failed. Step: an earlier step failed |
| `always()` | Always true |

A condition without a status function is joined with an implicit `success()`, so `when: branch == 'main'` never runs
after a failure. Use `failure()` or `always()` for clean-up and notification jobs; `fail-fast` does not stop those.

In YAML a value that starts with `!` is a tag, so quote it: `when: "!failure()"`.

## How it works

```text
.shipline.yml --PipelineParser--> PipelineDefinition --LocalRunner--> JobScheduler (one coordinator loop)
                                                                       |  starts ready jobs in parallel,
                                                                       |  skips or cancels the others
                                                                       v
                                                       JobRunner (one per job, steps in order)
                                                                       |
                                     for each step: ShellScripts writes the script to a temp file
                                                    ProcessRunner starts the shell in a Job Object,
                                                    streams stdout/stderr to the IRunReporter,
                                                    and kills the whole tree at the end
```

- `src/Shipline.Core/Pipelines` reads YAML as a node tree that keeps line and column numbers, then maps it to the
  model by hand, so every error is reported at once with its position.
- `src/Shipline.Core/Pipelines/Conditions` is a small expression language: a lexer, a recursive descent parser that
  checks types, and a syntax tree that evaluates itself.
- `src/Shipline.Core/Execution/ExecutionState.cs` is the state machine (`Pending -> Running -> Succeeded | Failed |
  TimedOut | Cancelled`, or `Pending -> Skipped | Cancelled`). Only the scheduler loop changes job states, so no locks
  are needed.
- `src/Shipline.Core/Execution` runs processes. `WindowsJobObject` uses "kill on job close", so even if Shipline
  crashes, Windows kills the step's processes.
- `src/Shipline.Cli` is the `shipline` executable.

## Roadmap

| Phase | Content | Status |
|---|---|---|
| 0 | Repo skeleton, CI | done |
| 1 | `shipline run` and `shipline lint` | done |
| 2 | Engine: parallel jobs, state machine, `when`, fail-fast | done |
| 3 | `shipline server`: SQLite history, HTTP API, live logs (SSE), recovery after restart | |
| 4 | Web UI | |
| 5 | Git and GitHub webhooks, commit status | |
| 6 | Remote agents: HTTP long polling, leases, heartbeats | |
| 7 | Secrets, artifacts, environments with approval, login | |
| 8 | Docker executor | |
| 9 | Benchmarks against Jenkins, cleanup, metrics, Windows service, cron | |
| 10 | Move the cicd-lab Jenkins pipeline to Shipline | |

## Development

```powershell
dotnet build Shipline.slnx     # warnings are errors
dotnet test Shipline.slnx
```

The code style is in `.editorconfig` and is checked at build time.

## License

[MIT](LICENSE)
