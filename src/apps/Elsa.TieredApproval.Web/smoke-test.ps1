#requires -Version 7
<#
.SYNOPSIS
    End-to-end smoke test: builds the app, starts it with a fresh App_Data, checks the API Elsa Studio uses, drives six
    requests through the real HTTP endpoints using the .eml files from the pickup directory (restarting the app half way
    through one of them), briefly runs the Studio host, and always stops everything. Exits non-zero on any failure.
.EXAMPLE
    pwsh -File smoke-test.ps1                      # app on http://localhost:5180, Studio on 5190
    pwsh -File smoke-test.ps1 -Port 5280           # if 5180 is busy (Studio then uses 5290)
    pwsh -File smoke-test.ps1 -WithStudio:$false   # skip building and starting the Studio host
#>
param([int]$Port = 5180, [switch]$WithStudio = $true)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$appDir = $PSScriptRoot
$project = Join-Path $appDir 'Elsa.TieredApproval.Web.csproj'
$dll = Join-Path $appDir 'bin/Debug/net10.0/Elsa.TieredApproval.Web.dll'
$appData = Join-Path $appDir 'App_Data'
$mailDir = Join-Path $appData 'mail'
$base = "http://localhost:$Port"
$auth = @{ Authorization = 'ApiKey 00000000-0000-0000-0000-000000000000' }
$logOut = Join-Path ([IO.Path]::GetTempPath()) "tiered-approval-smoke-$Port.out.log"
$logErr = Join-Path ([IO.Path]::GetTempPath()) "tiered-approval-smoke-$Port.err.log"
$studioDir = Join-Path $appDir '../Elsa.TieredApproval.Studio'
$studioBase = "http://localhost:$($Port + 10)"
$seen = [System.Collections.Generic.HashSet[string]]::new()

$people = @{
    10 = @{ name = 'Sam Staff'; email = 'staff@example.com' }
    20 = @{ name = 'Sue Section'; email = 'section.head@example.com' }
    30 = @{ name = 'Dan Dept'; email = 'dept.head@example.com' }
    40 = @{ name = 'Dee Division'; email = 'division.head@example.com' }
}

function Assert([bool]$Condition, [string]$Message) {
    if (-not $Condition) { throw "ASSERT FAILED: $Message" }
    Write-Host "  ok  $Message"
}

function Read-Mail([IO.FileInfo]$File) {
    $raw = Get-Content $File.FullName -Raw
    $headers, $body = $raw -split '\r?\n\r?\n', 2
    $headers = $headers -replace '\r?\n[ \t]+', ' ' # unfold folded header lines
    $header = { param($name) [regex]::Match($headers, "(?mi)^${name}: (.*)$").Groups[1].Value.Trim() }
    $links = foreach ($m in [regex]::Matches($body, 'href="([^"]*/approvals/confirm\?[^"]*)"[^>]*>([^<]+)</a>')) {
        $url = [Net.WebUtility]::HtmlDecode($m.Groups[1].Value)
        $token = [uri]::UnescapeDataString(([regex]::Match($url, '[?&]t=([^&]+)')).Groups[1].Value)
        [pscustomobject]@{ Label = [Net.WebUtility]::HtmlDecode($m.Groups[2].Value); Url = $url; Token = $token }
    }
    [pscustomobject]@{ Name = $File.Name; To = (& $header 'To'); Cc = (& $header 'CC'); Subject = (& $header 'Subject'); Body = $body; Links = @($links) }
}

# Emails for this instance that the test has not consumed yet.
function Get-NewMail([string]$InstanceId) {
    if (-not (Test-Path $mailDir)) { return @() }
    @(Get-ChildItem $mailDir -Filter *.eml | Where-Object { -not $seen.Contains($_.Name) } | Sort-Object LastWriteTimeUtc, Name |
        ForEach-Object { Read-Mail $_ } | Where-Object { $_.Body.Contains("Reference: $InstanceId") })
}

