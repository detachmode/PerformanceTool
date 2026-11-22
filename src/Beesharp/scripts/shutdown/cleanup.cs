// Shutdown program: always runs at the end of a run (after a normal finish, after Stop, and after a failed preflight).
// Use it to clean up what the run created. `Data` is the run bag: read what the preflight and the scenarios stored there
// (context.RunBag in a scenario) and write results for the Environment tab.
// Call Log("...") to stream output. A failure here shows as failed but does not change the scenarios' results.

Log("Cleaning up what this run created...");
var tenant = Data.TryGetValue("tenantId", out var t) ? t?.ToString() : "none";
Log($"Releasing tenant {tenant}");
await Task.Delay(800, CancellationToken);

Data["tenantId"] = "released";
Data["cleanedUp"] = true;
Log("Shutdown finished.");
