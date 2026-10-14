$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$repository = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$results = Join-Path $repository 'dist/test-results/scheduler'
$filter = 'FullyQualifiedName=MeetTape.Tests.JobTests.SchedulerProcessesOtherMeetingsWhileFirstMeetingWaitsForRetry'
for ($attempt = 1; $attempt -le 3; $attempt++) {
    dotnet test (Join-Path $repository 'tests/MeetTape.Tests/MeetTape.Tests.csproj') --configuration Release --no-build --no-restore --filter $filter --blame-hang --blame-hang-timeout 20s --logger "trx;LogFileName=attempt-$attempt.trx" --results-directory $results
    if ($LASTEXITCODE -eq 0 -and $attempt -eq 1) { return }
}
throw 'Scheduler regression failed on its first run. Two diagnostic repeats were retained; inspect TRX before another full CI run.'