function Wait-Mail([string]$To, [string]$InstanceId, [int]$TimeoutSeconds = 30) {
    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    while ((Get-Date) -lt $deadline) {
        $mail = Get-NewMail $InstanceId | Where-Object { $_.To -eq $To } | Select-Object -First 1
        if ($mail) { [void]$seen.Add($mail.Name); return $mail }
        Start-Sleep -Milliseconds 250
    }
    throw "No email to $To for instance $InstanceId within $TimeoutSeconds s."
}

function Invoke-Http([string]$Method, [string]$Uri, $Body = $null, [hashtable]$Headers = @{}) {
    $params = @{ Method = $Method; Uri = $Uri; Headers = $Headers; SkipHttpErrorCheck = $true; TimeoutSec = 60 }
    if ($null -ne $Body) { $params.Body = if ($Body -is [string]) { $Body } else { $Body | ConvertTo-Json -Depth 10 }; $params.ContentType = 'application/json' }
    Invoke-WebRequest @params
}

function Resume([string]$Token, $Comment) {
    Invoke-Http Post "$base/elsa/api/bookmarks/resume?t=$([uri]::EscapeDataString($Token))" @{ input = @{ comment = $Comment } }
}

function Start-Request([string]$Title) {
    $body = @{
        title = $Title
        description = "Smoke test request <i>$Title</i> & co."
        requester = $people[10]
        approvers = @{ '20' = $people[20]; '30' = $people[30]; '40' = $people[40] }
    }
    $response = Invoke-Http Post "$base/workflows/tiered-approval/requests" $body $auth
    Assert ($response.StatusCode -eq 202) "start '$Title' returns 202 (got $($response.StatusCode))"
    $id = ($response.Content | ConvertFrom-Json).workflowInstanceId
    Assert (-not [string]::IsNullOrWhiteSpace($id)) "start returns workflowInstanceId ($id)"
    $submitted = Wait-Mail $people[10].email $id
    Assert ($submitted.Subject -like 'Submitted:*') "requester gets the 'submitted' confirmation"
    $id
}

# Takes one decision at $Level through the emailed link. $Action is Approve, Reject or Revision (with $Target).
function Invoke-Step([string]$InstanceId, [int]$Level, [string]$Action, [int]$Target = 0, $Comment = "L$Level $Action <b>ok</b>", [switch]$CheckConfirmPageIsSafe) {
    Write-Host "-- level $Level : $Action$(if ($Target) { " -> $Target" })"
    $mail = Wait-Mail $people[$Level].email $InstanceId
    Assert ($mail.Subject -like 'Action needed*') "level $Level assignee got an action email"
    Assert (-not $mail.Body.Contains('<script>')) 'user-supplied text is HTML-encoded in the email'

    $expected = @(@(10, 20, 30) | Where-Object { $_ -lt $Level })
    $offered = @($mail.Links | ForEach-Object { if ($_.Label -match '^Revision to .+ \((\d+)\)$') { [int]$Matches[1] } })
    Assert (($offered -join ',') -eq ($expected -join ',')) "level $Level offers revision targets [$($expected -join ',')] (got [$($offered -join ',')])"
    $approveLabel, $rejectLabel = if ($Level -eq 10) { 'Resubmit', 'Withdraw' } else { 'Approve', 'Reject' }
    Assert ($mail.Links.Label -contains $approveLabel -and $mail.Links.Label -contains $rejectLabel) "level $Level offers '$approveLabel' and '$rejectLabel'"
    Assert ($mail.Links.Count -eq 2 + $expected.Count) "level $Level email has one link per action"

    $label = switch ($Action) { 'Approve' { $approveLabel } 'Reject' { $rejectLabel } 'Revision' { "Revision to $(@{10='Staff';20='Section Head';30='Dept Head'}[$Target]) ($Target)" } }
    $link = $mail.Links | Where-Object Label -eq $label | Select-Object -First 1
    Assert ($null -ne $link) "email has a '$label' link"

    # The confirm page renders only once the bookmark is persisted, so polling it also orders us after the commit.
    $deadline = (Get-Date).AddSeconds(15)
    do { $page = Invoke-Http Get $link.Url; if ($page.StatusCode -eq 200) { break }; Start-Sleep -Milliseconds 250 } while ((Get-Date) -lt $deadline)
    Assert ($page.StatusCode -eq 200 -and $page.Content.Contains("Confirm: $([Net.WebUtility]::HtmlEncode($label))")) "confirm page for '$label' renders (200)"
    Assert ($page.Content.Contains('decision on the request <b>') -and -not $page.Content.Contains('<script>alert')) 'confirm page names the request (HTML-encoded)'

    if ($CheckConfirmPageIsSafe) {
        $embedded = [regex]::Match($page.Content, 'fetch\("([^"]+)"').Groups[1].Value
        Assert ($embedded.StartsWith("$base/elsa/api/bookmarks/resume?t=")) 'confirm page posts to the resume URL built from Http:BaseUrl'
        Assert ((Invoke-Http Get $embedded).StatusCode -eq 405) 'GET on the resume URL embedded in the page is refused (405)'
        $again = Invoke-Http Get $link.Url
        Assert ($again.StatusCode -eq 200) 'confirm page GET is repeatable (the GET did not consume the link)'
        Assert (@(Get-NewMail $InstanceId).Count -eq 0) 'confirm page GET did not advance the workflow'
    }

    $response = Resume $link.Token $Comment
    Assert ($response.StatusCode -eq 200) "resume POST for '$label' returns 200"
    [pscustomobject]@{ Mail = $mail; Link = $link }
}

