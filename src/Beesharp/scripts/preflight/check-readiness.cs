// Preflight script: runs once before scenarios start.
// Call Log("...") or Logger.LogInformation("...") to stream live output to the popup.
// Throw an exception or call Fail("reason") to abort the run before any scenarios start.
// `Data` is the run bag (runtime data shared by the whole run): write what scenarios and the shutdown program need.
// Scenarios read it as context.RunBag; the Environment tab shows it live.

Log("Step 1/3: Checking target environment readiness...");
await Task.Delay(500, CancellationToken);
Data["tenantId"] = "acme-load-7f3";

Log("Step 2/3: Warming up caches & verifying auth token...");
await Task.Delay(1500, CancellationToken);
Data["authToken"] = $"tok-{Guid.NewGuid():N}";

Log("Step 3/3: All preflight checks passed! Ready to launch scenarios.");
Data["preflightAt"] = DateTimeOffset.UtcNow.ToString("HH:mm:ss");
