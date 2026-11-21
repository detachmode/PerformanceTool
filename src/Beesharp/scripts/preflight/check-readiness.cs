// Preflight script: runs once before scenarios start.
// Call Log("...") or Logger.LogInformation("...") to stream live output to the popup.
// Throw an exception or call Fail("reason") to abort the run before any scenarios start.

Log("Step 1/3: Checking target environment readiness...");
await Task.Delay(500, CancellationToken);

Log("Step 2/3: Warming up caches & verifying auth token...");
await Task.Delay(500, CancellationToken);

Log("Step 3/3: All preflight checks passed! Ready to launch scenarios.");