# Emails go out before the run commits, so poll the instance until it reaches the expected "status/subStatus".
function Wait-InstanceState([string]$InstanceId, [string]$Expected) {
    $deadline = (Get-Date).AddSeconds(15)
    do {
        $instance = (Invoke-Http Get "$base/elsa/api/workflow-instances/$InstanceId" -Headers $auth).Content | ConvertFrom-Json
        $state = "$($instance.status)/$($instance.subStatus)"
        if ($state -eq $Expected) { break }
        Start-Sleep -Milliseconds 250
    } while ((Get-Date) -lt $deadline)
    $state
}

function Wait-Final([string]$InstanceId, [string]$Status, [int[]]$CcLevels, [int]$Decisions) {
    $mail = Wait-Mail $people[10].email $InstanceId
    Assert ($mail.Subject -like "${Status}:*") "requester gets the final '$Status' email"
    foreach ($level in $CcLevels) { Assert ($mail.Cc -match [regex]::Escape($people[$level].email)) "final email CCs level $level approver" }
    $rows = [regex]::Matches($mail.Body, '<tr><td>').Count
    Assert ($rows -eq $Decisions) "history table has $Decisions rows (got $rows)"
    Assert ($mail.Body.Contains('&lt;b&gt;ok&lt;/b&gt;') -and -not $mail.Body.Contains('<b>ok</b>')) 'comments are HTML-encoded in the history'
    $state = Wait-InstanceState $InstanceId 'Finished/Finished'
    Assert ($state -eq 'Finished/Finished') "workflow instance finished cleanly ($state)"
    Assert (@(Get-NewMail $InstanceId).Count -eq 0) 'no further emails after the final one'
}

# Starts the app on the current App_Data and waits until it serves requests.
function Start-App {
    $script:process = Start-Process dotnet -ArgumentList "`"$dll`"" -WorkingDirectory $appDir -PassThru -NoNewWindow -RedirectStandardOutput $logOut -RedirectStandardError $logErr
    $deadline = (Get-Date).AddSeconds(120)
    while ($true) {
        if ($process.HasExited) { throw "App exited during startup (code $($process.ExitCode))." }
        try { if ((Invoke-WebRequest "$base/approvals/confirm" -SkipHttpErrorCheck -TimeoutSec 5).StatusCode -eq 410) { break } } catch { }
        if ((Get-Date) -gt $deadline) { throw 'App did not respond within 120 s.' }
        Start-Sleep -Milliseconds 500
    }
    Write-Host "  ok  app is up (process $($process.Id))"
}

function Stop-Proc($Process) {
    if ($Process -and -not $Process.HasExited) { Stop-Process -Id $Process.Id -Force; $Process.WaitForExit(10000) | Out-Null }
}

$failed = $false
$process = $null
$studio = $null
$savedEnv = @{}

try {
    foreach ($p in @($Port) + @(if ($WithStudio) { $Port + 10 })) {
        if (Get-NetTCPConnection -LocalPort $p -State Listen -ErrorAction SilentlyContinue) { throw "Port $p is busy; rerun with -Port <free port>." }
    }

    Write-Host "== build"
    foreach ($proj in @($project) + @(if ($WithStudio) { Join-Path $studioDir 'Elsa.TieredApproval.Studio.csproj' })) {
        dotnet build $proj --nologo -v q --disable-build-servers # leaves no MSBuild/compiler server processes behind
        if ($LASTEXITCODE -ne 0) { throw "dotnet build $proj failed." }
    }

    Write-Host "== start app on $base (fresh App_Data)"
    if (Test-Path $appData) { Remove-Item $appData -Recurse -Force }
    foreach ($pair in @{ ASPNETCORE_ENVIRONMENT = 'Development'; ASPNETCORE_URLS = $base; Http__BaseUrl = $base }.GetEnumerator()) {
        $savedEnv[$pair.Key] = [Environment]::GetEnvironmentVariable($pair.Key)
        [Environment]::SetEnvironmentVariable($pair.Key, $pair.Value)
    }
    Start-App

    Write-Host '== API used by Elsa Studio'
    # The designer draws a Flowchart root from its nodes (label = name, position = metadata.designer.position) and connections.
    $response = Invoke-Http Get "$base/elsa/api/workflow-definitions/by-definition-id/TieredApprovalWorkflow?versionOptions=Latest" -Headers $auth
    Assert ($response.StatusCode -eq 200) "definition by id returns 200 ($($response.StatusCode))"
    $definition = $response.Content | ConvertFrom-Json
    Assert ($definition.root.type -eq 'Elsa.Flowchart') "the root is a flowchart ($($definition.root.type))"
    $nodes = 'SubmitRequest', 'ValidRequest', 'Respond400', 'EmailSubmitted', 'Respond202', 'Staff', 'SectionHead', 'DeptHead', 'DivisionHead', 'SendBackTo', 'EmailApproved', 'EmailRejected'
    $activities = @($definition.root.activities)
    Assert ((($activities.id | Sort-Object) -join ',') -eq (($nodes | Sort-Object) -join ',')) "the flowchart has the 12 expected nodes (got $($activities.id -join ','))"
    $placed = @($activities | Where-Object { try { $_.name -and $_.metadata.designer.position.x -is [ValueType] -and $_.metadata.designer.position.y -is [ValueType] } catch { $false } })
    Assert ($placed.Count -eq $nodes.Count) "every node has a label and a designer position ($($placed.Count)/$($nodes.Count))"
    # Studio sizes a node to its label (~280px for 'Email requester: submitted') and draws out-port labels past its right edge.
    $crowded = @($activities | Group-Object { $_.metadata.designer.position.y } | ForEach-Object {
            $xs = @($_.Group.metadata.designer.position.x | Sort-Object)
            for ($i = 1; $i -lt $xs.Count; $i++) { if ($xs[$i] - $xs[$i - 1] -lt 340) { "x $($xs[$i - 1])/$($xs[$i]) at y $($_.Name)" } }
        })
    Assert ($crowded.Count -eq 0) "no two nodes share a position and nodes in a row are at least 340px apart ($($crowded -join '; '))"
    $edges = @($definition.root.connections | ForEach-Object { "$($_.source.activity):$($_.source.port)>$($_.target.activity)" })
    $expectedEdges = @(
        'SubmitRequest:Done>ValidRequest', 'SubmitRequest:Request too large>ValidRequest', 'ValidRequest:False>Respond400', 'ValidRequest:True>EmailSubmitted',
        'EmailSubmitted:Done>Respond202', 'Respond202:Done>SectionHead', 'SectionHead:Approve>DeptHead', 'DeptHead:Approve>DivisionHead',
        'DivisionHead:Approve>EmailApproved', 'Staff:Approve>SectionHead', 'Staff:Reject>EmailRejected', 'SectionHead:Reject>EmailRejected',
        'DeptHead:Reject>EmailRejected', 'DivisionHead:Reject>EmailRejected', 'SectionHead:Revision>SendBackTo', 'DeptHead:Revision>SendBackTo',
        'DivisionHead:Revision>SendBackTo', 'SendBackTo:10>Staff', 'SendBackTo:20>SectionHead', 'SendBackTo:30>DeptHead')
    Assert ((($edges | Sort-Object) -join '|') -eq (($expectedEdges | Sort-Object) -join '|')) "the flowchart has the 20 expected connections (got $($edges -join ', '))"
    $steps = @($activities | Where-Object type -eq 'TieredApproval.ApprovalStep')
    $unbound = @($steps | Where-Object { $_.history.expression.value.name -ne 'History' -or $_.updatedHistory.memoryReference.id -ne $_.history.expression.value.id })
    Assert ($steps.Count -eq 4 -and $unbound.Count -eq 0) 'each approval step reads the History variable and writes it back (serializable bindings, no Inline)'
    Assert ($definition.isReadonly -and $definition.links.rel -notcontains 'publish') 'the code-first definition is read-only in Studio (no publish link)'
    $descriptors = Invoke-Http Get "$base/elsa/api/descriptors/activities" -Headers $auth
    Assert ($descriptors.StatusCode -eq 200 -and (($descriptors.Content | ConvertFrom-Json).items.typeName -contains 'TieredApproval.ApprovalStep')) 'activity descriptors include ApprovalStep'
    $features = Invoke-Http Get "$base/elsa/api/features/installed" -Headers $auth
    $installed = @(($features.Content | ConvertFrom-Json).items.fullName)
    Assert ($features.StatusCode -eq 200 -and $installed -contains 'Elsa.RealTimeWorkflowUpdates' -and $installed -contains 'Elsa.DashboardApi') 'installed features (200) include real-time updates and the dashboard API'
    $dashboard = Invoke-Http Get "$base/elsa/api/dashboard/overview" -Headers $auth
    Assert ($dashboard.StatusCode -eq 200) "dashboard overview returns 200 ($($dashboard.StatusCode))"
    # Studio's sign-in: admin/password against Backend:Url, then a bearer token on every API call and on the real-time hub.
    $login = Invoke-Http Post "$base/elsa/api/identity/login" @{ username = 'admin'; password = 'password' }
    $token = try { ($login.Content | ConvertFrom-Json).accessToken } catch { $null }
    Assert ($login.StatusCode -eq 200 -and $token) "login admin/password returns an access token ($($login.StatusCode))"
    $bearer = @{ Authorization = "Bearer $token" }
    $list = Invoke-Http Get "$base/elsa/api/workflow-definitions" -Headers $bearer
    Assert ($list.StatusCode -eq 200 -and (($list.Content | ConvertFrom-Json).items.definitionId -contains 'TieredApprovalWorkflow')) 'with the token, the definitions list shows the workflow'
    Assert ((Invoke-Http Post "$base/elsa/hubs/workflow-instance/negotiate?negotiateVersion=1" -Headers $bearer).StatusCode -eq 200) 'with the token, the real-time hub accepts the connection'
    Assert ((Invoke-Http Post "$base/elsa/hubs/workflow-instance/negotiate?negotiateVersion=1").StatusCode -eq 401) 'the real-time hub refuses anonymous callers (401)'

    if ($WithStudio) {
        Write-Host "== Studio host on $studioBase (backend $base/elsa/api)"
        # Command-line settings win over the environment, which still carries the app's ASPNETCORE_URLS.
        $studio = Start-Process dotnet -ArgumentList "`"$(Join-Path $studioDir 'bin/Debug/net10.0/Elsa.TieredApproval.Studio.dll')`"", "--urls=$studioBase", "--Backend:Url=$base/elsa/api" -WorkingDirectory $studioDir -PassThru -NoNewWindow -RedirectStandardOutput "$logOut.studio" -RedirectStandardError "$logErr.studio"
        $deadline = (Get-Date).AddSeconds(60)
        do { $studioHome = try { Invoke-WebRequest $studioBase -SkipHttpErrorCheck -TimeoutSec 10 } catch { $null }; if ($studioHome -or $studio.HasExited) { break }; Start-Sleep -Milliseconds 250 } while ((Get-Date) -lt $deadline)
        Assert ($studioHome -and $studioHome.StatusCode -eq 200 -and $studioHome.Content.Contains('<title>Elsa Studio</title>')) "Studio home page renders (200)"
        $signIn = Invoke-Http Get "$studioBase/login"
        Assert ($signIn.StatusCode -eq 200 -and $signIn.Content -match 'username' -and $signIn.Content -match 'password') 'Studio sign-in page renders with username and password fields (200)'
        Assert ((Invoke-Http Post "$studioBase/_blazor/negotiate?negotiateVersion=1").StatusCode -eq 200) 'Studio accepts the Blazor circuit the sign-in form runs over'
        Stop-Proc $studio
        Write-Host '  Studio stopped'
    }

    Write-Host '== start endpoint guards'
    $valid = @{ title = 'x'; requester = $people[10]; approvers = @{ '20' = $people[20]; '30' = $people[30]; '40' = $people[40] } }
    $anonymous = Invoke-Http Post "$base/workflows/tiered-approval/requests" $valid
    Assert ($anonymous.StatusCode -in 401, 403) "unauthenticated start is rejected ($($anonymous.StatusCode))"
    $invalid = Invoke-Http Post "$base/workflows/tiered-approval/requests" @{ title = ' '; requester = @{ name = 'X'; email = 'not-an-email' }; approvers = @{ '20' = $people[20]; '40' = @{ email = 'Eve <eve@example.com>' }; '50' = $people[40] } } $auth
    Assert ($invalid.StatusCode -eq 400) "invalid payload returns 400 ($($invalid.StatusCode))"
    $errors = @(($invalid.Content | ConvertFrom-Json).errors)
    Assert ($errors.Count -eq 5) "400 lists title, requester, approver 30, approver 40 and unknown level 50 errors (got: $($errors -join ' | '))"
    foreach ($bad in '{"title": 5}', '{"title": "x", ', "{`"title`": `"$('x' * 70000)`"}") {
        $response = Invoke-Http Post "$base/workflows/tiered-approval/requests" $bad $auth
        $shape = try { @(($response.Content | ConvertFrom-Json).errors).Count -gt 0 } catch { $false }
        Assert ($response.StatusCode -eq 400 -and $shape) "malformed or oversized body ($($bad.Length) chars) returns the app's 400 error list ($($response.StatusCode))"
    }
    $deadline = (Get-Date).AddSeconds(15)
    do {
        $instances = @((Invoke-Http Get "$base/elsa/api/workflow-instances" -Headers $auth).Content | ConvertFrom-Json | ForEach-Object items)
        $states = @($instances | ForEach-Object { "$($_.status)/$($_.subStatus)" })
        if ($states.Count -eq 4 -and -not ($states -ne 'Finished/Finished')) { break }
        Start-Sleep -Milliseconds 250
    } while ((Get-Date) -lt $deadline)
    Assert ($states.Count -eq 4 -and -not ($states -ne 'Finished/Finished')) "the 4 rejected starts all finished, none left suspended ($($states -join ','))"
    Assert ($null -eq (Get-ChildItem $mailDir -Filter *.eml -ErrorAction SilentlyContinue)) 'rejected starts send no email'

    Write-Host '== request A: full path with revisions'
    $a = Start-Request "Laptop <script>alert('A')</script>"
    $first = Invoke-Step $a 20 Approve -CheckConfirmPageIsSafe
    Invoke-Step $a 30 Revision 10 | Out-Null

    # Replays: the used token and a sibling token from the same (already answered) email must not move the workflow.
    # Resume runs synchronously, so the only unread email now is the one the revision just sent to Staff.
    $pending = @(Get-NewMail $a).Count
    Assert ($pending -eq 1) 'the revision produced exactly one new email (to Staff)'
    $reuse = Resume $first.Link.Token 'replay'
    $sibling = Resume ($first.Mail.Links | Where-Object Label -eq 'Reject').Token 'sibling'
    Start-Sleep -Milliseconds 500
    Assert (@(Get-NewMail $a).Count -eq $pending) "reusing a used token ($($reuse.StatusCode)) or a sibling token ($($sibling.StatusCode)) does not advance the workflow"
    Assert ((Invoke-Http Get $first.Link.Url).StatusCode -eq 410) 'confirm page reports a used link as gone (410)'

    # A new process on the same App_Data: the rest of request A (bookmarks, variables, history) can only come from SQLite.
    Write-Host '== restart the app (hard kill) while request A waits at Staff'
    $before = $process.Id
    Stop-Proc $process
    Start-App
    Assert ($process.Id -ne $before) "the app runs as a new process ($before -> $($process.Id))"
    $state = Wait-InstanceState $a 'Running/Suspended'
    Assert ($state -eq 'Running/Suspended') "request A is still suspended after the restart ($state)"

    Invoke-Step $a 10 Approve -Comment $true | Out-Null # Resubmit; a non-string comment is ignored, not a fault
    Invoke-Step $a 20 Approve | Out-Null
    Invoke-Step $a 30 Approve | Out-Null
    Invoke-Step $a 40 Revision 20 | Out-Null
    Invoke-Step $a 20 Approve | Out-Null
    Invoke-Step $a 30 Approve | Out-Null
    $last = Invoke-Step $a 40 Approve
    Assert ([regex]::Matches($last.Mail.Body, '<tr><td>').Count -eq 8) 'action email shows the 8 decisions so far'
    Wait-Final $a 'Approved' @(20, 30, 40) 9

    # What Studio's instance view shows: the journal (timeline) and per-node execution counts (the highlighted path).
    # Each node must run exactly once per arriving token, also where several paths and loops merge.
    $journal = (Invoke-Http Get "$base/elsa/api/workflow-instances/$a/journal" -Headers $auth).Content | ConvertFrom-Json
    $path = @($journal.items | Where-Object { $_.eventName -eq 'Completed' -and $_.activityId -in $nodes } | ForEach-Object activityId)
    $expectedPath = 'SubmitRequest', 'ValidRequest', 'EmailSubmitted', 'Respond202', 'SectionHead', 'DeptHead', 'SendBackTo', 'Staff',
        'SectionHead', 'DeptHead', 'DivisionHead', 'SendBackTo', 'SectionHead', 'DeptHead', 'DivisionHead', 'EmailApproved'
    Assert (($path -join ',') -eq ($expectedPath -join ',')) "journal lists the nodes request A ran, in order, across the restart (got $($path -join ','))"
    $report = Invoke-Http Post "$base/elsa/api/activity-executions/report" @{ workflowInstanceId = $a; activityNodeIds = @($activities.nodeId) } $auth
    $counts = @(($report.Content | ConvertFrom-Json).stats | Where-Object completedCount -gt 0 | ForEach-Object { "$($_.activityId)=$($_.completedCount)" } | Sort-Object)
    $expectedCounts = @('SubmitRequest=1', 'ValidRequest=1', 'EmailSubmitted=1', 'Respond202=1', 'SectionHead=3', 'DeptHead=3', 'DivisionHead=2', 'SendBackTo=2', 'Staff=1', 'EmailApproved=1' | Sort-Object)
    Assert ($report.StatusCode -eq 200 -and ($counts -join ',') -eq ($expectedCounts -join ',')) "execution report highlights the nodes that ran, with their counts (got $($counts -join ','))"

    Write-Host '== request B: rejected by Section Head'
    $b = Start-Request 'Conference trip'
    Invoke-Step $b 20 Reject | Out-Null
    Wait-Final $b 'Rejected' @(20) 1

    Write-Host '== request D: rejected by Division Head'
    $d = Start-Request 'Budget'
    Invoke-Step $d 20 Approve | Out-Null
    Invoke-Step $d 30 Approve | Out-Null
    Invoke-Step $d 40 Reject | Out-Null
    Wait-Final $d 'Rejected' @(20, 30, 40) 3

    Write-Host '== request E: withdrawn by the requester after a revision'
    $e = Start-Request 'Training'
    Invoke-Step $e 20 Revision 10 | Out-Null
    Invoke-Step $e 10 Reject | Out-Null # Withdraw
    Wait-Final $e 'Withdrawn' @(20) 2

    Write-Host '== request F: Division Head sends it back to Dept Head'
    $f = Start-Request 'Monitor'
    Invoke-Step $f 20 Approve | Out-Null
    Invoke-Step $f 30 Approve | Out-Null
    Invoke-Step $f 40 Revision 30 | Out-Null
    Invoke-Step $f 30 Approve | Out-Null
    Invoke-Step $f 40 Approve | Out-Null
    Wait-Final $f 'Approved' @(20, 30, 40) 5

    Write-Host '== request C: Approve and Reject confirmed at the same time'
    $c = Start-Request 'Race'
    $mail = Wait-Mail $people[20].email $c
    $deadline = (Get-Date).AddSeconds(15)
    while ((Invoke-Http Get $mail.Links[0].Url).StatusCode -ne 200 -and (Get-Date) -lt $deadline) { Start-Sleep -Milliseconds 250 }
    $urls = @('Approve', 'Reject' | ForEach-Object { $l = $_; "$base/elsa/api/bookmarks/resume?t=$([uri]::EscapeDataString(($mail.Links | Where-Object Label -eq $l).Token))" })
    $codes = $urls | ForEach-Object -ThrottleLimit 2 -Parallel {
        (Invoke-WebRequest -Method Post -Uri $_ -Body '{"input":{"comment":"race"}}' -ContentType 'application/json' -SkipHttpErrorCheck).StatusCode
    }
    Start-Sleep -Seconds 1
    $after = @(Get-NewMail $c)
    Assert ($after.Count -eq 1) "exactly one of the two decisions took effect (codes $($codes -join ','); emails: $($after.Subject -join ' | '))"
    $expected = if ($after[0].Subject -like 'Rejected:*') { 'Finished/Finished' } else { 'Running/Suspended' }
    $state = Wait-InstanceState $c $expected
    Assert ($state -eq $expected) "instance state matches the winning decision ($($after[0].Subject) -> $state)"

    Write-Host 'SMOKE TEST PASSED' -ForegroundColor Green
}
catch {
    $failed = $true
    Write-Host "SMOKE TEST FAILED: $($_.Exception.Message)" -ForegroundColor Red
    Write-Host $_.ScriptStackTrace
    foreach ($log in @($logOut, $logErr) + @(if ($studio) { "$logOut.studio", "$logErr.studio" })) { if (Test-Path $log) { Write-Host "--- $log (tail)"; Get-Content $log -Tail 40 } }
}
finally {
    Stop-Proc $studio
    Stop-Proc $process
    foreach ($pair in $savedEnv.GetEnumerator()) { [Environment]::SetEnvironmentVariable($pair.Key, $pair.Value) }
    Write-Host '== app stopped'
}

exit ([int]$failed)
